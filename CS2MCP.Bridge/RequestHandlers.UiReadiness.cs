using System;
using Colossal;
using Game.SceneFlow;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        // 只读取原生覆盖屏及同一LoadingScreen使用的三组任务，不清除覆盖屏、任务、错误或暂停屏障。
        private (bool ready, object state) ReadNativeUiLoadReadiness()
        {
            try
            {
                var manager = GameManager.instance;
                var overlay = manager?.userInterface?.overlayBindings;
                var tasks = TaskManager.instance;
                if (overlay == null || tasks == null)
                    return (false, new { available = false, readyForRuntimeSettings = false, reason = "native_ui_or_task_manager_unavailable" });
                string screen = overlay.currentlyActiveScreen.ToString();
                float a = tasks.GetTaskProgress(ProgressTracker.Group.Group1);
                float b = tasks.GetTaskProgress(ProgressTracker.Group.Group2);
                float c = tasks.GetTaskProgress(ProgressTracker.Group.Group3);
                bool ready = !manager.isGameLoading && manager.gameMode == Game.GameMode.Game
                    && UiLoadReadinessPolicy.IsReady(screen, a, b, c);
                return (ready, new { available = true, readyForRuntimeSettings = ready,
                    overlayScreen = screen, nativeTaskProgress = new { group1 = FiniteProgress(a), group2 = FiniteProgress(b), group3 = FiniteProgress(c) },
                    note = "仅原生加载覆盖屏/任务完成证据；菜单、错误和保存暂停另看clockControl，不消除任何屏障" });
            }
            catch (Exception error)
            {
                return (false, new { available = false, readyForRuntimeSettings = false,
                    reason = "native_ui_read_failed", errorType = error.GetType().Name });
            }
        }

        private static float? FiniteProgress(float progress)
        {
            return float.IsNaN(progress) || float.IsInfinity(progress) ? (float?)null : progress;
        }
    }
}
