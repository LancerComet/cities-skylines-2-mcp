using System;
using System.Threading;

namespace CS2MCP
{
    internal interface IBackgroundSceneRenderTarget
    {
        bool IsAlive { get; }
        bool IsGameCamera { get; }
        bool Enabled { get; }
        bool HasCustomRenderer { get; }
        bool FullscreenPassthrough { get; }
        int CameraInstanceId { get; }
        string CameraName { get; }
        void InstallOwn(Action onRender);
        void RemoveOwn();
    }

    /// <summary>公开相机扩展的委托归属；不持有World、不创建相机或纹理、不重复施加原生设置。</summary>
    internal sealed class BackgroundSceneRenderingPolicy
    {
        private IBackgroundSceneRenderTarget m_Target;
        private bool m_Attempted, m_Applied;
        private bool? m_RollbackComplete, m_RestoreComplete;
        private string m_Failure;
        private long m_Callbacks, m_LastDetachedCallbacks;
        private bool m_CaptureInProgress;
        private long m_CaptureRequests;
        private bool? m_LastCaptureRestoreComplete;

        internal bool Attempted => m_Attempted;
        internal bool HasPendingOwnership => m_Target != null;

        internal void Apply(Func<IBackgroundSceneRenderTarget> resolve)
        {
            if (m_Attempted) return;
            m_Attempted = true;
            m_Failure = null;
            m_RollbackComplete = m_RestoreComplete = null;
            Interlocked.Exchange(ref m_Callbacks, 0);
            try {
                var target = resolve();
                if (target == null || !target.IsAlive) throw new InvalidOperationException("native_scene_camera_unavailable");
                if (!target.IsGameCamera || !target.Enabled) throw new InvalidOperationException("native_scene_camera_inapplicable");
                if (target.FullscreenPassthrough) throw new InvalidOperationException("native_fullscreen_passthrough_incompatible");
                if (target.HasCustomRenderer) throw new InvalidOperationException("native_custom_renderer_already_present");
                // Install抛错前也可能已加委托：先保留所有权，失败时仅撤销自己的同一委托。
                m_Target = target;
                target.InstallOwn(RecordRender);
                if (!target.HasCustomRenderer) throw new InvalidOperationException("native_custom_renderer_not_registered");
                m_Applied = true;
            } catch (Exception e) {
                m_Failure = e.GetType().Name + ": " + e.Message;
                if (m_Target != null) {
                    try { Detach(); m_RollbackComplete = true; }
                    catch (Exception restore) {
                        m_RollbackComplete = false;
                        m_Failure += "; rollback: " + restore.GetType().Name + ": " + restore.Message;
                    }
                }
                // 包括撤销成功的失败，普通帧也不得重新注册。
                m_Attempted = true;
            }
        }

        private void RecordRender() => Interlocked.Increment(ref m_Callbacks);

        /// <summary>同步取景只临时撤下自身委托；不清空别人的绘制，不跨帧保留借用。</summary>
        internal T ExecuteNativeCapture<T>(IBackgroundSceneRenderTarget active, Func<T> capture)
        {
            if (m_CaptureInProgress) throw new InvalidOperationException("native_capture_already_running");
            if (active == null || !active.IsAlive || !active.IsGameCamera || !active.Enabled || active.FullscreenPassthrough)
                throw new InvalidOperationException("native_capture_camera_inapplicable");
            bool owned = m_Target != null;
            if (owned && (!m_Applied || !m_Target.IsAlive || m_Target.CameraInstanceId != active.CameraInstanceId))
                throw new InvalidOperationException("native_capture_scene_ownership_unavailable");
            if (!owned && active.HasCustomRenderer)
                throw new InvalidOperationException("native_capture_foreign_renderer_present");

            bool detached = false;
            m_CaptureInProgress = true;
            m_LastCaptureRestoreComplete = null;
            Interlocked.Increment(ref m_CaptureRequests);
            try {
                if (owned) {
                    // remove可能部分完成后抛错，故先降为未知并保留m_Target，不能直接重试。
                    m_Applied = false;
                    try { m_Target.RemoveOwn(); detached = true; }
                    catch (Exception e) { m_Failure = "capture_detach_failed: " + e.GetType().Name + ": " + e.Message; throw; }
                }
                if (active.HasCustomRenderer) throw new InvalidOperationException("native_capture_foreign_renderer_present");
                return capture();
            } finally {
                try {
                    if (detached) {
                        if (!m_Target.IsAlive || m_Target.HasCustomRenderer)
                            throw new InvalidOperationException("native_capture_restore_target_or_foreign_renderer_changed");
                        m_Target.InstallOwn(RecordRender);
                        if (!m_Target.HasCustomRenderer) throw new InvalidOperationException("native_capture_restore_not_registered");
                        m_Applied = true;
                    }
                    m_LastCaptureRestoreComplete = !owned || detached && m_Applied;
                } catch (Exception e) {
                    m_Applied = false;
                    m_LastCaptureRestoreComplete = false;
                    m_Failure = "capture_restore_failed: " + e.GetType().Name + ": " + e.Message;
                    throw;
                } finally { m_CaptureInProgress = false; }
            }
        }

        private void Detach()
        {
            if (m_Target == null) return;
            m_Target.RemoveOwn();
            m_Target = null;
            m_Applied = false;
            m_LastDetachedCallbacks = Interlocked.Exchange(ref m_Callbacks, 0);
        }

        internal bool Restore()
        {
            try {
                Detach();
                m_Attempted = false;
                m_RestoreComplete = true;
                return true;
            } catch (Exception e) {
                m_RestoreComplete = false;
                m_Failure = "scene_restore_failed: " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        internal bool ResetAfterLoad()
        {
            if (!Restore()) return false;
            Interlocked.Exchange(ref m_CaptureRequests, 0);
            m_LastCaptureRestoreComplete = null;
            m_Failure = null;
            m_RollbackComplete = null;
            return true;
        }

        internal sealed class Snapshot
        {
            public bool requested, attempted, ownRegistrationSucceeded, ownershipPending;
            public bool? nativeHookInstalled, targetAlive, cameraEnabled, nativeHasCustomRenderer;
            public bool ownCallbackObserved;
            public long callbackInvocations, lastDetachedCallbackInvocations;
            public bool captureInProgress;
            public long nativeCaptureRequests;
            public bool? lastCaptureRestoreComplete;
            public int? cameraInstanceId;
            public string cameraName, failure, lastFailure, nativeReadError;
            public bool? rollbackComplete, restoreComplete;
            public bool cameraOrTextureCreated = false, cameraEnabledChanged = false, resolutionChanged = false;
            public string note = "只注册/移除自有公开渲染委托；回调计数与提交内存趋势分别验收，不证明功耗或内存故障已修复";
        }

        internal Snapshot Read(bool requested)
        {
            var s = new Snapshot { requested = requested, attempted = m_Attempted,
                ownRegistrationSucceeded = m_Applied, ownershipPending = m_Target != null,
                callbackInvocations = Interlocked.Read(ref m_Callbacks),
                lastDetachedCallbackInvocations = m_LastDetachedCallbacks,
                captureInProgress = m_CaptureInProgress, nativeCaptureRequests = Interlocked.Read(ref m_CaptureRequests),
                lastCaptureRestoreComplete = m_LastCaptureRestoreComplete,
                failure = requested || m_Target != null ? m_Failure : null, lastFailure = m_Failure,
                rollbackComplete = m_RollbackComplete, restoreComplete = m_RestoreComplete,
                nativeHookInstalled = m_Target == null ? (bool?)false : null };
            if (m_Target != null) {
                try {
                    s.cameraInstanceId = m_Target.CameraInstanceId; s.cameraName = m_Target.CameraName;
                    bool alive = m_Target.IsAlive; s.targetAlive = alive;
                    if (alive) {
                        s.cameraEnabled = m_Target.Enabled;
                        s.nativeHasCustomRenderer = m_Target.HasCustomRenderer;
                        s.nativeHookInstalled = m_Applied ? (bool?)(s.cameraEnabled == true && s.nativeHasCustomRenderer == true) : null;
                    } else s.nativeHookInstalled = false;
                } catch (Exception e) { s.nativeReadError = e.GetType().Name + ": " + e.Message; s.nativeHookInstalled = null; }
            }
            s.ownCallbackObserved = s.nativeHookInstalled == true && s.callbackInvocations > 0;
            return s;
        }
    }
}
