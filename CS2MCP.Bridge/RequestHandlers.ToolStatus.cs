using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        /// <summary>
        /// GET /build/tools/status: stage of each headless bridge tool, which one
        /// is the game's active tool, and how often the watchdog had to
        /// reactivate a tool that another tool had displaced.
        /// </summary>
        private BridgeResponse ToolStatus(BridgeRequest request)
        {
            BridgeToolWatchdogSystem watchdog = World.GetOrCreateSystemManaged<BridgeToolWatchdogSystem>();
            Game.Tools.ToolSystem toolSystem = World.GetOrCreateSystemManaged<Game.Tools.ToolSystem>();
            return BridgeResponse.Json(new
            {
                activeTool = toolSystem.activeTool?.GetType().Name,
                tools = watchdog.Describe(),
                watchdogReactivations = watchdog.Reactivations,
                lastReactivated = watchdog.LastReactivated,
            });
        }
    }
}
