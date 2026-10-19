using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.Tools;
using UnityEngine.Scripting;

namespace CS2MCP
{
    /// <summary>
    /// Unsticks the bridge's headless tools. Each one only advances its stages
    /// while it is ToolSystem.activeTool; when two of them are queued in the
    /// same frame (or the player picks a tool), the later activation replaces
    /// the earlier one, which then stays "busy" forever and every build
    /// endpoint answers "another build operation is in progress". When a
    /// bridge tool has been busy but inactive for StuckSeconds, and no other
    /// bridge tool is mid-operation as the active tool, this makes it the
    /// active tool again so it can finish. Reads the tools' private m_Stage
    /// by reflection, so the tool systems themselves are unchanged.
    /// </summary>
    public sealed partial class BridgeToolWatchdogSystem : GameSystemBase
    {
        private const float StuckSeconds = 0.75f;

        private sealed class Watched
        {
            public ToolBaseSystem Tool;
            public FieldInfo Stage;
            public float BusySince = -1f;
        }

        private readonly List<Watched> m_Watched = new List<Watched>();
        private ToolSystem m_ToolSystem;

        public int Reactivations { get; private set; }

        public string LastReactivated { get; private set; }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            Add(World.GetOrCreateSystemManaged<BridgeToolSystem>());
            Add(World.GetOrCreateSystemManaged<BridgeRoadToolSystem>());
            Add(World.GetOrCreateSystemManaged<BridgeTransitToolSystem>());
            Add(World.GetOrCreateSystemManaged<BridgeAreaToolSystem>());
        }

        private void Add(ToolBaseSystem tool)
        {
            FieldInfo stage = tool.GetType().GetField("m_Stage", BindingFlags.Instance | BindingFlags.NonPublic);
            if (stage == null)
            {
                Mod.Log.Warn($"BridgeToolWatchdogSystem: {tool.GetType().Name} has no m_Stage; not watched");
                return;
            }
            m_Watched.Add(new Watched { Tool = tool, Stage = stage });
        }

        private static bool IsBusy(Watched w)
        {
            object value = w.Stage.GetValue(w.Tool);
            return value != null && !string.Equals(value.ToString(), "Idle", StringComparison.Ordinal);
        }

        /// <summary>Tool name, stage and whether it is the active tool, for diagnostics.</summary>
        public List<object> Describe()
        {
            var list = new List<object>();
            foreach (Watched w in m_Watched)
            {
                list.Add(new
                {
                    tool = w.Tool.GetType().Name,
                    stage = w.Stage.GetValue(w.Tool)?.ToString(),
                    active = m_ToolSystem.activeTool == w.Tool,
                    stuckSeconds = w.BusySince < 0f ? 0f : UnityEngine.Time.realtimeSinceStartup - w.BusySince,
                });
            }
            return list;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            ToolBaseSystem active = m_ToolSystem.activeTool;
            bool activeBridgeToolBusy = false;
            foreach (Watched w in m_Watched)
            {
                if (w.Tool == active && IsBusy(w))
                {
                    activeBridgeToolBusy = true;
                }
            }

            foreach (Watched w in m_Watched)
            {
                if (!IsBusy(w) || w.Tool == active)
                {
                    w.BusySince = -1f;
                    continue;
                }
                if (w.BusySince < 0f)
                {
                    w.BusySince = now;
                    continue;
                }
                if (activeBridgeToolBusy || now - w.BusySince < StuckSeconds)
                {
                    continue;
                }
                Mod.Log.Info($"BridgeToolWatchdogSystem: {w.Tool.GetType().Name} stuck in {w.Stage.GetValue(w.Tool)} " +
                             $"for {now - w.BusySince:F1}s while {active?.GetType().Name ?? "no tool"} was active; reactivating");
                m_ToolSystem.activeTool = w.Tool;
                w.BusySince = -1f;
                Reactivations++;
                LastReactivated = w.Tool.GetType().Name;
                // One per frame, so the reactivated tool gets a clean run.
                break;
            }
        }
    }
}
