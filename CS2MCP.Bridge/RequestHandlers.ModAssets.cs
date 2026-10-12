using System;
using System.Collections.Generic;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private bool IsStandaloneProp(Entity entity, PrefabBase prefab)
        {
            return prefab is StaticObjectPrefab && !(prefab is BuildingPrefab) && !(prefab is BuildingExtensionPrefab) && !EntityManager.HasComponent<TreeData>(entity)
                && !EntityManager.HasComponent<TransportStopData>(entity) && !EntityManager.HasComponent<NetObjectData>(entity)
                && EntityManager.HasComponent<PlaceableObjectData>(entity) && EntityManager.HasComponent<ObjectGeometryData>(entity)
                && (EntityManager.GetComponentData<ObjectGeometryData>(entity).m_Flags & (Game.Objects.GeometryFlags.Marker | Game.Objects.GeometryFlags.ReadOnly)) == 0
                && prefab.name.IndexOf("Invisible", StringComparison.OrdinalIgnoreCase) < 0;
        }
        private BridgeResponse GetModAssets(BridgeRequest request)
        {
            if (!TryGetCity(out _, out var error)) return error;
            request.Query.TryGetValue("query", out var search);
            int offset = request.TryGetInt("offset", out var rawOffset) ? math.clamp(rawOffset, 0, 100000) : 0;
            int limit = request.TryGetInt("limit", out var rawLimit) ? math.clamp(rawLimit, 1, 200) : 50;
            var prefabs = World.GetExistingSystemManaged<PrefabSystem>();
            var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<PlaceableObjectData>(), ComponentType.ReadOnly<ObjectGeometryData>());
            var results = new List<object>(); int count = 0;
            try
            {
                using (var entities = query.ToEntityArray(Allocator.Temp)) foreach (var entity in entities)
                {
                    var prefab = prefabs.GetPrefab<PrefabBase>(entity);
                    if (prefab == null || !IsStandaloneProp(entity, prefab)) continue;
                    string localized = null; Game.SceneFlow.GameManager.instance.localizationManager.activeDictionary?.TryGetValue("Assets.NAME[" + prefab.name + "]", out localized);
                    if (!string.IsNullOrEmpty(search) && prefab.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 && (localized ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (count++ < offset || results.Count >= limit) continue;
                    var placement = EntityManager.GetComponentData<PlaceableObjectData>(entity);
                    var geometry = EntityManager.GetComponentData<ObjectGeometryData>(entity);
                    results.Add(new { name = prefab.name, localizedName = localized, type = prefab.GetType().Name, locked = IsLocked(entity), constructionCost = placement.m_ConstructionCost,
                        placementFlags = placement.m_Flags.ToString(), size = OptionalModApi.JsonValue(geometry.m_Size) });
                }
            }
            finally { query.Dispose(); }
            return BridgeResponse.Json(new { totalMatches = count, offset, returned = results.Count, complete = offset + results.Count >= count, prefabs = results,
                note = "已注册且具有原生放置定义的可见静态道具；名称/分类/图标由导入与本地化库提供。搜索不证明场址可施工，放置仍须原生Validation" });
        }
        private BridgeResponse PlaceModProp(BridgeRequest request)
        {
            if (!TryGetCity(out _, out var error)) return error;
            if (!request.Query.TryGetValue("prefab", out var name) || !request.TryGetFloat("x", out float x) || !request.TryGetFloat("z", out float z)) return BridgeResponse.Error(400, "prefab and finite world x/z required");
            request.TryGetFloat("rotation", out var rotation);
            if (!math.isfinite(x) || !math.isfinite(z) || math.abs(x) > 7168 || math.abs(z) > 7168 || !math.isfinite(rotation)) return BridgeResponse.Error(400, "invalid world coordinates/rotation");
            var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<PlaceableObjectData>(), ComponentType.ReadOnly<ObjectGeometryData>());
            Entity entity; PrefabBase prefab;
            try { if (!TryFindPrefabByName(query, name, out entity, out prefab) || !IsStandaloneProp(entity, prefab)) return BridgeResponse.Error(400, "not an eligible visible standalone prop"); }
            finally { query.Dispose(); }
            if (IsLocked(entity)) return BridgeResponse.Error(409, "prop is locked or its content is unavailable");
            var terrain = World.GetExistingSystemManaged<TerrainSystem>().GetHeightData();
            var position = new float3(x, 0, z); position.y = TerrainUtils.SampleHeight(ref terrain, position);
            if (request.TryGetFloat("y", out var y))
            {
                if (!math.isfinite(y) || y < -100 || y > 3000) return BridgeResponse.Error(400, "invalid y"); position.y = y;
            }
            if (TransitToolsBusy(out error)) return error;
            if (!World.GetExistingSystemManaged<BridgeToolSystem>().TryQueuePlacement(entity, prefab, position, quaternion.RotateY(math.radians(rotation)), request, out error)) return error;
            return null;
        }
    }
}
