using Colossal.Mathematics;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    internal struct RoadLayoutPart
    {
        public Entity Prefab;
        public Bezier4x3 Curve;
        public int2 Nodes;
        public int2 ParentMesh;
        public CompositionFlags Upgrades;
    }

    public sealed partial class BridgeToolSystem
    {
        private RoadLayoutPart[] m_RoadLayout;
        private Entity[] m_RoadRemovalTargets;
        /// <summary>整组原生删除定义在同一次预览提交，避免逐段删除时邻路合并使编号过期。</summary>
        internal bool TryQueueRoadRemoval(Entity[] targets, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle) return false;
            m_PendingKind = OperationKind.RoadLayoutRemoval;
            m_RoadRemovalTargets = targets;
            m_PendingTarget = targets[0];
            m_PendingRequest = request;
            Activate();
            return true;
        }
        /// <summary>原生道路定义；最终道路只引用正常预制体，不依赖临时资产或不可见母体。</summary>
        internal bool TryQueueRoadLayout(RoadLayoutPart[] parts, float3 origin, quaternion rotation, PrefabBase previewPrefab, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle) return false;
            m_PendingKind = OperationKind.RoadLayout;
            m_PendingPrefabEntity = parts[0].Prefab;
            m_RoadLayout = parts;
            m_PendingPosition = origin;
            m_PendingRotation = rotation;
            m_PendingPrefab = previewPrefab;
            m_PendingRequest = request;
            Activate();
            return true;
        }

        private void CreateRoadLayoutDefinitions()
        {
            CreateDefinitions definitions = default;
            definitions.m_CommandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            definitions.m_PrefabNetGeometryData = GetComponentLookup<NetGeometryData>(true);
            definitions.m_TerrainHeightData = m_TerrainSystem.GetHeightData();
            definitions.CreateRoadLayout(m_RoadLayout,
                new Game.Objects.Transform { m_Position = m_PendingPosition, m_Rotation = m_PendingRotation });
        }
    }

    internal partial struct CreateDefinitions
    {
        /// <summary>复用原生CreateSubNet：节点取端点均值，保留原曲线与车道偏移，统一预览/收费/应用。</summary>
        public void CreateRoadLayout(RoadLayoutPart[] parts, Game.Objects.Transform origin)
        {
            var positions = new NativeList<float4>(parts.Length * 2, Allocator.Temp);
            try
            {
                foreach (var part in parts)
                {
                    int last = math.max(part.Nodes.x, part.Nodes.y);
                    while (positions.Length <= last) positions.Add(default(float4));
                    if (part.Nodes.x >= 0) positions[part.Nodes.x] += new float4(part.Curve.a, 1f);
                    if (part.Nodes.y >= 0) positions[part.Nodes.y] += new float4(part.Curve.d, 1f);
                }
                for (int i = 0; i < positions.Length; i++) positions[i] /= math.max(1f, positions[i].w);
                var random = Game.Common.RandomSeed.Next().GetRandom(0);
                foreach (var part in parts)
                    CreateSubNet(part.Prefab, Entity.Null, part.Curve, part.Nodes, part.ParentMesh, part.Upgrades,
                        positions, origin, default(OwnerDefinition), default,
                        default(Game.Buildings.BuildingUtils.LotInfo), false, ref random, CreationFlags.SubElevation, independentLayout: true);
            }
            finally { positions.Dispose(); }
        }
    }
}
