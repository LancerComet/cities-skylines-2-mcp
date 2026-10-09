// Only HTTP /ping references these types. No gameplay behavior is emulated.
namespace Game.SceneFlow
{
    internal sealed class GameManager
    {
        internal static GameManager instance => null;
        internal string gameMode => "Unknown";
        internal bool isGameLoading => false;
    }
}

namespace CS2MCP
{
    internal static class Mod
    {
        internal const string Name = "CS2MCP.Diagnostics";
        internal const string Version = "test";
        internal static Logger Log { get; } = new Logger();
        internal sealed class Logger { internal void Warn(string message) { } }
    }
}
