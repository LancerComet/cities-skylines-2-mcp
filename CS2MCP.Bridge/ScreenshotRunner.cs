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

        public void Save(BridgeRequest request, string name)
        {
            StartCoroutine(SaveRoutine(request, name));
        }

        private IEnumerator SaveRoutine(BridgeRequest request, string name)
        {
            yield return new WaitForEndOfFrame();
            Texture2D captured = null;
            Texture2D preview = null;
            System.Threading.Tasks.Task<bool> pendingSave = null;
            try {
                if (!Application.isFocused || Camera.main == null || Screen.width < 320 || Screen.height < 200
                    || Screen.width > 8192 || Screen.height > 8192)
                    throw new InvalidOperationException("保存需要已渲染的游戏帧；先正常恢复游戏窗口");
                captured = ScreenCapture.CaptureScreenshotAsTexture();
                if (captured == null) throw new InvalidOperationException("无法获取游戏预览帧");
                preview = Downscale(captured, 680);
                var world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
                var menu = world?.GetExistingSystemManaged<Game.UI.Menu.MenuUISystem>();
                if (menu == null) throw new InvalidOperationException("原生保存菜单未就绪");
                pendingSave = Game.SceneFlow.GameManager.instance.Save(name, menu.GetSaveInfo(autoSave: false),
                    Colossal.IO.AssetDatabase.AssetDatabase.user, preview);
            } catch (Exception e) {
                request.Complete(BridgeResponse.Error(500, $"保存未启动：{e.GetType().Name}: {e.Message}"));
            }
            // 纹理在原生异步GPU读回完成前保留，随后释放；不在异步任务期间再次渲染相机。
            if (pendingSave != null) {
                while (!pendingSave.IsCompleted) yield return null;
                if (pendingSave.IsFaulted || pendingSave.IsCanceled)
                    request.Complete(BridgeResponse.Error(500, "原生保存管线失败；结果可能已部分应用，读取主档与日志，不能盲重试"));
                else request.Complete(BridgeResponse.Json(new { name, pipelineCompleted = true,
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

        private static Texture2D Downscale(Texture2D source, int targetWidth)
        {
            int targetHeight = Mathf.Max(1, Mathf.RoundToInt((float)source.height * targetWidth / source.width));
            RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var result = new Texture2D(targetWidth, targetHeight, TextureFormat.RGB24, false);
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
