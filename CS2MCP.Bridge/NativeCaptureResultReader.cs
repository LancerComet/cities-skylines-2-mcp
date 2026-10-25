using System;

namespace CS2MCP
{
    /// <summary>仅领取既有托管结果；可在监听线程响应，不读游戏状态、不发起渲染。</summary>
    internal static class NativeCaptureResultReader
    {
        internal static BridgeResponse Read(BridgeRequest request)
        {
            if (request.Method != "GET" || !request.Query.TryGetValue("source", out string source) || source != "gameCamera"
                || !request.Query.TryGetValue("captureId", out string id) || !Guid.TryParseExact(id, "N", out _)
                || request.Query.ContainsKey("width"))
                return BridgeResponse.Error(400, "领取取景结果仅接受source=gameCamera及完整captureId，不能同时传width");
            var state = NativeCaptureOperation.Shared.Read(id, out byte[] png);
            if (state.status == "completed" && png != null) return BridgeResponse.Png(png);
            int status = state.status == "queued" || state.status == "running" ? 202 : state.status == "not_found" ? 404 : 409;
            return BridgeResponse.Json(state, status);
        }
    }
}
