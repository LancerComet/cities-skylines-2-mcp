using System;
using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private EntityQuery m_WaterwayEdgeQuery;
        private bool m_WaterwayEdgeQueryCreated;

        /// <summary>全图水路边的一帧只读快照；不按名字或中点裁剪，也不排除港口自有水路。</summary>
        private BridgeResponse WaterwayGraph(BridgeRequest request)
        {
            if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                return BridgeResponse.Error(405, "waterway graph is GET-only");
            foreach (string key in request.Query.Keys)
                if (key != "limit" && key != "expectedLoadedSaveGuid")
                    return BridgeResponse.Error(400, "unsupported waterway graph parameter: " + key);
            int limit = 1000;
            if (request.Query.ContainsKey("limit") && (!request.TryGetInt("limit", out limit) || limit < 1 || limit > 3000))
                return BridgeResponse.Error(400, "limit must be an integer within 1..3000");
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            string loaded = World.GetExistingSystemManaged<Game.Serialization.LoadGameSystem>()?.context.instigatorGuid.ToString();
            if (!Guid.TryParse(loaded, out Guid loadedGuid) || loadedGuid == Guid.Empty)
                return BridgeResponse.Error(409, "loaded save identity unavailable; no waterway snapshot returned");
            if (request.Query.TryGetValue("expectedLoadedSaveGuid", out string expected)) {
                if (!Guid.TryParse(expected, out Guid expectedGuid) || expectedGuid == Guid.Empty)
                    return BridgeResponse.Error(400, "expectedLoadedSaveGuid must be a nonempty GUID");
                if (expectedGuid != loadedGuid) return BridgeResponse.Error(409, "loaded save identity does not match expectedLoadedSaveGuid");
            }
            var prefabs = World.GetExistingSystemManaged<PrefabSystem>();
            if (prefabs == null) return BridgeResponse.Error(409, "native prefab system unavailable");
            if (!m_WaterwayEdgeQueryCreated) {
                m_WaterwayEdgeQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc {
                    All = new[] { ComponentType.ReadOnly<Waterway>(), ComponentType.ReadOnly<Edge>() },
                    None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() }
                });
                m_WaterwayEdgeQueryCreated = true;
            }
            m_WaterwayEdgeQuery.CompleteDependency();
            var rows = new List<object>();
            var missing = new List<object>();
            int matched;
            using (var entities = m_WaterwayEdgeQuery.ToEntityArray(Allocator.Temp)) {
                matched = entities.Length;
                for (int i = 0; i < entities.Length && i < limit; i++) {
                    Entity entity = entities[i];
                    var ends = EntityManager.GetComponentData<Edge>(entity);
                    Entity prefabEntity = EntityManager.HasComponent<PrefabRef>(entity)
                        ? EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab : Entity.Null;
                    PrefabBase prefab = null;
                    bool prefabKnown = prefabEntity != Entity.Null && EntityManager.Exists(prefabEntity)
                        && prefabs.TryGetPrefab(prefabEntity, out prefab);
                    object curve = null;
                    float? length = null;
                    if (EntityManager.HasComponent<Curve>(entity)) {
                        var data = EntityManager.GetComponentData<Curve>(entity);
                        var b = data.m_Bezier;
                        if (math.all(math.isfinite(b.a)) && math.all(math.isfinite(b.b))
                            && math.all(math.isfinite(b.c)) && math.all(math.isfinite(b.d))
                            && math.isfinite(data.m_Length) && data.m_Length >= 0f) {
                            curve = new { a = WaterwayPoint(b.a), b = WaterwayPoint(b.b), c = WaterwayPoint(b.c), d = WaterwayPoint(b.d) };
                            length = data.m_Length;
                        }
                    }
                    if (!prefabKnown || curve == null)
                        missing.Add(new { entity = Ref(entity), prefabKnown, curveKnown = curve != null });
                    object compositionGeometry = null;
                    if (EntityManager.HasComponent<Composition>(entity)) {
                        var composition = EntityManager.GetComponentData<Composition>(entity).m_Edge;
                        if (EntityManager.Exists(composition) && EntityManager.HasComponent<NetCompositionData>(composition)) {
                            var data = EntityManager.GetComponentData<NetCompositionData>(composition);
                            if (math.isfinite(data.m_Width) && data.m_Width > 0f && math.isfinite(data.m_HeightRange.min)
                                && math.isfinite(data.m_HeightRange.max) && data.m_HeightRange.min <= data.m_HeightRange.max)
                                compositionGeometry = new { width = data.m_Width,
                                    heightRange = new { min = data.m_HeightRange.min, max = data.m_HeightRange.max } };
                        }
                    }
                    rows.Add(new { entity = Ref(entity), prefab = prefabKnown ? prefab.name : null,
                        prefabType = prefabKnown ? prefab.GetType().Name : null, prefabEntity = Ref(prefabEntity),
                        owner = EntityManager.HasComponent<Owner>(entity) ? Ref(EntityManager.GetComponentData<Owner>(entity).m_Owner) : null,
                        hidden = EntityManager.HasComponent<Hidden>(entity), startNode = Ref(ends.m_Start), endNode = Ref(ends.m_End),
                        startNodeExists = EntityManager.Exists(ends.m_Start), endNodeExists = EntityManager.Exists(ends.m_End),
                        curve, length, compositionGeometry });
                }
            }
            return BridgeResponse.Json(new { schemaVersion = 1, capturedAtUtc = DateTime.UtcNow.ToString("o"),
                loadedSaveGuid = loadedGuid.ToString("N"), scope = "all live Waterway+Edge; includes owned pier networks; excludes Temp and Deleted",
                matched, returned = rows.Count, limit, truncated = matched > rows.Count,
                complete = matched == rows.Count && missing.Count == 0, missing, waterways = rows,
                note = "全图真实水路完整三次曲线；节点为本次加载的index/version。合成高度范围是原生网络占用声明，不是实船高度或桥墩通航、路径及运营验收。" });
        }

        private static object WaterwayPoint(float3 value) => new { x = value.x, y = value.y, z = value.z };
    }
}
