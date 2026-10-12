using System.Reflection;
using Game.UI.InGame;
using Game.Simulation;
using Unity.Entities;
using Colossal.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        // 只读原生暂停原因，时钟通过原生UI方法设置；不伪造焦点、不修改游戏或模组程序集。
        private static readonly FieldInfo ClockFocus = typeof(TimeUISystem).GetField("m_HasFocus", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo ClockBarrier = typeof(TimeUISystem).GetProperty("pausedBarrierActive", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo ClockSetSpeed = typeof(TimeUISystem).GetMethod("SetSimulationSpeed", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo ClockSetPaused = typeof(TimeUISystem).GetMethod("SetSimulationPaused", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MilestonePopup = typeof(MilestoneUISystem).GetField("m_UnlockedMilestoneBinding", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PopulationPopup = typeof(MilestoneUISystem).GetField("m_reachedPopulationGoal", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo DismissMilestone = typeof(MilestoneUISystem).GetMethod("ClearUnlockedMilestone", BindingFlags.Instance | BindingFlags.NonPublic);
        private bool m_BackgroundSimulation;
        private float m_RequestedSimulationSpeed = 1f;
        private bool m_LastClockFocus = true;

        private bool ReadClockGate(out TimeUISystem ui, out bool hasFocus, out bool barrier)
        {
            ui = World.GetExistingSystemManaged<TimeUISystem>();
            hasFocus = true; barrier = true;
            if (ui == null || ClockFocus == null || ClockBarrier == null || ClockSetSpeed == null || ClockSetPaused == null) return false;
            hasFocus = (bool)ClockFocus.GetValue(ui);
            barrier = (bool)ClockBarrier.GetValue(ui);
            return true;
        }

        private static void SetNativeClock(TimeUISystem ui, float speed)
        {
            if (speed == 0f) ClockSetPaused.Invoke(ui, new object[] { true });
            else ClockSetSpeed.Invoke(ui, new object[] { speed == 4f ? 2 : speed == 2f ? 1 : 0 });
        }

        private object GetClockControlState()
        {
            bool available = ReadClockGate(out _, out bool focus, out bool barrier);
            return new { backgroundSimulation = m_BackgroundSimulation, requestedSpeed = m_RequestedSimulationSpeed,
                nativeGateAvailable = available, nativeHasFocus = available ? (bool?)focus : null,
                nativePauseBarrier = available ? (bool?)barrier : null,
                activeNativePanel = World.GetExistingSystemManaged<GamePanelUISystem>()?.activePanel?.GetType().FullName,
                note = "后台控制仅本次加载有效；保留原生菜单/保存/错误暂停，帧率上限和实际模拟吞吐独立" };
        }

        // 原生“继续”通知动作；只清除已达到的里程碑提示，不能解锁、加点或清除错误/保存屏障。
        private BridgeResponse MilestonePopupControl(BridgeRequest request)
        {
            if (!TryGetCity(out _, out var error)) return error;
            if (request.Query.ContainsKey("dismiss") && !request.TryGetBool("dismiss", out _))
                return BridgeResponse.Error(400, "dismiss must be boolean");
            var ui = World.GetExistingSystemManaged<MilestoneUISystem>();
            if (ui == null || MilestonePopup == null || PopulationPopup == null || DismissMilestone == null)
                return BridgeResponse.Error(409, "native milestone notification controls unavailable");
            object binding = MilestonePopup.GetValue(ui);
            var valueProperty = binding?.GetType().GetProperty("value");
            if (valueProperty == null) return BridgeResponse.Error(409, "native milestone notification state unavailable");
            var entity = (Entity)valueProperty.GetValue(binding);
            bool populationGoal = (bool)PopulationPopup.GetValue(ui);
            bool pending = entity != Entity.Null || populationGoal;
            if (entity != Entity.Null && (!EntityManager.Exists(entity)
                || EntityManager.HasEnabledComponent<Game.Prefabs.Locked>(entity)))
                return BridgeResponse.Error(409, "notification milestone is not normally unlocked");
            bool dismissed = request.TryGetBool("dismiss", out bool dismiss) && dismiss && pending;
            if (dismissed) DismissMilestone.Invoke(ui, null);
            return BridgeResponse.Json(new { pendingBefore = pending, dismissed,
                milestone = entity == Entity.Null ? null : (object)new { index = entity.Index, version = entity.Version },
                populationGoal, clockControl = GetClockControlState(),
                note = "仅原生已达成通知的继续动作；解锁、资金、错误和保存屏障不变，异步回读时钟确认" });
        }

        public void ResetBackgroundSimulation()
        {
            m_BackgroundSimulation = false;
            m_RequestedSimulationSpeed = 1f;
            m_LastClockFocus = true;
        }

        public void PauseNativeSimulation()
        {
            m_RequestedSimulationSpeed = 0f;
            if (ReadClockGate(out var ui, out _, out _)) SetNativeClock(ui, 0f);
            else World.GetOrCreateSystemManaged<SimulationSystem>().selectedSpeed = 0f;
        }

        /// <summary>原生TimeUI先处理暂停屏障；明确授权后台运行后，仅补偿失焦暂停。</summary>
        public void ReconcileBackgroundSimulation()
        {
            var manager = Game.SceneFlow.GameManager.instance;
            if (!m_BackgroundSimulation || manager == null || manager.isGameLoading || manager.gameMode != Game.GameMode.Game) return;
            if (!ReadClockGate(out var ui, out bool focus, out bool barrier)) {
                m_BackgroundSimulation = false;
                PauseNativeSimulation();
                Mod.Log.Warn("原生时钟资格不可读，已停止后台模拟控制");
                return;
            }
            var sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            if (!barrier) {
                if (!focus) SetNativeClock(ui, m_RequestedSimulationSpeed);
                else {
                    // 显式后台暂停不能在返回前台时被原生的失焦恢复覆盖。
                    if (!m_LastClockFocus && m_RequestedSimulationSpeed == 0f) SetNativeClock(ui, 0f);
                    m_RequestedSimulationSpeed = sim.selectedSpeed;
                }
            }
            m_LastClockFocus = focus;
        }
    }
}
