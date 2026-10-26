using System;

namespace CS2MCP
{
    /// <summary>有界请求的短期工作集估计，不是预留系统份额、未来城市增长量或实测峰值。</summary>
    internal sealed class MemoryOperationBudget
    {
        internal const ulong MiB = 1024UL * 1024;
        public string operation, estimateBasis;
        public ulong estimatedAdditionalBytes;
        public bool isEstimate = true;

        internal static MemoryOperationBudget Ordinary() => Construction(0, 0, false, false);
        internal static MemoryOperationBudget Construction(int payloadBytes, int networkElements, bool cleanup, bool terrain)
        {
            // JSON、ECS预览/应用和撤销会生成临时数据；估计只随本批规模增长。
            // 清理采用较小控制工作集，不因旧占用较大而锁死取消/释放路径。
            ulong control = cleanup ? 8 * MiB : terrain ? 128 * MiB : 64 * MiB;
            ulong perElement = cleanup ? 64UL * 1024 : 2 * MiB;
            return new MemoryOperationBudget
            {
                operation = cleanup ? "native_cleanup" : terrain ? "native_terrain_edit" : networkElements > 0 ? "native_network_batch" : "native_construction",
                estimatedAdditionalBytes = checked(control + (ulong)Math.Max(0, payloadBytes) * 8 + (ulong)Math.Max(0, networkElements) * perElement),
                estimateBasis = "bounded control/native preview workspace + UTF8 payload copies + per-network-element workspace; estimate, not measured native peak",
            };
        }
        internal static MemoryOperationBudget Capture(int width, int height)
        {
            if (width < 1 || height < 1 || width > 8192 || height > 8192) throw new ArgumentOutOfRangeException("capture dimensions");
            return new MemoryOperationBudget
            {
                operation = "native_image_capture",
                // 场景色/深度、输出、RGB读回及PNG编码的多份缓冲；不按整个城市或系统总额估算。
                estimatedAdditionalBytes = checked((ulong)width * (ulong)height * 32 + 8 * MiB),
                estimateBasis = "32 bytes per pixel for temporary render/readback/encoding buffers + 8MiB control workspace; renderer caches remain separately observed",
            };
        }

        internal static MemoryOperationBudget FrameOutput(int width, int height)
        {
            if (width < 1 || height < 1 || width > 8192 || height > 8192) throw new ArgumentOutOfRangeException("output dimensions");
            return new MemoryOperationBudget {
                operation = "native_background_frame_output",
                estimatedAdditionalBytes = checked((ulong)width * (ulong)height * 16 + 32 * MiB),
                estimateBasis = "one reusable native color/depth target with format allowance + 32MiB control; no CPU image or encoder buffers; renderer caches separately observed",
            };
        }
    }
}
