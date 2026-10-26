using System;

namespace CS2MCP
{
    internal interface IBackgroundFrameOutputTarget
    {
        bool IsApplicable { get; }
        bool HasForeignOutput { get; }
        bool OwnOutputInstalled { get; }
        long RenderCallbacks { get; }
        void InstallOwnOutput();
        void RestoreOwnOutput();
        object ReadNativeState();
    }

    internal interface IBackgroundFrameRenderer
    {
        bool RenderFrameIfNeeded(int frame);
    }

    /// <summary>自有相机输出的单次应用/恢复；不由资源告警触发重启或逐帧重试。</summary>
    internal sealed class BackgroundFrameOutputPolicy
    {
        // 原生保存预览归还原目标，但将forceIntoRenderTexture置false；该临时标志不是纹理所有权。
        internal static bool OwnsTexture(bool cameraPresent, bool textureCreated, bool targetMatches)
            => cameraPresent && textureCreated && targetMatches;

        internal sealed class Snapshot
        {
            public bool requested, attempted, ownershipPending, nativeRenderObserved;
            public bool? nativeOutputInstalled, rollbackComplete, restoreComplete;
            public long? renderCallbacks;
            public string failure, nativeReadError;
            public object native;
            public string note = "公开原相机离屏输出；实际相机回调不等于GPU完成或内存根因修复，需独立观察提交趋势。无周期截图/读回/强制GC。";
        }
        private IBackgroundFrameOutputTarget m_Target;
        private bool m_Attempted;
        private bool? m_RollbackComplete, m_RestoreComplete;
        private string m_Failure;
        private object m_LastNativeState;

        internal bool RenderFrameIfNeeded(int frame)
        {
            if (m_Target is IBackgroundFrameRenderer renderer) return renderer.RenderFrameIfNeeded(frame);
            throw new InvalidOperationException("native_background_frame_target_unavailable");
        }

        internal void Apply(Func<IBackgroundFrameOutputTarget> resolve)
        {
            if (m_Attempted) return;
            m_Attempted = true;
            m_Failure = null;
            m_RollbackComplete = m_RestoreComplete = null;
            m_LastNativeState = null;
            try {
                var target = resolve();
                if (target == null || !target.IsApplicable) throw new InvalidOperationException("native_output_camera_inapplicable");
                if (target.HasForeignOutput) throw new InvalidOperationException("native_output_foreign_target_present");
                // 分配或赋值可能部分完成后失败；从第一步开始保留精确所有权。
                m_Target = target;
                target.InstallOwnOutput();
                if (!target.OwnOutputInstalled) throw new InvalidOperationException("native_output_not_installed");
            } catch (Exception e) {
                m_Failure = e.GetType().Name + ": " + e.Message;
                if (m_Target != null) {
                    try { Release(); m_RollbackComplete = true; }
                    catch (Exception restore) { m_RollbackComplete = false; m_Failure += "; rollback: " + restore.Message; }
                }
            }
        }

        private void Release()
        {
            if (m_Target == null) return;
            m_Target.RestoreOwnOutput();
            try { m_LastNativeState = m_Target.ReadNativeState(); }
            catch (Exception e) { m_LastNativeState = new { nativeReadError = e.Message }; }
            m_Target = null;
        }

        internal bool Restore()
        {
            try { Release(); m_Attempted = false; m_RestoreComplete = true; return true; }
            catch (Exception e) { m_RestoreComplete = false; m_Failure = "native_output_restore_failed: " + e.Message; return false; }
        }

        internal Snapshot Read(bool requested)
        {
            bool? installed = false;
            long? callbacks = null;
            object native = m_LastNativeState;
            string readError = null;
            if (m_Target != null) {
                try { installed = m_Target.OwnOutputInstalled; callbacks = m_Target.RenderCallbacks; native = m_Target.ReadNativeState(); }
                catch (Exception e) { installed = null; readError = e.GetType().Name + ": " + e.Message; }
            }
            return new Snapshot { requested = requested, attempted = m_Attempted, ownershipPending = m_Target != null,
                nativeOutputInstalled = installed, renderCallbacks = callbacks,
                nativeRenderObserved = installed == true && callbacks > 0,
                rollbackComplete = m_RollbackComplete, restoreComplete = m_RestoreComplete,
                failure = m_Failure, nativeReadError = readError, native = native };
        }
    }
}
