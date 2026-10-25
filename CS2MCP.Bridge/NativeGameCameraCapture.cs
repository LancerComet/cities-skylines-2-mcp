using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CS2MCP
{
    /// <summary>沿本机原生存档预览的公开相机路径取一次景；不读取桌面，不运行八次预览渲染。</summary>
    internal static class NativeGameCameraCapture
    {
        private static int s_Attempts, s_Completions, s_RenderCalls;
        private static int? s_LastCamera, s_LastWidth, s_LastHeight;
        private static string s_LastTime, s_LastFailure;
        private static bool? s_CameraRestored, s_ActiveTargetRestored, s_ResourcesReleased;

        internal static object Read() => new {
            source = "native_game_camera", attempts = s_Attempts, encodedImages = s_Completions,
            actualNativeCameraRenderCalls = s_RenderCalls, lastCameraInstanceId = s_LastCamera,
            lastWidth = s_LastWidth, lastHeight = s_LastHeight, lastCapturedAtUtc = s_LastTime,
            lastFailure = s_LastFailure, cameraTargetAndForceRestored = s_CameraRestored,
            activeRenderTargetRestored = s_ActiveTargetRestored, renderTargetsReleasedAndDestructionQueued = s_ResourcesReleased,
            note = "一次公开Camera.Render沿本机原生预览入口；仅游戏相机画面，无桌面/原生UI。不证明实际瓦数或内存根因修复。"
        };

        internal static void ResetAfterLoad()
        {
            s_Attempts = s_Completions = s_RenderCalls = 0;
            s_LastCamera = s_LastWidth = s_LastHeight = null;
            s_LastTime = s_LastFailure = null;
            s_CameraRestored = s_ActiveTargetRestored = s_ResourcesReleased = null;
        }

        internal static byte[] Capture(Camera camera, int width)
        {
            if (camera == null || camera.cameraType != CameraType.Game || !camera.enabled)
                throw new InvalidOperationException("native_game_camera_unavailable");
            if (width < 64 || width > 3840) throw new ArgumentOutOfRangeException(nameof(width));
            double aspect = camera.aspect;
            if (double.IsNaN(aspect) || double.IsInfinity(aspect) || aspect < 0.25 || aspect > 4)
                throw new InvalidOperationException("native_camera_aspect_unavailable");
            int height = Math.Max(1, (int)Math.Round(width / aspect));
            if (height > 2160 || (long)width * height > 3840L * 2160)
                throw new InvalidOperationException("native_capture_pixel_budget_exceeded");
            var asset = QualitySettings.GetRenderPipelineAssetAt(QualitySettings.GetQualityLevel()) as HDRenderPipelineAsset;
            if (asset == null) throw new InvalidOperationException("native_hdrp_asset_unavailable");
            var shader = Shader.Find("Hidden/ScreenCaptureCompose");
            if (shader == null) throw new InvalidOperationException("native_capture_compose_shader_unavailable");

            var previousCameraTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            bool previousForce = camera.forceIntoRenderTexture;
            RenderTexture scene = null, output = null;
            Texture2D pixels = null;
            Material compose = null;
            s_Attempts++;
            s_LastCamera = camera.GetInstanceID(); s_LastWidth = width; s_LastHeight = height;
            s_LastTime = DateTime.UtcNow.ToString("o"); s_LastFailure = null;
            s_CameraRestored = s_ActiveTargetRestored = s_ResourcesReleased = null;
            try {
                // 本机Game.UI.ScreenCaptureHelper也先完成此原生灯光作业，再渲染存档预览相机。
                HDRPDotsInputs.punctualLightsJobHandle.Complete();
                var format = (GraphicsFormat)asset.currentPlatformRenderPipelineSettings.colorBufferFormat;
                scene = new RenderTexture(width, height, 16, format, 0) { name = "CS2MCP.NativeCapture.Scene", hideFlags = HideFlags.HideAndDontSave };
                if (!scene.Create()) throw new InvalidOperationException("native_capture_scene_target_failed");
                output = Game.UI.ScreenCaptureHelper.CreateRenderTarget("CS2MCP.NativeCapture.Output", width, height);
                if (output == null || !output.IsCreated()) throw new InvalidOperationException("native_capture_output_target_failed");
                compose = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                compose.DisableKeyword("STYLIZE"); compose.SetFloat("_Radius", 0);
                camera.forceIntoRenderTexture = true;
                camera.targetTexture = scene;
                s_RenderCalls++;
                camera.Render();
                Graphics.Blit(scene, output, compose, 0);
                RenderTexture.active = output;
                pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
                pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                pixels.Apply(false, false);
                var png = ImageConversion.EncodeToPNG(pixels);
                if (png == null || png.Length < 8) throw new InvalidOperationException("native_capture_png_failed");
                s_Completions++;
                return png;
            } catch (Exception e) {
                s_LastFailure = e.GetType().Name + ": " + e.Message;
                throw;
            } finally {
                // 无论渲染、读回或编码在哪一步失败，先恢复玩家原相机和全局当前目标。
                try {
                    camera.targetTexture = previousCameraTarget;
                    camera.forceIntoRenderTexture = previousForce;
                    s_CameraRestored = camera.targetTexture == previousCameraTarget && camera.forceIntoRenderTexture == previousForce;
                } finally {
                    RenderTexture.active = previousActive;
                    s_ActiveTargetRestored = RenderTexture.active == previousActive;
                    if (scene != null) { scene.Release(); UnityEngine.Object.Destroy(scene); }
                    if (output != null) { output.Release(); UnityEngine.Object.Destroy(output); }
                    if (pixels != null) UnityEngine.Object.Destroy(pixels);
                    if (compose != null) UnityEngine.Object.Destroy(compose);
                    s_ResourcesReleased = true;
                }
            }
        }
    }
}
