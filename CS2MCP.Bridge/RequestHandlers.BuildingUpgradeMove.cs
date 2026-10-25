using System;
using Colossal.Mathematics;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        /// <summary>仅搬迁已安装的独立正常服务扩建；母体、原附属与加载身份必须同时匹配。</summary>
        private BridgeResponse RelocateBuildingUpgrade(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error) || !ReadExistingEntity(request, out Entity child, out error)) return error;
            string guid = World.GetExistingSystemManaged<Game.Serialization.LoadGameSystem>()?.context.instigatorGuid.ToString();
            if (!request.Query.TryGetValue("expectedLoadedSaveGuid", out string expectedGuid)
                || !string.Equals(guid, expectedGuid, StringComparison.OrdinalIgnoreCase))
                return BridgeResponse.Error(409, "loaded save identity differs; refresh before upgrade relocation");
            if (!request.Query.TryGetValue("owner", out string ownerRef) || !TryParseEntityRef(ownerRef, out Entity owner)
                || !EntityManager.Exists(owner) || EntityManager.HasComponent<Deleted>(owner) || EntityManager.HasComponent<Temp>(owner)
                || !EntityManager.HasComponent<Game.Buildings.Building>(owner) || !EntityManager.HasComponent<Transform>(owner)
                || !EntityManager.HasComponent<PrefabRef>(owner) || !EntityManager.HasBuffer<Game.Buildings.InstalledUpgrade>(owner))
                return BridgeResponse.Error(400, "provide the existing parent building owner index:version");
            if (!EntityManager.HasComponent<Game.Buildings.Building>(child) || !EntityManager.HasComponent<Owner>(child)
                || EntityManager.GetComponentData<Owner>(child).m_Owner != owner
                || !EntityManager.HasComponent<Transform>(child) || !EntityManager.HasComponent<PrefabRef>(child))
                return BridgeResponse.Error(409, "the existing upgrade does not belong to the expected parent");
            Entity prefabEntity = EntityManager.GetComponentData<PrefabRef>(child).m_Prefab;
            if (!EntityManager.HasComponent<ServiceUpgradeData>(prefabEntity) || !EntityManager.HasComponent<BuildingData>(prefabEntity)
                || !EntityManager.HasComponent<PlaceableObjectData>(prefabEntity) || EntityManager.HasComponent<BuildingExtensionData>(prefabEntity)
                || EntityManager.HasComponent<SpawnableBuildingData>(prefabEntity) || EntityManager.HasComponent<PlaceholderBuildingData>(prefabEntity))
                return BridgeResponse.Error(400, "only existing freestanding service building upgrades can be relocated; fixed extensions and growables are excluded");
            Entity parentPrefab = EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab;
            bool offered = false;
            if (EntityManager.HasBuffer<BuildingUpgradeElement>(parentPrefab))
                foreach (var item in EntityManager.GetBuffer<BuildingUpgradeElement>(parentPrefab, true)) offered |= item.m_Upgrade == prefabEntity;
            var installed = EntityManager.GetBuffer<Game.Buildings.InstalledUpgrade>(owner, true);
            int references = 0;
            foreach (var item in installed) if (item.m_Upgrade == child) references++;
            if (!offered || references != 1) return BridgeResponse.Error(409, "the child must occur exactly once in its parent's installed native upgrade list");
            if (IsLocked(prefabEntity)) return BridgeResponse.Error(409, "upgrade content or normal development qualification is unavailable");
            if (!request.TryGetFloat("x", out float x) || !request.TryGetFloat("z", out float z))
                return BridgeResponse.Error(400, "provide finite x and z");
            bool dryRun = true;
            if (request.Query.ContainsKey("dryRun") && !request.TryGetBool("dryRun", out dryRun))
                return BridgeResponse.Error(400, "dryRun must be boolean; omitted defaults to complete native preview");
            request.Query["dryRun"] = dryRun ? "true" : "false";
            var original = EntityManager.GetComponentData<Transform>(child);
            if (!TryResolveUpgradeOwnerSide(owner, prefabEntity, new float2(x, z), original.m_Rotation, out ControlPoint point, out error)) return error;
            var prefab = World.GetOrCreateSystemManaged<PrefabSystem>().GetPrefab<PrefabBase>(prefabEntity);
            if (!(prefab is ObjectPrefab) || prefab.name.IndexOf("Invisible", StringComparison.OrdinalIgnoreCase) >= 0)
                return BridgeResponse.Error(400, "only native visible object upgrades are supported");
            var before = new Entity[installed.Length];
            for (int i = 0; i < before.Length; i++) before[i] = installed[i].m_Upgrade;
            if (!World.GetOrCreateSystemManaged<BridgeToolSystem>().TryQueueBuildingUpgradeMove(child, owner, prefabEntity, prefab, point, before, request))
                return BridgeResponse.Error(409, "another construction operation is in progress");
            return null;
        }

        /// <summary>复用原生OwnerSide四边、格点和优先级，再执行RoadSide前缘地形高度计算。</summary>
        private bool TryResolveUpgradeOwnerSide(Entity owner, Entity childPrefab, float2 requested, quaternion requestedRotation,
            out ControlPoint point, out BridgeResponse error)
        {
            point = default; error = null;
            Entity parentPrefab = EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab;
            if (!EntityManager.HasComponent<BuildingData>(parentPrefab) || !EntityManager.HasComponent<BuildingData>(childPrefab)
                || !EntityManager.HasComponent<ServiceUpgradeData>(childPrefab) || !EntityManager.HasComponent<PlaceableObjectData>(childPrefab))
            { error = BridgeResponse.Error(400, "native parent/upgrade placement data is incomplete"); return false; }
            var placement = EntityManager.GetComponentData<PlaceableObjectData>(childPrefab);
            if ((placement.m_Flags & PlacementFlags.OwnerSide) == 0
                || (placement.m_Flags & (PlacementFlags.Shoreline | PlacementFlags.Floating | PlacementFlags.Hovering)) != 0)
            { error = BridgeResponse.Error(400, "this narrow owner-side resolver excludes shoreline/floating/hovering upgrades"); return false; }
            var transform = EntityManager.GetComponentData<Transform>(owner);
            var parent = EntityManager.GetComponentData<BuildingData>(parentPrefab);
            var child = EntityManager.GetComponentData<BuildingData>(childPrefab);
            var upgrade = EntityManager.GetComponentData<ServiceUpgradeData>(childPrefab);
            if (math.any(parent.m_LotSize <= 0) || math.any(child.m_LotSize <= 0)
                || !math.isfinite(upgrade.m_MaxPlacementDistance) || upgrade.m_MaxPlacementDistance < 0
                || !math.all(math.isfinite(placement.m_PlacementOffset)))
            { error = BridgeResponse.Error(409, "native upgrade geometry or placement range is invalid"); return false; }
            if (!UpgradePointInRange(transform, parent, child, upgrade, requested))
            { error = BridgeResponse.Error(400, "requested point is outside the native parent upgrade range"); return false; }
            var terrain = World.GetOrCreateSystemManaged<TerrainSystem>().GetHeightData();
            float3 hit = new float3(requested.x, 0, requested.y);
            hit.y = TerrainUtils.SampleHeight(ref terrain, hit);
            var input = new ControlPoint { m_Position = hit, m_HitPosition = hit, m_Rotation = requestedRotation,
                m_ElementIndex = new int2(-1, -1), m_OriginalEntity = Entity.Null };
            point = input;
            Quad2 outer = Game.Buildings.BuildingUtils.CalculateCorners(transform, parent.m_LotSize + child.m_LotSize.y).xz;
            int maxOffset = upgrade.m_MaxPlacementOffset >= 0 ? upgrade.m_MaxPlacementOffset : child.m_LotSize.x - 1;
            bool forceSnap = upgrade.m_MaxPlacementDistance == 0;
            if (!forceSnap)
            {
                Quad2 parentCorners = Game.Buildings.BuildingUtils.CalculateCorners(transform, parent.m_LotSize).xz;
                Quad2 childCorners = Game.Buildings.BuildingUtils.CalculateCorners(hit, requestedRotation, (float2)child.m_LotSize * 4f - .4f).xz;
                forceSnap = MathUtils.Intersect(parentCorners, childCorners) && MathUtils.Intersect(outer, hit.xz);
            }
            bool snapped = false;
            snapped |= AddUpgradeOwnerSnap(child, transform, input, ref point, new Line2(outer.a, outer.b), maxOffset, 0, forceSnap);
            snapped |= AddUpgradeOwnerSnap(child, transform, input, ref point, new Line2(outer.b, outer.c), maxOffset, math.PI * .5f, forceSnap);
            snapped |= AddUpgradeOwnerSnap(child, transform, input, ref point, new Line2(outer.c, outer.d), maxOffset, math.PI, forceSnap);
            snapped |= AddUpgradeOwnerSnap(child, transform, input, ref point, new Line2(outer.d, outer.a), maxOffset, math.PI * 1.5f, forceSnap);
            // 原生SnapJob保留未被候选替换的输入点。正放置距离下OwnerSide是吸附选项；
            // 仅零距离或与母体交叠时必须吸附，不能把合法的独立附属强制贴在母体边上。
            if ((!snapped && forceSnap) || !UpgradePointInRange(transform, parent, child, upgrade, point.m_Position.xz))
            { error = BridgeResponse.Error(409, "native owner-side snap did not produce an eligible placement point"); return false; }
            // ObjectTool.SnapJob.CalculateHeight在OwnerSide之后覆盖Y；不是永久采用母体floorY。
            float3 heightSample = (placement.m_Flags & PlacementFlags.RoadSide) != 0
                ? Game.Buildings.BuildingUtils.CalculateFrontPosition(new Transform(point.m_Position, point.m_Rotation), child.m_LotSize.y)
                : point.m_Position;
            point.m_Position.y = TerrainUtils.SampleHeight(ref terrain, heightSample) + placement.m_PlacementOffset.y;
            if (!math.all(math.isfinite(point.m_Position)) || !math.all(math.isfinite(point.m_Rotation.value)))
            { error = BridgeResponse.Error(409, "native owner-side placement produced non-finite geometry"); return false; }
            return true;
        }

        private static bool UpgradePointInRange(Transform transform, BuildingData parent, BuildingData child, ServiceUpgradeData upgrade, float2 point)
        {
            Game.Buildings.BuildingUtils.CalculateUpgradeRangeValues(transform.m_Rotation, parent, child, upgrade,
                out float3 forward, out float width, out float length, out float roundness, out bool _);
            float2 delta = point - transform.m_Position.xz;
            float2 local = math.abs(new float2(math.dot(delta, new float2(forward.z, -forward.x)), math.dot(delta, forward.xz)));
            float2 outside = math.max(local - (new float2(width, length) * .5f - roundness), 0);
            return math.length(outside) <= roundness + .01f;
        }

        private static bool AddUpgradeOwnerSnap(BuildingData child, Transform owner, ControlPoint input, ref ControlPoint best,
            Line2 line, int maxOffset, float angle, bool forceSnap)
        {
            MathUtils.Distance(line, input.m_Position.xz, out float t);
            float phase = ((child.m_LotSize.x - child.m_LotSize.y) & 1) != 0 ? 4 : 0;
            float margin = math.min(2 * maxOffset - child.m_LotSize.y - child.m_LotSize.x, child.m_LotSize.y - child.m_LotSize.x) * 4f;
            float length = math.distance(line.a, line.b);
            if (!math.isfinite(length) || length <= 0) return false;
            float distance = math.clamp(MathUtils.Snap(t * length + phase, 8f) - phase, -margin, length + margin);
            var candidate = input;
            candidate.m_OriginalEntity = Entity.Null;
            candidate.m_Position.y = owner.m_Position.y;
            candidate.m_Position.xz = MathUtils.Position(line, distance / length);
            candidate.m_Direction = math.mul(math.mul(owner.m_Rotation, quaternion.RotateY(angle)), new float3(0, 0, 1)).xz;
            candidate.m_Rotation = ToolUtils.CalculateRotation(candidate.m_Direction);
            candidate.m_SnapPriority = ToolUtils.CalculateSnapPriority(forceSnap ? 1f : 0f, 1f, 0f,
                input.m_HitPosition * .5f, candidate.m_Position * .5f, candidate.m_Direction);
            if (!ToolUtils.CompareSnapPriority(candidate.m_SnapPriority, best.m_SnapPriority)) return false;
            best = candidate;
            return true;
        }
    }
}
