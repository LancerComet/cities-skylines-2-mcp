namespace CS2MCP
{
    // 原生HTTP边界与SDK是两个独立契约；此规则可脱离Unity验证，防止SDK接受而原生拒绝新字段。
    internal static class RoadLayoutInputRules
    {
        internal static bool IsKnownField(string name) => name == "x" || name == "z" || name == "rotation"
            || name == "elevation" || name == "dryRun" || name == "segments"
            || name == "preserveVerticalProfile";
    }
}
