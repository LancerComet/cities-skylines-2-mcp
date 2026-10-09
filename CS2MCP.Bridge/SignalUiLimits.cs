using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace CS2MCP
{
    // TLM 不公开滑块范围；从已加载模组旁的实际 UI 资源读取，格式未知时停止数值写入。
    internal sealed class SignalUiLimits
    {
        internal sealed class Range
        {
            public float min;
            public float max;
            public float step;
        }
        internal readonly Dictionary<string, Range> ranges = new Dictionary<string, Range>();

        internal static SignalUiLimits Read(string path)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 4 * 1024 * 1024) throw new ArgumentException("TLM native UI limits unavailable; numeric writes disabled");
            string source = File.ReadAllText(path);
            var limits = new SignalUiLimits();
            foreach (string key in new[] { "MinimumDuration", "MaximumDuration", "TargetDurationMultiplier", "LaneOccupiedMultiplier", "IntervalFactor" }) {
                string pattern = "key:\"" + key + "\"(?:(?!key:\").){0,700}?min:(?<min>[0-9.]+),max:(?<max>[0-9.]+),step:(?<step>[0-9.]+)";
                var match = Regex.Match(source, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
                if (!match.Success) throw new ArgumentException("TLM native UI range not recognized for " + key + "; numeric writes disabled");
                float min = float.Parse(match.Groups["min"].Value, CultureInfo.InvariantCulture);
                float max = float.Parse(match.Groups["max"].Value, CultureInfo.InvariantCulture);
                float step = float.Parse(match.Groups["step"].Value, CultureInfo.InvariantCulture);
                if (float.IsNaN(min) || float.IsInfinity(max) || min > max || step <= 0) throw new ArgumentException("invalid native UI range");
                limits.ranges.Add(key, new Range { min = min, max = max, step = step });
            }
            return limits;
        }

        internal float Validate(string key, float value)
        {
            var range = ranges[key];
            if (float.IsNaN(value) || float.IsInfinity(value) || value < range.min || value > range.max)
                throw new ArgumentException(key + " must be within TLM native UI range " + range.min + ".." + range.max);
            return value;
        }
    }
}
