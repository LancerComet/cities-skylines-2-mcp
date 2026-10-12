using System;
using System.Linq;
using Game.Net;
using Newtonsoft.Json.Linq;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private BridgeResponse ActTraffic(string action, JObject data)
        {
            OptionalModApi.Assembly("Traffic");
            if (new[] { "previewConnection", "previewPriority", "apply", "cancel" }.Contains(action)) return BridgeModToolSystem.Instance.HandleTraffic(action, data);
            OptionalModApi.Keys(data, "entity", "dryRun");
            bool prepare = action == "prepareConnection" || action == "preparePriority";
            if (!prepare && !new[] { "removeUnsafe", "removeUTurns", "resetConnections", "resetPriorities" }.Contains(action)) throw new ArgumentException("unknown Traffic action");
            var node = ModEntity(data["entity"], true);
            if (!EntityManager.HasBuffer<ConnectedEdge>(node) || EntityManager.GetBuffer<ConnectedEdge>(node, true).Length < 2) throw new ArgumentException("connected intersection required");
            if (!prepare && (bool?)data["dryRun"] != false) return BridgeResponse.Json(new { applied = false, action, node = OptionalModApi.JsonValue(node), before = ModEntityComponents(node, "Traffic.Components."), note = "只检查身份与操作范围；真正应用由Traffic原生工具生成临时连接、校验与应用" });
            if (TransitToolsBusy(out var busy)) return busy;
            return BridgeModToolSystem.Instance.StartTraffic(action, node);
        }
    }
}
