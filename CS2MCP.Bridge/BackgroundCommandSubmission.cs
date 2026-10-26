using System;
using System.Diagnostics;

namespace CS2MCP
{
    /// <summary>只核公开提交调用是否返回；不把它当GPU完成、相机帧或已修复内存。</summary>
    internal sealed class BackgroundCommandSubmission
    {
        internal sealed class Snapshot
        {
            public bool requested, active, gpuCompletionVerified, cameraRenderInvoked, imageReadbackInvoked, forcedGcInvoked;
            public string failure, note;
            public long nativeCallAttempts, nativeCallsReturned;
            public int lastAttemptFrame;
            public double lastCallMilliseconds, maxCallMilliseconds;
        }
        internal bool Requested { get; private set; }
        internal string Failure { get; private set; }
        private long m_Attempts, m_Returned;
        private int m_LastFrame = -1;
        private double m_LastMilliseconds, m_MaxMilliseconds;

        internal void SetRequested(bool value)
        {
            if (value == Requested) return;
            Requested = value;
            if (!value) return;
            Failure = null; m_Attempts = m_Returned = 0; m_LastFrame = -1;
            m_LastMilliseconds = m_MaxMilliseconds = 0;
        }

        internal void SetupFailed(Exception e) { Failure = e.GetType().Name + ": " + e.Message; }

        internal void Tick(int frame, bool ready, Action submit)
        {
            if (!Requested || Failure != null || !ready || frame == m_LastFrame) return;
            m_LastFrame = frame;
            m_Attempts++;
            long started = Stopwatch.GetTimestamp();
            try { submit(); m_Returned++; }
            catch (Exception e) { SetupFailed(e); }
            finally {
                m_LastMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                m_MaxMilliseconds = Math.Max(m_MaxMilliseconds, m_LastMilliseconds);
            }
        }

        internal Snapshot Read() => new Snapshot {
            requested = Requested, active = Requested && Failure == null, failure = Failure,
            nativeCallAttempts = m_Attempts, nativeCallsReturned = m_Returned,
            lastAttemptFrame = m_LastFrame, lastCallMilliseconds = m_LastMilliseconds,
            maxCallMilliseconds = m_MaxMilliseconds, gpuCompletionVerified = false,
            cameraRenderInvoked = false, imageReadbackInvoked = false, forcedGcInvoked = false,
            note = "公开GL.Flush只提交排队驱动命令；调用返回不是GPU完成、相机渲染或内存收益。只在本次显式后台请求/就绪帧执行，失败停止，无周期截图/读回/强制GC。"
        };
    }
}
