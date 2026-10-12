using System;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class BridgeToolSystem
    {
        private string m_StopMoveContract;
        private Func<string> m_StopMoveReadContract;
        private Transform m_StopMoveOriginal;
        private bool m_StopMoveApplied;
        private bool m_StopMoveDryRun;
        private bool m_StopMoveAllowed;
        private int m_StopMoveCost;
        private int m_StopMoveClearRounds;
        private object m_StopMoveErrors;
        private object m_StopMovePreview;

        /// <summary>复用原生original定义，不改活站Transform、Attached、线路或车队缓冲。</summary>
        public bool TryQueueTransitStopMove(Entity stop, Entity prefabEntity, PrefabBase prefab, ControlPoint point,
            string contract, Func<string> readContract, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle) return false;
            m_PendingKind = OperationKind.TransitStopMove;
            m_PendingTarget = stop;
            m_PendingPrefabEntity = prefabEntity;
            m_PendingPrefab = prefab;
            m_PendingAttachedPoint = point;
            m_PendingPosition = point.m_Position;
            m_PendingRotation = point.m_Rotation;
            m_PendingRequest = request;
            m_StopMoveContract = contract;
            m_StopMoveReadContract = readContract;
            m_StopMoveOriginal = EntityManager.GetComponentData<Transform>(stop);
            m_StopMoveApplied = false;
            m_StopMoveClearRounds = 0;
            Activate();
            return true;
        }

        private void ApplyTransitStopMovePreview()
        {
            m_StopMoveDryRun = !m_PendingRequest.TryGetBool("dryRun", out bool dryRun) || dryRun;
            m_StopMoveAllowed = GetAllowApply();
            m_StopMoveCost = NativePreviewCost();
            m_StopMoveErrors = CollectPlacementErrorLocations();
            m_StopMovePreview = null;
            using (var entities = m_PreviewCostQuery.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities)
                    if (EntityManager.GetComponentData<Temp>(entity).m_Original == m_PendingTarget
                        && EntityManager.HasComponent<Game.Routes.TransportStop>(entity))
                    { m_StopMovePreview = DescribePreviewOwner(entity); break; }
            // 原站引用的预览对象缺失时绝不把新建对象当迁站应用。
            m_StopMoveAllowed &= m_StopMovePreview != null;
            string currentGuid = World.GetExistingSystemManaged<Game.Serialization.LoadGameSystem>()?.context.instigatorGuid.ToString();
            m_StopMoveAllowed &= m_PendingRequest.Query.TryGetValue("expectedLoadedSaveGuid", out string expectedGuid)
                && string.Equals(currentGuid, expectedGuid, StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(m_StopMoveContract, m_StopMoveReadContract(), StringComparison.Ordinal))
                m_StopMoveAllowed = false;
            m_StopMoveApplied = !m_StopMoveDryRun && m_StopMoveAllowed;
            applyMode = m_StopMoveApplied ? ApplyMode.Apply : ApplyMode.Clear;
        }

        /// <summary>等原生清除/应用后回读旧站与现线；未知或不一致明确禁止重放。</summary>
        private bool CompleteTransitStopMove()
        {
            applyMode = ApplyMode.None;
            int relatedTemp = 0;
            using (var entities = m_PreviewCostQuery.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities) {
                    Entity original = EntityManager.GetComponentData<Temp>(entity).m_Original;
                    if (original == m_PendingTarget || original == m_PendingAttachedPoint.m_OriginalEntity) relatedTemp++;
                }
            if (relatedTemp > 0 && ++m_StopMoveClearRounds < 8) return false;
            bool exists = EntityManager.Exists(m_PendingTarget) && !EntityManager.HasComponent<Deleted>(m_PendingTarget)
                && !EntityManager.HasComponent<Temp>(m_PendingTarget) && EntityManager.HasComponent<Transform>(m_PendingTarget)
                && EntityManager.HasComponent<Game.Routes.TransportStop>(m_PendingTarget) && EntityManager.HasComponent<Attached>(m_PendingTarget);
            var actual = exists ? EntityManager.GetComponentData<Transform>(m_PendingTarget) : default;
            var attached = exists ? EntityManager.GetComponentData<Attached>(m_PendingTarget) : default;
            bool pose = exists && (m_StopMoveApplied
                ? math.distance(actual.m_Position.xz, m_PendingPosition.xz) <= 4f
                    && math.abs(math.dot(actual.m_Rotation.value, m_StopMoveOriginal.m_Rotation.value)) >= .9f
                    && attached.m_Parent == m_PendingAttachedPoint.m_OriginalEntity
                : math.distance(actual.m_Position, m_StopMoveOriginal.m_Position) < .01f
                    && math.abs(math.dot(actual.m_Rotation.value, m_StopMoveOriginal.m_Rotation.value)) >= .99999f
                    && attached.m_Parent == m_PendingAttachedPoint.m_OriginalEntity);
            string after = m_StopMoveReadContract();
            bool lines = string.Equals(m_StopMoveContract, after, StringComparison.Ordinal);
            bool verified = pose && lines && relatedTemp == 0;
            bool rejected = !m_StopMoveDryRun && !m_StopMoveAllowed;
            CompletePending(BridgeResponse.Json(new {
                error = !verified ? "native stop relocation readback/cleanup differs; refresh stop and all serving lines before any further action"
                    : rejected ? "native stop relocation validation rejected; old stop and line contract verified unchanged" : null,
                dryRun = m_StopMoveDryRun, allowed = m_StopMoveAllowed, applied = m_StopMoveApplied,
                mayHaveApplied = m_StopMoveApplied, safeToRetry = false,
                stop = new { index = m_PendingTarget.Index, version = m_PendingTarget.Version }, prefab = m_PendingPrefab.name,
                actual = exists ? DescribePreviewOwner(m_PendingTarget) : null,
                nativePreview = m_StopMovePreview, nativePreviewCost = m_StopMoveCost, validationErrors = m_StopMoveErrors,
                oldStopPreserved = exists, poseVerified = pose, servingLineContractPreserved = lines,
                relatedTempRemaining = relatedTemp,
                beforeLineContract = JArray.Parse(m_StopMoveContract), afterLineContract = JArray.Parse(after),
                note = "完整原生Relocate预览/正常费用与应用，不新建或重建线；仅同路同侧。实际路径更新、到站上下客及步行运营仍须后续核验。"
            }, !verified ? 409 : rejected ? 409 : 200));
            return true;
        }
    }
}
