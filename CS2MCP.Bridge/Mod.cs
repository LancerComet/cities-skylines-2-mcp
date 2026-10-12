using Colossal.Logging;
using Game;
using Game.Modding;

namespace CS2MCP
{
    public sealed class Mod : IMod
    {
        public const string Name = "CS2MCP";
        public const string Version = "0.10.3";

        public static readonly ILog Log = LogManager.GetLogger(Name).SetShowsErrorsInUI(false);

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info($"{Name} {Version} loading, registering bridge systems");
            updateSystem.UpdateAfter<BridgeSystem, Game.UI.InGame.TimeUISystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<BridgeToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<BridgeTransitToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<BridgeTransitRenameSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<BridgeMapTileRefreshSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<BridgeRoadToolSystem>(SystemUpdatePhase.ToolUpdate);
            // NT先完成悬停更新，桥接再恢复明确选择；随后才由原生工具输出提交。
            // 不能笼统排在ToolUpdate最后：ToolOutputBarrier此时已经关闭命令缓冲。
            updateSystem.UpdateBefore<BridgeModToolSystem, Game.Tools.ToolOutputSystem>(SystemUpdatePhase.ToolUpdate);
        }

        public void OnDispose()
        {
            Log.Info($"{Name} disposing");
            BridgeSystem.Instance?.StopServer();
        }
    }
}
