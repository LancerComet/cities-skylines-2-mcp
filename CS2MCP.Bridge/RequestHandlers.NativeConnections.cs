using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        // Attachment 指向占位母体实际挂接的经营建筑；与附属物的 Attached 父级关系分开。
        private object DescribeNativeAttachment(Entity entity)
        {
            if (!EntityManager.HasComponent<Game.Objects.Attachment>(entity)) return null;
            Entity attached = EntityManager.GetComponentData<Game.Objects.Attachment>(entity).m_Attached;
            bool exists = attached != Entity.Null && EntityManager.Exists(attached);
            return new { entity = Ref(attached), exists,
                deleted = exists && EntityManager.HasComponent<Game.Common.Deleted>(attached),
                temporary = exists && EntityManager.HasComponent<Game.Tools.Temp>(attached),
                prefab = exists ? PrefabNameOf(attached) : null,
                isBuilding = exists && EntityManager.HasComponent<Game.Buildings.Building>(attached) };
        }

        // 入口以原生组件为准；名称或靠近道路不能证明接通。完整车道仍保留访问限制及真实端口。
        private object DescribeNativeSpawnLocation(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity)
                || !EntityManager.HasComponent<Game.Objects.SpawnLocation>(entity)) return null;
            var data = EntityManager.GetComponentData<Game.Objects.SpawnLocation>(entity);
            object prefabQualification = null;
            if (EntityManager.HasComponent<Game.Prefabs.PrefabRef>(entity)) {
                Entity prefab = EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(entity).m_Prefab;
                if (prefab != Entity.Null && EntityManager.Exists(prefab)
                    && EntityManager.HasComponent<Game.Prefabs.SpawnLocationData>(prefab)) {
                    var qualification = EntityManager.GetComponentData<Game.Prefabs.SpawnLocationData>(prefab);
                    prefabQualification = new { connectionType = qualification.m_ConnectionType.ToString(),
                        activityMask = qualification.m_ActivityMask.ToString(), roadTypes = qualification.m_RoadTypes.ToString(),
                        trackTypes = qualification.m_TrackTypes.ToString(), requireAuthorization = qualification.m_RequireAuthorization,
                        hangaroundOnLane = qualification.m_HangaroundOnLane };
                }
            }
            return new { accessRestriction = Ref(data.m_AccessRestriction), flags = data.m_Flags.ToString(),
                groupIndex = data.m_GroupIndex, prefabQualification, connectedLane1 = DescribeTransitLane(data.m_ConnectedLane1),
                connectedLane2 = DescribeTransitLane(data.m_ConnectedLane2),
                curvePosition1 = data.m_CurvePosition1, curvePosition2 = data.m_CurvePosition2 };
        }

        // LaneConnection 是存在时的额外关联；原生 Inside ConnectionLane 不保证带有该组件。
        private object DescribeNativeLaneConnection(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity)
                || !EntityManager.HasComponent<Game.Net.LaneConnection>(entity)) return null;
            var data = EntityManager.GetComponentData<Game.Net.LaneConnection>(entity);
            return new { startLane = Ref(data.m_StartLane), endLane = Ref(data.m_EndLane),
                startPosition = data.m_StartPosition, endPosition = data.m_EndPosition };
        }

        // 原生站点/入口不总带 SpawnLocation；RoutesModifiedSystem 使用 AccessLane 作为实际步行种子。
        private object DescribeNativeAccessLane(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity)
                || !EntityManager.HasComponent<Game.Routes.AccessLane>(entity)) return null;
            var data = EntityManager.GetComponentData<Game.Routes.AccessLane>(entity);
            return new { lane = DescribeTransitLane(data.m_Lane), curvePosition = data.m_CurvePos };
        }

        // TakeoffLocation 也承载原生步行入口弧；名称不能作为只适用于航空的判断依据。
        private object DescribeNativeTakeoffLocation(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity)
                || !EntityManager.HasComponent<Game.Routes.TakeoffLocation>(entity)) return null;
            var data = EntityManager.GetComponentData<Game.Routes.TakeoffLocation>(entity);
            return new { accessRestriction = Ref(data.m_AccessRestriction), flags = data.m_Flags.ToString() };
        }

        // 道路上的建筑不一定使用 Attached；同时读取 Building 道路关联及原生子物体，避免把模型当作完整路径。
        private object DescribeNativeObjectConnections(Entity entity)
        {
            object buildingAccess = null;
            if (EntityManager.HasComponent<Game.Buildings.Building>(entity)) {
                var data = EntityManager.GetComponentData<Game.Buildings.Building>(entity);
                buildingAccess = new { road = Ref(data.m_RoadEdge), roadPrefab = PrefabNameOf(data.m_RoadEdge),
                    curvePosition = data.m_CurvePosition, flags = data.m_Flags.ToString(), optionMask = data.m_OptionMask };
            }
            var children = new List<object>();
            int count = 0;
            if (EntityManager.HasBuffer<Game.Objects.SubObject>(entity)) {
                var buffer = EntityManager.GetBuffer<Game.Objects.SubObject>(entity, true);
                count = buffer.Length;
                for (int i = 0; i < buffer.Length && i < 128; i++) {
                    Entity child = buffer[i].m_SubObject;
                    int laneCount = EntityManager.HasBuffer<Game.Net.SubLane>(child)
                        ? EntityManager.GetBuffer<Game.Net.SubLane>(child, true).Length : 0;
                    children.Add(new { entity = Ref(child), prefab = PrefabNameOf(child), subLaneCount = laneCount });
                }
            }
            var spawnLocations = new List<object>();
            int spawnCount = 0;
            if (EntityManager.HasBuffer<Game.Buildings.SpawnLocationElement>(entity)) {
                var buffer = EntityManager.GetBuffer<Game.Buildings.SpawnLocationElement>(entity, true);
                spawnCount = buffer.Length;
                for (int i = 0; i < buffer.Length && i < 128; i++) {
                    Entity location = buffer[i].m_SpawnLocation;
                    bool exists = location != Entity.Null && EntityManager.Exists(location);
                    object position = exists && EntityManager.HasComponent<Game.Objects.Transform>(location)
                        ? InterchangePoint(EntityManager.GetComponentData<Game.Objects.Transform>(location).m_Position) : null;
                    spawnLocations.Add(new { entity = Ref(location), type = buffer[i].m_Type.ToString(), exists,
                        prefab = exists ? PrefabNameOf(location) : null, position,
                        connection = DescribeNativeSpawnLocation(location) });
                }
            }
            object routeConnectionQualification = null;
            if (EntityManager.HasComponent<Game.Prefabs.PrefabRef>(entity)) {
                Entity prefab = EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(entity).m_Prefab;
                if (prefab != Entity.Null && EntityManager.Exists(prefab))
                    routeConnectionQualification = DescribeRouteConnection(prefab);
            }
            return new { buildingAccess, attachment = DescribeNativeAttachment(entity),
                spawnLocation = DescribeNativeSpawnLocation(entity), accessLane = DescribeNativeAccessLane(entity),
                routeLane = DescribeRouteLane(entity), takeoffLocation = DescribeNativeTakeoffLocation(entity),
                routeConnectionQualification,
                laneConnection = DescribeNativeLaneConnection(entity), utilityPorts = DescribeNativeUtilityPorts(entity),
                spawnLocationCount = spawnCount, spawnLocations, spawnLocationsComplete = spawnLocations.Count == spawnCount,
                subObjectCount = count, subObjects = children,
                subObjectsComplete = children.Count == count,
                note = "native associations only; verify child and road lanes to prove pedestrian or vehicle connectivity" };
        }

        // 每次请求只扫描一次原生停放车辆；不逐车道重复查全城，也不把空闲长度换算成虚构泊位数。
        private object DescribeParkingOccupancy(Entity entity)
        {
            var lanes = new HashSet<Entity>();
            var visited = new HashSet<Entity>();
            var pending = new Queue<Entity>();
            pending.Enqueue(entity);
            bool complete = true;
            while (pending.Count > 0 && visited.Count < 256) {
                Entity current = pending.Dequeue();
                if (!visited.Add(current) || !EntityManager.Exists(current)) continue;
                if (EntityManager.HasComponent<Game.Net.ParkingLane>(current)) lanes.Add(current);
                if (EntityManager.HasBuffer<Game.Net.SubLane>(current))
                    foreach (var lane in EntityManager.GetBuffer<Game.Net.SubLane>(current, true))
                        if (EntityManager.HasComponent<Game.Net.ParkingLane>(lane.m_SubLane)) lanes.Add(lane.m_SubLane);
                if (EntityManager.HasBuffer<Game.Net.SubNet>(current))
                    foreach (var net in EntityManager.GetBuffer<Game.Net.SubNet>(current, true)) pending.Enqueue(net.m_SubNet);
                if (EntityManager.HasBuffer<Game.Objects.SubObject>(current))
                    foreach (var child in EntityManager.GetBuffer<Game.Objects.SubObject>(current, true)) pending.Enqueue(child.m_SubObject);
            }
            complete = pending.Count == 0;
            var counts = new Dictionary<Entity, int>();
            var samples = new Dictionary<Entity, List<object>>();
            if (lanes.Count > 0) {
                using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Vehicles.ParkedCar>()))
                using (var cars = query.ToEntityArray(Allocator.Temp))
                    foreach (Entity car in cars) {
                        if (EntityManager.HasComponent<Game.Common.Deleted>(car) || EntityManager.HasComponent<Game.Tools.Temp>(car)) continue;
                        var data = EntityManager.GetComponentData<Game.Vehicles.ParkedCar>(car);
                        if (!lanes.Contains(data.m_Lane)) continue;
                        counts.TryGetValue(data.m_Lane, out int count);
                        counts[data.m_Lane] = count + 1;
                        if (!samples.TryGetValue(data.m_Lane, out var list)) samples[data.m_Lane] = list = new List<object>();
                        if (list.Count < 10) list.Add(new { entity = Ref(car), prefab = PrefabNameOf(car), curvePosition = data.m_CurvePosition });
                    }
            }
            var rows = new List<object>();
            int total = 0;
            int physical = 0, virtualLanes = 0, enabledPhysical = 0;
            foreach (Entity lane in lanes) {
                var data = EntityManager.GetComponentData<Game.Net.ParkingLane>(lane);
                bool isVirtual = (data.m_Flags & Game.Net.ParkingLaneFlags.VirtualLane) != 0;
                bool disabled = (data.m_Flags & Game.Net.ParkingLaneFlags.ParkingDisabled) != 0;
                if (isVirtual) virtualLanes++; else { physical++; if (!disabled) enabledPhysical++; }
                counts.TryGetValue(lane, out int count);
                total += count;
                samples.TryGetValue(lane, out var sample);
                rows.Add(new { lane = Ref(lane), prefab = PrefabNameOf(lane), virtualLane = isVirtual,
                    parkingDisabled = disabled, flags = data.m_Flags.ToString(), parkedCars = count, sample });
            }
            return new { parkingLanes = lanes.Count, physicalParkingLanes = physical, enabledPhysicalParkingLanes = enabledPhysical,
                virtualParkingLanes = virtualLanes, parkedCars = total, childTraversalComplete = complete,
                childEntitiesInspected = visited.Count, lanes = rows,
                note = "parkingLanes是原始组件数，VirtualLane上下客连接不是实停车道；非虚拟且未禁用也不代表泊位容量。真实ParkedCar全部保留，子物体遍历有界。" };
        }
    }
}
