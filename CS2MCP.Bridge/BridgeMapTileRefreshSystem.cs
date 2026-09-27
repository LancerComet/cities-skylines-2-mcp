using System.Collections.Generic;
using Game;
using Game.Common;
using Unity.Entities;
using UnityEngine.Scripting;

namespace CS2MCP
{
    /// <summary>
    /// Flags map tiles bought through the bridge as Updated again at the start
    /// of the next frame. MapTilePurchaseSystem.UnlockTile flags them from the
    /// UI phase, which runs after the area rendering (AreaBufferSystem, in
    /// PreCulling) and is cleared by CleanUpSystem at the end of the frame, so
    /// the owned-area border would only redraw the next time the Map Tiles
    /// panel's selection tool runs. Flagging them in ToolUpdate, before the
    /// modification and rendering phases, lets the area systems see the change.
    /// </summary>
    public sealed partial class BridgeMapTileRefreshSystem : GameSystemBase
    {
        private readonly List<Entity> m_Pending = new List<Entity>();

        /// <summary>Must be called on the simulation thread.</summary>
        public void Enqueue(IEnumerable<Entity> tiles)
        {
            m_Pending.AddRange(tiles);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (m_Pending.Count == 0)
            {
                return;
            }
            foreach (Entity tile in m_Pending)
            {
                if (EntityManager.Exists(tile) && !EntityManager.HasComponent<Updated>(tile))
                {
                    EntityManager.AddComponent<Updated>(tile);
                }
            }
            m_Pending.Clear();
        }
    }
}
