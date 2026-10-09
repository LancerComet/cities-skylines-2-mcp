using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class BridgeToolSystem
    {
        private BrushDefinition m_PendingTerrainBrush;
        private Entity m_TerrainBrushPrefab;
        private int m_TerrainPasses;
        private int m_TerrainAppliedPasses;

        /// <summary>有限次原生笔触，交给 GenerateBrushes/ApplyBrushes/TerrainSystem 更新地形及水体。</summary>
        public bool TryQueueTerrain(Entity toolEntity, PrefabBase toolPrefab, Entity brushEntity,
            BrushDefinition definition, int passes, BridgeRequest request)
        {
            if (IsBusy) return false;
            m_PendingKind = OperationKind.Terrain;
            m_PendingPrefabEntity = toolEntity;
            m_PendingPrefab = toolPrefab;
            m_TerrainBrushPrefab = brushEntity;
            m_PendingTerrainBrush = definition;
            m_TerrainPasses = passes;
            m_TerrainAppliedPasses = 0;
            m_PendingRequest = request;
            Activate();
            return true;
        }

        private void CreateTerrainDefinition()
        {
            var buffer = m_ToolOutputBarrier.CreateCommandBuffer();
            Entity entity = buffer.CreateEntity();
            buffer.AddComponent(entity, new CreationDefinition { m_Prefab = m_TerrainBrushPrefab });
            var brush = m_PendingTerrainBrush;
            brush.m_Time = UnityEngine.Time.deltaTime;
            buffer.AddComponent(entity, brush);
            buffer.AddComponent<Updated>(entity);
        }
    }
}
