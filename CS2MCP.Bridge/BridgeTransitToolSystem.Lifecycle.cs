using System;
using Game.Tools;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class BridgeTransitToolSystem
    {
        private enum Stage { Idle, CreateDefinitions, WaitForPaths, Apply, Finish }
        private enum OperationKind { Stop, Line }

        /// <summary>UI阶段也核归属，覆盖首次ToolUpdate前被替换而收不到OnStopRunning的请求。</summary>
        public void ReconcileToolOwnership()
        {
            if (m_Stage == Stage.Idle || m_ToolSystem.activeTool == this) return;
            applyMode = ApplyMode.Clear;
            CompletePending(TransitFailure(409,
                "transit operation interrupted because another tool became active; inspect the target before continuing",
                "transit_tool_interrupted"));
            Deactivate();
        }

        private BridgeResponse TransitFailure(int status, string error, string code, bool forceUnknown = false)
        {
            return BridgeResponse.Json(new {
                error, code, failurePhase = code == "transit_tool_interrupted" ? "tool_interrupted"
                    : code == "transit_commit_unverified" ? "commit_readback" : "tool_failed",
                operation = m_PendingKind.ToString(), stage = m_Stage.ToString(),
                target = m_ResultEntity.Index > 0 ? new { index = m_ResultEntity.Index, version = m_ResultEntity.Version } : null,
                mayHaveApplied = forceUnknown || m_Applied, safeToRetry = false,
            }, status);
        }

        private void CompletePending(BridgeResponse response)
        {
            var request = m_PendingRequest;
            m_PendingRequest = null;
            request?.Complete(response);
        }

        private void Deactivate()
        {
            var previous = m_PreviousTool;
            m_Stage = Stage.Idle;
            m_PendingRequest = null;
            m_PendingPrefab = null;
            m_PendingPrefabEntity = Entity.Null;
            m_LineStops = null;
            m_LineName = m_PendingTypeName = null;
            m_StopControlPoint = default;
            m_LineColor = default;
            m_HasLineColor = false;
            m_DepotCount = m_DefinitionRounds = 0;
            m_Deadline = default;
            m_ResultEntity = Entity.Null;
            m_Applied = false;
            m_PreviousTool = null;
            // 被玩家换走时只清自有状态；不能把旧工具重新抢回来。
            if (m_ToolSystem.activeTool == this)
                m_ToolSystem.activeTool = previous != null && previous != this ? previous : m_DefaultToolSystem;
        }
    }
}
