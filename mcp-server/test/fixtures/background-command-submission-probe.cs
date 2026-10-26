using System;
using System.Collections.Generic;

namespace CS2MCP
{
    public static class BackgroundCommandSubmissionProbe
    {
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        public static object Run()
        {
            var cases = new List<string>(); var p = new BackgroundCommandSubmission(); int calls = 0;
            Action submit = () => calls++;
            p.Tick(1, true, submit); Check(calls == 0 && !p.Read().active, "default disabled");
            cases.Add("default_disabled");
            p.SetRequested(true); p.Tick(2, false, submit); Check(calls == 0 && p.Read().nativeCallAttempts == 0, "loading barrier");
            cases.Add("not_ready_preserves_barrier");
            p.Tick(3, true, submit); p.Tick(3, true, submit); p.SetRequested(true); p.Tick(3, true, submit);
            Check(calls == 1 && p.Read().nativeCallsReturned == 1 && p.Read().nativeCallAttempts == 1, "same frame/idempotent enable");
            cases.Add("one_native_call_per_frame_and_idempotent_enable");
            p.Tick(4, true, submit); var s = p.Read();
            Check(calls == 2 && s.nativeCallsReturned == 2 && !s.gpuCompletionVerified && !s.cameraRenderInvoked && !s.imageReadbackInvoked && !s.forcedGcInvoked, "honest observability");
            cases.Add("returned_call_does_not_claim_gpu_or_memory");
            p.Tick(5, true, () => { throw new Exception("native_submit_failed"); });
            p.Tick(6, true, submit); p.SetRequested(true); p.Tick(7, true, submit); s = p.Read();
            Check(calls == 2 && s.nativeCallAttempts == 3 && s.nativeCallsReturned == 2 && !s.active && s.failure.Contains("native_submit_failed"), "no retry after native exception");
            cases.Add("native_failure_stops_without_auto_retry");
            p.SetRequested(false); p.Tick(8, true, submit); Check(calls == 2 && !p.Read().requested, "stop releases ticking");
            p.SetRequested(true); p.Tick(9, true, submit); Check(calls == 3 && p.Read().nativeCallsReturned == 1 && p.Read().failure == null, "explicit restart new observation window");
            cases.Add("explicit_stop_and_reenable_only");
            p.SetupFailed(new InvalidOperationException("foreign_owner")); p.Tick(10, true, submit);
            Check(calls == 3 && !p.Read().active && p.Read().failure.Contains("foreign_owner"), "setup failure does not invoke native");
            p.SetRequested(false); p.Tick(11, true, submit); Check(calls == 3, "scene release");
            cases.Add("setup_failure_and_scene_release_do_not_tick");
            return new { passed = cases.Count, cases, nativeGpuAndMemoryBenefitVerified = false };
        }
    }
}
