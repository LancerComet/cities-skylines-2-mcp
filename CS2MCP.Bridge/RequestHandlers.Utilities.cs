using System.Collections.Generic;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private BridgeResponse GetOutsideConnections()
        {
            var rows = new List<object>();
            using (var query = EntityManager.CreateEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Game.Objects.OutsideConnection>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() }
            }))
            using (var entities = query.ToEntityArray(Allocator.Temp)) {
                if (entities.Length > 512) return BridgeResponse.Error(503, "outside connection count exceeds the complete inspection limit 512");
                foreach (var entity in entities) {
                    var prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    Entity owner = EntityManager.HasComponent<Game.Common.Owner>(entity)
                        ? EntityManager.GetComponentData<Game.Common.Owner>(entity).m_Owner : Entity.Null;
                    string transportTypes = EntityManager.HasComponent<OutsideConnectionData>(prefab)
                        ? EntityManager.GetComponentData<OutsideConnectionData>(prefab).m_Type.ToString() : null;
                    object position = null;
                    if (owner != Entity.Null && EntityManager.HasComponent<Game.Net.Node>(owner)) {
                        var p = EntityManager.GetComponentData<Game.Net.Node>(owner).m_Position;
                        position = new { x = p.x, y = p.y, z = p.z };
                    } else if (EntityManager.HasComponent<Game.Objects.Transform>(entity)) {
                        var p = EntityManager.GetComponentData<Game.Objects.Transform>(entity).m_Position;
                        position = new { x = p.x, y = p.y, z = p.z };
                    }
                    var edges = new List<object>();
                    int count = 0;
                    if (owner != Entity.Null && EntityManager.HasBuffer<Game.Net.ConnectedEdge>(owner)) {
                        var b = EntityManager.GetBuffer<Game.Net.ConnectedEdge>(owner, true); count = b.Length;
                        if (count > 32) return BridgeResponse.Error(503, "outside port connected edge count exceeds complete limit 32");
                        foreach (var edge in b) edges.Add(new { entity = Ref(edge.m_Edge), prefab = PrefabNameOf(edge.m_Edge) });
                    }
                    rows.Add(new { entity = Ref(entity), prefab = PrefabNameOf(entity),
                        name = World.GetOrCreateSystemManaged<Game.UI.NameSystem>().GetRenderedLabelName(entity), transportTypes,
                        electricity = EntityManager.HasComponent<Game.Objects.ElectricityOutsideConnection>(entity),
                        waterAndSewage = EntityManager.HasComponent<Game.Objects.WaterPipeOutsideConnection>(entity),
                        owner = owner == Entity.Null ? null : Ref(owner), position, connectedEdges = edges, connectedEdgeCount = count });
                }
            }
            return BridgeResponse.Json(new { connections = rows, complete = true,
                note = "native physical outside markers and owner network nodes; existence does not prove an operating passenger/cargo line or completed utility path" });
        }

        // 与原生信息面板同源；外部连接是节点的子物体，不能只查路网节点上的 OutsideConnection。
        private BridgeResponse GetUtilityOverview()
        {
            var power = World.GetOrCreateSystemManaged<ElectricityStatisticsSystem>();
            var powerTrade = World.GetOrCreateSystemManaged<ElectricityTradeSystem>();
            var water = World.GetOrCreateSystemManaged<WaterStatisticsSystem>();
            var waterTrade = World.GetOrCreateSystemManaged<WaterTradeSystem>();
            var ports = new List<object>();
            using (var query = EntityManager.CreateEntityQuery(new EntityQueryDesc {
                Any = new[] { ComponentType.ReadOnly<Game.Objects.ElectricityOutsideConnection>(),
                    ComponentType.ReadOnly<Game.Objects.WaterPipeOutsideConnection>() },
                None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() }
            }))
            using (var entities = query.ToEntityArray(Allocator.Temp)) {
                if (entities.Length > 256) return BridgeResponse.Error(503, "utility outside port count exceeds the complete inspection limit 256");
                foreach (var entity in entities) {
                    Entity owner = EntityManager.HasComponent<Game.Common.Owner>(entity)
                        ? EntityManager.GetComponentData<Game.Common.Owner>(entity).m_Owner : Entity.Null;
                    object position = null;
                    if (owner != Entity.Null && EntityManager.HasComponent<Game.Net.Node>(owner)) {
                        var p = EntityManager.GetComponentData<Game.Net.Node>(owner).m_Position;
                        position = new { x = p.x, y = p.y, z = p.z };
                    } else if (EntityManager.HasComponent<Game.Objects.Transform>(entity)) {
                        var p = EntityManager.GetComponentData<Game.Objects.Transform>(entity).m_Position;
                        position = new { x = p.x, y = p.y, z = p.z };
                    }
                    ports.Add(new { entity = Ref(entity), prefab = PrefabNameOf(entity),
                        electricity = EntityManager.HasComponent<Game.Objects.ElectricityOutsideConnection>(entity),
                        waterAndSewage = EntityManager.HasComponent<Game.Objects.WaterPipeOutsideConnection>(entity),
                        owner = owner == Entity.Null ? null : Ref(owner), position,
                        utilities = owner != Entity.Null && EntityManager.Exists(owner) ? DescribeUtilityState(owner) : null });
                }
            }
            object prices = null;
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<OutsideTradeParameterData>())) {
                if (query.CalculateEntityCount() == 1) {
                    var p = query.GetSingleton<OutsideTradeParameterData>();
                    prices = new { electricityImport = p.m_ElectricityImportPrice, electricityExport = p.m_ElectricityExportPrice,
                        freshWaterImport = p.m_WaterImportPrice, freshWaterExport = p.m_WaterExportPrice,
                        freshWaterPollutionTolerance = p.m_WaterExportPollutionTolerance,
                        sewageExportCost = p.m_SewageExportPrice,
                        note = "native trade prices; sewage export is a paid disposal service, not sales revenue" };
                }
            }
            return BridgeResponse.Json(new {
                electricity = new { production = power.production, consumption = power.consumption,
                    fulfilled = power.fulfilledConsumption, export = powerTrade.export, import = powerTrade.import,
                    batteryCharge = power.batteryCharge, batteryCapacity = power.batteryCapacity },
                freshWater = new { capacity = water.freshCapacity, consumption = water.freshConsumption,
                    fulfilled = water.fulfilledFreshConsumption, export = waterTrade.freshExport, import = waterTrade.freshImport },
                sewage = new { capacity = water.sewageCapacity, consumption = water.sewageConsumption,
                    fulfilled = water.fulfilledSewageConsumption, exportedForPaidDisposal = waterTrade.sewageExport },
                prices, ports, portsComplete = true,
                units = new { electricityRawPerMW = 10000, water = "native volumePerMonth, cubic meters per game month",
                    rawGraphCapacitySentinel = 1073741823,
                    note = "original fields retain native values; 1073741823 is the native effectively-unbounded flow capacity sentinel, not a promised operating throughput" },
                note = "read-only; sum of connections is not proven network throughput. Inspect shared edges, transformers, actual flows and fulfilled demand. Water currently has no sewage-import sales mechanism."
            });
        }

        // 网络图与建筑端口一起读取；零流量不等于无作用，备用线路仍需拓扑判断。
        private object DescribeUtilityPrefab(Entity prefab)
        {
            object electricity = null, water = null;
            if (EntityManager.HasComponent<ElectricityConnectionData>(prefab))
            {
                var d = EntityManager.GetComponentData<ElectricityConnectionData>(prefab);
                electricity = new { capacity = d.m_Capacity, voltage = d.m_Voltage.ToString(), direction = d.m_Direction.ToString(),
                    compositionAll = DescribeCompositionFlags(d.m_CompositionAll), compositionAny = DescribeCompositionFlags(d.m_CompositionAny), compositionNone = DescribeCompositionFlags(d.m_CompositionNone) };
            }
            if (EntityManager.HasComponent<WaterPipeConnectionData>(prefab))
            {
                var d = EntityManager.GetComponentData<WaterPipeConnectionData>(prefab);
                water = new { freshCapacity = d.m_FreshCapacity, sewageCapacity = d.m_SewageCapacity, stormCapacity = d.m_StormCapacity };
            }
            return new { electricity, water, note = "native prefab components; absence here does not prove absence on generated road composition; inspect an actual road for live flow graph" };
        }

        private static object DescribeCompositionFlags(CompositionFlags flags) => new
        {
            general = flags.m_General.ToString(), left = flags.m_Left.ToString(), right = flags.m_Right.ToString()
        };

        private object DescribeElectricityEdge(Entity edge)
        {
            if (edge == Entity.Null || !EntityManager.Exists(edge) || !EntityManager.HasComponent<ElectricityFlowEdge>(edge)) return null;
            var d = EntityManager.GetComponentData<ElectricityFlowEdge>(edge);
            return new { entity = Ref(edge), start = Ref(d.m_Start), end = Ref(d.m_End), capacity = d.m_Capacity, flow = d.m_Flow,
                direction = d.direction.ToString(), d.isBottleneck, d.isBeyondBottleneck, d.isDisconnected, flags = d.m_Flags.ToString() };
        }

        private object DescribeWaterEdge(Entity edge)
        {
            if (edge == Entity.Null || !EntityManager.Exists(edge) || !EntityManager.HasComponent<WaterPipeEdge>(edge)) return null;
            var d = EntityManager.GetComponentData<WaterPipeEdge>(edge);
            return new { entity = Ref(edge), start = Ref(d.m_Start), end = Ref(d.m_End), freshCapacity = d.m_FreshCapacity,
                sewageCapacity = d.m_SewageCapacity, freshFlow = d.m_FreshFlow, sewageFlow = d.m_SewageFlow,
                freshPollution = d.m_FreshPollution, flags = d.m_Flags.ToString() };
        }

        private object DescribeUtilityFlowNode(Entity node, bool electricity)
        {
            if (node == Entity.Null || !EntityManager.Exists(node)) return null;
            var edges = new List<object>();
            int count = 0;
            if (EntityManager.HasBuffer<ConnectedFlowEdge>(node))
            {
                var buffer = EntityManager.GetBuffer<ConnectedFlowEdge>(node, true);
                count = buffer.Length;
                for (int i = 0; i < count && i < 32; i++)
                    edges.Add(electricity ? DescribeElectricityEdge(buffer[i].m_Edge) : DescribeWaterEdge(buffer[i].m_Edge));
            }
            return new { entity = Ref(node), edges, edgeCount = count, edgesComplete = count <= 32 };
        }

        private object DescribeUtilityState(Entity entity)
        {
            var result = new Dictionary<string, object>();
            if (EntityManager.HasComponent<Game.Buildings.ElectricityConsumer>(entity))
            {
                var d = EntityManager.GetComponentData<Game.Buildings.ElectricityConsumer>(entity);
                result["electricityConsumer"] = new { wanted = d.m_WantedConsumption, fulfilled = d.m_FulfilledConsumption, d.electricityConnected, flags = d.m_Flags.ToString() };
            }
            if (EntityManager.HasComponent<Game.Buildings.ElectricityProducer>(entity))
            {
                var d = EntityManager.GetComponentData<Game.Buildings.ElectricityProducer>(entity);
                result["electricityProducer"] = new { capacity = d.m_Capacity, lastProduction = d.m_LastProduction };
            }
            if (EntityManager.HasComponent<Game.Buildings.Battery>(entity))
            {
                var d = EntityManager.GetComponentData<Game.Buildings.Battery>(entity);
                result["battery"] = new { storedEnergy = d.m_StoredEnergy, capacity = d.m_Capacity, lastFlow = d.m_LastFlow,
                    note = "raw storedEnergy is not directly comparable to capacity; native storedEnergyHours converts by 85", storedEnergyHours = d.storedEnergyHours };
            }
            if (EntityManager.HasComponent<Game.Buildings.WaterConsumer>(entity))
            {
                var d = EntityManager.GetComponentData<Game.Buildings.WaterConsumer>(entity);
                result["waterConsumer"] = new { wanted = d.m_WantedConsumption, freshFulfilled = d.m_FulfilledFresh,
                    sewageFulfilled = d.m_FulfilledSewage, d.waterConnected, d.sewageConnected, pollution = d.m_Pollution, flags = d.m_Flags.ToString() };
            }
            if (EntityManager.HasComponent<ElectricityNodeConnection>(entity))
                result["electricityGraph"] = DescribeUtilityFlowNode(EntityManager.GetComponentData<ElectricityNodeConnection>(entity).m_ElectricityNode, true);
            if (EntityManager.HasComponent<WaterPipeNodeConnection>(entity))
                result["waterGraph"] = DescribeUtilityFlowNode(EntityManager.GetComponentData<WaterPipeNodeConnection>(entity).m_WaterPipeNode, false);
            if (EntityManager.HasComponent<ElectricityBuildingConnection>(entity))
            {
                var d = EntityManager.GetComponentData<ElectricityBuildingConnection>(entity);
                result["electricityBuildingGraph"] = new { transformer = DescribeUtilityFlowNode(d.m_TransformerNode, true),
                    producer = DescribeElectricityEdge(d.m_ProducerEdge), consumer = DescribeElectricityEdge(d.m_ConsumerEdge),
                    charge = DescribeElectricityEdge(d.m_ChargeEdge), discharge = DescribeElectricityEdge(d.m_DischargeEdge) };
            }
            if (EntityManager.HasComponent<WaterPipeBuildingConnection>(entity))
            {
                var d = EntityManager.GetComponentData<WaterPipeBuildingConnection>(entity);
                result["waterBuildingGraph"] = new { producer = DescribeWaterEdge(d.m_ProducerEdge), consumer = DescribeWaterEdge(d.m_ConsumerEdge) };
            }
            if (EntityManager.HasComponent<Game.Net.Composition>(entity))
            {
                var c = EntityManager.GetComponentData<Game.Net.Composition>(entity);
                result["roadCompositionUtilities"] = new { edge = DescribeUtilityPrefab(c.m_Edge), start = DescribeUtilityPrefab(c.m_StartNode), end = DescribeUtilityPrefab(c.m_EndNode) };
                if (EntityManager.HasComponent<Game.Prefabs.NetCompositionData>(c.m_Edge))
                {
                    var data = EntityManager.GetComponentData<Game.Prefabs.NetCompositionData>(c.m_Edge);
                    result["roadCompositionGeometry"] = new { width = data.m_Width,
                        flags = DescribeCompositionFlags(data.m_Flags),
                        note = "实际生成横断面宽度，含边侧空间；不能用基础道路名称推定升级后的占地或车道数" };
                }
            }
            var attachments = new List<object>();
            int attachmentCount = 0;
            if (EntityManager.HasBuffer<Game.Net.ConnectedNode>(entity))
            {
                var b = EntityManager.GetBuffer<Game.Net.ConnectedNode>(entity, true);
                attachmentCount = b.Length;
                for (int i = 0; i < b.Length && i < 64; i++)
                {
                    Entity node = b[i].m_Node;
                    if (!EntityManager.Exists(node)) continue;
                    Entity owner = EntityManager.HasComponent<Game.Common.Owner>(node) ? EntityManager.GetComponentData<Game.Common.Owner>(node).m_Owner : Entity.Null;
                    attachments.Add(new { entity = Ref(node), prefab = PrefabNameOf(node), owner = owner == Entity.Null ? null : Ref(owner), ownerPrefab = owner == Entity.Null ? null : PrefabNameOf(owner) });
                }
            }
            result["attachedNodes"] = attachments;
            result["attachedNodeCount"] = attachmentCount;
            result["attachedNodesComplete"] = attachmentCount <= 64;
            if (EntityManager.HasComponent<Game.Tools.LocalTransformCache>(entity))
                result["parentMesh"] = EntityManager.GetComponentData<Game.Tools.LocalTransformCache>(entity).m_ParentMesh;
            result["note"] = "read-only native utility graph, capacities and flow in raw native units; inspect building ports and connected graph before retiring a line, zero instantaneous flow alone is not evidence of redundancy";
            return result;
        }
    }
}
