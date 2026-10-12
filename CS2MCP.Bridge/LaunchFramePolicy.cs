namespace CS2MCP
{
    /// <summary>显式本次启动模式；无模式时保留玩家设置，不根据焦点自动切换。</summary>
    internal static class LaunchFramePolicy
    {
        internal const string EnvironmentName = "CS2MCP_LAUNCH_MODE";
        internal static int? Resolve(string mode)
        {
            if (mode == "background") return 30;
            if (mode == "foreground") return 90;
            return null;
        }
    }
}
