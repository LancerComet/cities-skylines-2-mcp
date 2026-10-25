namespace CS2MCP
{
    /// <summary>数据世界已载入后，原生加载覆盖屏和全部任务仍可能未结束；只决定何时可应用后台渲染策略。</summary>
    internal static class UiLoadReadinessPolicy
    {
        internal static bool IsReady(string overlay, float group1, float group2, float group3)
        {
            return overlay == "None" && Complete(group1) && Complete(group2) && Complete(group3);
        }

        private static bool Complete(float progress)
        {
            return !float.IsNaN(progress) && !float.IsInfinity(progress) && progress >= 1f;
        }
    }
}
