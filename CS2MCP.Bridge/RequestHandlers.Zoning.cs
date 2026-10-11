using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Prefabs;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// 明确边界的批量分区及实际格复核。旧圆刷/多边形沿既有 Cell 写入与 ToolUpdate 刷新；
    /// 原生填充、框选和细刷由 ZoneTools + GenerateZones/ApplyZones 实现，两者语义不混淆。
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private const float kCellSize = 8f;

        private EntityQuery m_ZonePrefabQuery;
        private bool m_ZonePrefabQueryCreated;
        private EntityQuery m_ZoneBlockQuery;
        private bool m_ZoneBlockQueryCreated;

        private EntityQuery ZonePrefabQuery
        {
            get
            {
                if (!m_ZonePrefabQueryCreated)
                {
                    m_ZonePrefabQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<ZoneData>());
                    m_ZonePrefabQueryCreated = true;
                }
                return m_ZonePrefabQuery;
            }
        }

        private EntityQuery ZoneBlockQuery
        {
            get
            {
                if (!m_ZoneBlockQueryCreated)
                {
                    m_ZoneBlockQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Block>(),
                            ComponentType.ReadOnly<Cell>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Game.Tools.Temp>(),
                            ComponentType.ReadOnly<Game.Common.Deleted>(),
                        },
                    });
                    m_ZoneBlockQueryCreated = true;
                }
                return m_ZoneBlockQuery;
            }
        }

        private BridgeResponse GetZoneTypes()
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var zones = new List<object>();
            using (NativeArray<Entity> entities = ZonePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(entity);
                    if (prefab == null)
                    {
                        continue;
                    }
                    ZoneData zoneData = EntityManager.GetComponentData<ZoneData>(entity);
                    zones.Add(new
                    {
                        name = prefab.name,
                        areaType = zoneData.m_AreaType.ToString(),
                        office = zoneData.IsOffice(),
                        locked = IsLocked(entity),
                    });
                }
            }

            return BridgeResponse.Json(new
            {
                note = "use 'name' with /build/zone; zone 'None' clears zoning (dezone)",
                stalenessWarning = LockStalenessWarning,
                zones,
            });
        }

        private BridgeResponse ZoneArea(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            if (!request.Query.TryGetValue("zone", out string zoneName) || string.IsNullOrEmpty(zoneName))
            {
                return BridgeResponse.Error(400, "provide ?zone=<name from /zones, or 'None' to dezone>");
            }
            float x = 0, z = 0;
            List<float2> polygon = null;
            if (request.Query.ContainsKey("nodes")) {
                if (!ReadZonePolygon(request, out polygon, out error)) return error;
                foreach (var point in polygon) { x += point.x; z += point.y; }
                x /= polygon.Count; z /= polygon.Count;
            } else if (!request.TryGetFloat("x", out x) || !request.TryGetFloat("z", out z))
            {
                return BridgeResponse.Error(400, "provide ?x=&z= center coordinates");
            }
            float radius = request.TryGetFloat("radius", out float rawRadius)
                ? math.clamp(rawRadius, kCellSize, 200f)
                : 32f;
            if (polygon != null) foreach (var point in polygon) radius = math.max(radius, math.distance(point, new float2(x, z)));
            bool onlyUnzoned = request.TryGetBool("onlyUnzoned", out bool rawOnlyUnzoned) && rawOnlyUnzoned;

            ZoneType targetZone;
            string resolvedName;
            if (string.Equals(zoneName, "None", StringComparison.OrdinalIgnoreCase))
            {
                targetZone = ZoneType.None;
                resolvedName = "None";
            }
            else
            {
                if (!TryFindPrefabByName(ZonePrefabQuery, zoneName, out Entity zonePrefabEntity, out PrefabBase zonePrefab))
                {
                    return BridgeResponse.Error(404, $"unknown zone '{zoneName}'; list via /zones");
                }
                if (IsLocked(zonePrefabEntity) && !IsForced(request))
                {
                    return BridgeResponse.Error(409, $"zone '{zonePrefab.name}' is locked; reach its native milestone/development prerequisites before zoning");
                }
                targetZone = EntityManager.GetComponentData<ZoneData>(zonePrefabEntity).m_ZoneType;
                resolvedName = zonePrefab.name;
            }

            float2 center = new float2(x, z);
            int cellsChanged = 0;
            int blocksTouched = 0;

            var refreshBlocks = new System.Collections.Generic.List<Entity>();
            using (NativeArray<Entity> blocks = ZoneBlockQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity blockEntity in blocks)
                {
                    Block block = EntityManager.GetComponentData<Block>(blockEntity);
                    float blockExtent = kCellSize * (math.cmax(block.m_Size) + 1) * 0.71f;
                    if (math.distance(block.m_Position.xz, center) > radius + blockExtent)
                    {
                        continue;
                    }

                    DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(blockEntity);
                    bool blockChanged = false;
                    for (int cellZ = 0; cellZ < block.m_Size.y; cellZ++)
                    {
                        for (int cellX = 0; cellX < block.m_Size.x; cellX++)
                        {
                            int index = cellZ * block.m_Size.x + cellX;
                            if (index >= cells.Length)
                            {
                                continue;
                            }
                            Cell cell = cells[index];
                            if ((cell.m_State & CellFlags.Visible) == 0
                                || (cell.m_State & (CellFlags.Blocked | CellFlags.Overridden)) != 0)
                            {
                                continue;
                            }
                            if (cell.m_Zone.Equals(targetZone))
                            {
                                continue;
                            }
                            if (onlyUnzoned && !cell.m_Zone.Equals(ZoneType.None)) continue;
                            float3 cellPosition = ZoneUtils.GetCellPosition(block, new int2(cellX, cellZ));
                            if (polygon != null ? !InsideZonePolygon(cellPosition.xz, polygon) : math.distance(cellPosition.xz, center) > radius)
                            {
                                continue;
                            }
                            cell.m_Zone = targetZone;
                            cells[index] = cell;
                            cellsChanged++;
                            blockChanged = true;
                        }
                    }

                    if (blockChanged)
                    {
                        blocksTouched++;
                        refreshBlocks.Add(blockEntity);
                    }
                }
            }

            World.GetOrCreateSystemManaged<BridgeMapTileRefreshSystem>().Enqueue(refreshBlocks);
            return BridgeResponse.Json(new
            {
                zone = resolvedName,
                center = new { x, z },
                radius,
                cellsChanged,
                blocksTouched,
                nativeRefreshQueued = true,
                shape = polygon == null ? "circle" : "polygon",
                onlyUnzoned,
                note = cellsChanged == 0
                    ? "no changes; use /zones/cells to distinguish already painted, excluded cells and actual gaps"
                    : "zoned; buildings will grow while the simulation runs if demand exists",
            });
        }

        private bool ReadZonePolygon(BridgeRequest request, out List<float2> points, out BridgeResponse error)
        {
            points = new List<float2>(); error = null;
            if (!request.Query.TryGetValue("nodes", out string raw)) { error = BridgeResponse.Error(400, "provide polygon nodes x,z;x,z;..."); return false; }
            foreach (string item in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) {
                var pair = item.Split(',');
                if (pair.Length != 2 || !float.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) || !float.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) || float.IsNaN(x) || float.IsNaN(z) || float.IsInfinity(x) || float.IsInfinity(z)) {
                    error = BridgeResponse.Error(400, "invalid polygon coordinate"); return false;
                }
                points.Add(new float2(x, z));
            }
            if (points.Count < 3 || points.Count > 64) { error = BridgeResponse.Error(400, "polygon needs 3..64 vertices"); return false; }
            float2 min = points[0], max = points[0];
            foreach (var point in points) { min = math.min(min, point); max = math.max(max, point); }
            if (math.cmax(max - min) > 2000) { error = BridgeResponse.Error(400, "polygon extent must be <=2000m per axis"); return false; }
            return true;
        }

        private static bool InsideZonePolygon(float2 point, List<float2> polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++) {
                float2 a = polygon[j], b = polygon[i], edge = b - a;
                float lengthSq = math.lengthsq(edge);
                if (lengthSq > 0 && math.distance(point, a + math.clamp(math.dot(point - a, edge) / lengthSq, 0, 1) * edge) < .01f) return true;
                if ((a.y > point.y) != (b.y > point.y) && point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        /// <summary>按实际可见格检查漏涂，排除道路/设施遮挡和被覆盖格；不把未长楼当漏涂。</summary>
        private BridgeResponse GetZoneCells(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            if (!ReadZonePolygon(request, out var polygon, out error)) return error;
            var names = new Dictionary<ZoneType, string> { [ZoneType.None] = "None" };
            using (var prefabs = ZonePrefabQuery.ToEntityArray(Allocator.Temp)) foreach (Entity entity in prefabs)
                names[EntityManager.GetComponentData<ZoneData>(entity).m_ZoneType] = NativeName(entity);
            var counts = new Dictionary<string, int>(); var gaps = new List<object>();
            int excluded = 0, visible = 0, eligible = 0, unzoned = 0;
            using (var blocks = ZoneBlockQuery.ToEntityArray(Allocator.Temp)) foreach (Entity entity in blocks) {
                var block = EntityManager.GetComponentData<Block>(entity);
                var cells = EntityManager.GetBuffer<Cell>(entity, true);
                for (int cellZ = 0; cellZ < block.m_Size.y; cellZ++) for (int cellX = 0; cellX < block.m_Size.x; cellX++) {
                    int index = cellZ * block.m_Size.x + cellX;
                    if (index >= cells.Length) continue;
                    var cell = cells[index];
                    if ((cell.m_State & CellFlags.Visible) == 0) continue;
                    var position = ZoneUtils.GetCellPosition(block, new int2(cellX, cellZ));
                    if (!InsideZonePolygon(position.xz, polygon)) continue;
                    visible++;
                    if ((cell.m_State & (CellFlags.Blocked | CellFlags.Overridden)) != 0) { excluded++; continue; }
                    eligible++;
                    string name = names.TryGetValue(cell.m_Zone, out string found) ? found : "Unknown";
                    counts.TryGetValue(name, out int count); counts[name] = count + 1;
                    if (cell.m_Zone.Equals(ZoneType.None)) {
                        unzoned++;
                        if (gaps.Count < 500) gaps.Add(new { x = position.x, z = position.z, block = Ref(entity), cell = index });
                    }
                }
            }
            return BridgeResponse.Json(new { visibleCells = visible, excludedCells = excluded, eligibleCells = eligible, unzonedCells = unzoned,
                zones = counts, gaps, gapsComplete = unzoned <= gaps.Count,
                note = "visible non-blocked non-overridden native cells; unzoned can be deliberate public space, compare with plan reservations; no building growth inferred" });
        }
    }
}
