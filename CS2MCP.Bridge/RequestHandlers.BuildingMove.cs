using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private BridgeResponse MoveBuilding(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error) || !ReadExistingEntity(request, out Entity target, out error)) return error;
            if (!EntityManager.HasComponent<Game.Buildings.Building>(target) || EntityManager.HasComponent<Owner>(target)
                || !EntityManager.HasComponent<Transform>(target) || !EntityManager.HasComponent<PrefabRef>(target))
                return BridgeResponse.Error(400, "only standalone placed buildings can be moved; subbuildings and upgrades move with their parent");
            Entity prefabEntity = EntityManager.GetComponentData<PrefabRef>(target).m_Prefab;
            if (!IsContentAvailable(prefabEntity)) return BridgeResponse.Error(409, "building content is unavailable");
            // 专门产业的经营 Hub 通过 Attachment 关联，不是普通 SubObject/扩建。
            // 现有搬迁定义只移动占位母体及场地，会留下原企业；禁止进入不完整的搬迁管线。
            if (EntityManager.HasComponent<PlaceholderBuildingData>(prefabEntity))
                return BridgeResponse.Error(409, "specialized industry composite relocation is unsupported; preserve the existing hub and expand in place, or rebuild normally with explicit scope");
            if (!EntityManager.HasComponent<PlaceableObjectData>(prefabEntity)
                || EntityManager.HasComponent<SpawnableBuildingData>(prefabEntity) && !EntityManager.HasComponent<SignatureBuildingData>(prefabEntity))
                return BridgeResponse.Error(400, "growable zoning buildings cannot be moved through the normal building relocation tool");
            if (!request.TryGetFloat("x", out float x) || !request.TryGetFloat("z", out float z) || !math.isfinite(x) || !math.isfinite(z))
                return BridgeResponse.Error(400, "provide finite x and z");
            var original = EntityManager.GetComponentData<Transform>(target);
            var terrain = World.GetOrCreateSystemManaged<TerrainSystem>().GetHeightData();
            float3 position = new float3(x, 0f, z);
            position.y = TerrainUtils.SampleHeight(ref terrain, position);
            quaternion rotation = original.m_Rotation;
            if (request.TryGetFloat("rotation", out float degrees))
            {
                if (!math.isfinite(degrees)) return BridgeResponse.Error(400, "rotation must be finite");
                rotation = quaternion.RotateY(math.radians(degrees));
            }
            if (!TryResolveRoadAttachedBuilding(prefabEntity, position, out Game.Tools.ControlPoint attachedPoint, out error)) return error;
            if (attachedPoint.m_OriginalEntity != Entity.Null)
            {
                position = attachedPoint.m_Position;
                rotation = attachedPoint.m_Rotation;
            }
            if (math.distance(original.m_Position.xz, position.xz) < .01f && math.abs(math.dot(original.m_Rotation.value, rotation.value)) > .99999f)
                return BridgeResponse.Json(new { moved = false, unchanged = true, building = Ref(target) });
            if (request.TryGetBool("dryRun", out bool dryRun) && dryRun)
                return BridgeResponse.Json(new { building = Ref(target), position = new { x = position.x, y = position.y, z = position.z },
                    rotation = new { x = rotation.value.x, y = rotation.value.y, z = rotation.value.z, w = rotation.value.w },
                    note = "geometry only; native overlap, access, money and relocation validation runs on actual application" });
            var prefab = World.GetOrCreateSystemManaged<PrefabSystem>().GetPrefab<PrefabBase>(prefabEntity);
            if (!World.GetOrCreateSystemManaged<BridgeToolSystem>().TryQueueBuildingMove(target, prefabEntity, prefab, position, rotation, request, attachedPoint))
                return BridgeResponse.Error(409, "another build operation is in progress");
            return null;
        }

        /// <summary>原生 SignatureBuildingData 识别全部五类，排除名称相似的升级/普通成长建筑。</summary>
        private BridgeResponse ListSignatureBuildings()
        {
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            var prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var placed = new Dictionary<Entity, List<object>>();
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<Game.Buildings.Signature>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Transform>()))
            using (var buildings = query.ToEntityArray(Allocator.Temp))
                foreach (var building in buildings)
                {
                    if (EntityManager.HasComponent<Deleted>(building) || EntityManager.HasComponent<Game.Tools.Temp>(building)) continue;
                    Entity p = EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
                    if (!placed.TryGetValue(p, out var instances)) placed[p] = instances = new List<object>();
                    var t = EntityManager.GetComponentData<Transform>(building);
                    instances.Add(new { entity = Ref(building), position = new { x = t.m_Position.x, y = t.m_Position.y, z = t.m_Position.z },
                        rotation = new { x = t.m_Rotation.value.x, y = t.m_Rotation.value.y, z = t.m_Rotation.value.z, w = t.m_Rotation.value.w } });
                }
            var rows = new List<object>();
            using (var prefabs = BuildingPrefabQuery.ToEntityArray(Allocator.Temp))
                foreach (var p in prefabs)
                {
                    if (!EntityManager.HasComponent<SignatureBuildingData>(p)) continue;
                    var prefab = prefabSystem.GetPrefab<BuildingPrefab>(p);
                    if (prefab == null) continue;
                    prefab.TryGet<SignatureBuilding>(out var signature);
                    string label = null, description = null;
                    Game.SceneFlow.GameManager.instance?.localizationManager?.activeDictionary?.TryGetValue($"Assets.NAME[{prefab.name}]", out label);
                    Game.SceneFlow.GameManager.instance?.localizationManager?.activeDictionary?.TryGetValue($"Assets.DESCRIPTION[{prefab.name}]", out description);
                    var modifiers = new List<object>();
                    if (EntityManager.HasBuffer<CityModifierData>(p)) foreach (var m in EntityManager.GetBuffer<CityModifierData>(p, true))
                        modifiers.Add(new { scope = "city", type = m.m_Type.ToString(), mode = m.m_Mode.ToString(), range = new { min = m.m_Range.min, max = m.m_Range.max } });
                    var data = EntityManager.GetComponentData<BuildingData>(p);
                    var place = EntityManager.GetComponentData<PlaceableObjectData>(p);
                    rows.Add(new { prefab = prefab.name, localizedName = label, localizedDescription = description,
                        zone = signature?.m_ZoneType?.name, contentAvailable = IsContentAvailable(p), locked = IsLocked(p),
                        lotMeters = new { width = data.m_LotSize.x * 8, depth = data.m_LotSize.y * 8 }, placementFlags = place.m_Flags.ToString(),
                        constructionCost = place.m_ConstructionCost, modifiers, placed = placed.TryGetValue(p, out var instances) ? instances : new List<object>() });
                }
            return BridgeResponse.Json(new { capturedAtUtc = System.DateTime.UtcNow, count = rows.Count, stalenessWarning = LockStalenessWarning,
                note = "native signature catalog; normal unlock and unique-object validation remain enforced; modifiers shown in native units", buildings = rows });
        }
    }
}
