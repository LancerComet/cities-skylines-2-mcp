using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// Road changes through BridgeRoadToolSystem: Replace (the road tool's
    /// Replace mode) changes the road type of existing segments, e.g. widen a
    /// highway or turn a two-way street into a one-way one, keeping the nodes,
    /// connections and (where the width allows) the buildings along it; Connect
    /// builds a segment whose ends snap to existing junctions or split existing
    /// segments, so new links, ramps and track spurs are actually joined up.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        // 原生道路工具会切换合法地下变体；只合并当前可用变体的范围，不启用扩展高程。
        private bool CheckNetElevation(Entity prefab, float elevation, out BridgeResponse error)
        {
            error = null;
            if (!EntityManager.HasComponent<PlaceableNetData>(prefab)) {
                error = BridgeResponse.Error(409, "native network elevation range unavailable"); return false;
            }
            var data = EntityManager.GetComponentData<PlaceableNetData>(prefab);
            float min = data.m_ElevationRange.min, max = data.m_ElevationRange.max;
            if (data.m_UndergroundPrefab != Entity.Null && !IsLocked(data.m_UndergroundPrefab)
                && EntityManager.HasComponent<PlaceableNetData>(data.m_UndergroundPrefab)) {
                var underground = EntityManager.GetComponentData<PlaceableNetData>(data.m_UndergroundPrefab).m_ElevationRange;
                min = math.min(min, underground.min); max = math.max(max, underground.max);
            }
            if (!math.isfinite(elevation) || !math.isfinite(min) || !math.isfinite(max) || min > max || elevation < min || elevation > max) {
                error = BridgeResponse.Error(400, $"elevation must be within native prefab/underground range {min}..{max}m"); return false;
            }
            return true;
        }
        private BridgeResponse ConnectRoad(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("prefab", out string prefabName) || string.IsNullOrEmpty(prefabName))
            {
                return BridgeResponse.Error(400, "provide ?prefab=<network prefab name> (see cs2_find_prefabs category road or net)");
            }
            if (!request.TryGetFloat("x1", out float x1) || !request.TryGetFloat("z1", out float z1)
                || !request.TryGetFloat("x2", out float x2) || !request.TryGetFloat("z2", out float z2))
            {
                return BridgeResponse.Error(400, "provide ?x1=&z1=&x2=&z2= world coordinates for both ends");
            }
            foreach (string key in new[] { "x1", "z1", "x2", "z2", "cx", "cz", "c1x", "c1z", "c2x", "c2z", "e1", "e2", "c1e", "c2e", "snap", "heightTolerance" })
                if (request.Query.ContainsKey(key) && (!request.TryGetFloat(key, out float number) || !math.isfinite(number)))
                    return BridgeResponse.Error(400, $"{key} must be finite");
            foreach (string key in new[] { "x1", "z1", "x2", "z2", "cx", "cz", "c1x", "c1z", "c2x", "c2z" })
                if (request.TryGetFloat(key,out float coordinate) && math.abs(coordinate) > 7168) return BridgeResponse.Error(400,$"{key} must remain in the playable map");
            bool cubic = request.Query.ContainsKey("c1x") || request.Query.ContainsKey("c1z") || request.Query.ContainsKey("c2x") || request.Query.ContainsKey("c2z");
            bool quadratic = request.Query.ContainsKey("cx") || request.Query.ContainsKey("cz");
            if (cubic && quadratic) return BridgeResponse.Error(400, "use either cx/cz or c1x/c1z/c2x/c2z, not both");
            if (quadratic && (!request.Query.ContainsKey("cx") || !request.Query.ContainsKey("cz"))) return BridgeResponse.Error(400, "cx/cz must be provided together");
            if (cubic && (!request.Query.ContainsKey("c1x") || !request.Query.ContainsKey("c1z") || !request.Query.ContainsKey("c2x") || !request.Query.ContainsKey("c2z")))
                return BridgeResponse.Error(400, "a cubic curve requires both control handles c1x/c1z/c2x/c2z");
            if (!cubic && (request.Query.ContainsKey("c1e") || request.Query.ContainsKey("c2e"))) return BridgeResponse.Error(400, "c1e/c2e require cubic control handles");
            foreach (string key in new[] { "e1", "e2", "c1e", "c2e" })
                if (request.TryGetFloat(key, out float elevation) && (elevation < -30 || elevation > 60)) return BridgeResponse.Error(400, $"{key} must be -30..60 metres");
            float heightTolerance = request.TryGetFloat("heightTolerance", out float ht) ? ht : 3f;
            if (heightTolerance < 0 || heightTolerance > 30) return BridgeResponse.Error(400, "heightTolerance must be 0..30 metres");
            float snap = request.TryGetFloat("snap", out float rawSnap) ? math.clamp(rawSnap, 0f, 50f) : 8f;
            if (!TryFindPrefabByName(NetPrefabQuery, prefabName, out Entity prefabEntity, out PrefabBase prefab))
            {
                return BridgeResponse.Error(404, $"unknown network prefab '{prefabName}' (see cs2_find_prefabs category road or net)");
            }
            if (IsLocked(prefabEntity) && !IsForced(request))
            {
                return BridgeResponse.Error(409, $"prefab '{prefab.name}' is locked; reach its native milestone/development prerequisites before building");
            }
            foreach (string key in new[] { "e1", "e2", "c1e", "c2e" })
                if (request.TryGetFloat(key, out float requestedElevation) && !CheckNetElevation(prefabEntity, requestedElevation, out error)) return error;
            if (!EntityManager.HasComponent<NetData>(prefabEntity))
            {
                return BridgeResponse.Error(400, $"'{prefab.name}' is not a network prefab");
            }
            NetData netData = EntityManager.GetComponentData<NetData>(prefabEntity);
            string composition = request.Query.TryGetValue("composition", out string selectedComposition) ? selectedComposition : "auto";
            if (composition != "auto" && composition != "ground") return BridgeResponse.Error(400, "composition must be auto or ground");
            if (composition != "auto" && !(prefab is RoadPrefab || prefab is PathwayPrefab))
                return BridgeResponse.Error(400, "explicit ground composition supports normal roads and paths only");
            bool constantSlope = request.TryGetBool("constantSlope", out bool requestedSlope) && requestedSlope;
            if (constantSlope && (cubic || quadratic || request.Query.ContainsKey("c1e") || request.Query.ContainsKey("c2e")))
                return BridgeResponse.Error(400, "constantSlope currently supports straight courses only; curved slope requires length-based sampling");

            TerrainHeightData heightData = World.GetOrCreateSystemManaged<TerrainSystem>().GetHeightData();
            bool waterway = prefab is WaterwayPrefab;
            WaterSurfaceData<SurfaceWater> waterData = default;
            if (waterway)
            {
                waterData = World.GetOrCreateSystemManaged<WaterSystem>().GetSurfaceData(out var waterDependencies);
                waterDependencies.Complete();
            }
            bool hasE1 = request.TryGetFloat("e1", out float e1), hasE2 = request.TryGetFloat("e2", out float e2);
            bool filterHeight = request.Query.ContainsKey("heightTolerance");
            BridgeRoadToolSystem.BuildEnd start = ResolveBuildEnd(new float2(x1, z1), snap, netData, ref heightData,
                hasE1 || filterHeight ? (float?) (SampleNetworkSurface(waterway, ref waterData, ref heightData, new float3(x1,0,z1)) + e1) : null, heightTolerance);
            BridgeRoadToolSystem.BuildEnd end = ResolveBuildEnd(new float2(x2, z2), snap, netData, ref heightData,
                hasE2 || filterHeight ? (float?) (SampleNetworkSurface(waterway, ref waterData, ref heightData, new float3(x2,0,z2)) + e2) : null, heightTolerance);
            // 航道原生工具以水面为基准；道路和轨道继续保留既有地形基准。
            if (waterway && start.Entity == Entity.Null) start.Position.y = SampleNetworkSurface(true, ref waterData, ref heightData, start.Position);
            if (waterway && end.Entity == Entity.Null) end.Position.y = SampleNetworkSurface(true, ref waterData, ref heightData, end.Position);
            // Free ends can be raised (bridges, flyovers) or lowered (tunnels); snapped ends keep the height of what they join.
            if (start.Entity == Entity.Null && hasE1)
            {
                start.Elevation = math.clamp(e1, -30f, 60f);
                start.Position.y += start.Elevation;
            }
            if (end.Entity == Entity.Null && hasE2)
            {
                end.Elevation = math.clamp(e2, -30f, 60f);
                end.Position.y += end.Elevation;
            }
            // 显式参数不是全部输入：未传高程的自由端默认0，吸附端则采用真实世界位置。
            // 地下专用网络不能凭默认0或旧节点的缺省Elevation字段落到地面；未知/越界在排队前拒绝。
            float resolvedStartElevation = start.Entity == Entity.Null ? start.Elevation
                : start.Position.y - SampleNetworkSurface(waterway, ref waterData, ref heightData, start.Position);
            float resolvedEndElevation = end.Entity == Entity.Null ? end.Elevation
                : end.Position.y - SampleNetworkSurface(waterway, ref waterData, ref heightData, end.Position);
            if (!CheckNetElevation(prefabEntity, resolvedStartElevation, out error)
                || !CheckNetElevation(prefabEntity, resolvedEndElevation, out error)) return error;
            if (start.Entity != Entity.Null && start.Entity == end.Entity)
            {
                return BridgeResponse.Error(400, "both ends snap to the same junction or segment; move an end or lower snap");
            }
            float length = math.distance(start.Position.xz, end.Position.xz);
            if (length < 4f || length > 1500f)
            {
                return BridgeResponse.Error(400, $"segment length after snapping is {length:F0}m; it must be 4-1500m");
            }

            Bezier4x3 bezier;
            if (cubic)
            {
                request.TryGetFloat("c1x", out float c1x); request.TryGetFloat("c1z", out float c1z);
                request.TryGetFloat("c2x", out float c2x); request.TryGetFloat("c2z", out float c2z);
                bezier = new Bezier4x3(start.Position, new float3(c1x,math.lerp(start.Position.y,end.Position.y,1f/3f),c1z),
                    new float3(c2x,math.lerp(start.Position.y,end.Position.y,2f/3f),c2z),end.Position);
            }
            else if (request.TryGetFloat("cx", out float cx) & request.TryGetFloat("cz", out float cz))
            {
                // 控制柄决定切线，曲线通常不经过控制柄；二次曲线精确提升为三次表示。
                float3 a = start.Position;
                float3 d = end.Position;
                float3 m = new float3(cx, math.lerp(a.y, d.y, 0.5f), cz);
                bezier = new Bezier4x3(a, a + (m - a) * (2f / 3f), d + (m - d) * (2f / 3f), d);
            }
            else
            {
                bezier = NetUtils.StraightCurve(start.Position, end.Position);
            }
            // 先拟合地形基准，再叠加桥隧高度；固定端点的世界高度不能参与地形拟合，
            // 否则原生 AdjustPosition 会用控制柄抵消已升高端点，造成高架下陷。
            Curve raw = new Curve { m_Bezier = bezier, m_Length = MathUtils.Length(bezier) };
            if (waterway)
            {
                bezier.b.y = SampleNetworkSurface(true, ref waterData, ref heightData, bezier.b);
                bezier.c.y = SampleNetworkSurface(true, ref waterData, ref heightData, bezier.c);
            }
            else
            {
                raw.m_Bezier.a.y -= start.Elevation;
                raw.m_Bezier.d.y -= end.Elevation;
                bezier = NetUtils.AdjustPosition(raw, start.Entity != Entity.Null, false, end.Entity != Entity.Null, ref heightData).m_Bezier;
            }
            bezier.b.y += math.lerp(start.Elevation, end.Elevation, 1f / 3f);
            bezier.c.y += math.lerp(start.Elevation, end.Elevation, 2f / 3f);
            if (request.TryGetFloat("c1e", out float c1e)) bezier.b.y = SampleNetworkSurface(waterway, ref waterData, ref heightData, bezier.b) + c1e;
            if (request.TryGetFloat("c2e", out float c2e)) bezier.c.y = SampleNetworkSurface(waterway, ref waterData, ref heightData, bezier.c) + c2e;
            bezier.a = start.Position;
            bezier.d = end.Position;
            if (constantSlope)
            {
                bezier.b.y = math.lerp(start.Position.y, end.Position.y, 1f / 3f);
                bezier.c.y = math.lerp(start.Position.y, end.Position.y, 2f / 3f);
            }
            float curveLength = MathUtils.Length(bezier);
            if (!math.isfinite(curveLength) || curveLength > 2000) return BridgeResponse.Error(400, "curve length must be finite and <=2000m; split longer curves into connected segments");
            if (request.TryGetBool("dryRun", out bool dryRun) && dryRun)
                return BridgeResponse.Json(new { dryRun = true, prefab = prefab.name, geometry = cubic ? "cubic" : quadratic ? "quadratic" : "straight",
                    lengthMeters = curveLength, start = RoadEndInfo(start), end = RoadEndInfo(end),
                    controls = new[] { RoadPointInfo(bezier.b), RoadPointInfo(bezier.c) },
                    composition, constantSlope,
                    heightTolerance, startHeightFiltered = hasE1 || filterHeight, endHeightFiltered = hasE2 || filterHeight,
                    note = "geometry and compatible endpoint resolution only; native collision/slope/ownership/cost validation occurs on actual tool preview; control handles are not points the curve must pass through" });

            if (TransitToolsBusy(out error))
            {
                return error;
            }
            BridgeRoadToolSystem tool = World.GetOrCreateSystemManaged<BridgeRoadToolSystem>();
            if (!tool.TryQueueBuild(prefabEntity, prefab, start, end, bezier, request))
            {
                return BridgeResponse.Error(409, "another road operation is in progress, retry shortly");
            }
            // Completed asynchronously by BridgeRoadToolSystem over the next tool frames.
            return null;
        }

        /// <summary>
        /// Snap like the road tool: the nearest junction of a compatible network
        /// within the snap distance, else the nearest compatible segment (split
        /// there, or its end node when the point is at an end), else a free point
        /// on the terrain. Compatible = the networks' layers can connect (so a
        /// track never joins a road).
        /// </summary>
        private static object RoadPointInfo(float3 point) => new { x = point.x, y = point.y, z = point.z };
        private static float SampleNetworkSurface(bool waterway, ref WaterSurfaceData<SurfaceWater> water, ref TerrainHeightData terrain, float3 point)
            => waterway ? WaterUtils.SampleHeight(ref water, ref terrain, point) : TerrainUtils.SampleHeight(ref terrain, point);
        private static object RoadEndInfo(BridgeRoadToolSystem.BuildEnd end) => new { position = RoadPointInfo(end.Position), elevation = end.Elevation,
            snapped = end.Entity != Entity.Null, entity = new { index = end.Entity.Index, version = end.Entity.Version }, split = end.Split };

        private BridgeRoadToolSystem.BuildEnd ResolveBuildEnd(float2 point, float snap, NetData netData, ref TerrainHeightData heightData,
            float? targetHeight = null, float heightTolerance = 3f)
        {
            Entity bestNode = Entity.Null;
            float bestNodeDistance = snap;
            Entity bestEdge = Entity.Null;
            float bestEdgeDistance = snap;
            float bestT = 0f;
            // 未接线的场站端口也属于原生网络节点，不能仅从既有边反查节点。
            // 仍按网络层、实际高度和距离筛选，避免把电缆接到公路或不同层。
            using (EntityQuery nodeQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            }))
            using (NativeArray<Entity> nodes = nodeQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity node in nodes)
                {
                    float3 position = EntityManager.GetComponentData<Node>(node).m_Position;
                    float distance = math.distance(position.xz, point);
                    if (distance >= bestNodeDistance || (targetHeight.HasValue && math.abs(position.y - targetHeight.Value) > heightTolerance)) continue;
                    Entity nodePrefab = EntityManager.GetComponentData<PrefabRef>(node).m_Prefab;
                    if (!EntityManager.HasComponent<NetData>(nodePrefab)) continue;
                    NetData other = EntityManager.GetComponentData<NetData>(nodePrefab);
                    if ((netData.m_ConnectLayers & other.m_RequiredLayers) == 0 && (other.m_ConnectLayers & netData.m_RequiredLayers) == 0) continue;
                    bestNode = node;
                    bestNodeDistance = distance;
                }
            }
            using (NativeArray<Entity> edges = NetEdgeQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity edge in edges)
                {
                    Entity edgePrefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                    if (!EntityManager.HasComponent<NetData>(edgePrefab))
                    {
                        continue;
                    }
                    NetData other = EntityManager.GetComponentData<NetData>(edgePrefab);
                    if ((netData.m_ConnectLayers & other.m_RequiredLayers) == 0 && (other.m_ConnectLayers & netData.m_RequiredLayers) == 0)
                    {
                        continue;
                    }
                    Bezier4x3 curve = EntityManager.GetComponentData<Curve>(edge).m_Bezier;
                    float2 min = math.min(math.min(curve.a.xz, curve.b.xz), math.min(curve.c.xz, curve.d.xz));
                    float2 max = math.max(math.max(curve.a.xz, curve.b.xz), math.max(curve.c.xz, curve.d.xz));
                    if (math.any(point < min - snap) || math.any(point > max + snap))
                    {
                        continue;
                    }
                    Edge ends = EntityManager.GetComponentData<Edge>(edge);
                    foreach (Entity node in new[] { ends.m_Start, ends.m_End })
                    {
                        float3 nodePosition = EntityManager.GetComponentData<Node>(node).m_Position;
                        if (targetHeight.HasValue && math.abs(nodePosition.y-targetHeight.Value) > heightTolerance) continue;
                        float nodeDistance = math.distance(nodePosition.xz, point);
                        if (nodeDistance < bestNodeDistance)
                        {
                            bestNodeDistance = nodeDistance;
                            bestNode = node;
                        }
                    }
                    float edgeDistance = MathUtils.Distance(curve.xz, point, out float t);
                    if (edgeDistance < bestEdgeDistance && (!targetHeight.HasValue || math.abs(MathUtils.Position(curve,t).y-targetHeight.Value) <= heightTolerance))
                    {
                        bestEdgeDistance = edgeDistance;
                        bestEdge = edge;
                        bestT = t;
                    }
                }
            }

            var result = new BridgeRoadToolSystem.BuildEnd();
            if (bestNode == Entity.Null && bestEdge != Entity.Null && (bestT < 0.02f || bestT > 0.98f))
            {
                Edge ends = EntityManager.GetComponentData<Edge>(bestEdge);
                Entity candidate = bestT < 0.02f ? ends.m_Start : ends.m_End;
                if (!targetHeight.HasValue || math.abs(EntityManager.GetComponentData<Node>(candidate).m_Position.y-targetHeight.Value) <= heightTolerance) bestNode = candidate;
            }
            if (bestNode != Entity.Null)
            {
                result.Entity = bestNode;
                result.Position = EntityManager.GetComponentData<Node>(bestNode).m_Position;
                result.Elevation = EntityManager.HasComponent<Game.Net.Elevation>(bestNode)
                    ? math.csum(EntityManager.GetComponentData<Game.Net.Elevation>(bestNode).m_Elevation) * 0.5f
                    : 0f;
            }
            else if (bestEdge != Entity.Null)
            {
                result.Entity = bestEdge;
                result.Split = bestT;
                result.Position = MathUtils.Position(EntityManager.GetComponentData<Curve>(bestEdge).m_Bezier, bestT);
                result.Elevation = EntityManager.HasComponent<Game.Net.Elevation>(bestEdge)
                    ? math.csum(EntityManager.GetComponentData<Game.Net.Elevation>(bestEdge).m_Elevation) * 0.5f
                    : 0f;
            }
            else
            {
                result.Position = new float3(point.x, 0f, point.y);
                result.Position.y = TerrainUtils.SampleHeight(ref heightData, result.Position);
            }
            return result;
        }

        private BridgeResponse ReplaceRoad(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("prefab", out string prefabName) || string.IsNullOrEmpty(prefabName))
            {
                return BridgeResponse.Error(400, "provide ?prefab=<road prefab name> (see cs2_find_prefabs category road)");
            }
            if (!request.Query.TryGetValue("roads", out string rawRoads) || string.IsNullOrEmpty(rawRoads))
            {
                return BridgeResponse.Error(400,
                    "provide ?roads=<list separated by ';'>, each 'index:version' of a road segment from cs2_list_roads");
            }
            string[] items = rawRoads.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (items.Length > 50)
            {
                return BridgeResponse.Error(400, "too many segments in one replacement (max 50)");
            }

            var edges = new List<Entity>();
            for (int i = 0; i < items.Length; i++)
            {
                string item = items[i].Trim();
                if (!TryParseEntityRef(item, out Entity edge) || !EntityManager.Exists(edge)
                    || !EntityManager.HasComponent<Edge>(edge) || !EntityManager.HasComponent<Curve>(edge)
                    || !EntityManager.HasComponent<Composition>(edge))
                {
                    return BridgeResponse.Error(404, $"segment #{i + 1} ('{item}') is not an existing road segment (use index:version from cs2_list_roads)");
                }
                if (EntityManager.HasComponent<Temp>(edge) || EntityManager.HasComponent<Deleted>(edge))
                {
                    return BridgeResponse.Error(409, $"segment #{i + 1} ('{item}') is being modified; retry shortly");
                }
                if (EntityManager.HasComponent<Owner>(edge))
                {
                    return BridgeResponse.Error(409,
                        $"segment #{i + 1} ('{item}') belongs to a building or asset (e.g. an interchange); replace the owner instead");
                }
                if (!edges.Contains(edge))
                {
                    edges.Add(edge);
                }
            }

            if (!TryFindPrefabByName(RoadPrefabQuery, prefabName, out Entity prefabEntity, out PrefabBase prefab))
            {
                return BridgeResponse.Error(404, $"no road prefab named '{prefabName}' (see cs2_find_prefabs category road)");
            }
            if (IsLocked(prefabEntity) && !IsForced(request))
            {
                return BridgeResponse.Error(409, $"road prefab '{prefab.name}' is locked; reach its native milestone/development prerequisites before use");
            }
            bool invert = request.TryGetBool("invert", out bool rawInvert) && rawInvert;
            if (TransitToolsBusy(out error))
            {
                return error;
            }

            BridgeRoadToolSystem tool = World.GetOrCreateSystemManaged<BridgeRoadToolSystem>();
            if (!tool.TryQueueReplace(edges.ToArray(), prefabEntity, prefab, invert, request))
            {
                return BridgeResponse.Error(409, "another road operation is in progress, retry shortly");
            }
            // Completed asynchronously by BridgeRoadToolSystem over the next tool frames.
            return null;
        }
    }
}
