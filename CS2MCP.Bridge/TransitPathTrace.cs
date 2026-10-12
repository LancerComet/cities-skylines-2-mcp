using System;
using System.Collections.Generic;

namespace CS2MCP
{
    /// <summary>只读Owner追溯的数量与环边界；不缓存、不选择或修改任何线路路径。</summary>
    internal static class TransitPathTrace
    {
        internal sealed class Result<T>
        {
            public readonly List<T> Entities = new List<T>();
            public T Next;
            public string Termination;
            public bool Complete => Termination == "root";
        }

        internal static Result<T> WalkOwners<T>(T target, T nullEntity, Func<T, bool> exists,
            Func<T, T> owner, int maxEntities = 12)
        {
            if (maxEntities < 1) throw new ArgumentOutOfRangeException(nameof(maxEntities));
            var result = new Result<T>();
            var seen = new HashSet<T>();
            var equality = EqualityComparer<T>.Default;
            var current = target;
            while (!equality.Equals(current, nullEntity)) {
                if (seen.Contains(current)) { result.Termination = "ownerCycle"; break; }
                if (result.Entities.Count >= maxEntities) { result.Termination = "ownerDepthLimit"; break; }
                seen.Add(current);
                result.Entities.Add(current);
                if (!exists(current)) { result.Termination = "missingEntity"; break; }
                current = owner(current);
            }
            result.Next = current;
            if (result.Termination == null) result.Termination = result.Entities.Count == 0 ? "nullTarget" : "root";
            return result;
        }
    }
}
