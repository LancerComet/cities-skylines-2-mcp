using System;

namespace CS2MCP
{
    /// <summary>作业线完整读取的固定分配预算；没有放宽或截断成功的入口。</summary>
    internal sealed class WorkRouteReadBudget
    {
        internal const int CandidateRoutes = 4096;
        internal const int DeclaredUpgrades = 256;
        internal const int LinkedElements = 16384;
        internal int Used { get; private set; }

        internal void Take(int count)
        {
            Check(count, LinkedElements - Used);
            Used += count;
        }

        internal static void Check(int count, int limit)
        {
            if (count < 0 || count > limit)
                throw new WorkRouteReadBudgetException("work-route readonly budget exceeded; no partial result returned");
        }
    }

    internal sealed class WorkRouteReadBudgetException : Exception
    {
        internal WorkRouteReadBudgetException(string message) : base(message) { }
    }
}
