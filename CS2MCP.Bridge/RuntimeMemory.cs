using System;
using System.Runtime.InteropServices;

namespace CS2MCP
{
    /// <summary>只读进程及可用提交预算；批量原生网络操作前留出保存/退出所需余量。</summary>
    internal static class RuntimeMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Length, Load;
            public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
                TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemory
        {
            public uint Length, PageFaultCount;
            public UIntPtr PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool,
                PeakNonPagedPool, NonPagedPool, PageFile, PeakPageFile, PrivateUsage;
        }
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemory counters, uint size);

        internal static Snapshot Read()
        {
            var snapshot = new Snapshot();
            try {
                snapshot.managedHeapBytes = GC.GetTotalMemory(false);
                if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
                    // Unity Mono的Process内存属性可能恒为0；以Windows只读计数器核验，不把0冒充实际占用。
                    var counters = new ProcessMemory { Length = (uint)Marshal.SizeOf(typeof(ProcessMemory)) };
                    if (!GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Length)) {
                        snapshot.unavailableReason = "process_counter_unavailable:" + Marshal.GetLastWin32Error();
                        return snapshot;
                    }
                    snapshot.privateMemoryBytes = (long)counters.PrivateUsage.ToUInt64();
                    snapshot.workingSetBytes = (long)counters.WorkingSet.ToUInt64();
                    var status = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
                    if (!GlobalMemoryStatusEx(ref status)) {
                        snapshot.unavailableReason = "commit_counter_unavailable:" + Marshal.GetLastWin32Error();
                        return snapshot;
                    }
                    snapshot.SetCommitBudget(status.TotalPageFile, status.AvailablePageFile, snapshot.privateMemoryBytes.Value);
                } else snapshot.unavailableReason = "unsupported_platform_for_native_memory_counters";
            } catch (Exception error) { snapshot.unavailableReason = error.GetType().Name; }
            return snapshot;
        }
        internal sealed class Snapshot
        {
            public long? privateMemoryBytes, workingSetBytes;
            public long managedHeapBytes;
            public ulong? commitLimitBytes, availableCommitBytes;
            // 未知计数器不能当作内存充足；读取、保存及退出不受施工路由保护影响。
            public bool constructionAllowed = false;
            public string unavailableReason;
            internal void SetCommitBudget(ulong limit, ulong available, long privateBytes)
            {
                commitLimitBytes = limit;
                availableCommitBytes = available;
                privateMemoryBytes = privateBytes;
                constructionAllowed = false;
                if (limit == 0 || available > limit || privateBytes <= 0) {
                    unavailableReason = "invalid_native_memory_counters";
                    return;
                }
                unavailableReason = null;
                ulong reserve = Math.Max(2UL * 1024 * 1024 * 1024, limit / 100 * 15);
                constructionAllowed = available > reserve && (ulong)privateBytes < limit / 100 * 65;
            }
            public string rule = "重建/预览前核真实计数器、保留至少15%提交预算且进程低于预算65%；计数器未知拒绝施工，不足先保存正常退出；不是泄漏已修复证明";
        }
    }
}
