using System;
using System.Collections.Generic;
using System.Globalization;
using Colossal.Mathematics;
using Game.Prefabs;
using Game.Tools;
using Game.UI;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    /// <summary>
    /// Public transport endpoints: list/place stops and list/create/delete
    /// transit lines. Placement and creation run through
    /// BridgeTransitToolSystem (the game's own tool pipelines); deletion does
    /// exactly what the game's "delete line" button does.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private static readonly Dictionary<string, TransportType> kTransitTypes =
            new Dictionary<string, TransportType>(StringComparer.OrdinalIgnoreCase)
            {
                ["bus"] = TransportType.Bus,
                ["tram"] = TransportType.Tram,
                ["metro"] = TransportType.Subway,
                ["subway"] = TransportType.Subway,
                ["train"] = TransportType.Train,
            };

        private EntityQuery m_TransitStopQuery;
        private bool m_TransitStopQueryCreated;
        private EntityQuery m_TransitLineQuery;
        private bool m_TransitLineQueryCreated;
        private EntityQuery m_TransitStopPrefabQuery;
        private bool m_TransitStopPrefabQueryCreated;
        private EntityQuery m_TransitLinePrefabQuery;
        private bool m_TransitLinePrefabQueryCreated;
        private EntityQuery m_TransitRoadQuery;
        private bool m_TransitRoadQueryCreated;
        private EntityQuery m_TransitDepotQuery;
        private bool m_TransitDepotQueryCreated;

        private EntityQuery TransitStopQuery
        {
            get
            {
                if (!m_TransitStopQueryCreated)
                {
                    m_TransitStopQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Game.Routes.TransportStop>(),
                            ComponentType.ReadOnly<Game.Routes.ConnectedRoute>(),
                            ComponentType.ReadOnly<PrefabRef>(),
                            ComponentType.ReadOnly<Transform>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Temp>(),
                            ComponentType.ReadOnly<Game.Common.Deleted>(),
                        },
                    });
                    m_TransitStopQueryCreated = true;
                }
                return m_TransitStopQuery;
            }
        }

        /// <summary>Same query the game's transportation overview panel uses.</summary>
        private EntityQuery TransitLineQuery
        {
            get
            {
                if (!m_TransitLineQueryCreated)
                {
                    m_TransitLineQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Game.Routes.Route>(),
                            ComponentType.ReadOnly<Game.Routes.TransportLine>(),
                            ComponentType.ReadOnly<Game.Routes.RouteWaypoint>(),
                            ComponentType.ReadOnly<PrefabRef>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Game.Common.Deleted>(),
                            ComponentType.ReadOnly<Temp>(),
                        },
                    });
                    m_TransitLineQueryCreated = true;
                }
                return m_TransitLineQuery;
            }
        }

        /// <summary>Roadside stops (bus/tram poles) are net objects; stations are buildings.</summary>
        private EntityQuery TransitStopPrefabQuery
        {
            get
            {
                if (!m_TransitStopPrefabQueryCreated)
                {
                    m_TransitStopPrefabQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<PrefabData>(),
                            ComponentType.ReadOnly<TransportStopData>(),
                            ComponentType.ReadOnly<NetObjectData>(),
                        },
                        None = new[] { ComponentType.ReadOnly<BuildingData>() },
                    });
                    m_TransitStopPrefabQueryCreated = true;
                }
                return m_TransitStopPrefabQuery;
            }
        }

        private EntityQuery TransitLinePrefabQuery
        {
            get
            {
                if (!m_TransitLinePrefabQueryCreated)
                {
                    m_TransitLinePrefabQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<TransportLineData>(),
                        ComponentType.ReadOnly<RouteData>());
                    m_TransitLinePrefabQueryCreated = true;
                }
                return m_TransitLinePrefabQuery;
            }
        }

        private EntityQuery TransitRoadQuery
        {
            get
            {
                if (!m_TransitRoadQueryCreated)
                {
                    m_TransitRoadQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Game.Net.Edge>(),
                            ComponentType.ReadOnly<Game.Net.Curve>(),
                            ComponentType.ReadOnly<Game.Net.Composition>(),
                            ComponentType.ReadOnly<Game.Net.EdgeGeometry>(),
                            ComponentType.ReadOnly<PrefabRef>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Temp>(),
                            ComponentType.ReadOnly<Game.Common.Deleted>(),
                            ComponentType.ReadOnly<Game.Common.Owner>(),
                        },
                    });
                    m_TransitRoadQueryCreated = true;
                }
                return m_TransitRoadQuery;
            }
        }

        private EntityQuery TransitDepotQuery
        {
            get
            {
                if (!m_TransitDepotQueryCreated)
                {
                    m_TransitDepotQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Game.Buildings.TransportDepot>(),
                            ComponentType.ReadOnly<PrefabRef>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Temp>(),
                            ComponentType.ReadOnly<Game.Common.Deleted>(),
                        },
                    });
                    m_TransitDepotQueryCreated = true;
                }
                return m_TransitDepotQuery;
            }
        }

        private BridgeResponse ListTransitStops(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            TransportType? typeFilter = null;
            if (request.Query.TryGetValue("type", out string rawType) && !string.IsNullOrEmpty(rawType))
            {
                if (!TryParseTransitType(rawType, out TransportType parsed, out _, out error))
                {
                    return error;
                }
                typeFilter = parsed;
            }
            int limit = request.TryGetInt("limit", out int rawLimit) ? math.clamp(rawLimit, 1, 500) : 100;
            bool hasCenter = request.TryGetFloat("x", out float x) & request.TryGetFloat("z", out float z);
            float radius = request.TryGetFloat("radius", out float rawRadius) ? math.max(rawRadius, 1f) : 500f;
            float2 center = new float2(x, z);

            NameSystem names = World.GetOrCreateSystemManaged<NameSystem>();
            var matches = new List<KeyValuePair<float, Entity>>();
            using (NativeArray<Entity> stops = TransitStopQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity stop in stops)
                {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(stop).m_Prefab;
                    if (!EntityManager.HasComponent<TransportStopData>(prefab))
                    {
                        continue;
                    }
                    if (typeFilter.HasValue && EntityManager.GetComponentData<TransportStopData>(prefab).m_TransportType != typeFilter.Value)
                    {
                        continue;
                    }
                    float distance = math.distance(EntityManager.GetComponentData<Transform>(stop).m_Position.xz, center);
                    if (hasCenter && distance > radius)
                    {
                        continue;
                    }
                    matches.Add(new KeyValuePair<float, Entity>(hasCenter ? distance : 0f, stop));
                }
            }
            if (hasCenter)
            {
                matches.Sort((a, b) => a.Key.CompareTo(b.Key));
            }

            var results = new List<object>();
            for (int i = 0; i < matches.Count && results.Count < limit; i++)
            {
                results.Add(DescribeTransitStop(matches[i].Value, names));
            }

            object prefabs = null;
            if (request.TryGetBool("prefabs", out bool wantPrefabs) && wantPrefabs)
            {
                prefabs = ListRoadsideStopPrefabs(typeFilter);
            }

            return BridgeResponse.Json(new
            {
                totalMatches = matches.Count,
                returned = results.Count,
                note = "use stop entity ids with cs2_create_transit_line; place new roadside bus/tram stops with " +
                       "cs2_place_transit_stop; train/metro platforms belong to station buildings (station field)",
                stops = results,
                placeablePrefabs = prefabs,
            });
        }

        private object DescribeTransitStop(Entity stop, NameSystem names)
        {
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(stop).m_Prefab;
            TransportStopData stopData = EntityManager.GetComponentData<TransportStopData>(prefab);
            Transform transform = EntityManager.GetComponentData<Transform>(stop);

            object station = null;
            if (EntityManager.HasComponent<Game.Common.Owner>(stop))
            {
                Entity owner = EntityManager.GetComponentData<Game.Common.Owner>(stop).m_Owner;
                station = new { index = owner.Index, version = owner.Version, prefab = PrefabNameOf(owner) };
            }
            object road = null;
            if (EntityManager.HasComponent<Game.Objects.Attached>(stop))
            {
                Entity parent = EntityManager.GetComponentData<Game.Objects.Attached>(stop).m_Parent;
                if (parent != Entity.Null && EntityManager.HasComponent<Game.Net.Edge>(parent))
                {
                    road = new { index = parent.Index, version = parent.Version };
                }
            }

            var lines = new List<object>();
            var seen = new HashSet<Entity>();
            DynamicBuffer<Game.Routes.ConnectedRoute> connected = EntityManager.GetBuffer<Game.Routes.ConnectedRoute>(stop, isReadOnly: true);
            for (int i = 0; i < connected.Length; i++)
            {
                Entity waypoint = connected[i].m_Waypoint;
                if (!EntityManager.Exists(waypoint) || !EntityManager.HasComponent<Game.Common.Owner>(waypoint))
                {
                    continue;
                }
                Entity route = EntityManager.GetComponentData<Game.Common.Owner>(waypoint).m_Owner;
                if (!seen.Add(route) || !EntityManager.Exists(route) || EntityManager.HasComponent<Temp>(route))
                {
                    continue;
                }
                lines.Add(new
                {
                    index = route.Index,
                    version = route.Version,
                    name = LabelOf(names, route),
                    number = EntityManager.HasComponent<Game.Routes.RouteNumber>(route)
                        ? EntityManager.GetComponentData<Game.Routes.RouteNumber>(route).m_Number
                        : 0,
                });
            }

            bool active = (EntityManager.GetComponentData<Game.Routes.TransportStop>(stop).m_Flags & Game.Routes.StopFlags.Active) != 0;
            return new
            {
                entity = new { index = stop.Index, version = stop.Version },
                type = TransitTypeName(stopData.m_TransportType),
                passenger = stopData.m_PassengerTransport,
                cargo = stopData.m_CargoTransport,
                name = LabelOf(names, stop),
                prefab = PrefabNameOf(stop),
                position = new { x = transform.m_Position.x, y = transform.m_Position.y, z = transform.m_Position.z },
                station,
                road,
                active,
                lines,
            };
        }

        private List<object> ListRoadsideStopPrefabs(TransportType? typeFilter)
        {
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var result = new List<object>();
            using (NativeArray<Entity> entities = TransitStopPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    TransportStopData data = EntityManager.GetComponentData<TransportStopData>(entity);
                    if (typeFilter.HasValue && data.m_TransportType != typeFilter.Value)
                    {
                        continue;
                    }
                    PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(entity);
                    if (prefab == null)
                    {
                        continue;
                    }
                    result.Add(new
                    {
                        name = prefab.name,
                        type = TransitTypeName(data.m_TransportType),
                        passenger = data.m_PassengerTransport,
                        cargo = data.m_CargoTransport,
                        locked = IsLocked(entity),
                    });
                }
            }
            return result;
        }

        private BridgeResponse PlaceTransitStop(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("type", out string rawType) || string.IsNullOrEmpty(rawType))
            {
                return BridgeResponse.Error(400, "provide ?type=bus|tram (roadside stops)");
            }
            if (!TryParseTransitType(rawType, out TransportType type, out string typeName, out error))
            {
                return error;
            }
            if (!request.TryGetFloat("x", out float x) || !request.TryGetFloat("z", out float z))
            {
                return BridgeResponse.Error(400, "provide ?x=<float>&z=<float>: a point beside the road, on the side where the stop should go");
            }
            float searchRadius = request.TryGetFloat("radius", out float rawRadius) ? math.clamp(rawRadius, 4f, 200f) : 40f;
            request.Query.TryGetValue("prefab", out string wantedPrefab);

            if (!TryResolveStopPrefab(type, typeName, wantedPrefab, out Entity prefabEntity, out PrefabBase prefab, out error))
            {
                return error;
            }
            if (IsLocked(prefabEntity) && !IsForced(request))
            {
                return BridgeResponse.Error(409, $"stop prefab '{prefab.name}' is locked (milestone not reached); pass force=true to place anyway");
            }

            Entity forcedRoad = Entity.Null;
            if (request.Query.TryGetValue("road", out string rawRoad) && !string.IsNullOrEmpty(rawRoad))
            {
                if (!TryParseEntityRef(rawRoad, out forcedRoad) || !EntityManager.Exists(forcedRoad)
                    || !EntityManager.HasComponent<Game.Net.Edge>(forcedRoad))
                {
                    return BridgeResponse.Error(404, $"road '{rawRoad}' is not an existing road segment (use index:version from cs2_list_roads)");
                }
            }
            if (TransitToolsBusy(out error))
            {
                return error;
            }

            var point = new float3(x, 0f, z);
            if (!TryComputeStopPlacement(prefabEntity, point, forcedRoad, searchRadius, out ControlPoint controlPoint, out string reason))
            {
                return BridgeResponse.Error(409, reason);
            }

            BridgeTransitToolSystem tool = World.GetOrCreateSystemManaged<BridgeTransitToolSystem>();
            if (!tool.TryQueueStop(prefabEntity, prefab, typeName, controlPoint, request))
            {
                return BridgeResponse.Error(409, "another transit operation is in progress, retry shortly");
            }
            // Completed asynchronously by BridgeTransitToolSystem over the next tool frames.
            return null;
        }

        private BridgeResponse ListTransitLines(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            TransportType? typeFilter = null;
            if (request.Query.TryGetValue("type", out string rawType) && !string.IsNullOrEmpty(rawType))
            {
                if (!TryParseTransitType(rawType, out TransportType parsed, out _, out error))
                {
                    return error;
                }
                typeFilter = parsed;
            }
            request.Query.TryGetValue("query", out string search);
            bool includeStops = request.TryGetBool("includeStops", out bool rawInclude) && rawInclude;
            int limit = request.TryGetInt("limit", out int rawLimit) ? math.clamp(rawLimit, 1, 200) : 50;

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            NameSystem names = World.GetOrCreateSystemManaged<NameSystem>();
            var results = new List<object>();
            int total = 0;
            using (NativeArray<Entity> lines = TransitLineQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity line in lines)
                {
                    // The transportation overview panel builds its rows with this
                    // exact helper, so the numbers match the game UI.
                    Game.UI.InGame.UITransportLineData data = Game.UI.InGame.TransportUIUtils.BuildTransportLine(line, EntityManager, prefabSystem);
                    if (typeFilter.HasValue && data.type != typeFilter.Value)
                    {
                        continue;
                    }
                    string name = LabelOf(names, line);
                    if (!string.IsNullOrEmpty(search) && (name == null || name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0))
                    {
                        continue;
                    }
                    total++;
                    if (results.Count >= limit)
                    {
                        continue;
                    }
                    results.Add(new
                    {
                        entity = new { index = line.Index, version = line.Version },
                        name,
                        number = EntityManager.HasComponent<Game.Routes.RouteNumber>(line)
                            ? EntityManager.GetComponentData<Game.Routes.RouteNumber>(line).m_Number
                            : 0,
                        type = TransitTypeName(data.type),
                        isCargo = data.isCargo,
                        prefab = PrefabNameOf(line),
                        color = BridgeTransitToolSystem.ToHex(data.color),
                        active = data.active,
                        visible = data.visible,
                        schedule = ((Game.UI.InGame.RouteSchedule)data.schedule).ToString(),
                        stops = data.stops,
                        vehicles = data.vehicles,
                        passengersOnBoard = data.cargo,
                        usage = data.usage,
                        lengthMeters = data.length,
                        stopList = includeStops ? DescribeLineStops(line, names) : null,
                    });
                }
            }

            return BridgeResponse.Json(new
            {
                totalMatches = total,
                returned = results.Count,
                note = "stats come from the game's transportation panel helper: usage = passengers on board / capacity; " +
                       "the game keeps no per-line ridership history (city-wide PassengerCount* series: cs2_statistics). " +
                       "includeStops=true adds waiting passengers per stop.",
                lines = results,
            });
        }

        private List<object> DescribeLineStops(Entity line, NameSystem names)
        {
            var result = new List<object>();
            DynamicBuffer<Game.Routes.RouteWaypoint> waypoints = EntityManager.GetBuffer<Game.Routes.RouteWaypoint>(line, isReadOnly: true);
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                Entity stop = EntityManager.HasComponent<Game.Routes.Connected>(waypoint)
                    ? EntityManager.GetComponentData<Game.Routes.Connected>(waypoint).m_Connected
                    : Entity.Null;
                bool isStop = stop != Entity.Null && EntityManager.Exists(stop)
                    && EntityManager.HasComponent<Game.Routes.TransportStop>(stop);
                float3 position = EntityManager.HasComponent<Game.Routes.Position>(waypoint)
                    ? EntityManager.GetComponentData<Game.Routes.Position>(waypoint).m_Position
                    : default;
                result.Add(new
                {
                    order = i + 1,
                    isStop,
                    stop = isStop ? new { index = stop.Index, version = stop.Version } : null,
                    name = isStop ? LabelOf(names, stop) : null,
                    position = new { x = position.x, z = position.z },
                    waitingPassengers = EntityManager.HasComponent<Game.Routes.WaitingPassengers>(waypoint)
                        ? EntityManager.GetComponentData<Game.Routes.WaitingPassengers>(waypoint).m_Count
                        : 0,
                });
            }
            return result;
        }

        private BridgeResponse CreateTransitLine(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("type", out string rawType) || string.IsNullOrEmpty(rawType))
            {
                return BridgeResponse.Error(400, "provide ?type=bus|tram|metro|train");
            }
            if (!TryParseTransitType(rawType, out TransportType type, out string typeName, out error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("stops", out string rawStops) || string.IsNullOrEmpty(rawStops))
            {
                return BridgeResponse.Error(400,
                    "provide ?stops=<ordered list separated by ';'>, each item 'index:version' (a stop, or a station building) " +
                    "or 'x,z' (snaps to the nearest existing stop); the line loops back to the first stop automatically");
            }
            float snapRadius = request.TryGetFloat("snapRadius", out float rawSnap) ? math.clamp(rawSnap, 5f, 300f) : 60f;

            if (!TryResolveLinePrefab(type, typeName, IsForced(request), out Entity prefabEntity, out PrefabBase prefab, out error))
            {
                return error;
            }

            string[] items = rawStops.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (items.Length > 100)
            {
                return BridgeResponse.Error(400, "too many stops (max 100)");
            }
            var stops = new List<Entity>();
            for (int i = 0; i < items.Length; i++)
            {
                if (!TryResolveLineStop(items[i].Trim(), type, typeName, snapRadius, out Entity stop, out string why))
                {
                    return BridgeResponse.Error(400, $"stop #{i + 1} ('{items[i].Trim()}'): {why}");
                }
                stops.Add(stop);
            }
            // An explicit trailing repeat of the first stop is the loop closure, which is implied.
            if (stops.Count >= 3 && stops[stops.Count - 1] == stops[0])
            {
                stops.RemoveAt(stops.Count - 1);
            }
            if (stops.Count < 2)
            {
                return BridgeResponse.Error(400, "a line needs at least 2 different stops (the loop back to the first stop is added automatically)");
            }

            // RouteUtils.GetMinWaypointDistance: the vanilla tool merges closer waypoints.
            float minSpacing = EntityManager.GetComponentData<RouteData>(prefabEntity).m_SnapDistance * 0.5f;
            var lineStops = new BridgeTransitToolSystem.LineStop[stops.Count];
            for (int i = 0; i < stops.Count; i++)
            {
                lineStops[i] = new BridgeTransitToolSystem.LineStop
                {
                    Stop = stops[i],
                    Position = EntityManager.GetComponentData<Transform>(stops[i]).m_Position,
                };
            }
            for (int i = 0; i < lineStops.Length; i++)
            {
                int next = (i + 1) % lineStops.Length;
                if (lineStops[i].Stop == lineStops[next].Stop)
                {
                    return BridgeResponse.Error(400, $"stops #{i + 1} and #{next + 1} are the same stop; consecutive stops must differ");
                }
                if (math.distance(lineStops[i].Position, lineStops[next].Position) < minSpacing)
                {
                    return BridgeResponse.Error(400, $"stops #{i + 1} and #{next + 1} are closer than {minSpacing:F1}m; the game would merge them");
                }
            }

            bool hasColor = false;
            UnityEngine.Color32 color = default;
            if (request.Query.TryGetValue("color", out string rawColor) && !string.IsNullOrEmpty(rawColor))
            {
                if (!TryParseHexColor(rawColor, out color))
                {
                    return BridgeResponse.Error(400, "color must be #RRGGBB");
                }
                hasColor = true;
            }
            string name = request.Query.TryGetValue("name", out string rawName) && rawName != null ? rawName.Trim() : null;
            if (name != null && name.Length > 64)
            {
                return BridgeResponse.Error(400, "name too long (max 64 characters)");
            }
            if (TransitToolsBusy(out error))
            {
                return error;
            }

            BridgeTransitToolSystem tool = World.GetOrCreateSystemManaged<BridgeTransitToolSystem>();
            if (!tool.TryQueueLine(prefabEntity, prefab, typeName, lineStops, hasColor, color,
                    string.IsNullOrEmpty(name) ? null : name, CountDepots(type), request))
            {
                return BridgeResponse.Error(409, "another transit operation is in progress, retry shortly");
            }
            // Completed asynchronously once the game has pathfound and validated the line.
            return null;
        }

        private BridgeResponse DeleteTransitLine(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.TryGetInt("index", out int index) || !request.TryGetInt("version", out int version))
            {
                return BridgeResponse.Error(400, "provide ?index=&version= of a line from /transit/lines");
            }
            var line = new Entity { Index = index, Version = version };
            if (!EntityManager.Exists(line)
                || !EntityManager.HasComponent<Game.Routes.Route>(line)
                || !EntityManager.HasComponent<Game.Routes.TransportLine>(line))
            {
                return BridgeResponse.Error(404, $"entity {index}:{version} is not an existing transit line");
            }
            if (EntityManager.HasComponent<Temp>(line) || EntityManager.HasComponent<Game.Common.Deleted>(line))
            {
                return BridgeResponse.Error(409, "line is already being modified or deleted");
            }
            if (EntityManager.HasComponent<Game.Common.Owner>(line))
            {
                return BridgeResponse.Error(400, "this route belongs to a building (internal service route); refusing to delete it");
            }

            NameSystem names = World.GetOrCreateSystemManaged<NameSystem>();
            string name = LabelOf(names, line);
            int stops = Game.UI.InGame.TransportUIUtils.GetStopCount(EntityManager, line);
            int cargo = 0;
            int capacity = 0;
            int vehicles = Game.UI.InGame.TransportUIUtils.GetRouteVehiclesCount(EntityManager, line, ref cargo, ref capacity);

            // Exactly what TransportationOverviewUISystem.DeleteLine (the game's
            // own delete button) does; the game's route systems then remove the
            // waypoints/segments and recall the vehicles.
            EntityCommandBuffer commandBuffer = World.GetOrCreateSystemManaged<Game.EndFrameBarrier>().CreateCommandBuffer();
            commandBuffer.AddComponent(line, default(Game.Common.Deleted));

            return BridgeResponse.Json(new
            {
                deleted = true,
                line = new { index, version },
                name,
                stops,
                vehicles,
                note = "deleted like the game's own 'delete line' button (applied at end of this frame); " +
                       "stops remain in place and can be reused",
            });
        }

        private bool TryComputeStopPlacement(Entity stopPrefab, float3 point, Entity forcedRoad, float searchRadius,
            out ControlPoint controlPoint, out string reason)
        {
            controlPoint = default;
            reason = null;
            float objectRadius = EntityManager.HasComponent<ObjectGeometryData>(stopPrefab)
                ? EntityManager.GetComponentData<ObjectGeometryData>(stopPrefab).m_Size.z * 0.5f
                : 1f;

            var candidates = new List<KeyValuePair<float, Entity>>();
            if (forcedRoad != Entity.Null)
            {
                candidates.Add(new KeyValuePair<float, Entity>(0f, forcedRoad));
            }
            else
            {
                using (NativeArray<Entity> edges = TransitRoadQuery.ToEntityArray(Allocator.Temp))
                {
                    foreach (Entity edge in edges)
                    {
                        Bezier4x3 curve = EntityManager.GetComponentData<Game.Net.Curve>(edge).m_Bezier;
                        // The curve lies inside its control points' bounding box: cheap reject.
                        float2 min = math.min(math.min(curve.a.xz, curve.b.xz), math.min(curve.c.xz, curve.d.xz));
                        float2 max = math.max(math.max(curve.a.xz, curve.b.xz), math.max(curve.c.xz, curve.d.xz));
                        float2 outside = math.max(math.max(min - point.xz, point.xz - max), 0f);
                        if (math.length(outside) > searchRadius)
                        {
                            continue;
                        }
                        float distance = MathUtils.Distance(curve.xz, point.xz, out _);
                        if (distance <= searchRadius)
                        {
                            candidates.Add(new KeyValuePair<float, Entity>(distance, edge));
                        }
                    }
                }
                candidates.Sort((a, b) => a.Key.CompareTo(b.Key));
            }
            if (candidates.Count == 0)
            {
                reason = $"no road segment within {searchRadius:F0}m of ({point.x:F0}, {point.z:F0})";
                return false;
            }

            string firstProblem = null;
            for (int i = 0; i < candidates.Count && i < 10; i++)
            {
                Entity edge = candidates[i].Value;
                if (!RoadSuitsStop(edge, stopPrefab, out string why))
                {
                    firstProblem = firstProblem ?? why;
                    continue;
                }
                if (TrySnapStopToRoad(edge, point, objectRadius, out controlPoint))
                {
                    return true;
                }
                firstProblem = firstProblem ?? "the road has no roadside area wide enough for this stop";
            }
            reason = $"no suitable road near ({point.x:F0}, {point.z:F0}): {firstProblem}";
            return false;
        }

        /// <summary>
        /// Pre-check mirroring the game's net-object validation (required road
        /// type, pedestrian access) plus the stop's route connection (a lane the
        /// vehicles can stop on), so the nearest *suitable* road is chosen. The
        /// game's own validation still has the final say.
        /// </summary>
        private bool RoadSuitsStop(Entity edge, Entity stopPrefab, out string reason)
        {
            reason = null;
            if (!EntityManager.HasComponent<Game.Net.Composition>(edge) || !EntityManager.HasComponent<Game.Net.EdgeGeometry>(edge)
                || !EntityManager.HasBuffer<Game.Net.SubLane>(edge))
            {
                reason = "segment has no road geometry";
                return false;
            }
            Game.Net.RoadTypes requireRoad = EntityManager.HasComponent<NetObjectData>(stopPrefab)
                ? EntityManager.GetComponentData<NetObjectData>(stopPrefab).m_RequireRoad
                : 0;
            bool requirePedestrian = EntityManager.HasComponent<PlaceableObjectData>(stopPrefab)
                && (EntityManager.GetComponentData<PlaceableObjectData>(stopPrefab).m_Flags & Game.Objects.PlacementFlags.RequirePedestrian) != 0;
            RouteConnectionData connection = EntityManager.HasComponent<RouteConnectionData>(stopPrefab)
                ? EntityManager.GetComponentData<RouteConnectionData>(stopPrefab)
                : default;

            bool hasRequiredRoad = requireRoad == 0;
            bool hasPedestrian = !requirePedestrian;
            bool hasRouteLane = connection.m_RouteConnectionType != RouteConnectionType.Road
                && connection.m_RouteConnectionType != RouteConnectionType.Track;
            DynamicBuffer<Game.Net.SubLane> lanes = EntityManager.GetBuffer<Game.Net.SubLane>(edge, isReadOnly: true);
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity lane = lanes[i].m_SubLane;
                if (!EntityManager.HasComponent<PrefabRef>(lane))
                {
                    continue;
                }
                Entity lanePrefab = EntityManager.GetComponentData<PrefabRef>(lane).m_Prefab;
                if (EntityManager.HasComponent<Game.Net.CarLane>(lane) && EntityManager.HasComponent<CarLaneData>(lanePrefab))
                {
                    Game.Net.RoadTypes roadTypes = EntityManager.GetComponentData<CarLaneData>(lanePrefab).m_RoadTypes;
                    hasRequiredRoad |= (roadTypes & requireRoad) != 0;
                    hasRouteLane |= connection.m_RouteConnectionType == RouteConnectionType.Road
                        && (roadTypes & connection.m_RouteRoadType) != 0;
                }
                if (EntityManager.HasComponent<Game.Net.TrackLane>(lane) && EntityManager.HasComponent<TrackLaneData>(lanePrefab))
                {
                    Game.Net.TrackTypes trackTypes = EntityManager.GetComponentData<TrackLaneData>(lanePrefab).m_TrackTypes;
                    hasRouteLane |= connection.m_RouteConnectionType == RouteConnectionType.Track
                        && (trackTypes & connection.m_RouteTrackType) != 0;
                }
                hasPedestrian |= EntityManager.HasComponent<Game.Net.PedestrianLane>(lane);
            }
            if (!hasRequiredRoad)
            {
                reason = "road lacks the lane type this stop requires";
                return false;
            }
            if (!hasPedestrian)
            {
                reason = "road has no sidewalk (this stop needs pedestrian access)";
                return false;
            }
            if (!hasRouteLane)
            {
                reason = connection.m_RouteConnectionType == RouteConnectionType.Track
                    ? "road has no tracks for this stop type"
                    : "road has no lane this transport type can stop on";
                return false;
            }
            return true;
        }

        private bool TrySnapStopToRoad(Entity edge, float3 point, float objectRadius, out ControlPoint controlPoint)
        {
            controlPoint = default;
            Game.Net.Composition composition = EntityManager.GetComponentData<Game.Net.Composition>(edge);
            if (!EntityManager.HasBuffer<NetCompositionArea>(composition.m_Edge)
                || !EntityManager.HasComponent<NetCompositionData>(composition.m_Edge))
            {
                return false;
            }
            DynamicBuffer<NetCompositionArea> areas = EntityManager.GetBuffer<NetCompositionArea>(composition.m_Edge, isReadOnly: true);
            NetCompositionData compositionData = EntityManager.GetComponentData<NetCompositionData>(composition.m_Edge);
            Game.Net.EdgeGeometry geometry = EntityManager.GetComponentData<Game.Net.EdgeGeometry>(edge);

            float bestDistance = float.MaxValue;
            bool found = false;
            SnapToSegmentAreas(edge, geometry.m_Start, point, objectRadius, compositionData, areas, ref controlPoint, ref bestDistance, ref found);
            SnapToSegmentAreas(edge, geometry.m_End, point, objectRadius, compositionData, areas, ref controlPoint, ref bestDistance, ref found);
            if (found)
            {
                Bezier4x3 curve = EntityManager.GetComponentData<Game.Net.Curve>(edge).m_Bezier;
                MathUtils.Distance(curve.xz, controlPoint.m_Position.xz, out float curvePosition);
                controlPoint.m_CurvePosition = curvePosition;
            }
            return found;
        }

        /// <summary>
        /// Port of ObjectToolSystem.SnapJob.SnapSegmentAreas for non-building
        /// objects: the game passes snapToEdge only for buildings, so a stop is
        /// moved from the buildable area's (sidewalk's) centre line towards the
        /// area's snap line, then towards the requested point within the snap
        /// width, facing the road. Among all candidate areas, the one closest to
        /// the requested point wins, which picks the road side. The game's
        /// AttachPositionSystem re-snaps the committed stop onto its route lane.
        /// </summary>
        private static void SnapToSegmentAreas(Entity edge, Game.Net.Segment segment, float3 point, float objectRadius,
            NetCompositionData compositionData, DynamicBuffer<NetCompositionArea> areas,
            ref ControlPoint best, ref float bestDistance, ref bool found)
        {
            if (compositionData.m_Width <= 0f)
            {
                return;
            }
            Bezier4x3 middle = MathUtils.Lerp(segment.m_Left, segment.m_Right, 0.5f);
            for (int i = 0; i < areas.Length; i++)
            {
                NetCompositionArea area = areas[i];
                if ((area.m_Flags & NetAreaFlags.Buildable) == 0 || objectRadius >= area.m_Width * 0.51f)
                {
                    continue;
                }
                Bezier4x3 areaCurve = MathUtils.Lerp(segment.m_Left, segment.m_Right, area.m_Position.x / compositionData.m_Width + 0.5f);
                MathUtils.Distance(areaCurve.xz, point.xz, out float t);
                float3 tangent = MathUtils.Tangent(areaCurve, t);
                float2 direction = math.normalizesafe(tangent.xz);
                if ((area.m_Flags & NetAreaFlags.Median) != 0)
                {
                    direction = MathUtils.Left(direction);
                    if (((compositionData.m_Flags.m_Left ^ compositionData.m_Flags.m_Right) & CompositionFlags.Side.Raised) != 0)
                    {
                        if ((compositionData.m_Flags.m_Left & CompositionFlags.Side.Raised) != 0)
                        {
                            direction = -direction;
                        }
                    }
                    else if (math.dot(MathUtils.Position(middle, t).xz - point.xz, direction) < 0f)
                    {
                        direction = -direction;
                    }
                }
                else if ((area.m_Flags & NetAreaFlags.Invert) != 0)
                {
                    direction = MathUtils.Right(direction);
                }
                else
                {
                    direction = MathUtils.Left(direction);
                }

                float3 position = MathUtils.Position(areaCurve, t);
                float3 snapPosition = MathUtils.Position(
                    MathUtils.Lerp(segment.m_Left, segment.m_Right, area.m_SnapPosition.x / compositionData.m_Width + 0.5f), t);
                float towardSnapLine = math.max(0f, math.min(area.m_Width * 0.5f,
                    math.abs(area.m_SnapPosition.x - area.m_Position.x) + area.m_SnapWidth * 0.5f) - objectRadius);
                float towardPoint = math.max(0f, area.m_SnapWidth * 0.5f - objectRadius);
                position.xz += MathUtils.ClampLength(snapPosition.xz - position.xz, towardSnapLine);
                position.xz += MathUtils.ClampLength(point.xz - position.xz, towardPoint);
                position.y += area.m_Position.y;

                float distance = math.distance(position.xz, point.xz);
                if (distance >= bestDistance)
                {
                    continue;
                }
                bestDistance = distance;
                found = true;
                best = new ControlPoint
                {
                    m_Position = position,
                    m_HitPosition = new float3(point.x, position.y, point.z),
                    m_Direction = direction,
                    m_Rotation = ToolUtils.CalculateRotation(direction),
                    m_OriginalEntity = edge,
                };
            }
        }

        private bool TryResolveStopPrefab(TransportType type, string typeName, string wanted,
            out Entity prefabEntity, out PrefabBase prefab, out BridgeResponse error)
        {
            prefabEntity = Entity.Null;
            prefab = null;
            error = null;
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var candidates = new List<KeyValuePair<Entity, PrefabBase>>();
            using (NativeArray<Entity> entities = TransitStopPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    TransportStopData data = EntityManager.GetComponentData<TransportStopData>(entity);
                    if (data.m_TransportType != type || !data.m_PassengerTransport)
                    {
                        continue;
                    }
                    PrefabBase candidate = prefabSystem.GetPrefab<PrefabBase>(entity);
                    if (candidate != null)
                    {
                        candidates.Add(new KeyValuePair<Entity, PrefabBase>(entity, candidate));
                    }
                }
            }
            if (candidates.Count == 0)
            {
                error = BridgeResponse.Error(404, type == TransportType.Train || type == TransportType.Subway
                    ? $"{typeName} stops are station buildings, not roadside stops: place a station with cs2_place_building " +
                      "(cs2_find_prefabs query 'Station') and connect it to tracks"
                    : $"no roadside {typeName} stop prefab found");
                return false;
            }
            candidates.Sort((a, b) => string.CompareOrdinal(a.Value.name, b.Value.name));
            if (!string.IsNullOrEmpty(wanted))
            {
                foreach (KeyValuePair<Entity, PrefabBase> candidate in candidates)
                {
                    if (string.Equals(candidate.Value.name, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        prefabEntity = candidate.Key;
                        prefab = candidate.Value;
                        return true;
                    }
                }
                var available = new List<string>();
                foreach (KeyValuePair<Entity, PrefabBase> candidate in candidates)
                {
                    available.Add(candidate.Value.name);
                }
                error = BridgeResponse.Error(404, $"unknown {typeName} stop prefab '{wanted}'; available: {string.Join(", ", available)}");
                return false;
            }
            foreach (KeyValuePair<Entity, PrefabBase> candidate in candidates)
            {
                if (!IsLocked(candidate.Key))
                {
                    prefabEntity = candidate.Key;
                    prefab = candidate.Value;
                    return true;
                }
            }
            prefabEntity = candidates[0].Key;
            prefab = candidates[0].Value;
            return true;
        }

        private bool TryResolveLinePrefab(TransportType type, string typeName, bool force,
            out Entity prefabEntity, out PrefabBase prefab, out BridgeResponse error)
        {
            prefabEntity = Entity.Null;
            prefab = null;
            error = null;
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var candidates = new List<KeyValuePair<Entity, PrefabBase>>();
            using (NativeArray<Entity> entities = TransitLinePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    TransportLineData data = EntityManager.GetComponentData<TransportLineData>(entity);
                    if (data.m_TransportType != type || !data.m_PassengerTransport || data.m_CargoTransport)
                    {
                        continue;
                    }
                    PrefabBase candidate = prefabSystem.GetPrefab<PrefabBase>(entity);
                    if (candidate != null)
                    {
                        candidates.Add(new KeyValuePair<Entity, PrefabBase>(entity, candidate));
                    }
                }
            }
            if (candidates.Count == 0)
            {
                error = BridgeResponse.Error(404, $"no passenger {typeName} line prefab found in this game");
                return false;
            }
            candidates.Sort((a, b) => string.CompareOrdinal(a.Value.name, b.Value.name));
            foreach (KeyValuePair<Entity, PrefabBase> candidate in candidates)
            {
                if (!IsLocked(candidate.Key))
                {
                    prefabEntity = candidate.Key;
                    prefab = candidate.Value;
                    return true;
                }
            }
            if (!force)
            {
                error = BridgeResponse.Error(409, $"{typeName} lines are locked (milestone not reached); pass force=true to create anyway");
                return false;
            }
            prefabEntity = candidates[0].Key;
            prefab = candidates[0].Value;
            return true;
        }

        private bool TryResolveLineStop(string item, TransportType type, string typeName, float snapRadius,
            out Entity stop, out string reason)
        {
            stop = Entity.Null;
            reason = null;
            if (item.IndexOf(':') >= 0)
            {
                if (!TryParseEntityRef(item, out Entity entity) || !EntityManager.Exists(entity))
                {
                    reason = "entity does not exist (stale id?)";
                    return false;
                }
                if (IsValidLineStop(entity, type))
                {
                    stop = entity;
                    return true;
                }
                if (TryFindStopInStation(entity, type, out stop))
                {
                    return true;
                }
                reason = EntityManager.HasComponent<Game.Routes.TransportStop>(entity)
                    ? $"it is not a {typeName} passenger stop"
                    : $"it is neither a {typeName} stop nor a station containing one";
                return false;
            }

            string[] parts = item.Split(',');
            if (parts.Length != 2
                || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
            {
                reason = "expected 'index:version' or 'x,z'";
                return false;
            }
            if (!TryFindNearestLineStop(type, new float2(x, z), snapRadius, out stop))
            {
                reason = $"no {typeName} stop within {snapRadius:F0}m of ({x:F0}, {z:F0}); place one first with cs2_place_transit_stop";
                return false;
            }
            return true;
        }

        private bool IsValidLineStop(Entity entity, TransportType type)
        {
            if (!EntityManager.HasComponent<Game.Routes.TransportStop>(entity)
                || !EntityManager.HasBuffer<Game.Routes.ConnectedRoute>(entity)
                || !EntityManager.HasComponent<PrefabRef>(entity)
                || !EntityManager.HasComponent<Transform>(entity)
                || EntityManager.HasComponent<Temp>(entity)
                || EntityManager.HasComponent<Game.Common.Deleted>(entity))
            {
                return false;
            }
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (!EntityManager.HasComponent<TransportStopData>(prefab))
            {
                return false;
            }
            // RouteToolSystem.SnapJob.ValidateStop (transport type) plus
            // ValidationHelpers.ValidateRoute (passenger line needs passenger stop).
            TransportStopData data = EntityManager.GetComponentData<TransportStopData>(prefab);
            return data.m_TransportType == type && data.m_PassengerTransport;
        }

        /// <summary>
        /// Like the vanilla line tool when a station building is clicked: use
        /// the building's platform/stop matching the line's transport type.
        /// </summary>
        private bool TryFindStopInStation(Entity building, TransportType type, out Entity stop)
        {
            stop = Entity.Null;
            if (EntityManager.HasBuffer<Game.Objects.SubObject>(building))
            {
                DynamicBuffer<Game.Objects.SubObject> subObjects = EntityManager.GetBuffer<Game.Objects.SubObject>(building, isReadOnly: true);
                for (int i = 0; i < subObjects.Length; i++)
                {
                    if (IsValidLineStop(subObjects[i].m_SubObject, type))
                    {
                        stop = subObjects[i].m_SubObject;
                        return true;
                    }
                }
            }
            // Platforms nested deeper (sub-buildings, upgrades): follow owner chains.
            using (NativeArray<Entity> stops = TransitStopQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity candidate in stops)
                {
                    if (!IsValidLineStop(candidate, type))
                    {
                        continue;
                    }
                    Entity owner = candidate;
                    for (int depth = 0; depth < 8 && EntityManager.HasComponent<Game.Common.Owner>(owner); depth++)
                    {
                        owner = EntityManager.GetComponentData<Game.Common.Owner>(owner).m_Owner;
                        if (owner == building)
                        {
                            stop = candidate;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private bool TryFindNearestLineStop(TransportType type, float2 point, float radius, out Entity stop)
        {
            stop = Entity.Null;
            float bestDistance = float.MaxValue;
            using (NativeArray<Entity> stops = TransitStopQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity candidate in stops)
                {
                    if (!IsValidLineStop(candidate, type))
                    {
                        continue;
                    }
                    float distance = math.distance(EntityManager.GetComponentData<Transform>(candidate).m_Position.xz, point);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        stop = candidate;
                    }
                }
            }
            return stop != Entity.Null && bestDistance <= radius;
        }

        private int CountDepots(TransportType type)
        {
            int count = 0;
            using (NativeArray<Entity> depots = TransitDepotQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity depot in depots)
                {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(depot).m_Prefab;
                    if (EntityManager.HasComponent<TransportDepotData>(prefab)
                        && EntityManager.GetComponentData<TransportDepotData>(prefab).m_TransportType == type)
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        private bool TransitToolsBusy(out BridgeResponse error)
        {
            error = null;
            if (World.GetOrCreateSystemManaged<BridgeToolSystem>().IsBusy
                || World.GetOrCreateSystemManaged<BridgeTransitToolSystem>().IsBusy
                || World.GetOrCreateSystemManaged<BridgeRoadToolSystem>().IsBusy)
            {
                error = BridgeResponse.Error(409, "another build operation is in progress, retry shortly");
                return true;
            }
            return false;
        }

        private static bool TryParseTransitType(string raw, out TransportType type, out string typeName, out BridgeResponse error)
        {
            error = null;
            typeName = null;
            if (!kTransitTypes.TryGetValue(raw.Trim(), out type))
            {
                error = BridgeResponse.Error(400, $"unknown transit type '{raw}'; use bus, tram, metro (subway) or train");
                return false;
            }
            typeName = TransitTypeName(type);
            return true;
        }

        private static string TransitTypeName(TransportType type)
        {
            return type == TransportType.Subway ? "metro" : type.ToString().ToLowerInvariant();
        }

        private static bool TryParseEntityRef(string raw, out Entity entity)
        {
            entity = Entity.Null;
            string[] parts = raw.Trim().Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
            {
                return false;
            }
            entity = new Entity { Index = index, Version = version };
            return true;
        }

        private static bool TryParseHexColor(string raw, out UnityEngine.Color32 color)
        {
            color = default;
            string hex = raw.Trim().TrimStart('#');
            if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
            {
                return false;
            }
            color = new UnityEngine.Color32((byte)(value >> 16), (byte)(value >> 8), (byte)value, 255);
            return true;
        }

        private string PrefabNameOf(Entity entity)
        {
            if (!EntityManager.HasComponent<PrefabRef>(entity))
            {
                return null;
            }
            PrefabBase prefab = World.GetOrCreateSystemManaged<PrefabSystem>().GetPrefab<PrefabBase>(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
            return prefab != null ? prefab.name : null;
        }

        private static string LabelOf(NameSystem names, Entity entity)
        {
            try
            {
                return names.GetRenderedLabelName(entity);
            }
            catch
            {
                return null;
            }
        }
    }
}
