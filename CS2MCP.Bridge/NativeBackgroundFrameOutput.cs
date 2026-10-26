using System;
using System.Threading;
using Game.Rendering;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CS2MCP
{
    /// <summary>原相机的自有持久输出；显式帧循环可补绘缺失帧，不读回像素。</summary>
    internal sealed class NativeBackgroundFrameOutput : IBackgroundFrameOutputTarget, IBackgroundFrameRenderer
    {
        private readonly Camera m_Camera;
        private readonly int m_CameraId, m_Width, m_Height;
        private RenderTexture m_Output;
        private bool m_PreviousForce, m_ForceAssigned, m_TargetAssigned, m_Subscribed;
        private bool? m_OriginalStateRestored;
        private bool m_ForeignTargetPreserved;
        private long m_RenderCallbacks;
        private int m_LastNaturalFrame = -1;
        private bool m_OwnRenderInProgress;
        private long m_ManualRenderInvocations;
        private double m_LastLightJobMilliseconds, m_LastRenderMilliseconds;

        private NativeBackgroundFrameOutput(Camera camera)
        {
            m_Camera = camera;
            m_CameraId = camera.GetInstanceID();
            m_Width = camera.pixelWidth;
            m_Height = camera.pixelHeight;
            if (m_Width < 64 || m_Height < 64 || m_Width > 8192 || m_Height > 8192)
                throw new InvalidOperationException("native_output_dimensions_unavailable");
        }

        internal static IBackgroundFrameOutputTarget Resolve(World world)
        {
            var camera = world.GetExistingSystemManaged<CameraUpdateSystem>()?.activeCamera;
            if (camera == null) throw new InvalidOperationException("native_active_game_camera_unavailable");
            return new NativeBackgroundFrameOutput(camera);
        }

        public bool IsApplicable => m_Camera != null && m_Camera.cameraType == CameraType.Game && m_Camera.enabled;
        public bool HasForeignOutput => m_Camera != null && m_Camera.targetTexture != null;
        public bool OwnOutputInstalled => BackgroundFrameOutputPolicy.OwnsTexture(m_Camera != null,
            m_Output != null && m_Output.IsCreated(), m_Camera != null && m_Output != null && m_Camera.targetTexture == m_Output);
        public long RenderCallbacks => Interlocked.Read(ref m_RenderCallbacks);

        public void InstallOwnOutput()
        {
            if (!IsApplicable || HasForeignOutput) throw new InvalidOperationException("native_output_camera_or_ownership_changed");
            var budget = RuntimeMemory.Read().Assess(MemoryOperationBudget.FrameOutput(m_Width, m_Height));
            if (!budget.allowed) throw new InvalidOperationException("native_output_" + budget.reason);
            var asset = QualitySettings.GetRenderPipelineAssetAt(QualitySettings.GetQualityLevel()) as HDRenderPipelineAsset;
            if (asset == null) throw new InvalidOperationException("native_hdrp_asset_unavailable");
            var data = m_Camera.GetComponent<HDAdditionalCameraData>();
            if (data == null || data.fullscreenPassthrough || data.hasCustomRender)
                throw new InvalidOperationException("native_output_camera_renderer_incompatible");
            m_PreviousForce = m_Camera.forceIntoRenderTexture;
            m_Output = new RenderTexture(m_Width, m_Height, 16,
                (GraphicsFormat)asset.currentPlatformRenderPipelineSettings.colorBufferFormat, 0) {
                    name = "CS2MCP.BackgroundFrameOutput", hideFlags = HideFlags.HideAndDontSave
                };
            if (!m_Output.Create()) throw new InvalidOperationException("native_output_target_creation_failed");
            m_Camera.targetTexture = m_Output;
            m_TargetAssigned = true;
            m_ForceAssigned = true;
            m_Camera.forceIntoRenderTexture = true;
            RenderPipelineManager.endCameraRendering += OnCameraRendered;
            m_Subscribed = true;
        }

        private void OnCameraRendered(ScriptableRenderContext context, Camera camera)
        {
            if (camera != m_Camera || !OwnOutputInstalled) return;
            Interlocked.Increment(ref m_RenderCallbacks);
            if (!m_OwnRenderInProgress) m_LastNaturalFrame = Time.frameCount;
        }

        public bool RenderFrameIfNeeded(int frame)
        {
            // 显式取景独占相机期间让出；正常管线已完成本帧时不重复渲染。
            if (NativeCaptureOperation.Shared.IsInFlight || m_LastNaturalFrame == frame) return false;
            if (m_Camera != null && m_Camera.cameraType == CameraType.Game && !m_Camera.enabled) return false;
            if (!IsApplicable || !OwnOutputInstalled) throw new InvalidOperationException("native_scene_frame_camera_ownership_changed");
            if (m_OwnRenderInProgress) throw new InvalidOperationException("native_scene_frame_reentrant_render");
            var data = m_Camera.GetComponent<HDAdditionalCameraData>();
            if (data == null || data.fullscreenPassthrough || data.hasCustomRender)
                throw new InvalidOperationException("native_scene_frame_renderer_changed");
            var previousActive = RenderTexture.active;
            m_OwnRenderInProgress = true;
            try {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                // 与本机原生存档预览入口一致；在ECS更新之外完成原生灯光作业。
                HDRPDotsInputs.punctualLightsJobHandle.Complete();
                m_LastLightJobMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                started = System.Diagnostics.Stopwatch.GetTimestamp();
                m_ManualRenderInvocations++;
                m_Camera.Render();
                m_LastRenderMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                return true;
            } finally { RenderTexture.active = previousActive; m_OwnRenderInProgress = false; }
        }

        public void RestoreOwnOutput()
        {
            // 先仅撤下自己的订阅；不清空或替换其他模组的事件列表。
            if (m_Subscribed) { RenderPipelineManager.endCameraRendering -= OnCameraRendered; m_Subscribed = false; }
            if (m_Camera != null && m_Output != null && m_Camera.targetTexture == m_Output) {
                m_Camera.targetTexture = null; // 预检只接管原来为null的输出。
                if (m_ForceAssigned && m_Camera.forceIntoRenderTexture) m_Camera.forceIntoRenderTexture = m_PreviousForce;
                m_OriginalStateRestored = m_Camera.targetTexture == null && m_Camera.forceIntoRenderTexture == m_PreviousForce;
            } else if (m_Camera != null && m_TargetAssigned) {
                // 外部已接管时保其目标/force状态；释放自己的纹理不冒称精确恢复了外部相机。
                m_ForeignTargetPreserved = true;
                m_OriginalStateRestored = false;
            }
            if (m_Output != null) { m_Output.Release(); UnityEngine.Object.Destroy(m_Output); m_Output = null; }
            m_ForceAssigned = false;
            m_TargetAssigned = false;
        }

        public object ReadNativeState() => new { cameraInstanceId = m_CameraId,
            cameraAlive = m_Camera != null, cameraEnabled = m_Camera == null ? (bool?)null : m_Camera.enabled,
            forceIntoRenderTexture = m_Camera == null ? (bool?)null : m_Camera.forceIntoRenderTexture,
            outputWidth = m_Width, outputHeight = m_Height, ownTexturePresent = m_Output != null,
            renderCallbacks = RenderCallbacks, originalCameraStateRestored = m_OriginalStateRestored,
            foreignTargetPreserved = m_ForeignTargetPreserved,
            cameraCreated = false, cameraEnabledChanged = false, playerGraphicsSettingsChanged = false,
            screenResolutionChanged = false, imageReadbackInvoked = false,
            manualRenderInvocations = m_ManualRenderInvocations, lastNaturalRenderFrame = m_LastNaturalFrame,
            lastLightJobMilliseconds = m_LastLightJobMilliseconds, lastRenderMilliseconds = m_LastRenderMilliseconds };
    }
}
