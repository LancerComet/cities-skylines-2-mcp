using System;

namespace CS2MCP
{
    /// <summary>唯一按需取景的托管结果槽；状态读取不接触World或Unity，迟到结果可按原ID领取。</summary>
    internal sealed class NativeCaptureOperation
    {
        internal static readonly NativeCaptureOperation Shared = new NativeCaptureOperation();
        private readonly object m_Gate = new object();
        private readonly Func<DateTime> m_Clock;
        private Record m_Current;
        private sealed class Record
        {
            internal string Id, SaveGuid, Status, Error;
            internal int Width, Camera;
            internal DateTime Created;
            internal DateTime? Started, Finished;
            internal byte[] Png;
            internal object Diagnostics;
        }
        internal sealed class Snapshot
        {
            public string captureId, loadedSaveGuid, status, error, createdAtUtc, startedAtUtc, completedAtUtc;
            public int? width, cameraInstanceId;
            public bool resultAvailable, mayHaveRendered;
            public bool mayHaveApplied = false, safeToRetry = false, resultQuerySafe = true;
            public object diagnostics;
            public string note = "只启动一次；用原captureId查询。queued/running不重新渲染；结果只保留一份，下一次显式取景或加载/退出清理。";
        }
        internal NativeCaptureOperation(Func<DateTime> clock = null) { m_Clock = clock ?? (() => DateTime.UtcNow); }

        internal bool IsInFlight
        {
            get { lock (m_Gate) { ExpireQueued(); return m_Current?.Status == "queued" || m_Current?.Status == "running"; } }
        }

        private void ExpireQueued()
        {
            if (m_Current?.Status != "queued" || (m_Clock() - m_Current.Created).TotalSeconds <= 30) return;
            m_Current.Status = "failed"; m_Current.Error = "native_end_of_frame_not_observed";
            m_Current.Finished = m_Clock();
        }
        private Snapshot Copy(Record value) => value == null ? new Snapshot { status = "none" } : new Snapshot {
            captureId = value.Id, loadedSaveGuid = value.SaveGuid, status = value.Status, error = value.Error,
            width = value.Width, cameraInstanceId = value.Camera, createdAtUtc = value.Created.ToString("o"),
            startedAtUtc = value.Started?.ToString("o"), completedAtUtc = value.Finished?.ToString("o"),
            resultAvailable = value.Status == "completed" && value.Png != null,
            mayHaveRendered = value.Started.HasValue, diagnostics = value.Diagnostics
        };
        internal bool TryBegin(string saveGuid, int camera, int width, out Snapshot snapshot)
        {
            if (!Guid.TryParseExact(saveGuid, "N", out _) || camera == 0 || width < 64 || width > 3840)
                throw new ArgumentException("invalid_native_capture_identity_or_size");
            lock (m_Gate) {
                ExpireQueued();
                if (m_Current?.Status == "queued" || m_Current?.Status == "running") { snapshot = Copy(m_Current); return false; }
                m_Current = new Record { Id = Guid.NewGuid().ToString("N"), SaveGuid = saveGuid, Camera = camera,
                    Width = width, Status = "queued", Created = m_Clock() };
                snapshot = Copy(m_Current); return true;
            }
        }
        private bool Matches(string id) => m_Current != null && string.Equals(id, m_Current.Id, StringComparison.OrdinalIgnoreCase);
        internal bool TryMarkRunning(string id)
        {
            lock (m_Gate) {
                ExpireQueued();
                if (!Matches(id) || m_Current.Status != "queued") return false;
                m_Current.Status = "running"; m_Current.Started = m_Clock(); return true;
            }
        }
        internal bool Complete(string id, byte[] png, object diagnostics)
        {
            if (png == null || png.Length < 8 || png.Length > 32 * 1024 * 1024
                || png[0] != 137 || png[1] != 80 || png[2] != 78 || png[3] != 71
                || png[4] != 13 || png[5] != 10 || png[6] != 26 || png[7] != 10)
                throw new ArgumentException("native_capture_result_not_bounded_png");
            lock (m_Gate) {
                if (!Matches(id) || m_Current.Status != "running") return false;
                m_Current.Png = png; m_Current.Diagnostics = diagnostics;
                m_Current.Status = "completed"; m_Current.Finished = m_Clock(); return true;
            }
        }
        internal void Fail(string id, string reason)
        {
            lock (m_Gate) {
                if (!Matches(id) || (m_Current.Status != "queued" && m_Current.Status != "running")) return;
                m_Current.Status = "failed"; m_Current.Error = reason;
                m_Current.Png = null; m_Current.Finished = m_Clock();
            }
        }
        internal void CancelAndClearResult(string reason)
        {
            lock (m_Gate) {
                if (m_Current == null) return;
                m_Current.Png = null; m_Current.Diagnostics = null; m_Current.Status = "cancelled";
                m_Current.Error = reason; m_Current.Finished = m_Clock();
            }
        }
        internal Snapshot Read(string id, out byte[] png)
        {
            lock (m_Gate) {
                ExpireQueued(); png = null;
                if (!Matches(id)) return new Snapshot { captureId = id, status = "not_found", error = "capture_not_found_or_replaced" };
                if (m_Current.Status == "completed") png = m_Current.Png;
                return Copy(m_Current);
            }
        }
        internal Snapshot ReadLatest()
        {
            lock (m_Gate) { ExpireQueued(); return Copy(m_Current); }
        }
    }
}
