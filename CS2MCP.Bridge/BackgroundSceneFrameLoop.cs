using System;
using System.Diagnostics;

namespace CS2MCP
{
    /// <summary>显式恢复缺失的场景帧；返回、回调和内存收益分别核验。</summary>
    internal sealed class BackgroundSceneFrameLoop
    {
        internal sealed class Snapshot
        {
            public bool requested, active, gpuCompletionVerified, imageReadbackInvoked, forcedGcInvoked;
            public string failure, cleanupFailure;
            public long frameAttempts, renderedFramesReturned, skippedFrames;
            public int lastAttemptFrame;
            public double lastFrameMilliseconds, maxFrameMilliseconds;
        }
        internal bool Requested { get; private set; }
        internal string Failure { get; private set; }
        private string m_CleanupFailure;
        private long m_Attempts, m_Returned, m_Skipped;
        private int m_LastFrame = -1;
        private double m_LastMilliseconds, m_MaxMilliseconds;

        internal void SetRequested(bool value)
        {
            if (value == Requested) return;
            Requested = value;
            if (!value) return;
            Failure = m_CleanupFailure = null;
            m_Attempts = m_Returned = m_Skipped = 0; m_LastFrame = -1;
            m_LastMilliseconds = m_MaxMilliseconds = 0;
        }

        internal void SetupFailed(Exception e) { Failure = e.GetType().Name + ": " + e.Message; }

        internal void FailAndRelease(Exception e, Action release)
        {
            if (Failure != null) return;
            SetupFailed(e);
            try { release(); }
            catch (Exception cleanup) { m_CleanupFailure = cleanup.GetType().Name + ": " + cleanup.Message; }
        }

        internal void Tick(int frame, bool ready, Func<int, bool> render, Action release)
        {
            if (!Requested || Failure != null || !ready || frame == m_LastFrame) return;
            m_LastFrame = frame; m_Attempts++;
            long started = Stopwatch.GetTimestamp();
            try { if (render(frame)) m_Returned++; else m_Skipped++; }
            catch (Exception e) { FailAndRelease(e, release); }
            finally {
                m_LastMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                m_MaxMilliseconds = Math.Max(m_MaxMilliseconds, m_LastMilliseconds);
            }
        }

        internal Snapshot Read() => new Snapshot {
            requested = Requested, active = Requested && Failure == null, failure = Failure, cleanupFailure = m_CleanupFailure,
            frameAttempts = m_Attempts, renderedFramesReturned = m_Returned, skippedFrames = m_Skipped,
            lastAttemptFrame = m_LastFrame, lastFrameMilliseconds = m_LastMilliseconds, maxFrameMilliseconds = m_MaxMilliseconds,
            gpuCompletionVerified = false, imageReadbackInvoked = false, forcedGcInvoked = false
        };
    }
}
