using System;
using System.Text;
using Newtonsoft.Json.Linq;

namespace CS2MCP
{
    /// <summary>入队及主线程共用；不改变原生资格、碰撞、费用或请求未知结果语义。</summary>
    internal static class RequestMemoryAdmission
    {
        internal static BridgeResponse Check(BridgeRequest request, bool enqueued)
        {
            var operation = Estimate(request);
            return operation == null ? null : Check(RuntimeMemory.Read(), operation, enqueued);
        }
        internal static MemoryOperationBudget Estimate(BridgeRequest request)
        {
            if (!(request.Path.StartsWith("/build/", StringComparison.Ordinal) || request.Path == "/transit/stops/relocate")) return null;
            if (request.Path.EndsWith("/cancel", StringComparison.Ordinal)
                || request.Path == "/build/mod/action" && request.Query.TryGetValue("action", out string action) && action == "cancel") return null;
            bool cleanup = request.Path.EndsWith("/remove", StringComparison.Ordinal) || request.Path == "/build/demolish";
            bool terrain = request.Path.IndexOf("terrain", StringComparison.Ordinal) >= 0;
            int count = 0;
            if (request.Query.TryGetValue("roads", out string roads)) count = roads.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Length;
            if (!string.IsNullOrEmpty(request.Body))
            {
                try
                {
                    var input = JObject.Parse(request.Body);
                    if (input["segments"] is JArray segments) { if (segments.Count > 250) return null; count += segments.Count; }
                    if (input["replaceRoads"] is JArray replacement) { if (replacement.Count > 250) return null; count += replacement.Count; }
                    if (input["roads"] is JArray removal) { if (removal.Count > 250) return null; count += removal.Count; }
                }
                catch { return null; } // 非法输入交给原处理器，不能用资源错误遮住400校验。
            }
            int bytes = request.Body == null ? 0 : Encoding.UTF8.GetByteCount(request.Body);
            return MemoryOperationBudget.Construction(bytes, count, cleanup, terrain);
        }
        internal static BridgeResponse Check(RuntimeMemory.Snapshot memory, MemoryOperationBudget operation, bool enqueued)
        {
            var admission = memory.Assess(operation);
            if (admission.allowed) return null;
            return BridgeResponse.Json(new
            {
                code = "resource_budget", failurePhase = enqueued ? "before_handler" : "prequeue", enqueued,
                handlerStarted = false, mayHaveApplied = false, safeToRetry = false,
                reason = admission.reason,
                error = admission.reason == "capacity_counter_unavailable"
                    ? "本次额外分配的可提交容量无法核验；请求未应用。保留游戏实例，检查计数器，读取/保存/取消仍可用。"
                    : "当前可提交或地址空间不足以承接本次操作估计。缩小本批或等待后重读现场；不因告警退出、重启或重放旧请求。",
                resourceBudget = memory, admission,
            }, 409);
        }
    }
}
