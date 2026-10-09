using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private sealed class AreaCorner { public float? x { get; set; } public float? z { get; set; } }

        /// <summary>有界只读原生资源格；与区域积分/城市加成和月产出分开，不修改资源图。</summary>
        private BridgeResponse GetNaturalResourceField(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            if (!request.TryGetFloat("x", out float x) || !request.TryGetFloat("z", out float z)
                || !request.TryGetFloat("radius", out float radius) || !math.all(math.isfinite(new float3(x, z, radius)))
                || math.abs(x) > 7168 || math.abs(z) > 7168 || radius < 50 || radius > 7168)
                return BridgeResponse.Error(400, "finite x/z within map and radius 50..7168 are required");
            int resolution = 16;
            if (request.Query.ContainsKey("resolution") && !request.TryGetInt("resolution", out resolution)) return BridgeResponse.Error(400, "resolution must be an integer");
            if (resolution < 4 || resolution > 64) return BridgeResponse.Error(400, "resolution must be 4..64");
            var data = World.GetOrCreateSystemManaged<NaturalResourceSystem>().GetData(readOnly: true, out var dependencies);
            dependencies.Complete();
            if (!data.m_Buffer.IsCreated || data.m_Buffer.Length < data.m_TextureSize.x * data.m_TextureSize.y)
                return BridgeResponse.Error(503, "native natural-resource CPU map is not ready");
            float2 min = math.max(new float2(x - radius, z - radius), new float2(-7168));
            float2 max = math.min(new float2(x + radius, z + radius), new float2(7168));
            float2 step = (max - min) / resolution;
            var samples = new List<object>(resolution * resolution);
            for (int row = 0; row < resolution; row++) for (int col = 0; col < resolution; col++)
            {
                float2 point = min + new float2(col + .5f, row + .5f) * step;
                int2 cell = math.clamp((int2)math.floor(point / data.m_CellSize + (float2)data.m_TextureSize * .5f), new int2(0), data.m_TextureSize - 1);
                var native = data.m_Buffer[cell.x + cell.y * data.m_TextureSize.x];
                samples.Add(new { x = point.x, z = point.y, nativeCell = new { x = cell.x, z = cell.y },
                    oil = NaturalResourceAmountInfo(native.m_Oil), ore = NaturalResourceAmountInfo(native.m_Ore),
                    fertility = NaturalResourceAmountInfo(native.m_Fertility), fish = NaturalResourceAmountInfo(native.m_Fish) });
            }
            return BridgeResponse.Json(new { capturedAtUtc = DateTime.UtcNow.ToString("o"), simulationFrame = World.GetOrCreateSystemManaged<SimulationSystem>().frameIndex,
                simulationHasTickedSinceLoad = m_System.SimulationHasTickedSinceLoad, resolution,
                bounds = new { xMin = min.x, xMax = max.x, zMin = min.y, zMax = max.y },
                nativeCellSizeMeters = new { x = data.m_CellSize.x, z = data.m_CellSize.y }, samples,
                note = "read-only native cell values, not interpolated percentages/tons/monthly production; remainingRaw=max(0,base-used) before city modifiers and polygon integration. Sampling may miss deposits smaller than the grid step; repeat native cells are not additional resource. Forest uses separate tree/wood data, not this map. Reloaded maps may be stale until simulation updates" });
        }

        private static object NaturalResourceAmountInfo(NaturalResourceAmount value) => new { baseRaw = (int)value.m_Base, usedRaw = (int)value.m_Used,
            remainingRaw = math.max(0, (int)value.m_Base - (int)value.m_Used) };

        /// <summary>有界读取指定建筑自有区域或一个区域，不扫描全城，不把边界等同投产。</summary>
        private BridgeResponse GetExtractorAreas(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error) || !ReadExistingEntity(request, out Entity target, out error)) return error;
            var queue = new Queue<Entity>(); var seen = new HashSet<Entity>(); var rows = new List<object>();
            queue.Enqueue(target);
            while (queue.Count > 0 && seen.Count < 64)
            {
                Entity entity = queue.Dequeue();
                if (!seen.Add(entity) || !EntityManager.Exists(entity) || EntityManager.HasComponent<Deleted>(entity) || EntityManager.HasComponent<Temp>(entity)) continue;
                if (EntityManager.HasBuffer<AreaNode>(entity) && EntityManager.HasComponent<PrefabRef>(entity)) rows.Add(DescribeExtractorArea(entity));
                if (EntityManager.HasBuffer<Game.Areas.SubArea>(entity))
                    foreach (var sub in EntityManager.GetBuffer<Game.Areas.SubArea>(entity, true))
                        if (queue.Count < 64) queue.Enqueue(sub.m_Area);
                        else return BridgeResponse.Json(new { target = Ref(target), areas = rows, complete = false, note = "subarea traversal limit 64; inspect individual area references for complete detail" });
            }
            return BridgeResponse.Json(new { capturedAtUtc = DateTime.UtcNow.ToString("o"), target = Ref(target), areas = rows,
                complete = queue.Count == 0, note = "read-only native area boundaries and extraction counters; raw resource/work amounts are not tons or production per month; area editing does not by itself prove company operation" });
        }

        private object DescribeExtractorArea(Entity entity)
        {
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            Entity owner = EntityManager.HasComponent<Owner>(entity) ? EntityManager.GetComponentData<Owner>(entity).m_Owner : Entity.Null;
            var nodes = EntityManager.GetBuffer<AreaNode>(entity, true); var points = new List<object>();
            bool editable = TryResolveEditableExtractorArea(entity, out _, out _, out _, out string editReason);
            for (int i = 0; i < math.min(nodes.Length, 128); i++) points.Add(new { index = i, position = RoadPointInfo(nodes[i].m_Position), elevationRaw = nodes[i].m_Elevation });
            var row = new Dictionary<string, object> { ["entity"] = Ref(entity), ["prefab"] = NativeName(prefab),
                ["owner"] = owner == Entity.Null ? null : new { entity = Ref(owner), prefab = PrefabNameOf(owner) },
                ["isLot"] = EntityManager.HasComponent<Game.Areas.Lot>(entity), ["isExtractor"] = EntityManager.HasComponent<Extractor>(entity),
                ["flags"] = EntityManager.HasComponent<Area>(entity) ? EntityManager.GetComponentData<Area>(entity).m_Flags.ToString() : null,
                ["nodeCount"] = nodes.Length, ["nodes"] = points, ["nodesComplete"] = nodes.Length <= 128,
                ["cachedNodeCount"] = EntityManager.HasBuffer<LocalNodeCache>(entity) ? EntityManager.GetBuffer<LocalNodeCache>(entity, true).Length : 0,
                ["editable"] = editable, ["editRejectionReason"] = editReason,
                ["lockedEntranceNodeIndices"] = EntityManager.HasComponent<Game.Areas.Lot>(entity) ? new[] { 0, 1 } : null };
            if (EntityManager.HasComponent<Geometry>(entity))
            {
                var g = EntityManager.GetComponentData<Geometry>(entity);
                row["geometry"] = new { surfaceAreaSquareMeters = g.m_SurfaceArea, center = RoadPointInfo(g.m_CenterPosition), min = RoadPointInfo(g.m_Bounds.min), max = RoadPointInfo(g.m_Bounds.max) };
            }
            if (EntityManager.HasComponent<LotData>(prefab))
            {
                var lot = EntityManager.GetComponentData<LotData>(prefab);
                row["lotRules"] = new { maxRadiusMeters = lot.m_MaxRadius, allowEditing = lot.m_AllowEditing, onWater = lot.m_OnWater, allowOverlap = lot.m_AllowOverlap };
            }
            if (EntityManager.HasComponent<ExtractorAreaData>(prefab))
            {
                var data = EntityManager.GetComponentData<ExtractorAreaData>(prefab);
                row["extractionConfig"] = new { mapFeature = data.m_MapFeature.ToString(), requireNaturalResource = data.m_RequireNaturalResource,
                    objectSpawnFactorRaw = data.m_ObjectSpawnFactor, maxObjectAreaProportion = data.m_MaxObjectArea, workAmountFactor = data.m_WorkAmountFactor };
            }
            if (EntityManager.HasComponent<Extractor>(entity))
            {
                var data = EntityManager.GetComponentData<Extractor>(entity);
                row["extraction"] = new { resourceAmountRaw = data.m_ResourceAmount, maxConcentrationRaw = data.m_MaxConcentration,
                    extractedAmountRaw = data.m_ExtractedAmount, totalExtractedRaw = data.m_TotalExtracted, harvestedAmountRaw = data.m_HarvestedAmount,
                    workAmountRaw = data.m_WorkAmount, workType = data.m_WorkType.ToString() };
            }
            return row;
        }

        private bool TryResolveEditableExtractorArea(Entity area, out Entity owner, out Entity prefab, out LotData lot, out string reason)
        {
            owner = Entity.Null; prefab = Entity.Null; lot = default; reason = "target must be an existing native editable extractor lot";
            if (!EntityManager.Exists(area) || EntityManager.HasComponent<Deleted>(area) || EntityManager.HasComponent<Temp>(area)
                || !EntityManager.HasComponent<Game.Areas.Lot>(area) || !EntityManager.HasComponent<Extractor>(area)
                || !EntityManager.HasBuffer<AreaNode>(area) || !EntityManager.HasComponent<PrefabRef>(area) || !EntityManager.HasComponent<Owner>(area)) return false;
            prefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
            if (!EntityManager.HasComponent<ExtractorAreaData>(prefab) || !EntityManager.HasComponent<LotData>(prefab) || !EntityManager.HasComponent<AreaGeometryData>(prefab)) return false;
            lot = EntityManager.GetComponentData<LotData>(prefab);
            if (!lot.m_AllowEditing) { reason = "native lot does not allow editing"; return false; }
            owner = EntityManager.GetComponentData<Owner>(area).m_Owner;
            if (!EntityManager.Exists(owner) || EntityManager.HasComponent<Deleted>(owner) || EntityManager.HasComponent<Temp>(owner)
                || !EntityManager.HasComponent<Game.Buildings.Building>(owner) || !EntityManager.HasComponent<PrefabRef>(owner) || !EntityManager.HasComponent<Game.Objects.Transform>(owner)
                || !EntityManager.HasBuffer<Game.Areas.SubArea>(owner)) { reason = "area owner must be a live native building, not another area or an editor asset"; return false; }
            bool attached = false;
            foreach (var sub in EntityManager.GetBuffer<Game.Areas.SubArea>(owner, true)) if (sub.m_Area == area) attached = true;
            if (!attached) { reason = "area is not referenced by its building owner"; return false; }
            reason = null; return true;
        }

        /// <summary>保留原生入口边、所属及缓存，已有Lot沿原生Relocate预览/校验/应用，不创建独立区域。</summary>
        private BridgeResponse EditExtractorArea(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error) || !ReadExistingEntity(request, out Entity area, out error)) return error;
            if (!TryResolveEditableExtractorArea(area, out Entity owner, out Entity prefabEntity, out LotData lot, out string reason)) return BridgeResponse.Error(400, reason);
            if (IsLocked(prefabEntity) || IsLocked(EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab)) return BridgeResponse.Error(409, "native area/building asset is locked or its content is unavailable");
            if (!request.Query.TryGetValue("nodes", out string raw)) return BridgeResponse.Error(400, "nodes must be a JSON array of world x/z corners, starting with the unchanged entrance nodes 0 and 1");
            List<AreaCorner> corners;
            try { corners = JsonConvert.DeserializeObject<List<AreaCorner>>(raw); }
            catch (JsonException) { return BridgeResponse.Error(400, "invalid nodes JSON; expected [{x:number,z:number},...]"); }
            if (corners == null || corners.Count < 3 || corners.Count > 64) return BridgeResponse.Error(400, "provide 3..64 distinct corners, without repeating the closing point");
            var oldNodes = EntityManager.GetBuffer<AreaNode>(area, true);
            if (oldNodes.Length < 2) return BridgeResponse.Error(409, "native entrance edge is missing; inspect the building instead of creating an unattached area");
            TerrainHeightData terrain = World.GetOrCreateSystemManaged<TerrainSystem>().GetHeightData();
            var points = new AreaNode[corners.Count]; var caches = new LocalNodeCache[corners.Count];
            var parent = EntityManager.GetComponentData<Game.Objects.Transform>(owner);
            float2 min = new float2(float.MaxValue), max = new float2(float.MinValue); double signedArea = 0;
            float minDistance = AreaUtils.GetMinNodeDistance(EntityManager.GetComponentData<AreaGeometryData>(prefabEntity));
            bool hasCache = EntityManager.HasBuffer<LocalNodeCache>(area);
            var oldCache = hasCache ? EntityManager.GetBuffer<LocalNodeCache>(area, true) : default;
            if (hasCache && oldCache.Length != oldNodes.Length) return BridgeResponse.Error(409, "native node/cache lengths disagree; stop and inspect before editing");
            for (int i = 0; i < corners.Count; i++)
            {
                var corner = corners[i];
                if (corner?.x == null || corner.z == null || !math.all(math.isfinite(new float2(corner.x.Value, corner.z.Value)))) return BridgeResponse.Error(400, "every corner must have finite x/z");
                float2 xz = new float2(corner.x.Value, corner.z.Value);
                if (math.any(math.abs(xz) > 7168)) return BridgeResponse.Error(400, "polygon must remain in the playable map");
                if (math.distance(xz, parent.m_Position.xz) > lot.m_MaxRadius + .01f) return BridgeResponse.Error(400, "corner exceeds the native lot maximum radius");
                if (i < 2 && math.distance(xz, oldNodes[i].m_Position.xz) > .01f) return BridgeResponse.Error(400, "entrance nodes 0 and 1 must stay at their native positions and in the original order");
                float3 position = new float3(xz.x, 0, xz.y); position.y = TerrainUtils.SampleHeight(ref terrain, position);
                points[i] = i < 2 ? oldNodes[i] : new AreaNode(position, float.MinValue);
                caches[i] = i < 2 && hasCache ? oldCache[i] : new LocalNodeCache { m_Position = math.mul(math.inverse(parent.m_Rotation), points[i].m_Position - parent.m_Position), m_ParentMesh = -1 };
                min = math.min(min, xz); max = math.max(max, xz);
            }
            for (int i = 0; i < points.Length; i++)
            {
                float2 a = points[i].m_Position.xz, b = points[(i + 1) % points.Length].m_Position.xz;
                if (math.distance(a, b) < math.max(.1f, minDistance)) return BridgeResponse.Error(400, "polygon edge is shorter than the native minimum node distance");
                signedArea += (double)a.x * b.y - (double)b.x * a.y;
                for (int j = i + 1; j < points.Length; j++)
                {
                    if (j == i + 1 || i == 0 && j == points.Length - 1) continue;
                    var first = new Line2.Segment(a, b); var second = new Line2.Segment(points[j].m_Position.xz, points[(j + 1) % points.Length].m_Position.xz);
                    if (MathUtils.Intersect(first, second, out _)) return BridgeResponse.Error(400, "polygon intersects itself; no construction was queued");
                }
            }
            if (Math.Abs(signedArea) < 2) return BridgeResponse.Error(400, "polygon has no usable surface area");
            using (var tiles = MapTileAreaQuery.ToEntityArray(Allocator.Temp))
                foreach (Entity tile in tiles)
                {
                    var bounds = EntityManager.GetComponentData<Geometry>(tile).m_Bounds;
                    if (EntityManager.HasComponent<Native>(tile) && math.all(bounds.min.xz < max) && math.all(bounds.max.xz > min))
                        return BridgeResponse.Error(409, "polygon bounding rectangle reaches unowned land; buy the required tiles normally or reduce the boundary");
                }
            bool same = oldNodes.Length == points.Length;
            if (same) for (int i = 0; i < points.Length; i++) if (math.distance(oldNodes[i].m_Position.xz, points[i].m_Position.xz) > .01f) same = false;
            if (request.TryGetBool("dryRun", out bool dry) && dry || same)
                return BridgeResponse.Json(new { dryRun = dry, changed = !same, area = Ref(area), owner = Ref(owner), nodes = points.Length, projectedAreaSquareMeters = Math.Abs(signedArea) * .5,
                    note = "eligibility, native entrance/radius, owned bounding rectangle and polygon checked only; not a collision, cost, natural-resource or production preview; identical boundary is not queued" });
            if (TransitToolsBusy(out error)) return error;
            var prefab = World.GetOrCreateSystemManaged<PrefabSystem>().GetPrefab<PrefabBase>(prefabEntity);
            if (!World.GetOrCreateSystemManaged<BridgeToolSystem>().TryQueueExtractorAreaEdit(area, owner, prefabEntity, prefab, points, hasCache ? caches : null, request))
                return BridgeResponse.Error(409, "another construction operation is in progress");
            return null;
        }
    }
}
