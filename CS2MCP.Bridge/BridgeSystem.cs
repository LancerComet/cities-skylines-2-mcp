using System;
using System.Collections.Concurrent;
using Colossal.Serialization.Entities;
using Game;
using Game.Simulation;
using UnityEngine.Scripting;

namespace CS2MCP
{
    /// <summary>
    /// ECS system that owns the localhost bridge server. Requests arrive on
    /// listener threads, get queued here, and are executed in OnUpdate on the
    /// simulation main thread (the only place ECS access is safe).
    /// Registered at SystemUpdatePhase.UIUpdate so it keeps running while paused.
    /// </summary>
    public sealed partial class BridgeSystem : GameSystemBase
    {
        public const int DefaultPort = 8642;

        public static BridgeSystem Instance { get; private set; }

        private HttpBridgeServer m_Server;
        private RequestHandlers m_Handlers;
        private SimulationSystem m_SimulationSystem;
        private uint m_FrameIndexAtLoad;
        private readonly ConcurrentQueue<BridgeRequest> m_Pending = new ConcurrentQueue<BridgeRequest>();

        /// <summary>
        /// False until the simulation has advanced at least one frame after the
        /// current save finished loading. While false, unlock replay (Locked
        /// component removal, tax parameter unlocks...) may not have run yet,
        /// so lock states read from ECS can be stale.
        /// </summary>
        public bool SimulationHasTickedSinceLoad { get; private set; } = true;

        /// <summary>Frame at which the simulation auto-pauses (0 = disabled).</summary>
        public uint AutoPauseTargetFrame { get; private set; }

        public void SetAutoPause(uint targetFrame)
        {
            AutoPauseTargetFrame = targetFrame;
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            Instance = this;
            m_Handlers = new RequestHandlers(this);
            m_SimulationSystem = base.World.GetOrCreateSystemManaged<SimulationSystem>();

            int port = DefaultPort;
            string envPort = Environment.GetEnvironmentVariable("CS2MCP_PORT");
            if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out int parsed) && parsed > 0 && parsed < 65536)
            {
                port = parsed;
            }

            m_Server = new HttpBridgeServer(port, m_Pending.Enqueue);
            try
            {
                m_Server.Start();
                Mod.Log.Info($"bridge listening on http://127.0.0.1:{port}");
            }
            catch (Exception e)
            {
                Mod.Log.Error(e, $"failed to start bridge server on port {port}");
                m_Server = null;
            }
        }

        [Preserve]
        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            m_FrameIndexAtLoad = m_SimulationSystem.frameIndex;
            SimulationHasTickedSinceLoad = false;
            AutoPauseTargetFrame = 0;
            m_Handlers.ResetBackgroundSimulation();
            m_Handlers.ResetRuntimeSettingsAfterLoad();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            m_Handlers.RestoreFrameLimit();
            m_Handlers.ReconcileBackgroundSimulation();
            if (!SimulationHasTickedSinceLoad && m_SimulationSystem.frameIndex != m_FrameIndexAtLoad)
            {
                SimulationHasTickedSinceLoad = true;
            }
            if (AutoPauseTargetFrame != 0 && m_SimulationSystem.frameIndex >= AutoPauseTargetFrame)
            {
                m_Handlers.PauseNativeSimulation();
                AutoPauseTargetFrame = 0;
                Mod.Log.Info("timed run finished, simulation auto-paused");
            }
            while (m_Pending.TryDequeue(out BridgeRequest request))
            {
                BridgeResponse response;
                try
                {
                    // A null response means the handler completes the request
                    // asynchronously itself (e.g. screenshots at end-of-frame).
                    response = m_Handlers.Handle(request);
                }
                catch (Exception e)
                {
                    Mod.Log.Warn($"error handling {request.Method} {request.Path}: {e}");
                    response = BridgeResponse.Error(500, $"{e.GetType().Name}: {e.Message}");
                }
                if (response != null)
                {
                    request.Complete(response);
                }
            }
        }

        public void StopServer()
        {
            m_Server?.Stop();
            m_Server = null;
        }

        [Preserve]
        protected override void OnDestroy()
        {
            StopServer();
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }
    }
}
