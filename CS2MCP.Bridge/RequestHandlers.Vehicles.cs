using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// Vehicle census and road graph for traffic work. The census classifies
    /// every driving road vehicle (not parked cars or trailers) by type and
    /// destination, and follows each stopped vehicle's Blocker (what the game's
    /// car AI says it is waiting for) to the head of its queue, so the places
    /// holding up the most vehicles and any deadlock loops (vehicles blocking
    /// each other in a cycle) can be found. The road graph lists road segments
    /// with their junctions, traffic lights and flow. Read-only.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private sealed class VehicleCounts
        {
            public int Total;
            public int Stopped;
        }

        private sealed class QueueGroup
        {
            public Entity Location;
            public float3 Position;
            public int Queued;
            public int Heads;
            public readonly Dictionary<string, int> HeadBlockers = new Dictionary<string, int>();
            public readonly Dictionary<string, int> HeadTypes = new Dictionary<string, int>();
        }

        private sealed class DeadlockGroup
        {
            public int Members;
            public int Queued;
            public readonly Dictionary<Entity, float3> Locations = new Dictionary<Entity, float3>();
        }

        // Stopped = slower than this (m/s).
        private const float kStoppedSpeed = 0.5f;

        private EntityQuery m_DrivingCarQuery;
        private bool m_DrivingCarQueryCreated;
        private EntityQuery m_NetEdgeQuery;
        private bool m_NetEdgeQueryCreated;

        /// <summary>Every network segment (roads, tracks, pipes, paths...).</summary>
        private EntityQuery NetEdgeQuery
        {
            get
            {
                if (!m_NetEdgeQueryCreated)
                {
                    m_NetEdgeQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Edge>(),
                            ComponentType.ReadOnly<Curve>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Deleted>(),
                            ComponentType.ReadOnly<Temp>(),
                        },
                    });
                    m_NetEdgeQueryCreated = true;
                }
                return m_NetEdgeQuery;
            }
        }

        private EntityQuery DrivingCarQuery
        {
            get
            {
                if (!m_DrivingCarQueryCreated)
                {
                    m_DrivingCarQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Car>(),
                            ComponentType.ReadOnly<CarCurrentLane>(),
                            ComponentType.ReadOnly<Game.Objects.Transform>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<ParkedCar>(),
                            ComponentType.ReadOnly<Deleted>(),
                            ComponentType.ReadOnly<Temp>(),
                        },
                    });
                    m_DrivingCarQueryCreated = true;
                }
                return m_DrivingCarQuery;
            }
        }

        private BridgeResponse VehicleCensus(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            bool hasCenter = request.TryGetFloat("x", out float x) & request.TryGetFloat("z", out float z);
            float radius = request.TryGetFloat("radius", out float rawRadius) ? math.max(rawRadius, 1f) : 500f;
            float2 center = new float2(x, z);
            int limit = request.TryGetInt("limit", out int rawLimit) ? math.clamp(rawLimit, 1, 100) : 15;

            NativeArray<Entity> cars = DrivingCarQuery.ToEntityArray(Allocator.Temp);
            try
            {
                int count = cars.Length;
                var indexOf = new Dictionary<Entity, int>(count);
                for (int i = 0; i < count; i++)
                {
                    indexOf[cars[i]] = i;
                }

                var types = new string[count];
                var positions = new float3[count];
                var stopped = new bool[count];
                var blockerIndex = new int[count];
                var blockerTypes = new BlockerType[count];
                var locations = new Entity[count];
                var inArea = new bool[count];
                for (int i = 0; i < count; i++)
                {
                    Entity car = cars[i];
                    types[i] = VehicleKind(car);
                    positions[i] = EntityManager.GetComponentData<Game.Objects.Transform>(car).m_Position;
                    inArea[i] = !hasCenter || math.distance(positions[i].xz, center) <= radius;
                    float speed = EntityManager.HasComponent<Moving>(car)
                        ? math.length(EntityManager.GetComponentData<Moving>(car).m_Velocity)
                        : 0f;
                    stopped[i] = speed < kStoppedSpeed;
                    blockerIndex[i] = -1;
                    if (EntityManager.HasComponent<Blocker>(car))
                    {
                        Blocker blocker = EntityManager.GetComponentData<Blocker>(car);
                        blockerTypes[i] = blocker.m_Type;
                        Entity blocking = blocker.m_Blocker;
                        // A blocking trailer stands for the vehicle pulling it.
                        if (blocking != Entity.Null && EntityManager.HasComponent<Controller>(blocking))
                        {
                            blocking = EntityManager.GetComponentData<Controller>(blocking).m_Controller;
                        }
                        if (blocking != car && blocking != Entity.Null && indexOf.TryGetValue(blocking, out int b))
                        {
                            blockerIndex[i] = b;
                        }
                    }
                    Entity lane = EntityManager.GetComponentData<CarCurrentLane>(car).m_Lane;
                    locations[i] = lane != Entity.Null && EntityManager.HasComponent<Owner>(lane)
                        ? EntityManager.GetComponentData<Owner>(lane).m_Owner
                        : Entity.Null;
                }

                // Follow each stopped vehicle's blockers to the head of its queue.
                // head >= 0: index of the head vehicle; head <= -2: deadlock loop id (-2 - id).
                var head = new int[count];
                var state = new byte[count];
                var path = new List<int>();
                var deadlockMembers = new List<List<int>>();
                for (int i = 0; i < count; i++)
                {
                    if (!stopped[i] || state[i] == 2)
                    {
                        continue;
                    }
                    path.Clear();
                    int j = i;
                    int result;
                    while (true)
                    {
                        if (state[j] == 2)
                        {
                            result = head[j];
                            break;
                        }
                        if (state[j] == 1)
                        {
                            var members = new List<int>();
                            for (int p = path.IndexOf(j); p < path.Count; p++)
                            {
                                members.Add(path[p]);
                            }
                            deadlockMembers.Add(members);
                            result = -2 - (deadlockMembers.Count - 1);
                            break;
                        }
                        state[j] = 1;
                        path.Add(j);
                        int b = blockerIndex[j];
                        if (b < 0 || !stopped[b])
                        {
                            result = j;
                            break;
                        }
                        j = b;
                    }
                    foreach (int p in path)
                    {
                        state[p] = 2;
                        head[p] = result;
                    }
                }

                // Aggregate.
                var cityTypes = new Dictionary<string, VehicleCounts>();
                var areaTypes = new Dictionary<string, VehicleCounts>();
                var destinations = new Dictionary<string, int>();
                var stoppedBy = new Dictionary<string, int>();
                var queues = new Dictionary<Entity, QueueGroup>();
                var deadlocks = new Dictionary<int, DeadlockGroup>();
                int stoppedTotal = 0;
                int areaTotal = 0;
                int areaStopped = 0;
                for (int i = 0; i < count; i++)
                {
                    Count(cityTypes, types[i], stopped[i]);
                    if (stopped[i])
                    {
                        stoppedTotal++;
                    }
                    if (inArea[i])
                    {
                        areaTotal++;
                        Count(areaTypes, types[i], stopped[i]);
                        Increment(destinations, DestinationKind(cars[i]));
                        if (stopped[i])
                        {
                            areaStopped++;
                            Increment(stoppedBy, blockerTypes[i].ToString());
                        }
                    }
                    if (!stopped[i])
                    {
                        continue;
                    }
                    int h = head[i];
                    if (h <= -2)
                    {
                        int id = -2 - h;
                        if (!deadlocks.TryGetValue(id, out DeadlockGroup deadlock))
                        {
                            deadlock = new DeadlockGroup { Members = deadlockMembers[id].Count };
                            foreach (int member in deadlockMembers[id])
                            {
                                Entity location = locations[member];
                                if (!deadlock.Locations.ContainsKey(location))
                                {
                                    deadlock.Locations.Add(location, LocationPosition(location, positions[member]));
                                }
                            }
                            deadlocks.Add(id, deadlock);
                        }
                        deadlock.Queued++;
                        continue;
                    }
                    Entity headLocation = locations[h];
                    if (!queues.TryGetValue(headLocation, out QueueGroup queue))
                    {
                        queue = new QueueGroup
                        {
                            Location = headLocation,
                            Position = LocationPosition(headLocation, positions[h]),
                        };
                        queues.Add(headLocation, queue);
                    }
                    queue.Queued++;
                    if (h == i)
                    {
                        queue.Heads++;
                        Increment(queue.HeadBlockers, blockerTypes[i].ToString());
                        Increment(queue.HeadTypes, types[i]);
                    }
                }

                var queueList = new List<QueueGroup>(queues.Values);
                if (hasCenter)
                {
                    queueList.RemoveAll(q => math.distance(q.Position.xz, center) > radius);
                }
                queueList.Sort((a, b) => b.Queued.CompareTo(a.Queued));
                var queueHeads = new List<object>();
                for (int i = 0; i < queueList.Count && i < limit; i++)
                {
                    QueueGroup q = queueList[i];
                    queueHeads.Add(new
                    {
                        location = LocationInfo(q.Location, q.Position),
                        vehiclesQueued = q.Queued,
                        headVehicles = q.Heads,
                        headWaitingFor = q.HeadBlockers,
                        headVehicleTypes = q.HeadTypes,
                    });
                }

                var deadlockList = new List<DeadlockGroup>(deadlocks.Values);
                if (hasCenter)
                {
                    deadlockList.RemoveAll(d =>
                    {
                        foreach (float3 position in d.Locations.Values)
                        {
                            if (math.distance(position.xz, center) <= radius)
                            {
                                return false;
                            }
                        }
                        return true;
                    });
                }
                deadlockList.Sort((a, b) => b.Queued.CompareTo(a.Queued));
                var deadlockItems = new List<object>();
                for (int i = 0; i < deadlockList.Count && i < limit; i++)
                {
                    DeadlockGroup d = deadlockList[i];
                    var where = new List<object>();
                    foreach (KeyValuePair<Entity, float3> pair in d.Locations)
                    {
                        where.Add(LocationInfo(pair.Key, pair.Value));
                    }
                    deadlockItems.Add(new
                    {
                        vehiclesInLoop = d.Members,
                        vehiclesQueued = d.Queued,
                        locations = where,
                    });
                }

                return BridgeResponse.Json(new
                {
                    drivingVehicles = count,
                    stoppedVehicles = stoppedTotal,
                    byType = ToCountObject(cityTypes),
                    area = hasCenter ? (object)new { x, z, radius } : null,
                    inArea = hasCenter
                        ? (object)new
                        {
                            vehicles = areaTotal,
                            stopped = areaStopped,
                            byType = ToCountObject(areaTypes),
                            destinations,
                            stoppedWaitingFor = stoppedBy,
                        }
                        : null,
                    queueHeads,
                    deadlocks = new { count = deadlockList.Count, items = deadlockItems },
                    note = "driving road vehicles only (parked cars and trailers excluded); stopped = under 0.5 m/s. " +
                           "waitingFor is the game's car AI blocker: Continuing = the vehicle ahead, Signal = red light, " +
                           "Crossing = crossing traffic, Oncoming = oncoming traffic when turning, Spawn = leaving a " +
                           "building or entering the road, None = stopped by choice (loading, parking, at a stop). " +
                           "queueHeads follows each stopped vehicle's blockers to the head of its queue: the location of " +
                           "the head is where the hold-up is; vehiclesQueued counts every stopped vehicle behind it. " +
                           "deadlocks = loops of vehicles blocking each other (gridlock that cannot clear by itself).",
                });
            }
            finally
            {
                cars.Dispose();
            }
        }

        private BridgeResponse RoadGraph(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.TryGetFloat("x", out float x) || !request.TryGetFloat("z", out float z))
            {
                return BridgeResponse.Error(400, "provide ?x=&z= (and optionally radius, limit, query)");
            }
            float radius = request.TryGetFloat("radius", out float rawRadius) ? math.clamp(rawRadius, 1f, 2000f) : 300f;
            int limit = request.TryGetInt("limit", out int rawLimit) ? math.clamp(rawLimit, 1, 3000) : 400;
            request.Query.TryGetValue("query", out string query);
            bool allNets = request.Query.TryGetValue("nets", out string nets)
                && string.Equals(nets, "all", System.StringComparison.OrdinalIgnoreCase);
            float2 center = new float2(x, z);

            var edgeItems = new List<object>();
            var nodes = new Dictionary<Entity, List<Entity>>();
            int matched = 0;
            using (NativeArray<Entity> edges = (allNets ? NetEdgeQuery : TrafficRoadQuery).ToEntityArray(Allocator.Temp))
            {
                foreach (Entity edge in edges)
                {
                    Curve curve = EntityManager.GetComponentData<Curve>(edge);
                    if (math.distance(MathUtils.Position(curve.m_Bezier, 0.5f).xz, center) > radius)
                    {
                        continue;
                    }
                    string prefab = PrefabNameOf(edge);
                    if (!string.IsNullOrEmpty(query) && (prefab == null
                        || prefab.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) < 0))
                    {
                        continue;
                    }
                    matched++;
                    if (edgeItems.Count >= limit)
                    {
                        continue;
                    }
                    Edge ends = EntityManager.GetComponentData<Edge>(edge);
                    float? flow = null;
                    float? volume = null;
                    if (EntityManager.HasComponent<Road>(edge))
                    {
                        Road road = EntityManager.GetComponentData<Road>(edge);
                        flow = math.round(math.csum(NetUtils.GetTrafficFlowSpeed(road) * 100f) * 0.25f);
                        volume = math.round(math.csum((road.m_TrafficFlowDistance0 + road.m_TrafficFlowDistance1) * kTrafficVolumeScale) * 0.25f);
                    }
                    edgeItems.Add(new
                    {
                        road = new { index = edge.Index, version = edge.Version },
                        prefab,
                        startNode = ends.m_Start.Index,
                        endNode = ends.m_End.Index,
                        start = new { x = curve.m_Bezier.a.x, y = curve.m_Bezier.a.y, z = curve.m_Bezier.a.z },
                        end = new { x = curve.m_Bezier.d.x, y = curve.m_Bezier.d.y, z = curve.m_Bezier.d.z },
                        length = math.round(curve.m_Length),
                        partOfBuilding = EntityManager.HasComponent<Owner>(edge),
                        flowPercent = flow,
                        volume,
                    });
                    AddNodeEdge(nodes, ends.m_Start, edge);
                    AddNodeEdge(nodes, ends.m_End, edge);
                }
            }

            var nodeItems = new List<object>();
            foreach (KeyValuePair<Entity, List<Entity>> pair in nodes)
            {
                Entity node = pair.Key;
                if (!EntityManager.HasComponent<Node>(node))
                {
                    continue;
                }
                float3 position = EntityManager.GetComponentData<Node>(node).m_Position;
                int roadsConnected = 0;
                int segmentsConnected = 0;
                if (EntityManager.HasBuffer<ConnectedEdge>(node))
                {
                    DynamicBuffer<ConnectedEdge> connected = EntityManager.GetBuffer<ConnectedEdge>(node, isReadOnly: true);
                    segmentsConnected = connected.Length;
                    for (int i = 0; i < connected.Length; i++)
                    {
                        if (EntityManager.HasComponent<Road>(connected[i].m_Edge))
                        {
                            roadsConnected++;
                        }
                    }
                }
                nodeItems.Add(new
                {
                    node = new { index = node.Index, version = node.Version },
                    position = new { x = position.x, y = position.y, z = position.z },
                    roadsConnected,
                    segmentsConnected,
                    partOfBuilding = EntityManager.HasComponent<Owner>(node),
                    trafficLights = EntityManager.HasComponent<TrafficLights>(node),
                    roundabout = EntityManager.HasComponent<Roundabout>(node),
                    outsideConnection = EntityManager.HasComponent<Game.Objects.OutsideConnection>(node),
                });
            }

            return BridgeResponse.Json(new
            {
                area = new { x, z, radius },
                roadsMatched = matched,
                roads = edgeItems,
                junctions = nodeItems,
                note = "roads = road segments (every network with nets=all) whose midpoint is in the area " +
                       "(startNode/endNode are junction ids; one-way roads run from start to end); junctions = their " +
                       "end nodes with how many segments meet there and whether they have traffic lights. " +
                       "partOfBuilding = owned by a building or asset (e.g. a station's own tracks). flowPercent/volume " +
                       "as in cs2_traffic (roads only).",
            });
        }

        private static void AddNodeEdge(Dictionary<Entity, List<Entity>> nodes, Entity node, Entity edge)
        {
            if (!nodes.TryGetValue(node, out List<Entity> list))
            {
                list = new List<Entity>();
                nodes.Add(node, list);
            }
            list.Add(edge);
        }

        private string VehicleKind(Entity car)
        {
            if (EntityManager.HasComponent<PersonalCar>(car)) return "personalCar";
            if (EntityManager.HasComponent<Taxi>(car)) return "taxi";
            if (EntityManager.HasComponent<Game.Vehicles.PublicTransport>(car)) return "bus";
            if (EntityManager.HasComponent<DeliveryTruck>(car)) return "deliveryTruck";
            if (EntityManager.HasComponent<CargoTransport>(car)) return "cargoTruck";
            if (EntityManager.HasComponent<GarbageTruck>(car)) return "garbageTruck";
            if (EntityManager.HasComponent<PostVan>(car)) return "postVan";
            if (EntityManager.HasComponent<Game.Vehicles.PoliceCar>(car) || EntityManager.HasComponent<Game.Vehicles.FireEngine>(car)
                || EntityManager.HasComponent<Game.Vehicles.Ambulance>(car) || EntityManager.HasComponent<Game.Vehicles.Hearse>(car)
                || EntityManager.HasComponent<Game.Vehicles.PrisonerTransport>(car))
            {
                return "emergencyOrCare";
            }
            if (EntityManager.HasComponent<Game.Vehicles.MaintenanceVehicle>(car) || EntityManager.HasComponent<Game.Vehicles.WorkVehicle>(car))
            {
                return "workVehicle";
            }
            return "other";
        }

        private string DestinationKind(Entity car)
        {
            if (!EntityManager.HasComponent<Target>(car))
            {
                return "none";
            }
            Entity target = EntityManager.GetComponentData<Target>(car).m_Target;
            // Companies and households target through the building they rent.
            if (target != Entity.Null && EntityManager.Exists(target) && EntityManager.HasComponent<PropertyRenter>(target))
            {
                target = EntityManager.GetComponentData<PropertyRenter>(target).m_Property;
            }
            // Parking and building lanes, sub-buildings: walk up to the owning object.
            for (int i = 0; i < 4 && target != Entity.Null && EntityManager.Exists(target); i++)
            {
                if (EntityManager.HasComponent<Game.Objects.OutsideConnection>(target))
                {
                    return "outsideCity";
                }
                if (EntityManager.HasComponent<Building>(target) || !EntityManager.HasComponent<Owner>(target))
                {
                    break;
                }
                target = EntityManager.GetComponentData<Owner>(target).m_Owner;
            }
            if (target == Entity.Null || !EntityManager.Exists(target))
            {
                return "none";
            }
            if (EntityManager.HasComponent<Game.Objects.OutsideConnection>(target)) return "outsideCity";
            if (EntityManager.HasComponent<ResidentialProperty>(target)) return "residential";
            if (EntityManager.HasComponent<CommercialProperty>(target)) return "commercial";
            if (EntityManager.HasComponent<OfficeProperty>(target)) return "office";
            if (EntityManager.HasComponent<IndustrialProperty>(target) || EntityManager.HasComponent<ExtractorProperty>(target)
                || EntityManager.HasComponent<StorageProperty>(target))
            {
                return "industrial";
            }
            if (EntityManager.HasComponent<Building>(target)) return "serviceOrOtherBuilding";
            if (EntityManager.HasComponent<Edge>(target) || EntityManager.HasComponent<Node>(target)) return "street";
            return "other";
        }

        private float3 LocationPosition(Entity location, float3 fallback)
        {
            if (location != Entity.Null && EntityManager.Exists(location))
            {
                if (EntityManager.HasComponent<Node>(location))
                {
                    return EntityManager.GetComponentData<Node>(location).m_Position;
                }
                if (EntityManager.HasComponent<Curve>(location))
                {
                    return MathUtils.Position(EntityManager.GetComponentData<Curve>(location).m_Bezier, 0.5f);
                }
            }
            return fallback;
        }

        private object LocationInfo(Entity location, float3 position)
        {
            string kind = "other";
            if (location != Entity.Null && EntityManager.Exists(location))
            {
                if (EntityManager.HasComponent<Node>(location))
                {
                    kind = "junction";
                }
                else if (EntityManager.HasComponent<Edge>(location))
                {
                    kind = "road";
                }
                else if (EntityManager.HasComponent<Building>(location))
                {
                    kind = "building";
                }
            }
            return new
            {
                kind,
                entity = location == Entity.Null ? null : (object)new { index = location.Index, version = location.Version },
                prefab = location == Entity.Null ? null : PrefabNameOf(location),
                trafficLights = kind == "junction" && EntityManager.HasComponent<TrafficLights>(location),
                position = new { x = math.round(position.x), z = math.round(position.z) },
            };
        }

        private static void Count(Dictionary<string, VehicleCounts> counts, string type, bool isStopped)
        {
            if (!counts.TryGetValue(type, out VehicleCounts c))
            {
                c = new VehicleCounts();
                counts.Add(type, c);
            }
            c.Total++;
            if (isStopped)
            {
                c.Stopped++;
            }
        }

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out int value);
            counts[key] = value + 1;
        }

        private static Dictionary<string, object> ToCountObject(Dictionary<string, VehicleCounts> counts)
        {
            var result = new Dictionary<string, object>();
            foreach (KeyValuePair<string, VehicleCounts> pair in counts)
            {
                result[pair.Key] = new { total = pair.Value.Total, stopped = pair.Value.Stopped };
            }
            return result;
        }
    }
}
