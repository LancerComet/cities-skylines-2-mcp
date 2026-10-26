using System;
using System.Collections.Generic;

namespace CS2MCP
{
    public static class BackgroundFrameOutputProbe
    {
        private sealed class Target : IBackgroundFrameOutputTarget, IBackgroundFrameRenderer
        {
            public bool Applicable = true, Foreign, Installed, FailInstall, FailRestore, FailRead;
            public bool ForceIntoRenderTexture;
            public int Installs, Restores;
            public long Callbacks;
            public bool IsApplicable => Applicable;
            public bool HasForeignOutput => Foreign;
            public bool OwnOutputInstalled { get { if (FailRead) throw new Exception("read_unknown"); return BackgroundFrameOutputPolicy.OwnsTexture(Applicable, Installed, !Foreign); } }
            public long RenderCallbacks => Callbacks;
            public void InstallOwnOutput() { Installs++; Installed = ForceIntoRenderTexture = true; if (FailInstall) throw new Exception("partial_install"); }
            public void RestoreOwnOutput() { Restores++; if (FailRestore) throw new Exception("restore_failed"); Installed = ForceIntoRenderTexture = false; }
            public bool RenderFrameIfNeeded(int frame) { if (!OwnOutputInstalled) throw new Exception("foreign_output"); Callbacks++; return true; }
            public object ReadNativeState() { if (FailRead) throw new Exception("read_unknown"); return new { installed = Installed, callbacks = Callbacks }; }
        }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        public static object Run()
        {
            var cases = new List<string>();
            var p = new BackgroundFrameOutputPolicy(); var t = new Target();
            p.Apply(() => t); p.Apply(() => throw new Exception("must_not_resolve_twice"));
            Check(t.Installs == 1 && p.Read(true).nativeOutputInstalled == true && !p.Read(true).nativeRenderObserved, "installation is not an observed frame");
            cases.Add("single_apply_without_claiming_render");
            t.Callbacks = 4;
            Check(p.Read(true).nativeRenderObserved && p.Read(true).renderCallbacks == 4, "actual callbacks");
            cases.Add("observed_native_callbacks_read_separately");
            // 重放现装Game.UI.ScreenCaptureHelper归还原目标、无条件清force的实际状态转换。
            t.ForceIntoRenderTexture = false;
            Check(p.RenderFrameIfNeeded(10) && p.Read(true).nativeOutputInstalled == true && p.Read(true).renderCallbacks == 5
                && !t.ForceIntoRenderTexture, "native save reset must not lose owned texture or rewrite its flag");
            Check(!BackgroundFrameOutputPolicy.OwnsTexture(true, true, false)
                && !BackgroundFrameOutputPolicy.OwnsTexture(false, true, true)
                && !BackgroundFrameOutputPolicy.OwnsTexture(true, false, true), "foreign/destroyed texture still refused");
            cases.Add("native_preview_force_reset_preserves_owned_target_and_rendering");
            Check(p.Restore() && t.Restores == 1 && !t.Installed && !p.Read(false).ownershipPending && p.Read(false).restoreComplete == true, "release exact owned object");
            Check(p.Read(false).native != null, "retain last restoration facts");
            cases.Add("restore_releases_ownership_and_preserves_last_facts");
            p.Apply(() => t); Check(t.Installs == 2, "explicit successful restore allows new lifecycle"); p.Restore();
            cases.Add("explicit_restore_allows_next_lifecycle");

            p = new BackgroundFrameOutputPolicy(); t = new Target { Foreign = true };
            p.Apply(() => t); Check(t.Installs == 0 && !p.Read(true).ownershipPending && p.Read(true).failure.Contains("foreign"), "foreign preflight");
            p.Restore(); Check(t.Restores == 0 && t.Foreign, "foreign output preserved");
            cases.Add("foreign_output_refused_without_mutation");
            p = new BackgroundFrameOutputPolicy(); t = new Target { Applicable = false };
            p.Apply(() => t); Check(t.Installs == 0 && p.Read(true).failure.Contains("inapplicable"), "camera qualification");
            cases.Add("inapplicable_camera_refused");
            p = new BackgroundFrameOutputPolicy(); int resolves = 0;
            Func<IBackgroundFrameOutputTarget> missing = () => { resolves++; throw new Exception("camera_missing"); };
            p.Apply(missing); p.Apply(missing); Check(resolves == 1 && !p.Read(true).ownershipPending, "no per-frame resolve retry");
            cases.Add("resolution_failure_not_retried");

            p = new BackgroundFrameOutputPolicy(); t = new Target { FailInstall = true };
            p.Apply(() => t); p.Apply(() => throw new Exception("no_retry"));
            Check(t.Installs == 1 && t.Restores == 1 && !t.Installed && p.Read(true).rollbackComplete == true && !p.Read(true).ownershipPending && p.Read(true).attempted, "partial rollback");
            cases.Add("partial_install_rolls_back_without_retry");
            p = new BackgroundFrameOutputPolicy(); t = new Target { FailInstall = true, FailRestore = true };
            p.Apply(() => t); Check(p.Read(true).ownershipPending && p.Read(true).rollbackComplete == false, "failed rollback retains owned target");
            Check(!p.Restore() && p.Read(false).ownershipPending && p.Read(false).restoreComplete == false, "failed restore remains owned");
            cases.Add("failed_rollback_and_restore_retain_ownership");
            t.FailRestore = false; t.FailInstall = false;
            Check(p.Restore() && !p.Read(false).ownershipPending, "explicit successful cleanup");
            p.Apply(() => t); Check(p.Read(true).nativeOutputInstalled == true && t.Installs == 2, "next load after cleanup"); p.Restore();
            cases.Add("load_cleanup_then_new_camera_application");

            p = new BackgroundFrameOutputPolicy(); t = new Target(); p.Apply(() => t); t.FailRead = true;
            var s = p.Read(true); Check(s.nativeOutputInstalled == null && s.nativeReadError != null && !s.nativeRenderObserved, "read failure unknown");
            cases.Add("native_read_failure_is_unknown");
            Check(p.Restore() && !p.Read(false).ownershipPending, "diagnostic failure does not prevent successful native cleanup");
            cases.Add("diagnostic_read_failure_does_not_retain_released_output");
            return new { passed = cases.Count, cases, nativeMemoryBenefitVerified = false };
        }
    }
}
