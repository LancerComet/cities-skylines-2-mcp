using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace CS2MCP
{
    /// <summary>
    /// 仅读取Unity已完成的游戏帧，不读取桌面，不主动重复Camera.Render。
    /// 隐藏窗口没有可用游戏帧时拒绝截图；不以存档预览的八次渲染补偿。
    /// Completes the BridgeRequest itself, so the handler returns no response.
    /// </summary>
    public sealed class ScreenshotRunner : MonoBehaviour
    {
        private static ScreenshotRunner s_Instance;
        private sealed class DeferredSaveTextures
        {
            public System.Threading.Tasks.Task Pending;
            public Texture2D Captured, Preview;
        }
        private readonly List<DeferredSaveTextures> m_DeferredSaveTextures = new List<DeferredSaveTextures>();

        // 协程中断不能销毁原生保存仍在借用的纹理；在Unity主线程等待借用结束后释放。
        private void Update()
        {
            for (int i = m_DeferredSaveTextures.Count - 1; i >= 0; i--) {
                var cleanup = m_DeferredSaveTextures[i];
                if (!cleanup.Pending.IsCompleted) continue;
                _ = cleanup.Pending.Exception; // 观察任务异常，不触发未观察异常回调。
                ReleaseSaveTextures(cleanup.Captured, cleanup.Preview);
                m_DeferredSaveTextures.RemoveAt(i);
            }
        }

        private static void ReleaseSaveTextures(Texture2D captured, Texture2D preview)
        {
            if (preview != null && !ReferenceEquals(preview, captured)) Destroy(preview);
            if (captured != null) Destroy(captured);
        }

        private static bool HasUsableGameFrame()
        {
            return Application.isFocused && Camera.main != null && Screen.width >= 320 && Screen.height >= 200
                && Screen.width <= 8192 && Screen.height <= 8192;
        }

        public static ScreenshotRunner Ensure()
        {
            if (s_Instance == null)
            {
                var host = new GameObject("CS2MCP.ScreenshotRunner")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                DontDestroyOnLoad(host);
                s_Instance = host.AddComponent<ScreenshotRunner>();
            }
            return s_Instance;
        }

        public void Capture(BridgeRequest request)
        {
            // 隐藏窗口可能不产生EndOfFrame；先拒绝，避免留下永远等帧的请求。
            if (!HasUsableGameFrame()) {
                request.Complete(BridgeResponse.Error(409, "当前没有可核验游戏帧；不能在隐藏后台截图"));
                return;
            }
            StartCoroutine(CaptureRoutine(request));
        }

        public void Save(BridgeRequest request, string name, bool includePreview)
        {
            if (includePreview && !HasUsableGameFrame()) {
                request.Complete(BridgeResponse.Json(new { error = "后台保存应显式includePreview=false，原生保存未启动",
                    mayHaveApplied = false, safeToRetry = false, failurePhase = "before_native_save" }, 409));
                return;
            }
            StartCoroutine(SaveRoutine(request, name, includePreview));
        }

        private IEnumerator SaveRoutine(BridgeRequest request, string name, bool includePreview)
        {
            Texture2D captured = null;
            Texture2D preview = null;
            System.Threading.Tasks.Task<bool> pendingSave = null;
            bool reported = false;
            try {
              if (includePreview) yield return new WaitForEndOfFrame();
              else yield return null;
              try {
                if (includePreview) {
                    if (!Application.isFocused || Camera.main == null || Screen.width < 320 || Screen.height < 200
                        || Screen.width > 8192 || Screen.height > 8192)
                        throw new InvalidOperationException("当前没有可用游戏帧；后台保存请使用includePreview=false");
                    captured = ScreenCapture.CaptureScreenshotAsTexture();
                    if (captured == null) throw new InvalidOperationException("无法获取游戏预览帧；后台保存请使用includePreview=false");
                    // 原生纹理导入为RGBA32；在异步GPU读回结束前保留纹理。
                    preview = Downscale(captured, 680, TextureFormat.RGBA32);
                }
                var world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
                var menu = world?.GetExistingSystemManaged<Game.UI.Menu.MenuUISystem>();
                if (menu == null) throw new InvalidOperationException("原生保存菜单未就绪");
                // GameManager.Save的原生AsyncRequest重载明确支持null：仍正常序列化与打包，省略预览资产。
                // 隐藏窗口不强制渲染，也不拿旧缩略图冒充当前城市画面。
                pendingSave = includePreview
                    ? Game.SceneFlow.GameManager.instance.Save(name, menu.GetSaveInfo(autoSave: false), Colossal.IO.AssetDatabase.AssetDatabase.user, preview)
                    : Game.SceneFlow.GameManager.instance.Save(name, menu.GetSaveInfo(autoSave: false), Colossal.IO.AssetDatabase.AssetDatabase.user, (Game.UI.ScreenCaptureHelper.AsyncRequest)null);
            } catch (Exception e) {
                request.Complete(BridgeResponse.Json(new { error = $"保存未启动：{e.GetType().Name}: {e.Message}",
                    mayHaveApplied = false, safeToRetry = false, failurePhase = "before_native_save" }, 500));
                reported = true;
            }
            // 纹理在原生异步GPU读回完成前保留，随后释放；不在异步任务期间再次渲染相机。
            if (pendingSave != null) {
                while (!pendingSave.IsCompleted) yield return null;
                if (pendingSave.IsFaulted || pendingSave.IsCanceled) {
                    // 明确读取Exception使任务异常已观察，不交给全局UnobservedTaskException弹窗。
                    var failure = pendingSave.Exception?.GetBaseException();
                    request.Complete(BridgeResponse.Json(new { error = $"原生保存管线失败：{failure?.GetType().Name ?? "Canceled"}；读取主档与日志，不能盲重试",
                        mayHaveApplied = true, safeToRetry = false, failurePhase = "native_save_pipeline" }, 500));
                }
                else request.Complete(BridgeResponse.Json(new { name, pipelineCompleted = true, previewIncluded = includePreview,
                    note = "原生异步保存管线已结束；仍须核文件更新时间、完整性及独立备份，不以原生Task返回值宣称落盘成功" }));
                reported = true;
            }
            } finally {
                if (pendingSave != null && !pendingSave.IsCompleted) {
                    m_DeferredSaveTextures.Add(new DeferredSaveTextures { Pending = pendingSave, Captured = captured, Preview = preview });
                } else {
                    if (pendingSave != null) _ = pendingSave.Exception;
                    ReleaseSaveTextures(captured, preview);
                }
                if (!reported) request.Complete(BridgeResponse.Json(new { error = "保存协程已中断；核对实际主档，不能盲重试",
                    mayHaveApplied = pendingSave != null, safeToRetry = false,
                    failurePhase = pendingSave != null ? "native_save_interrupted" : "before_native_save" }, 503));
            }
        }

        private IEnumerator CaptureRoutine(BridgeRequest request)
        {
            yield return new WaitForEndOfFrame();

            Texture2D captured = null;
            Texture2D output = null;
            RenderTexture previousTarget = RenderTexture.active;
            try
            {
                if (!Application.isFocused || Camera.main == null || Screen.width < 320 || Screen.height < 200
                    || Screen.width > 8192 || Screen.height > 8192)
                    throw new InvalidOperationException("游戏没有可核实的前台渲染帧；先恢复游戏窗口再截图");
                captured = ScreenCapture.CaptureScreenshotAsTexture();
                if (captured == null) throw new InvalidOperationException("游戏帧捕获失败");

                output = captured;
                if (request.TryGetInt("width", out int width) && width > 0 && width < captured.width)
                {
                    output = Downscale(captured, width);
                }

                byte[] png = ImageConversion.EncodeToPNG(output);
                if (png == null || png.Length == 0)
                {
                    request.Complete(BridgeResponse.Error(500, "PNG encode failed"));
                }
                else
                {
                    request.Complete(BridgeResponse.Png(png));
                }
            }
            catch (Exception e)
            {
                request.Complete(BridgeResponse.Error(500, $"screenshot failed: {e.GetType().Name}: {e.Message}"));
            }
            finally
            {
                RenderTexture.active = previousTarget;
                if (output != null && !ReferenceEquals(output, captured))
                {
                    Destroy(output);
                }
                if (captured != null)
                {
                    Destroy(captured);
                }
            }
        }

        private static Texture2D Downscale(Texture2D source, int targetWidth, TextureFormat format = TextureFormat.RGB24)
        {
            int targetHeight = Mathf.Max(1, Mathf.RoundToInt((float)source.height * targetWidth / source.width));
            RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0);
            RenderTexture previous = RenderTexture.active;
            Texture2D result = null;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                result = new Texture2D(targetWidth, targetHeight, format, false);
                result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
                result.Apply();
                return result;
            }
            catch
            {
                // 返回前的纹理由本方法持有；成功返回后仍由截图/保存调用方释放。
                if (result != null) Destroy(result);
                throw;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
