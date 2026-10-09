using System.Collections.Generic;
using Game.Prefabs;
using Game.Simulation;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
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
