using System;
using System.Collections.Generic;
using Game;
using Game.UI;
using Unity.Entities;
using UnityEngine.Scripting;

namespace CS2MCP
{
    /// <summary>
    /// Names lines created by BridgeTransitToolSystem, then answers their
    /// requests. NameSystem writes through EndFrameBarrier, which refuses new
    /// command buffers during the tool phase, so the rename runs here in the UI
    /// phase, where the transportation panel renames lines too.
    /// </summary>
    public sealed partial class BridgeTransitRenameSystem : GameSystemBase
    {
        private struct PendingRename
        {
            public Entity Line;
            public string Name;
            public BridgeRequest Request;
            public Func<string, BridgeResponse> BuildResponse;
        }

        private readonly Queue<PendingRename> m_Pending = new Queue<PendingRename>();
        private NameSystem m_NameSystem;

        /// <summary>Must be called on the simulation thread.</summary>
        public void Enqueue(Entity line, string name, BridgeRequest request, Func<string, BridgeResponse> buildResponse)
        {
            m_Pending.Enqueue(new PendingRename { Line = line, Name = name, Request = request, BuildResponse = buildResponse });
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_NameSystem = base.World.GetOrCreateSystemManaged<NameSystem>();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            while (m_Pending.Count > 0)
            {
                PendingRename pending = m_Pending.Dequeue();
                string nameWarning = null;
                try
                {
                    // Same call the transportation panel uses to rename a line.
                    m_NameSystem.SetCustomName(pending.Line, pending.Name);
                    base.World.GetExistingSystemManaged<Game.UI.InGame.TransportationOverviewUISystem>()?.RequestUpdate();
                }
                catch (Exception e)
                {
                    Mod.Log.Warn($"renaming line {pending.Line} failed: {e}");
                    nameWarning = $"the line was created but naming it failed ({e.GetType().Name}: {e.Message}); " +
                                  "rename it in the transportation panel";
                }

                BridgeResponse response;
                try
                {
                    response = pending.BuildResponse(nameWarning);
                }
                catch (Exception e)
                {
                    Mod.Log.Warn($"building the reply for line {pending.Line} failed: {e}");
                    response = BridgeResponse.Error(500,
                        $"the line was created, but building the reply failed ({e.GetType().Name}: {e.Message}); " +
                        "check cs2_list_transit_lines");
                }
                pending.Request?.Complete(response);
            }
        }
    }
}
