using System;
using Game.Buildings;
using Unity.Entities;

namespace CS2MCP
{
    /// <summary>与原生BuildingUtils相同的非负因子乘积及百分位取整，兼容桥接net472的Span引用边界。</summary>
    internal static class NativeEfficiencyRules
    {
        internal static float Product(DynamicBuffer<Efficiency> factors, EfficiencyFactor? excluding = null)
        {
            float product = 1f;
            foreach (var factor in factors)
                if (!excluding.HasValue || factor.m_Factor != excluding.Value)
                    product *= Math.Max(0f, factor.m_Efficiency);
            return product;
        }

        internal static float Round(float product) => product > 0f ? Math.Max(.01f, (float)Math.Round(product * 100f) / 100f) : 0f;
    }
}
