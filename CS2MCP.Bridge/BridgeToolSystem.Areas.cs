using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class BridgeToolSystem
    {
        private Entity m_ExtractorAreaTarget, m_ExtractorAreaOwner;
        private Game.Areas.Node[] m_ExtractorAreaNodes;
        private LocalNodeCache[] m_ExtractorAreaCache;

        public bool TryQueueExtractorAreaEdit(Entity area, Entity owner, Entity prefabEntity, PrefabBase prefab,
            Game.Areas.Node[] nodes, LocalNodeCache[] cache, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle) return false;
            m_PendingKind = OperationKind.ExtractorAreaEdit;
            m_ExtractorAreaTarget = area; m_ExtractorAreaOwner = owner; m_ExtractorAreaNodes = nodes; m_ExtractorAreaCache = cache;
            m_PendingPrefabEntity = prefabEntity; m_PendingPrefab = prefab; m_PendingRequest = request;
            Activate(); return true;
        }

        private void CreateExtractorAreaEditDefinition()
        {
            var commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            Entity definition = commandBuffer.CreateEntity();
            commandBuffer.AddComponent(definition, new CreationDefinition { m_Original = m_ExtractorAreaTarget,
                m_Prefab = m_PendingPrefabEntity, m_Owner = m_ExtractorAreaOwner, m_Flags = CreationFlags.Relocate });
            commandBuffer.AddBuffer<Game.Areas.Node>(definition).CopyFrom(m_ExtractorAreaNodes);
            if (m_ExtractorAreaCache != null) commandBuffer.AddBuffer<LocalNodeCache>(definition).CopyFrom(m_ExtractorAreaCache);
            commandBuffer.AddComponent(definition, default(Updated));
        }

        private void CompleteExtractorAreaEdit()
        {
            if (m_PendingRequest == null) return;
            bool verified = EntityManager.Exists(m_ExtractorAreaTarget) && !EntityManager.HasComponent<Deleted>(m_ExtractorAreaTarget)
                && EntityManager.HasBuffer<Game.Areas.Node>(m_ExtractorAreaTarget)
                && EntityManager.HasComponent<Owner>(m_ExtractorAreaTarget)
                && EntityManager.GetComponentData<Owner>(m_ExtractorAreaTarget).m_Owner == m_ExtractorAreaOwner;
            int count = verified ? EntityManager.GetBuffer<Game.Areas.Node>(m_ExtractorAreaTarget, true).Length : 0;
            verified &= count == m_ExtractorAreaNodes.Length;
            if (verified)
            {
                var nodes = EntityManager.GetBuffer<Game.Areas.Node>(m_ExtractorAreaTarget, true);
                for (int i = 0; i < count; i++)
                    if (Unity.Mathematics.math.distance(nodes[i].m_Position.xz, m_ExtractorAreaNodes[i].m_Position.xz) > .01f) verified = false;
                for (int i = 0; i < 2; i++)
                    if (Unity.Mathematics.math.distance(nodes[i].m_Position, m_ExtractorAreaNodes[i].m_Position) > .01f
                        || nodes[i].m_Elevation != m_ExtractorAreaNodes[i].m_Elevation) verified = false;
            }
            bool cacheVerified = m_ExtractorAreaCache == null ? !EntityManager.HasBuffer<LocalNodeCache>(m_ExtractorAreaTarget)
                : EntityManager.HasBuffer<LocalNodeCache>(m_ExtractorAreaTarget);
            if (cacheVerified && m_ExtractorAreaCache != null)
            {
                var cache = EntityManager.GetBuffer<LocalNodeCache>(m_ExtractorAreaTarget, true);
                cacheVerified = cache.Length == m_ExtractorAreaCache.Length;
                if (cacheVerified) for (int i = 0; i < cache.Length; i++)
                    if (cache[i].m_ParentMesh != m_ExtractorAreaCache[i].m_ParentMesh
                        || Unity.Mathematics.math.distance(cache[i].m_Position, m_ExtractorAreaCache[i].m_Position) > .01f) cacheVerified = false;
            }
            verified &= cacheVerified;
            CompletePending(BridgeResponse.Json(new { accepted = true, edited = verified, verified,
                cacheVerified,
                area = new { index = m_ExtractorAreaTarget.Index, version = m_ExtractorAreaTarget.Version },
                owner = new { index = m_ExtractorAreaOwner.Index, version = m_ExtractorAreaOwner.Version }, nodes = count,
                note = "native preview/validation/application completed; boundary and owner checked after apply, not proof of resource sufficiency or operating company; re-read area/building details after simulation updates; do not blindly replay if verified is false" }));
        }
    }
}
