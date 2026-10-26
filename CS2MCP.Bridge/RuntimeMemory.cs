using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CS2MCP
{
    /// <summary>只读Windows容量；系统提交、进程可提交及物理驻留分别报告，不控制游戏生命周期。</summary>
    internal static class RuntimeMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Length, Load;
            public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
                TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct PerformanceInfo
        {
            public uint Length;
            public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
                SystemCache, KernelTotal, KernelPaged, KernelNonPaged, PageSize;
            public uint HandleCount, ProcessCount, ThreadCount;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemory
        {
            public uint Length, PageFaultCount;
            public UIntPtr PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool,
                PeakNonPagedPool, NonPagedPool, PageFile, PeakPageFile, PrivateUsage;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPerformanceInfo(ref PerformanceInfo info, uint size);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemory counters, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateMemoryResourceNotification(int type);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryMemoryResourceNotification(IntPtr handle, [MarshalAs(UnmanagedType.Bool)] out bool state);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        internal static Snapshot Read()
        {
            var snapshot = new Snapshot();
            var warnings = new List<string>();
            try { snapshot.managedHeapBytes = GC.GetTotalMemory(false); }
            catch (Exception error) { warnings.Add("managed_counter:" + error.GetType().Name); }
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                warnings.Add("unsupported_platform_for_native_memory_counters");
            else
            {
                // 独立采集；PrivateUsage未知不能丢掉有效的系统容量，Mono的Process属性0也不冒充占用。
                try
                {
                    var counters = new ProcessMemory { Length = (uint)Marshal.SizeOf(typeof(ProcessMemory)) };
                    if (GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Length))
                    {
                        ulong value = counters.PrivateUsage.ToUInt64();
                        if (value > 0 && value <= long.MaxValue) snapshot.privateMemoryBytes = (long)value;
                        else warnings.Add("process_private_counter_unavailable");
                        value = counters.WorkingSet.ToUInt64();
                        if (value > 0 && value <= long.MaxValue) snapshot.workingSetBytes = (long)value;
                        else warnings.Add("process_working_set_counter_unavailable");
                    }
                    else warnings.Add("process_counter:" + Marshal.GetLastWin32Error());
                }
                catch (Exception error) { warnings.Add("process_counter:" + error.GetType().Name); }
                try
                {
                    var info = new PerformanceInfo { Length = (uint)Marshal.SizeOf(typeof(PerformanceInfo)) };
                    if (GetPerformanceInfo(ref info, info.Length))
                    {
                        ulong page = info.PageSize.ToUInt64();
                        if (page == 0) throw new InvalidOperationException("zero_page_size");
                        snapshot.SetSystemCommit(checked(info.CommitLimit.ToUInt64() * page), checked(info.CommitTotal.ToUInt64() * page));
                        snapshot.systemCommitPeakBytes = checked(info.CommitPeak.ToUInt64() * page);
                        snapshot.totalPhysicalBytes = checked(info.PhysicalTotal.ToUInt64() * page);
                        snapshot.availablePhysicalBytes = checked(info.PhysicalAvailable.ToUInt64() * page);
                    }
                    else warnings.Add("system_commit_counter:" + Marshal.GetLastWin32Error());
                }
                catch (Exception error) { warnings.Add("system_commit_counter:" + error.GetType().Name); }
                try
                {
                    var status = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
                    if (GlobalMemoryStatusEx(ref status))
                    {
                        snapshot.SetProcessAvailability(status.TotalPageFile, status.AvailablePageFile, status.AvailableVirtual);
                        snapshot.physicalMemoryLoadPercent = status.Load;
                        if (!snapshot.totalPhysicalBytes.HasValue) snapshot.totalPhysicalBytes = status.TotalPhysical;
                        if (!snapshot.availablePhysicalBytes.HasValue) snapshot.availablePhysicalBytes = status.AvailablePhysical;
                    }
                    else warnings.Add("process_commit_counter:" + Marshal.GetLastWin32Error());
                }
                catch (Exception error) { warnings.Add("process_commit_counter:" + error.GetType().Name); }
                IntPtr notification = IntPtr.Zero;
                try
                {
                    notification = CreateMemoryResourceNotification(0); // Windows低物理内存信号仅告警。
                    if (notification != IntPtr.Zero && QueryMemoryResourceNotification(notification, out bool low))
                        snapshot.physicalMemoryLow = low;
                    else warnings.Add("physical_pressure_signal_unavailable");
                }
                catch (Exception error) { warnings.Add("physical_pressure_signal:" + error.GetType().Name); }
                finally { if (notification != IntPtr.Zero) CloseHandle(notification); }
            }
            snapshot.counterWarnings = warnings.ToArray();
            snapshot.Recalculate();
            return snapshot;
        }

        internal sealed class Snapshot
        {
            public int policyVersion = 2;
            public long? privateMemoryBytes, workingSetBytes;
            public long managedHeapBytes;
            public ulong? systemCommittedBytes, systemCommitLimitBytes, systemAvailableCommitBytes, systemCommitPeakBytes;
            public ulong? processCommitLimitBytes, processAvailableCommitBytes, availableVirtualAddressBytes;
            public ulong? commitLimitBytes, availableCommitBytes, totalPhysicalBytes, availablePhysicalBytes;
            public uint? physicalMemoryLoadPercent;
            public bool? physicalMemoryLow;
            public double? systemCommitFraction;
            public string[] counterWarnings = Array.Empty<string>();
            public string commitBudgetSource = "unknown";
            public string unavailableReason = "capacity_counter_unavailable";
            public string pressureLevel = "unknown";
            // 兼容字段只代表普通小操作；实际请求仍按本批规模单独判断。
            public bool constructionAllowed;
            public string constructionFailureReason = "capacity_counter_unavailable";
            public ulong? requiredCommitReserveBytes = 0, privateCommitCeilingBytes = null;
            public ulong ordinaryOperationEstimateBytes = MemoryOperationBudget.Ordinary().estimatedAdditionalBytes;
            public bool automaticRestartRecommended = false;
            public string rule = "按本次操作额外分配估计与当前有效可提交/地址空间判断；物理压力和高提交比例仅告警，无15%/2GiB储备或65%进程份额硬门槛，不自动暂停/退出/重启。";

            internal void SetSystemCommit(ulong limit, ulong committed)
            {
                if (limit == 0 || committed > limit) return;
                systemCommitLimitBytes = limit;
                systemCommittedBytes = committed;
                systemAvailableCommitBytes = limit - committed;
            }
            internal void SetProcessAvailability(ulong limit, ulong available, ulong availableVirtual)
            {
                if (limit == 0 || available > limit) return;
                processCommitLimitBytes = limit;
                processAvailableCommitBytes = available;
                availableVirtualAddressBytes = availableVirtual;
            }
            internal void Recalculate()
            {
                commitLimitBytes = systemCommitLimitBytes ?? processCommitLimitBytes;
                availableCommitBytes = systemAvailableCommitBytes.HasValue && processAvailableCommitBytes.HasValue
                    ? Math.Min(systemAvailableCommitBytes.Value, processAvailableCommitBytes.Value)
                    : systemAvailableCommitBytes ?? processAvailableCommitBytes;
                commitBudgetSource = systemAvailableCommitBytes.HasValue
                    ? processAvailableCommitBytes.HasValue ? "GetPerformanceInfo(system pages) + GlobalMemoryStatusEx(process capacity); admission=min(available)"
                        : "GetPerformanceInfo(system pages); process capacity unavailable"
                    : processAvailableCommitBytes.HasValue ? "GlobalMemoryStatusEx(process capacity fallback); system commit unknown" : "unknown";
                systemCommitFraction = systemCommittedBytes.HasValue && systemCommitLimitBytes > 0
                    ? (double?)systemCommittedBytes.Value / systemCommitLimitBytes.Value : null;
                unavailableReason = availableCommitBytes.HasValue ? null : "capacity_counter_unavailable";
                var ordinary = Assess(MemoryOperationBudget.Ordinary());
                constructionAllowed = ordinary.allowed;
                constructionFailureReason = ordinary.reason;
                pressureLevel = ordinary.pressureLevel;
            }
            internal Admission Assess(MemoryOperationBudget operation)
            {
                string reason = !availableCommitBytes.HasValue ? "capacity_counter_unavailable"
                    : operation.estimatedAdditionalBytes > availableCommitBytes.Value ? "operation_commit_capacity"
                    : availableVirtualAddressBytes.HasValue && operation.estimatedAdditionalBytes > availableVirtualAddressBytes.Value
                        ? "operation_address_space" : null;
                bool warning = physicalMemoryLow == true || systemCommitFraction >= .9
                    || availableCommitBytes.HasValue && availableCommitBytes.Value < operation.estimatedAdditionalBytes * 2;
                return new Admission
                {
                    allowed = reason == null, reason = reason,
                    pressureLevel = reason != null ? availableCommitBytes.HasValue ? "critical_for_operation" : "unknown" : warning ? "warning" : "normal",
                    operationBudget = operation, effectiveAvailableCommitBytes = availableCommitBytes,
                    availableVirtualAddressBytes = availableVirtualAddressBytes,
                    recommendedAction = reason == null ? warning ? "continue_bounded_operations_and_observe" : "continue"
                        : "reduce_operation_or_wait_then_observe_current_instance",
                };
            }
        }
        internal sealed class Admission
        {
            public bool allowed;
            public string reason, pressureLevel, recommendedAction;
            public MemoryOperationBudget operationBudget;
            public ulong? effectiveAvailableCommitBytes, availableVirtualAddressBytes;
            public bool automaticPauseRecommended = false, automaticRestartRecommended = false, preserveCurrentInstance = true;
        }
    }
}
