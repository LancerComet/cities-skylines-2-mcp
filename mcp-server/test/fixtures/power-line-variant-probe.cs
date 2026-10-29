using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace CS2MCP
{
    public static class PowerLineVariantProbe
    {
        public static string Run()
        {
            var cases = new List<string>();
            Action<bool, string> check = (ok, name) => { if (!ok) throw new Exception(name); cases.Add(name); };
            // Current native surface [0,10] and underground [-50,-10].
            check(NativePowerLineVariantRules.SelectUnderground(true, true, 0, 0, -15), "surface-to-buried-selects-real-cable");
            check(NativePowerLineVariantRules.SelectUnderground(true, true, 0, -15, 0), "buried-to-surface-selects-real-cable");
            check(NativePowerLineVariantRules.SelectUnderground(true, true, 0, -15, -15), "buried-to-buried-selects-real-cable");
            check(!NativePowerLineVariantRules.SelectUnderground(true, true, 0, 0, 10), "surface-line-retained");
            check(!NativePowerLineVariantRules.SelectUnderground(false, true, 0, -15, -15), "roads-and-tracks-not-reclassified");
            check(!NativePowerLineVariantRules.SelectUnderground(true, false, 0, 0, -15), "no-invented-underground-variant");
            check(!NativePowerLineVariantRules.SelectUnderground(true, false, -50, -15, -15), "explicit-cable-not-reselected");
            check(NativePowerLineVariantRules.FreeEndFits(true, 0, -50, -10), "fixed-native-surface-transition-height-retained");
            check(NativePowerLineVariantRules.FreeEndFits(false, -15, -50, -10), "legal-free-cable-depth");
            check(!NativePowerLineVariantRules.FreeEndFits(false, 0, -50, -10), "free-surface-cable-refused");
            check(!NativePowerLineVariantRules.FreeEndFits(false, -51, -50, -10), "too-deep-free-cable-refused");
            check(NativePowerLineVariantRules.FreeEndFits(false, -50, -50, -10), "native-lower-bound-inclusive");
            check(NativePowerLineVariantRules.FreeEndFits(false, -10, -50, -10), "native-upper-bound-inclusive");
            check(!NativePowerLineVariantRules.FreeEndFits(false, float.NaN, -50, -10), "nan-free-end-refused");
            return JsonConvert.SerializeObject(new { passed = cases.Count, cases, productionSourcesLinked = true,
                gameStarted = false, nativeConstructionVerified = false });
        }
    }
}
