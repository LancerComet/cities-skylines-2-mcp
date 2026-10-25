using System;
using System.Collections.Generic;

namespace CS2MCP
{
    public static class BackgroundSceneRenderingProbe
    {
        private sealed class Target : IBackgroundSceneRenderTarget
        {
            public bool Alive = true, Game = true, Active = true, Passthrough, FailAdd, FailRemove, FailRead;
            public int Adds, Removes, ForeignCalls;
            public int Id = 17;
            public Action Callbacks;
            public bool IsAlive => Alive;
            public bool IsGameCamera => Game;
            public bool Enabled => Active;
            public bool FullscreenPassthrough => Passthrough;
            public bool HasCustomRenderer { get { if (FailRead) throw new Exception("read_unknown"); return Callbacks != null; } }
            public int CameraInstanceId => Id;
            public string CameraName => "game";
            private Action own;
            public void InstallOwn(Action callback) { Adds++; own=callback; Callbacks+=own; if(FailAdd)throw new Exception("partial_add"); }
            public void RemoveOwn() { Removes++; if(FailRemove)throw new Exception("remove_failed"); Callbacks-=own; own=null; }
            public void Foreign() { ForeignCalls++; }
        }
        private static void Check(bool condition, string message) { if(!condition)throw new Exception(message); }
        public static object Run()
        {
            var cases=new List<string>();
            var p=new BackgroundSceneRenderingPolicy();var t=new Target();
            p.Apply(()=>t);p.Apply(()=>throw new Exception("must_not_resolve_twice"));t.Callbacks();t.Callbacks();
            var s=p.Read(true);Check(t.Adds==1 && s.nativeHookInstalled==true && s.ownCallbackObserved && s.callbackInvocations==2,"single registration/actual callback");
            cases.Add("single_registration_and_observed_callback");
            t.Callbacks+=t.Foreign;Check(p.Restore(),"restore");t.Callbacks();
            s=p.Read(false);Check(t.ForeignCalls==1 && t.Removes==1 && !s.ownershipPending && s.lastDetachedCallbackInvocations==2,"foreign retained");
            cases.Add("restore_keeps_foreign_delegate");

            p=new BackgroundSceneRenderingPolicy();t=new Target();t.Callbacks+=t.Foreign;
            p.Apply(()=>t);Check(t.Adds==0 && p.Read(true).failure.Contains("already_present"),"preexisting renderer");
            Check(p.Restore() && t.Removes==0 && t.Callbacks!=null,"foreign preflight unchanged");
            cases.Add("preexisting_renderer_refused_before_mutation");
            foreach(int invalid in new[]{0,1,2,3}) {
                p=new BackgroundSceneRenderingPolicy();t=new Target {Alive=invalid!=0,Game=invalid!=1,Active=invalid!=2,Passthrough=invalid==3};
                p.Apply(()=>t);Check(t.Adds==0 && p.Read(true).failure!=null,"invalid target");
            }
            cases.Add("dead_non_game_disabled_and_passthrough_refused");

            p=new BackgroundSceneRenderingPolicy();t=new Target {FailAdd=true};
            p.Apply(()=>t);p.Apply(()=>throw new Exception("no_auto_retry"));s=p.Read(true);
            Check(t.Adds==1 && t.Removes==1 && t.Callbacks==null && s.rollbackComplete==true && s.attempted,"partial rollback");
            cases.Add("partial_add_rolls_back_without_auto_retry");

            p=new BackgroundSceneRenderingPolicy();t=new Target {FailAdd=true,FailRemove=true};
            p.Apply(()=>t);s=p.Read(true);Check(s.ownershipPending && s.rollbackComplete==false && s.nativeHookInstalled==null,"failed cleanup retains uncertainty");
            Check(!p.Restore() && p.HasPendingOwnership,"restore failure retains ownership");
            t.FailRemove=false;t.FailAdd=false;Check(p.ResetAfterLoad() && !p.HasPendingOwnership && !p.Attempted,"explicit reset cleanup");
            p.Apply(()=>t);Check(p.Read(true).nativeHookInstalled==true,"next load applies");p.Restore();
            cases.Add("failed_rollback_owned_until_explicit_successful_cleanup");

            p=new BackgroundSceneRenderingPolicy();t=new Target();p.Apply(()=>t);t.FailRead=true;s=p.Read(true);
            Check(s.nativeHookInstalled==null && s.nativeReadError!=null && !s.ownCallbackObserved,"unknown native read");
            t.FailRead=false;t.Alive=false;Check(p.Read(true).nativeHookInstalled==false && p.Restore(),"destroyed target cleanup");
            cases.Add("unknown_reads_and_destroyed_target_cleanup");

            p=new BackgroundSceneRenderingPolicy();int resolves=0;
            Func<IBackgroundSceneRenderTarget> missing=()=>{resolves++;throw new Exception("not_ready");};
            p.Apply(missing);p.Apply(missing);Check(resolves==1 && !p.HasPendingOwnership && p.Read(true).failure!=null,"resolve once");
            p.ResetAfterLoad();p.Apply(missing);Check(resolves==2,"new load may resolve");
            cases.Add("resolve_failure_not_retried_until_explicit_reset");

            p=new BackgroundSceneRenderingPolicy();t=new Target();p.Apply(()=>t);t.Callbacks();
            var current=t;
            int value=p.ExecuteNativeCapture(t,()=>{Check(current.Callbacks==null,"own callback temporarily absent");return 42;});
            s=p.Read(true);Check(value==42 && t.Adds==2 && t.Removes==1 && s.nativeHookInstalled==true && s.callbackInvocations==1 && s.lastCaptureRestoreComplete==true && !s.captureInProgress,"capture restored without resetting counts");p.Restore();
            cases.Add("capture_temporarily_detaches_only_own_and_restores");

            p=new BackgroundSceneRenderingPolicy();t=new Target();p.Apply(()=>t);
            try {p.ExecuteNativeCapture<int>(t,()=>throw new Exception("render_failed"));Check(false,"must propagate");}
            catch(Exception e){Check(e.Message=="render_failed","original failure");}
            Check(p.Read(true).nativeHookInstalled==true && p.Read(true).lastCaptureRestoreComplete==true,"exception restores own");p.Restore();
            cases.Add("capture_exception_restores_before_propagating");

            p=new BackgroundSceneRenderingPolicy();t=new Target();
            Check(p.ExecuteNativeCapture(t,()=>12)==12 && t.Adds==0 && t.Removes==0 && !p.HasPendingOwnership,"no unnecessary registration");
            cases.Add("capture_without_owned_suspension_changes_no_hooks");

            p=new BackgroundSceneRenderingPolicy();t=new Target();t.Callbacks+=t.Foreign;int calls=0;
            try{p.ExecuteNativeCapture(t,()=>{calls++;return 1;});Check(false,"foreign must refuse");}catch(InvalidOperationException){}
            Check(calls==0 && t.Adds==0 && t.Removes==0 && t.Callbacks!=null,"foreign preserved");
            cases.Add("capture_preexisting_foreign_renderer_refused");

            p=new BackgroundSceneRenderingPolicy();t=new Target();p.Apply(()=>t);
            try{p.ExecuteNativeCapture(new Target{Id=18},()=>1);Check(false,"camera changed");}catch(InvalidOperationException){}
            Check(t.Removes==0 && p.Read(true).nativeHookInstalled==true,"different camera no mutation");p.Restore();
            cases.Add("capture_changed_camera_refused_before_detach");

            p=new BackgroundSceneRenderingPolicy();t=new Target();p.Apply(()=>t);current=t;
            try{p.ExecuteNativeCapture(t,()=>{current.Callbacks+=current.Foreign;return 3;});Check(false,"foreign appeared");}catch(InvalidOperationException){}
            Check(p.HasPendingOwnership && p.Read(true).lastCaptureRestoreComplete==false && t.Adds==1,"do not overwrite foreign on restore");
            Check(p.Restore() && t.Callbacks!=null,"explicit cleanup retains foreign");t.Callbacks();Check(t.ForeignCalls==1,"foreign callable");
            cases.Add("capture_new_foreign_renderer_retained_and_failure_exposed");

            p=new BackgroundSceneRenderingPolicy();t=new Target();p.Apply(()=>t);t.FailRemove=true;calls=0;
            try{p.ExecuteNativeCapture(t,()=>{calls++;return 1;});Check(false,"remove failure");}catch(Exception){}
            Check(calls==0 && p.HasPendingOwnership && !p.Read(true).captureInProgress && p.Read(true).lastCaptureRestoreComplete==false,"failed detach no render");
            p.Apply(()=>throw new Exception("must not automatically retry"));Check(t.Adds==1,"no retry");t.FailRemove=false;Check(p.Restore(),"explicit restore");
            cases.Add("capture_detach_failure_keeps_uncertainty_without_render_or_retry");

            p=new BackgroundSceneRenderingPolicy();t=new Target();p.Apply(()=>t);current=t;
            try{p.ExecuteNativeCapture(t,()=>{current.FailAdd=true;return 4;});Check(false,"restore failure");}catch(Exception){}
            Check(p.HasPendingOwnership && p.Read(true).lastCaptureRestoreComplete==false && !p.Read(true).captureInProgress,"partial re-add owned until cleanup");
            t.FailAdd=false;Check(p.Restore() && t.Callbacks==null,"remove partially restored own hook");
            cases.Add("capture_partial_restore_retains_ownership_until_explicit_cleanup");
            return new {passed=cases.Count,cases,nativeRendererMemoryAndWattsVerified=false};
        }
    }
}
