using System;
using System.Collections;
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
            StartCoroutine(CaptureRoutine(request));
        }

        public void Save(BridgeRequest request, string name, bool includePreview)
        {
            StartCoroutine(SaveRoutine(request, name, includePreview));
        }

        private IEnumerator SaveRoutine(BridgeRequest request, string name, bool includePreview)
        {
            if (includePreview) yield return new WaitForEndOfFrame();
            else yield return null;
            Texture2D captured = null;
            Texture2D preview = null;
            System.Threading.Tasks.Task<bool> pendingSave = null;
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
            }
            // 纹理在原生异步GPU读回完成前保留，随后释放；不在异步任务期间再次渲染相机。
            if (pendingSave != null) {
                while (!pendingSave.IsCompleted) yield return null;
                if (pendingSave.IsFaulted || pendingSave.IsCanceled) {
                    // 明确读取Exception使任务异常已观察，不交给全局UnobservedTaskException弹窗。
                    var failure = pendingSave.Exception?.GetBaseException();
                    request.Complete(BridgeResponse.Error(500, $"原生保存管线失败：{failure?.GetType().Name ?? "Canceled"}；结果可能已部分应用，读取主档与日志，不能盲重试"));
                }
                else request.Complete(BridgeResponse.Json(new { name, pipelineCompleted = true, previewIncluded = includePreview,
                    note = "原生异步保存管线已结束；仍须核文件更新时间、完整性及独立备份，不以原生Task返回值宣称落盘成功" }));
            }
            if (preview != null) Destroy(preview);
            if (captured != null) Destroy(captured);
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
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var result = new Texture2D(targetWidth, targetHeight, format, false);
                result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
                result.Apply();
                return result;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
