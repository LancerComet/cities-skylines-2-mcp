using System.Collections.Generic;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// Placement helpers: prefab dimensions and placement rules, and shoreline
    /// placement for buildings that must sit on the water's edge (harbors, water
    /// pumps...). The shoreline position is computed like
    /// ObjectToolSystem.SnapJob.SnapShoreline: water cells around the point are
    /// weighted into a wet and a dry centroid, the object goes midway between
    /// them facing the land, shifted by its placement offset; the placement
    /// itself goes through BridgeToolSystem like cs2_place_building.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private BridgeResponse PrefabInfo(BridgeRequest request)
        {
            if (!request.Query.TryGetValue("name", out string name) || string.IsNullOrEmpty(name))
            {
                return BridgeResponse.Error(400, "provide ?name=<prefab name>");
            }
            if (!TryFindPrefabByName(BuildingPrefabQuery, name, out Entity prefabEntity, out PrefabBase prefab)
                && !TryFindPrefabByName(NetPrefabQuery, name, out prefabEntity, out prefab))
            {
                return BridgeResponse.Error(404, $"no building or network prefab named '{name}'");
            }
            object building = null;
            if (EntityManager.HasComponent<BuildingData>(prefabEntity))
            {
                BuildingData data = EntityManager.GetComponentData<BuildingData>(prefabEntity);
                building = new
                {
                    lotCells = new { width = data.m_LotSize.x, depth = data.m_LotSize.y },
                    lotMeters = new { width = data.m_LotSize.x * 8, depth = data.m_LotSize.y * 8 },
                    flags = data.m_Flags.ToString(),
                };
            }
            object geometry = null;
            if (EntityManager.HasComponent<ObjectGeometryData>(prefabEntity))
            {
                ObjectGeometryData data = EntityManager.GetComponentData<ObjectGeometryData>(prefabEntity);
                geometry = new
                {
                    size = new { x = data.m_Size.x, y = data.m_Size.y, z = data.m_Size.z },
                    boundsMin = new { x = data.m_Bounds.min.x, y = data.m_Bounds.min.y, z = data.m_Bounds.min.z },
                    boundsMax = new { x = data.m_Bounds.max.x, y = data.m_Bounds.max.y, z = data.m_Bounds.max.z },
                };
            }
            object placement = null;
            if (EntityManager.HasComponent<PlaceableObjectData>(prefabEntity))
            {
                PlaceableObjectData data = EntityManager.GetComponentData<PlaceableObjectData>(prefabEntity);
                placement = new
                {
                    flags = data.m_Flags.ToString(),
                    offset = new { x = data.m_PlacementOffset.x, y = data.m_PlacementOffset.y, z = data.m_PlacementOffset.z },
                    constructionCost = data.m_ConstructionCost,
                };
            }
            object network = null;
            if (EntityManager.HasComponent<NetData>(prefabEntity))
            {
                NetData data = EntityManager.GetComponentData<NetData>(prefabEntity);
                float? width = null;
                float? maxSlope = null;
                float? elevatedLength = null;
                object segmentLength = null;
                if (EntityManager.HasComponent<NetGeometryData>(prefabEntity))
                {
                    NetGeometryData geometryData = EntityManager.GetComponentData<NetGeometryData>(prefabEntity);
                    width = geometryData.m_DefaultWidth;
                    maxSlope = geometryData.m_MaxSlopeSteepness;
                    elevatedLength = geometryData.m_ElevatedLength;
                    segmentLength = new { min = geometryData.m_EdgeLengthRange.min, max = geometryData.m_EdgeLengthRange.max };
                }
                object elevationRange = null;
                if (EntityManager.HasComponent<PlaceableNetData>(prefabEntity))
                {
                    PlaceableNetData placeableNet = EntityManager.GetComponentData<PlaceableNetData>(prefabEntity);
                    elevationRange = new { min = placeableNet.m_ElevationRange.min, max = placeableNet.m_ElevationRange.max };
                }
                network = new
                {
                    layers = data.m_RequiredLayers.ToString(),
                    connectsTo = data.m_ConnectLayers.ToString(),
                    width,
                    maxSlope,
                    elevationRange,
                    elevatedSpanLength = elevatedLength,
                    segmentLength,
                };
            }
            return BridgeResponse.Json(new
            {
                name = prefab.name,
                type = prefab.GetType().Name,
                locked = IsLocked(prefabEntity),
                building,
                geometry,
                placement,
                network,
                note = "lot = the building's footprint in 8 m cells (x = width along its front, depth = z); " +
                       "placement flags tell where it can go (Shoreline = on the water's edge, use cs2_place_shoreline).",
            });
        }

        /// <summary>
        /// Place a road-side building flush against a road segment: the lot's
        /// front touches the road edge and faces it, at curve position t on the
        /// chosen side (left/right as seen driving from the segment's start to
        /// its end). Places through BridgeToolSystem like cs2_place_building.
        /// </summary>
        private BridgeResponse PlaceRoadside(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("prefab", out string prefabName) || string.IsNullOrEmpty(prefabName))
            {
                return BridgeResponse.Error(400, "provide ?prefab=<building name>");
            }
            if (!request.Query.TryGetValue("road", out string rawRoad) || !TryParseEntityRef(rawRoad, out Entity road)
                || !EntityManager.Exists(road) || !EntityManager.HasComponent<Curve>(road) || !EntityManager.HasComponent<Edge>(road))
            {
                return BridgeResponse.Error(400, "provide ?road=index:version of a road segment (cs2_road_graph)");
            }
            request.Query.TryGetValue("side", out string side);
            bool left = string.Equals(side, "left", System.StringComparison.OrdinalIgnoreCase);
            if (!left && !string.Equals(side, "right", System.StringComparison.OrdinalIgnoreCase))
            {
                return BridgeResponse.Error(400, "provide ?side=left|right (as seen driving from the segment's start to its end)");
            }
            float t = request.TryGetFloat("t", out float rawT) ? math.clamp(rawT, 0f, 1f) : 0.5f;
            request.TryGetFloat("gap", out float gap);
            if (!TryFindPrefabByName(BuildingPrefabQuery, prefabName, out Entity prefabEntity, out PrefabBase prefab))
            {
                return BridgeResponse.Error(404, $"unknown building prefab '{prefabName}'");
            }
            if (IsLocked(prefabEntity) && !IsForced(request))
            {
                return BridgeResponse.Error(409, $"prefab '{prefab.name}' is locked (milestone not reached); pass force=true to place anyway");
            }
            float lotDepth = EntityManager.HasComponent<BuildingData>(prefabEntity)
                ? EntityManager.GetComponentData<BuildingData>(prefabEntity).m_LotSize.y * 8f
                : 16f;
            Entity roadPrefab = EntityManager.GetComponentData<PrefabRef>(road).m_Prefab;
            float roadWidth = EntityManager.HasComponent<NetGeometryData>(roadPrefab)
                ? EntityManager.GetComponentData<NetGeometryData>(roadPrefab).m_DefaultWidth
                : 16f;

            Colossal.Mathematics.Bezier4x3 curve = EntityManager.GetComponentData<Curve>(road).m_Bezier;
            float3 point = Colossal.Mathematics.MathUtils.Position(curve, t);
            float2 tangent = math.normalizesafe(Colossal.Mathematics.MathUtils.Tangent(curve, t).xz);
            // Left of the driving direction is (-tz, tx) in x/z.
            float2 normal = left ? new float2(-tangent.y, tangent.x) : new float2(tangent.y, -tangent.x);
            float distance = roadWidth * 0.5f + lotDepth * 0.5f + math.max(0f, gap);
            float3 position = point;
            position.xz += normal * distance;
            TerrainHeightData terrain = World.GetOrCreateSystemManaged<TerrainSystem>().GetHeightData();
            position.y = TerrainUtils.SampleHeight(ref terrain, position);
            float2 facing = -normal;
            quaternion rotation = ToolUtils.CalculateRotation(facing);
            float rotationDegrees = math.degrees(math.atan2(facing.x, facing.y));

            if (request.TryGetBool("dryRun", out bool dryRun) && dryRun)
            {
                return BridgeResponse.Json(new
                {
                    dryRun = true,
                    prefab = prefab.name,
                    position = new { x = position.x, y = position.y, z = position.z },
                    rotationDegrees,
                    lotDepth,
                    roadWidth,
                });
            }
            if (TransitToolsBusy(out error))
            {
                return error;
            }
            BridgeToolSystem tool = World.GetOrCreateSystemManaged<BridgeToolSystem>();
            if (!tool.TryQueuePlacement(prefabEntity, prefab, position, rotation, request))
            {
                return BridgeResponse.Error(409, "another build operation is in progress, retry shortly");
            }
            return null;
        }

        /// <summary>Buildings around a point with their footprint and what kind they are, nearest first.</summary>
        private BridgeResponse BuildingsNear(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.TryGetFloat("x", out float x) || !request.TryGetFloat("z", out float z))
            {
                return BridgeResponse.Error(400, "provide ?x=&z= (and optionally radius, limit, query)");
            }
            float radius = request.TryGetFloat("radius", out float rawRadius) ? math.clamp(rawRadius, 1f, 1000f) : 100f;
            int limit = request.TryGetInt("limit", out int rawLimit) ? math.clamp(rawLimit, 1, 300) : 40;
            request.Query.TryGetValue("query", out string query);
            float2 center = new float2(x, z);
            var found = new List<(float Distance, Entity Entity, Game.Objects.Transform Transform)>();
            using (Unity.Collections.NativeArray<Entity> buildings = PlacedBuildingQuery.ToEntityArray(Unity.Collections.Allocator.Temp))
            {
                foreach (Entity building in buildings)
                {
                    if (!EntityManager.HasComponent<Game.Objects.Transform>(building))
                    {
                        continue;
                    }
                    Game.Objects.Transform transform = EntityManager.GetComponentData<Game.Objects.Transform>(building);
                    float distance = math.distance(transform.m_Position.xz, center);
                    if (distance > radius)
                    {
                        continue;
                    }
                    if (!string.IsNullOrEmpty(query))
                    {
                        string name = PrefabNameOf(building);
                        if (name == null || name.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }
                    }
                    found.Add((distance, building, transform));
                }
            }
            found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            var items = new List<object>();
            for (int i = 0; i < found.Count && i < limit; i++)
            {
                Entity building = found[i].Entity;
                Game.Objects.Transform transform = found[i].Transform;
                Entity prefabEntity = EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
                int2 lot = EntityManager.HasComponent<BuildingData>(prefabEntity)
                    ? EntityManager.GetComponentData<BuildingData>(prefabEntity).m_LotSize
                    : default;
                float3 forward = math.mul(transform.m_Rotation, new float3(0f, 0f, 1f));
                string kind = EntityManager.HasComponent<Game.Buildings.ResidentialProperty>(building) ? "residential"
                    : EntityManager.HasComponent<Game.Buildings.CommercialProperty>(building) ? "commercial"
                    : EntityManager.HasComponent<Game.Buildings.OfficeProperty>(building) ? "office"
                    : EntityManager.HasComponent<Game.Buildings.IndustrialProperty>(building) ? "industrial"
                    : EntityManager.HasComponent<Game.Buildings.Park>(building) ? "park"
                    : "service/other";
                items.Add(new
                {
                    entity = new { index = building.Index, version = building.Version },
                    prefab = PrefabNameOf(building),
                    kind,
                    position = new { x = transform.m_Position.x, z = transform.m_Position.z },
                    distance = math.round(found[i].Distance),
                    facingDegrees = math.round(math.degrees(math.atan2(forward.x, forward.z))),
                    lotMeters = new { width = lot.x * 8, depth = lot.y * 8 },
                });
            }
            return BridgeResponse.Json(new
            {
                area = new { x, z, radius },
                matched = found.Count,
                buildings = items,
                note = "nearest first; facingDegrees = the direction the building's front (road side) points; lot = footprint.",
            });
        }

        private BridgeResponse PlaceShoreline(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("prefab", out string prefabName) || string.IsNullOrEmpty(prefabName))
            {
                return BridgeResponse.Error(400, "provide ?prefab=<building name>");
            }
            if (!request.TryGetFloat("x", out float x) || !request.TryGetFloat("z", out float z))
            {
                return BridgeResponse.Error(400, "provide ?x=&z= near the shore");
            }
            if (!TryFindPrefabByName(BuildingPrefabQuery, prefabName, out Entity prefabEntity, out PrefabBase prefab))
            {
                return BridgeResponse.Error(404, $"unknown building prefab '{prefabName}'");
            }
            if (IsLocked(prefabEntity) && !IsForced(request))
            {
                return BridgeResponse.Error(409, $"prefab '{prefab.name}' is locked (milestone not reached); pass force=true to place anyway");
            }
            bool dryRun = request.TryGetBool("dryRun", out bool rawDryRun) && rawDryRun;

            // Same radius and offset as ObjectToolSystem.SnapJob.Execute passes to SnapShoreline.
            float radius = 1f;
            if (EntityManager.HasComponent<BuildingData>(prefabEntity))
            {
                radius = math.length((float2)EntityManager.GetComponentData<BuildingData>(prefabEntity).m_LotSize) * 4f;
            }
            float3 offset = float3.zero;
            Game.Objects.PlacementFlags flags = Game.Objects.PlacementFlags.None;
            if (EntityManager.HasComponent<PlaceableObjectData>(prefabEntity))
            {
                PlaceableObjectData placeable = EntityManager.GetComponentData<PlaceableObjectData>(prefabEntity);
                offset = placeable.m_PlacementOffset;
                flags = placeable.m_Flags;
            }

            WaterSystem waterSystem = World.GetOrCreateSystemManaged<WaterSystem>();
            WaterSurfaceData<SurfaceWater> water = waterSystem.GetSurfaceData(out JobHandle deps);
            deps.Complete();
            TerrainHeightData terrain = World.GetOrCreateSystemManaged<TerrainSystem>().GetHeightData();
            float3 hit = new float3(x, TerrainUtils.SampleHeight(ref terrain, new float3(x, 0f, z)), z);

            // Port of SnapShoreline.
            int2 min = (int2)math.floor(WaterUtils.ToSurfaceSpace(ref water, hit - radius).xz);
            int2 max = (int2)math.ceil(WaterUtils.ToSurfaceSpace(ref water, hit + radius).xz);
            min = math.max(min, default(int2));
            max = math.min(max, water.resolution.xz - 1);
            float3 dry = default;
            float3 wet = default;
            float2 surface = default;
            for (int row = min.y; row <= max.y; row++)
            {
                for (int col = min.x; col <= max.x; col++)
                {
                    float3 cell = WaterUtils.GetWorldPosition(ref water, new int2(col, row));
                    float weight = math.max(0f, radius * radius - math.distancesq(cell.xz, hit.xz));
                    if (cell.y > 0.2f)
                    {
                        float height = TerrainUtils.SampleHeight(ref terrain, cell) + cell.y;
                        cell.y = (cell.y - 0.2f) * weight;
                        cell.xz *= cell.y;
                        wet += cell;
                        surface += new float2(height * weight, weight);
                    }
                    else if (cell.y < 0.2f)
                    {
                        cell.y = (0.2f - cell.y) * weight;
                        cell.xz *= cell.y;
                        dry += cell;
                    }
                }
            }
            if (dry.y == 0f || wet.y == 0f || surface.y == 0f)
            {
                return BridgeResponse.Error(409,
                    $"no shoreline within {radius:F0} m of ({x:F0}, {z:F0}): both water and land must be in range; pick a point on the water's edge");
            }
            dry /= dry.y;
            wet /= wet.y;
            float3 direction = default;
            direction.xz = dry.xz - wet.xz;
            if (!Colossal.Mathematics.MathUtils.TryNormalize(ref direction))
            {
                return BridgeResponse.Error(409, "could not work out which way the shore faces here; move the point along the coast");
            }
            float waterSurfaceHeight = surface.x / surface.y;
            float3 position = default;
            position.xz = math.lerp(wet.xz, dry.xz, 0.5f);
            position.y = waterSurfaceHeight + offset.y;
            position += direction * offset.z;
            quaternion rotation = ToolUtils.CalculateRotation(direction.xz);
            float rotationDegrees = math.degrees(math.atan2(direction.x, direction.z));

            object computed = new
            {
                position = new { x = position.x, y = position.y, z = position.z },
                rotationDegrees,
                facesLandToward = new { x = direction.x, z = direction.z },
                waterSurfaceHeight,
                snapRadius = radius,
                shorelinePlacement = (flags & Game.Objects.PlacementFlags.Shoreline) != 0,
            };
            if (dryRun)
            {
                return BridgeResponse.Json(new { dryRun = true, prefab = prefab.name, computed });
            }
            if (TransitToolsBusy(out error))
            {
                return error;
            }
            BridgeToolSystem tool = World.GetOrCreateSystemManaged<BridgeToolSystem>();
            if (!tool.TryQueuePlacement(prefabEntity, prefab, position, rotation, request))
            {
                return BridgeResponse.Error(409, "another build operation is in progress, retry shortly");
            }
            // Completed asynchronously by BridgeToolSystem, like cs2_place_building.
            return null;
        }
    }
}
