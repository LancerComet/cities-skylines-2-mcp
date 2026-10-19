using System;
using System.Collections.Generic;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// Specialized industry (agriculture, forestry, ore, oil, fish). In the
    /// vanilla game an extractor area is never drawn on its own: the player
    /// places an "... Area Placeholder" building (PlaceholderBuildingData,
    /// BuildingType.ExtractorBuilding) with the object tool; its prefab
    /// SubArea is a LotPrefab with ExtractorAreaData, created as the
    /// building's sub-area (Owner = building). ObjectToolSystem.Apply then
    /// hands that temp area to AreaToolSystem (recreate) so the player can
    /// redraw it. The extractor company that moves in reads its resources
    /// from the building's SubArea buffer, so an unowned extractor area does
    /// nothing. This file lists the prefabs/areas, places the placeholder
    /// building through the existing roadside placement and redraws an
    /// existing extractor area through BridgeAreaToolSystem.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private EntityQuery m_ExtractorPlaceholderQuery;
        private bool m_ExtractorPlaceholderQueryCreated;
        private EntityQuery m_ExtractorAreaQuery;
        private bool m_ExtractorAreaQueryCreated;

        private EntityQuery ExtractorPlaceholderQuery
        {
            get
            {
                if (!m_ExtractorPlaceholderQueryCreated)
                {
                    m_ExtractorPlaceholderQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<PlaceholderBuildingData>());
                    m_ExtractorPlaceholderQueryCreated = true;
                }
                return m_ExtractorPlaceholderQuery;
            }
        }

        private EntityQuery ExtractorAreaQuery
        {
            get
            {
                if (!m_ExtractorAreaQueryCreated)
                {
                    m_ExtractorAreaQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Extractor>(),
                            ComponentType.ReadOnly<Area>(),
                            ComponentType.ReadOnly<Geometry>(),
                            ComponentType.ReadOnly<PrefabRef>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Game.Tools.Temp>(),
                            ComponentType.ReadOnly<Deleted>(),
                        },
                    });
                    m_ExtractorAreaQueryCreated = true;
                }
                return m_ExtractorAreaQuery;
            }
        }

        private struct ExtractorPlaceholderInfo
        {
            public Entity Prefab;
            public string Name;
            public string Type;
            public List<string> AreaPrefabs;
            public List<string> MapFeatures;
            public bool Locked;
        }

        private static string SpecializedTypeFromFeature(MapFeature feature)
        {
            switch (feature)
            {
                case MapFeature.FertileLand:
                    return "agriculture";
                case MapFeature.Forest:
                    return "forestry";
                case MapFeature.Ore:
                    return "ore";
                case MapFeature.Oil:
                    return "oil";
                case MapFeature.Fish:
                    return "fish";
                default:
                    return null;
            }
        }

        private static string SpecializedTypeFromName(string name)
        {
            if (name.IndexOf("Agricultur", StringComparison.OrdinalIgnoreCase) >= 0) return "agriculture";
            if (name.IndexOf("Forest", StringComparison.OrdinalIgnoreCase) >= 0) return "forestry";
            if (name.IndexOf("Ore", StringComparison.Ordinal) >= 0) return "ore";
            if (name.IndexOf("Oil", StringComparison.OrdinalIgnoreCase) >= 0) return "oil";
            if (name.IndexOf("Aquacultur", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Fish", StringComparison.OrdinalIgnoreCase) >= 0) return "fish";
            return null;
        }

        private List<ExtractorPlaceholderInfo> CollectExtractorPlaceholders()
        {
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var result = new List<ExtractorPlaceholderInfo>();
            using (NativeArray<Entity> prefabs = ExtractorPlaceholderQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in prefabs)
                {
                    PlaceholderBuildingData data = EntityManager.GetComponentData<PlaceholderBuildingData>(entity);
                    if (data.m_Type != BuildingType.ExtractorBuilding)
                    {
                        continue;
                    }
                    PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(entity);
                    if (prefab == null)
                    {
                        continue;
                    }
                    var info = new ExtractorPlaceholderInfo
                    {
                        Prefab = entity,
                        Name = prefab.name,
                        AreaPrefabs = new List<string>(),
                        MapFeatures = new List<string>(),
                        Locked = IsLocked(entity),
                    };
                    if (EntityManager.HasBuffer<Game.Prefabs.SubArea>(entity))
                    {
                        DynamicBuffer<Game.Prefabs.SubArea> subAreas = EntityManager.GetBuffer<Game.Prefabs.SubArea>(entity, isReadOnly: true);
                        for (int i = 0; i < subAreas.Length; i++)
                        {
                            Entity areaPrefab = subAreas[i].m_Prefab;
                            PrefabBase areaPrefabBase = prefabSystem.GetPrefab<PrefabBase>(areaPrefab);
                            if (areaPrefabBase != null)
                            {
                                info.AreaPrefabs.Add(areaPrefabBase.name);
                            }
                            if (EntityManager.HasComponent<ExtractorAreaData>(areaPrefab))
                            {
                                MapFeature feature = EntityManager.GetComponentData<ExtractorAreaData>(areaPrefab).m_MapFeature;
                                info.MapFeatures.Add(feature.ToString());
                                if (info.Type == null)
                                {
                                    info.Type = SpecializedTypeFromFeature(feature);
                                }
                            }
                        }
                    }
                    if (info.Type == null)
                    {
                        info.Type = SpecializedTypeFromName(info.Name);
                    }
                    result.Add(info);
                }
            }
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            return result;
        }

        private BridgeResponse ListSpecializedAreas(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            var placeholders = new List<object>();
            foreach (ExtractorPlaceholderInfo info in CollectExtractorPlaceholders())
            {
                placeholders.Add(new
                {
                    name = info.Name,
                    type = info.Type,
                    areaPrefabs = info.AreaPrefabs,
                    mapFeatures = info.MapFeatures,
                    locked = info.Locked,
                });
            }

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var areas = new List<object>();
            var attachedByParent = new Dictionary<Entity, Entity>();
            EntityQuery attachedQuery = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<Game.Objects.Attached>(),
                ComponentType.ReadOnly<Game.Buildings.Building>(),
                ComponentType.Exclude<Game.Tools.Temp>(),
                ComponentType.Exclude<Deleted>());
            using (NativeArray<Entity> attachedEntities = attachedQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Game.Objects.Attached> attachedData = attachedQuery.ToComponentDataArray<Game.Objects.Attached>(Allocator.Temp))
            {
                for (int i = 0; i < attachedEntities.Length; i++)
                {
                    attachedByParent[attachedData[i].m_Parent] = attachedEntities[i];
                }
            }
            attachedQuery.Dispose();
            using (NativeArray<Entity> entities = ExtractorAreaQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    Entity areaPrefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(areaPrefab);
                    Geometry geometry = EntityManager.GetComponentData<Geometry>(entity);
                    Extractor extractor = EntityManager.GetComponentData<Extractor>(entity);
                    string feature = EntityManager.HasComponent<ExtractorAreaData>(areaPrefab)
                        ? EntityManager.GetComponentData<ExtractorAreaData>(areaPrefab).m_MapFeature.ToString()
                        : null;
                    object owner = null;
                    if (EntityManager.HasComponent<Owner>(entity))
                    {
                        Entity ownerEntity = EntityManager.GetComponentData<Owner>(entity).m_Owner;
                        string ownerPrefab = null;
                        if (EntityManager.HasComponent<PrefabRef>(ownerEntity))
                        {
                            PrefabBase op = prefabSystem.GetPrefab<PrefabBase>(EntityManager.GetComponentData<PrefabRef>(ownerEntity).m_Prefab);
                            ownerPrefab = op != null ? op.name : null;
                        }
                        object ownerPosition = null;
                        if (EntityManager.HasComponent<Game.Objects.Transform>(ownerEntity))
                        {
                            float3 p = EntityManager.GetComponentData<Game.Objects.Transform>(ownerEntity).m_Position;
                            ownerPosition = new { x = Math.Round(p.x, 1), z = Math.Round(p.z, 1) };
                        }
                        object extractorBuilding = null;
                        if (attachedByParent.TryGetValue(ownerEntity, out Entity attachedBuilding))
                        {
                            PrefabBase ab = prefabSystem.GetPrefab<PrefabBase>(EntityManager.GetComponentData<PrefabRef>(attachedBuilding).m_Prefab);
                            extractorBuilding = new
                            {
                                index = attachedBuilding.Index,
                                version = attachedBuilding.Version,
                                prefab = ab != null ? ab.name : null,
                                renters = EntityManager.HasBuffer<Game.Buildings.Renter>(attachedBuilding)
                                    ? EntityManager.GetBuffer<Game.Buildings.Renter>(attachedBuilding, isReadOnly: true).Length
                                    : 0,
                            };
                        }
                        owner = new
                        {
                            index = ownerEntity.Index,
                            version = ownerEntity.Version,
                            prefab = ownerPrefab,
                            position = ownerPosition,
                            extractorBuilding,
                            broken = extractorBuilding == null && EntityManager.HasComponent<Game.Buildings.Building>(ownerEntity),
                        };
                    }
                    areas.Add(new
                    {
                        entity = new { index = entity.Index, version = entity.Version },
                        prefab = prefab != null ? prefab.name : null,
                        mapFeature = feature,
                        center = new { x = Math.Round(geometry.m_CenterPosition.x, 1), z = Math.Round(geometry.m_CenterPosition.z, 1) },
                        surfaceArea = Math.Round(geometry.m_SurfaceArea),
                        resourceAmount = Math.Round(extractor.m_ResourceAmount),
                        maxConcentration = Math.Round(extractor.m_MaxConcentration, 3),
                        totalExtracted = Math.Round(extractor.m_TotalExtracted),
                        owner,
                    });
                }
            }

            return BridgeResponse.Json(new
            {
                note = "Specialized industry = an extractor placeholder building whose prefab carries an extractor area " +
                       "(its sub-area). Place one with /build/specialized-area?type=&variant=&road=&side=; the game creates " +
                       "the area from the prefab and an extractor company moves in. existingAreas lists the city's extractor " +
                       "areas with their owner building; owner.broken = true marks a placeholder without its attached extractor " +
                       "building (placed by the pre-fix tool): no company can move in, demolish it with cs2_demolish and place again.",
                placeholders,
                existingAreas = areas,
            });
        }

        private BridgeResponse PlaceSpecializedArea(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (request.Query.ContainsKey("points"))
            {
                return ReshapeSpecializedArea(request);
            }
            if (request.Query.ContainsKey("area") || request.Query.ContainsKey("building"))
            {
                return BridgeResponse.Error(400, "area=/building= need points=x,z;x,z;... (the new polygon)");
            }
            if (!request.Query.TryGetValue("type", out string type) || string.IsNullOrEmpty(type))
            {
                return BridgeResponse.Error(400, "provide ?type=agriculture|forestry|ore|oil|fish (see /build/specialized-area/list)");
            }
            type = type.ToLowerInvariant();
            request.Query.TryGetValue("variant", out string variant);

            var matches = new List<ExtractorPlaceholderInfo>();
            foreach (ExtractorPlaceholderInfo info in CollectExtractorPlaceholders())
            {
                if (info.Type != type)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(variant) && info.Name.IndexOf(variant, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                matches.Add(info);
            }
            if (matches.Count == 0)
            {
                return BridgeResponse.Error(404,
                    $"no extractor placeholder for type '{type}'" + (string.IsNullOrEmpty(variant) ? "" : $" and variant '{variant}'") +
                    "; see /build/specialized-area/list");
            }
            if (matches.Count > 1)
            {
                var names = new List<string>();
                foreach (ExtractorPlaceholderInfo info in matches)
                {
                    names.Add(info.Name);
                }
                return BridgeResponse.Error(400,
                    $"type '{type}' has several placeholders; pick one with ?variant=<part of the name>: {string.Join(", ", names)}");
            }

            // Same as the vanilla first step: place the placeholder building with
            // the object pipeline (the existing roadside placement); the game builds
            // the prefab's extractor sub-area together with it.
            return PlaceExtractorPlaceholder(request, matches[0]);
        }

        /// <summary>
        /// Places the placeholder flush against the road (same geometry as
        /// /build/place/roadside) through BridgeAreaToolSystem, with the attached
        /// extractor building chosen exactly like ObjectToolSystem's
        /// FindAttachmentBuildingJob.
        /// </summary>
        private BridgeResponse PlaceExtractorPlaceholder(BridgeRequest request, ExtractorPlaceholderInfo placeholder)
        {
            if (!request.Query.TryGetValue("road", out string rawRoad) || !TryParseEntityRef(rawRoad, out Entity road)
                || !EntityManager.Exists(road) || !EntityManager.HasComponent<Game.Net.Curve>(road) || !EntityManager.HasComponent<Game.Net.Edge>(road))
            {
                return BridgeResponse.Error(400, "provide ?road=index:version of a road segment (cs2_road_graph)");
            }
            request.Query.TryGetValue("side", out string side);
            bool left = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase);
            if (!left && !string.Equals(side, "right", StringComparison.OrdinalIgnoreCase))
            {
                return BridgeResponse.Error(400, "provide ?side=left|right (as seen driving from the segment's start to its end)");
            }
            if (placeholder.Locked && !IsForced(request))
            {
                return BridgeResponse.Error(409, $"prefab '{placeholder.Name}' is locked (milestone not reached); pass force=true to place anyway");
            }
            float t = request.TryGetFloat("t", out float rawT) ? math.clamp(rawT, 0f, 1f) : 0.5f;
            request.TryGetFloat("gap", out float gap);

            Entity prefabEntity = placeholder.Prefab;
            BuildingData buildingData = EntityManager.GetComponentData<BuildingData>(prefabEntity);
            float lotDepth = buildingData.m_LotSize.y * 8f;
            Entity roadPrefab = EntityManager.GetComponentData<PrefabRef>(road).m_Prefab;
            float roadWidth = EntityManager.HasComponent<NetGeometryData>(roadPrefab)
                ? EntityManager.GetComponentData<NetGeometryData>(roadPrefab).m_DefaultWidth
                : 16f;
            Colossal.Mathematics.Bezier4x3 curve = EntityManager.GetComponentData<Game.Net.Curve>(road).m_Bezier;
            float3 point = Colossal.Mathematics.MathUtils.Position(curve, t);
            float2 tangent = math.normalizesafe(Colossal.Mathematics.MathUtils.Tangent(curve, t).xz);
            float2 normal = left ? new float2(-tangent.y, tangent.x) : new float2(tangent.y, -tangent.x);
            float3 position = point;
            position.xz += normal * (roadWidth * 0.5f + lotDepth * 0.5f + math.max(0f, gap));
            Game.Simulation.TerrainHeightData terrain = World.GetOrCreateSystemManaged<Game.Simulation.TerrainSystem>().GetHeightData();
            position.y = Game.Simulation.TerrainUtils.SampleHeight(ref terrain, position);
            float2 facing = -normal;
            quaternion rotation = Game.Tools.ToolUtils.CalculateRotation(facing);

            FindAttachmentBuilding(prefabEntity, buildingData, out Entity attachment, out float3 attachmentOffset);
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            string attachmentName = attachment != Entity.Null ? prefabSystem.GetPrefab<PrefabBase>(attachment)?.name : null;
            if (attachment == Entity.Null)
            {
                return BridgeResponse.Error(409, $"no level-1 extractor building of {placeholder.Name}'s zone fits its lot; nothing placed");
            }

            if (request.TryGetBool("dryRun", out bool dryRun) && dryRun)
            {
                return BridgeResponse.Json(new
                {
                    dryRun = true,
                    prefab = placeholder.Name,
                    attachedBuilding = attachmentName,
                    position = new { x = position.x, y = position.y, z = position.z },
                    rotationDegrees = math.degrees(math.atan2(facing.x, facing.y)),
                    lotDepth,
                    roadWidth,
                });
            }
            if (TransitToolsBusy(out BridgeResponse busy))
            {
                return busy;
            }
            BridgeAreaToolSystem tool = World.GetOrCreateSystemManaged<BridgeAreaToolSystem>();
            if (!tool.TryQueuePlacement(prefabEntity, prefabSystem.GetPrefab<PrefabBase>(prefabEntity), position, rotation,
                attachment, attachmentOffset, request))
            {
                return BridgeResponse.Error(409, "another area operation is in progress, retry shortly");
            }
            return null;
        }

        /// <summary>Port of ObjectToolSystem.FindAttachmentBuildingJob (game mode, placeholder buildings).</summary>
        private void FindAttachmentBuilding(Entity placeholderPrefab, BuildingData placeholderData, out Entity attachment, out float3 offset)
        {
            attachment = Entity.Null;
            offset = default;
            PlaceholderBuildingData placeholder = EntityManager.GetComponentData<PlaceholderBuildingData>(placeholderPrefab);
            if (placeholder.m_ZonePrefab == Entity.Null || !EntityManager.HasComponent<ZoneData>(placeholder.m_ZonePrefab))
            {
                return;
            }
            Game.Zones.ZoneType zoneType = EntityManager.GetComponentData<ZoneData>(placeholder.m_ZonePrefab).m_ZoneType;

            Unity.Mathematics.Random random = Game.Common.RandomSeed.Next().GetRandom(2000000);
            int2 lotSize = placeholderData.m_LotSize;
            bool2 flags = new bool2((placeholderData.m_Flags & Game.Prefabs.BuildingFlags.LeftAccess) != 0,
                (placeholderData.m_Flags & Game.Prefabs.BuildingFlags.RightAccess) != 0);
            BuildingData best = default;
            float bestScore = 0f;

            EntityQuery query = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<BuildingData>(),
                ComponentType.ReadOnly<SpawnableBuildingData>(),
                ComponentType.ReadOnly<BuildingSpawnGroupData>(),
                ComponentType.ReadOnly<PrefabData>());
            try
            {
                query.SetSharedComponentFilter(new BuildingSpawnGroupData(zoneType));
                using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
                using (NativeArray<BuildingData> datas = query.ToComponentDataArray<BuildingData>(Allocator.Temp))
                using (NativeArray<SpawnableBuildingData> spawnables = query.ToComponentDataArray<SpawnableBuildingData>(Allocator.Temp))
                {
                    for (int i = 0; i < entities.Length; i++)
                    {
                        if (spawnables[i].m_Level != 1)
                        {
                            continue;
                        }
                        BuildingData data = datas[i];
                        int2 size = data.m_LotSize;
                        bool2 candidateFlags = new bool2((data.m_Flags & Game.Prefabs.BuildingFlags.LeftAccess) != 0,
                            (data.m_Flags & Game.Prefabs.BuildingFlags.RightAccess) != 0);
                        if (!math.all(size <= lotSize))
                        {
                            continue;
                        }
                        int2 spare = math.select(lotSize - size, 0, size == lotSize - 1);
                        float score = size.x * size.y * random.NextFloat(1f, 1.05f);
                        score += spare.x * size.y * random.NextFloat(0.95f, 1f);
                        score += lotSize.x * spare.y * random.NextFloat(0.55f, 0.6f);
                        score /= lotSize.x * lotSize.y;
                        score *= math.csum(math.select(0.01f, 0.5f, flags == candidateFlags));
                        if (score > bestScore)
                        {
                            attachment = entities[i];
                            best = data;
                            bestScore = score;
                        }
                    }
                }
            }
            finally
            {
                query.Dispose();
            }
            if (attachment != Entity.Null)
            {
                offset = new float3(0f, 0f, (lotSize.y - best.m_LotSize.y) * 4f);
            }
        }

        /// <summary>
        /// Redraws an extractor area like AreaToolSystem does in game mode (see
        /// BridgeAreaToolSystem). Pre-checks the constraints ValidationHelpers
        /// .ValidateArea applies, so the caller gets a specific error: at least
        /// 3 corners, no self-intersection, and every corner within the area
        /// prefab's LotData.m_MaxRadius of the owner building (LongDistance).
        /// The game's own validation then runs before anything is applied.
        /// </summary>
        private BridgeResponse ReshapeSpecializedArea(BridgeRequest request)
        {
            if (!TryResolveExtractorArea(request, out Entity area, out Entity owner, out BridgeResponse resolveError))
            {
                return resolveError;
            }

            string[] pairs = request.Query["points"].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (pairs.Length < 3)
            {
                return BridgeResponse.Error(400, "InvalidShape: the polygon needs at least 3 corners (points=x,z;x,z;x,z)");
            }
            if (pairs.Length > 64)
            {
                return BridgeResponse.Error(400, "polygon too complex (max 64 corners)");
            }
            var corners = new float2[pairs.Length];
            for (int i = 0; i < pairs.Length; i++)
            {
                string[] parts = pairs[i].Split(',');
                if (parts.Length != 2
                    || !float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                    || !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                {
                    return BridgeResponse.Error(400, $"cannot parse corner '{pairs[i]}'; expected x,z");
                }
                corners[i] = new float2(x, z);
            }

            // Duplicate / too-close corners and self-intersection (InvalidShape in the game).
            for (int i = 0; i < corners.Length; i++)
            {
                if (math.distance(corners[i], corners[(i + 1) % corners.Length]) < 1f)
                {
                    return BridgeResponse.Error(400, $"InvalidShape: corners {i} and {(i + 1) % corners.Length} are less than 1 m apart");
                }
            }
            for (int i = 0; i < corners.Length; i++)
            {
                float2 a1 = corners[i];
                float2 a2 = corners[(i + 1) % corners.Length];
                for (int j = i + 1; j < corners.Length; j++)
                {
                    // Skip edges sharing a corner with edge i.
                    if (j == i + 1 || (i == 0 && j == corners.Length - 1))
                    {
                        continue;
                    }
                    float2 b1 = corners[j];
                    float2 b2 = corners[(j + 1) % corners.Length];
                    if (SegmentsIntersect(a1, a2, b1, b2))
                    {
                        return BridgeResponse.Error(400,
                            $"InvalidShape: the polygon intersects itself (edge {i}-{(i + 1) % corners.Length} crosses edge {j}-{(j + 1) % corners.Length})");
                    }
                }
            }
            double signedArea = 0;
            for (int i = 0; i < corners.Length; i++)
            {
                float2 p = corners[i];
                float2 q = corners[(i + 1) % corners.Length];
                signedArea += (double)p.x * q.y - (double)q.x * p.y;
            }
            if (Math.Abs(signedArea) * 0.5 < 1.0)
            {
                return BridgeResponse.Error(400, "SmallArea: the polygon has (almost) no area");
            }

            Entity areaPrefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
            if (EntityManager.HasComponent<LotData>(areaPrefab) && EntityManager.HasComponent<Game.Objects.Transform>(owner))
            {
                float maxRadius = EntityManager.GetComponentData<LotData>(areaPrefab).m_MaxRadius;
                float2 ownerPosition = EntityManager.GetComponentData<Game.Objects.Transform>(owner).m_Position.xz;
                if (maxRadius > 0f)
                {
                    for (int i = 0; i < corners.Length; i++)
                    {
                        float distance = math.distance(corners[i], ownerPosition);
                        if (distance > maxRadius)
                        {
                            return BridgeResponse.Error(400,
                                $"LongDistance: corner {i} ({corners[i].x:0.#}, {corners[i].y:0.#}) is {distance:0} m from the owner building " +
                                $"at ({ownerPosition.x:0.#}, {ownerPosition.y:0.#}); this area allows at most {maxRadius:0} m");
                        }
                    }
                }
            }

            Game.Simulation.TerrainSystem terrain = World.GetOrCreateSystemManaged<Game.Simulation.TerrainSystem>();
            Game.Simulation.TerrainHeightData heightData = terrain.GetHeightData();
            var nodes = new float3[corners.Length];
            for (int i = 0; i < corners.Length; i++)
            {
                var position = new float3(corners[i].x, 0f, corners[i].y);
                position.y = Game.Simulation.TerrainUtils.SampleHeight(ref heightData, position);
                nodes[i] = position;
            }

            PrefabBase prefab = World.GetOrCreateSystemManaged<PrefabSystem>().GetPrefab<PrefabBase>(areaPrefab);
            BridgeAreaToolSystem tool = World.GetOrCreateSystemManaged<BridgeAreaToolSystem>();
            if (!tool.TryQueueReshape(area, owner, areaPrefab, prefab, nodes, request))
            {
                return BridgeResponse.Error(409, "another area operation is in progress, retry shortly");
            }
            return null;
        }

        private bool TryResolveExtractorArea(BridgeRequest request, out Entity area, out Entity owner, out BridgeResponse error)
        {
            area = Entity.Null;
            owner = Entity.Null;
            error = null;
            if (request.Query.TryGetValue("area", out string rawArea))
            {
                if (!TryParseEntityRef(rawArea, out area) || !EntityManager.Exists(area)
                    || !EntityManager.HasComponent<Extractor>(area) || !EntityManager.HasComponent<Owner>(area))
                {
                    error = BridgeResponse.Error(404, "area must be index:version of an extractor area with an owner building (/build/specialized-area/list)");
                    return false;
                }
                owner = EntityManager.GetComponentData<Owner>(area).m_Owner;
                return true;
            }
            if (request.Query.TryGetValue("building", out string rawBuilding))
            {
                if (!TryParseEntityRef(rawBuilding, out Entity building) || !EntityManager.Exists(building)
                    || !EntityManager.HasBuffer<Game.Areas.SubArea>(building))
                {
                    error = BridgeResponse.Error(404, "building must be index:version of a specialized-industry building with an extractor area");
                    return false;
                }
                DynamicBuffer<Game.Areas.SubArea> subAreas = EntityManager.GetBuffer<Game.Areas.SubArea>(building, isReadOnly: true);
                for (int i = 0; i < subAreas.Length; i++)
                {
                    Entity candidate = subAreas[i].m_Area;
                    if (EntityManager.HasComponent<Extractor>(candidate) && !EntityManager.HasComponent<Deleted>(candidate))
                    {
                        area = candidate;
                        owner = building;
                        return true;
                    }
                }
                error = BridgeResponse.Error(404, $"building {building.Index}:{building.Version} has no extractor area");
                return false;
            }
            error = BridgeResponse.Error(400,
                "to redraw an area pass area=index:version (or building=index:version) with points; to place a new " +
                "specialized industry and redraw it, place it first (type/variant/road/side), then call again with " +
                "building= or area= from /build/specialized-area/list (cs2_specialized_area does both steps)");
            return false;
        }

        private static bool SegmentsIntersect(float2 p1, float2 p2, float2 q1, float2 q2)
        {
            float d1 = Cross(q2 - q1, p1 - q1);
            float d2 = Cross(q2 - q1, p2 - q1);
            float d3 = Cross(p2 - p1, q1 - p1);
            float d4 = Cross(p2 - p1, q2 - p1);
            if (((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) && ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f)))
            {
                return true;
            }
            return (d1 == 0f && OnSegment(q1, q2, p1)) || (d2 == 0f && OnSegment(q1, q2, p2))
                || (d3 == 0f && OnSegment(p1, p2, q1)) || (d4 == 0f && OnSegment(p1, p2, q2));
        }

        private static float Cross(float2 a, float2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        private static bool OnSegment(float2 a, float2 b, float2 p)
        {
            return p.x >= math.min(a.x, b.x) && p.x <= math.max(a.x, b.x)
                && p.y >= math.min(a.y, b.y) && p.y <= math.max(a.y, b.y);
        }
    }
}
