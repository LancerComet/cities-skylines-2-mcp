using System;
using System.Collections.Generic;

namespace CS2MCP
{
    public static class BackgroundSceneFramesProbe
    {
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        public static object Run()
        {
            var cases = new List<string>(); var p = new BackgroundSceneFrameLoop(); int calls = 0, releases = 0;
            Func<int, bool> render = frame => { calls++; return true; };
            Action release = () => releases++;
            p.Tick(1, true, render, release); Check(calls == 0, "default disabled"); cases.Add("default_disabled");
            p.SetRequested(true); p.Tick(2, false, render, release);
            Check(calls == 0 && p.Read().frameAttempts == 0, "native loading/menu/save barrier"); cases.Add("native_barrier");
            p.Tick(3, true, render, release); p.SetRequested(true); p.Tick(3, true, render, release);
            Check(calls == 1 && p.Read().renderedFramesReturned == 1, "one call per frame"); cases.Add("single_frame_idempotence");
            p.Tick(4, true, frame => false, release);
            Check(p.Read().skippedFrames == 1 && p.Read().renderedFramesReturned == 1, "natural render/capture skipped without claim"); cases.Add("borrowed_camera_or_natural_frame_skip");
            p.Tick(5, true, frame => { throw new Exception("foreign_camera_owner"); }, release);
            p.SetRequested(true); p.Tick(6, true, render, release);
            Check(calls == 1 && releases == 1 && !p.Read().active && p.Read().failure.Contains("foreign_camera_owner"), "error cleanup once and no retry");
            cases.Add("error_cleanup_once_no_retry");
            p.SetRequested(false); p.Tick(7, true, render, release); p.SetRequested(true); p.Tick(8, true, render, release);
            Check(calls == 2 && p.Read().renderedFramesReturned == 1 && p.Read().failure == null, "explicit restart only"); cases.Add("explicit_stop_and_reenable");
            p.Tick(9, true, frame => { throw new Exception("native_render_error"); }, () => { throw new Exception("owned_release_failed"); });
            p.Tick(10, true, render, release);
            Check(calls == 2 && p.Read().cleanupFailure.Contains("owned_release_failed") && p.Read().failure.Contains("native_render_error"), "retain both failures");
            cases.Add("cleanup_failure_retains_evidence");
            var s = p.Read(); Check(!s.gpuCompletionVerified && !s.imageReadbackInvoked && !s.forcedGcInvoked, "honest evidence"); cases.Add("no_gpu_or_memory_claim");
            return new { passed = cases.Count, cases, nativeMemoryBenefitVerified = false };
        }
    }
}
