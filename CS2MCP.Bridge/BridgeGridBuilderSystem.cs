using System;
using System.Collections.Generic;
using System.Text;
using Game;
using Game.SceneFlow;
using Game.Zones;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Scripting;

namespace CS2MCP
{
    /// <summary>One road piece of a grid job (a connect call: prefab, two ends, optional free-end elevations).</summary>
    public sealed class GridPiece
    {
        public int Index;
        public string Prefab;
        public float X1, Z1, X2, Z2;
        public float E1, E2;
        public string Group;
        public string Status = "pending"; // pending | ok | failed | skipped | cancelled
        public string Message;
        public int Attempts;
    }

    /// <summary>One zoning rectangle of a grid job.</summary>
    public sealed class GridZoneRect
    {
        public float X0, Z0, X1, Z1;
        public string ZoneName;
        public ZoneType Zone;
        public int CellsChanged;
        public int CellsSkippedOccupied;
        public int CellsSkippedExcluded;
    }

    public sealed class GridJob
    {
        public int Id;
        public string State = "queued"; // queued | running | zoning | done | cancelled | failed
        public string Error;
        public List<GridPiece> Pieces = new List<GridPiece>();
        public List<GridZoneRect> Zones = new List<GridZoneRect>();
        public List<float[]> Exclusions = new List<float[]>(); // x, z, r
        public float Snap = 12f;
        public float GapSeconds = 0.2f;
        public bool CancelRequested;
        public int NextPiece;
        public int ZonePassesDone;
        public float CreatedAt;
        public float FinishedAt;
        public string Summary;

        public int Count(string status)
        {
            int n = 0;
            foreach (GridPiece piece in Pieces)
            {
                if (piece.Status == status)
                {
                    n++;
                }
            }
            return n;
        }
    }

    /// <summary>
    /// Runs grid jobs (see RequestHandlers.Grid.cs) one road piece at a time.
    /// Registered in ToolUpdate next to BridgeRoadToolSystem: each frame in
    /// which every bridge tool is idle (and no save/load or frame hitch is
    /// going on) it submits the next piece through RequestHandlers.ConnectRoad,
    /// the same code path as /build/road/connect, and records the result when
    /// the road tool completes the request. A pause between pieces lets
    /// requests from agents claim the road tool; the job never submits while
    /// any bridge tool is busy. After the last piece it applies the zoning
    /// plan (skipping occupied cells) once the new zone blocks have settled.
    /// </summary>
    public sealed partial class BridgeGridBuilderSystem : GameSystemBase
    {
        private const float PieceTimeoutSeconds = 10f;
        private const float StallSeconds = 1.5f;
        private const int MaxBusyAttempts = 40;
        private const int MaxRetries = 3;
        private const int KeptFinishedJobs = 10;

        private readonly List<GridJob> m_Jobs = new List<GridJob>();
        private RequestHandlers m_Handlers;
        private int m_NextId = 1;

        private GridJob m_Active;
        private GridPiece m_InFlight;
        private BridgeRequest m_InFlightRequest;
        private float m_InFlightSince;
        private float m_NotBefore;
        private float m_LastUpdate;
        private float m_ZoneAt;
        private uint m_LastPieceFrame;

        private struct Deferred
        {
            public BridgeRequest Proxy;
            public BridgeRequest Outer;
            public Func<BridgeResponse, BridgeResponse> Map;
        }

        private readonly List<Deferred> m_Deferred = new List<Deferred>();

        public IReadOnlyList<GridJob> Jobs => m_Jobs;

        /// <summary>Must be called on the simulation thread.</summary>
        public GridJob Submit(GridJob job, RequestHandlers handlers)
        {
            job.Id = m_NextId++;
            job.CreatedAt = UnityEngine.Time.realtimeSinceStartup;
            m_Handlers = handlers;
            m_Jobs.Add(job);
            int finished = 0;
            for (int i = m_Jobs.Count - 1; i >= 0; i--)
            {
                if (IsFinished(m_Jobs[i]) && ++finished > KeptFinishedJobs)
                {
                    m_Jobs.RemoveAt(i);
                }
            }
            return job;
        }

        public GridJob Find(int id)
        {
            foreach (GridJob job in m_Jobs)
            {
                if (job.Id == id)
                {
                    return job;
                }
            }
            return null;
        }

        /// <summary>Must be called on the simulation thread.</summary>
        public bool Cancel(GridJob job)
        {
            if (IsFinished(job))
            {
                return false;
            }
            job.CancelRequested = true;
            if (job.State == "queued")
            {
                Finish(job, "cancelled", "cancelled before it started");
            }
            return true;
        }

        /// <summary>
        /// Completes outer with map(response) once proxy completes. Lets a handler
        /// reuse a tool entry point that completes a request with its own wording.
        /// </summary>
        public void Defer(BridgeRequest proxy, BridgeRequest outer, Func<BridgeResponse, BridgeResponse> map)
        {
            m_Deferred.Add(new Deferred { Proxy = proxy, Outer = outer, Map = map });
        }

        public static bool IsFinished(GridJob job)
        {
            return job.State == "done" || job.State == "cancelled" || job.State == "failed";
        }

        [Preserve]
        protected override void OnUpdate()
        {
            PumpDeferred();
            float now = UnityEngine.Time.realtimeSinceStartup;
            // A long gap between frames means a save, a load or a hitch: back off.
            if (m_LastUpdate > 0f && now - m_LastUpdate > StallSeconds)
            {
                m_NotBefore = Math.Max(m_NotBefore, now + 2f);
            }
            m_LastUpdate = now;

            try
            {
                Step(now);
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"BridgeGridBuilderSystem error: {e}");
                if (m_Active != null)
                {
                    Finish(m_Active, "failed", $"{e.GetType().Name}: {e.Message}");
                }
                m_InFlight = null;
                m_InFlightRequest = null;
            }
        }

        private void PumpDeferred()
        {
            for (int i = m_Deferred.Count - 1; i >= 0; i--)
            {
                BridgeResponse response = m_Deferred[i].Proxy.WaitForResponse(0);
                if (response == null)
                {
                    continue;
                }
                Deferred item = m_Deferred[i];
                m_Deferred.RemoveAt(i);
                BridgeResponse mapped;
                try
                {
                    mapped = item.Map(response);
                }
                catch (Exception e)
                {
                    mapped = BridgeResponse.Error(500, $"{e.GetType().Name}: {e.Message}");
                }
                item.Outer.Complete(mapped);
            }
        }

        private static bool CityReady()
        {
            GameManager manager = GameManager.instance;
            return manager != null && manager.gameMode == GameMode.Game;
        }

        private static bool Loading()
        {
            GameManager manager = GameManager.instance;
            return manager == null || manager.isGameLoading || manager.state == GameManager.State.Loading;
        }

        private void Step(float now)
        {
            if (m_Active == null)
            {
                foreach (GridJob job in m_Jobs)
                {
                    if (job.State == "queued")
                    {
                        m_Active = job;
                        job.State = "running";
                        break;
                    }
                }
                if (m_Active == null)
                {
                    return;
                }
            }
            GridJob active = m_Active;

            if (!CityReady())
            {
                Finish(active, "failed", "the city was unloaded");
                return;
            }
            if (Loading())
            {
                return;
            }

            if (m_InFlight != null)
            {
                BridgeResponse response = m_InFlightRequest.WaitForResponse(0);
                if (response != null)
                {
                    RecordResult(active, m_InFlight, response, now);
                }
                else if (now - m_InFlightSince > PieceTimeoutSeconds)
                {
                    m_InFlight.Status = "failed";
                    m_InFlight.Message = $"timed out after {PieceTimeoutSeconds:0}s (the road tool did not finish; state unknown, check the road)";
                    m_InFlight = null;
                    m_InFlightRequest = null;
                    m_NotBefore = now + 1f;
                }
                return;
            }

            if (active.CancelRequested)
            {
                foreach (GridPiece piece in active.Pieces)
                {
                    if (piece.Status == "pending")
                    {
                        piece.Status = "cancelled";
                    }
                }
                Finish(active, "cancelled", "cancelled by request");
                return;
            }

            if (now < m_NotBefore || m_Handlers.GridToolsBusy())
            {
                return;
            }

            if (active.NextPiece < active.Pieces.Count)
            {
                SubmitNext(active, now);
                return;
            }

            // All roads are done: zoning, once the new zone blocks exist (two passes).
            if (active.Zones.Count == 0)
            {
                Finish(active, "done", null);
                return;
            }
            if (active.State != "zoning")
            {
                active.State = "zoning";
                m_ZoneAt = now + 1.5f;
            }
            SimulationFrame(out uint frame);
            if (now < m_ZoneAt || frame - m_LastPieceFrame < 8)
            {
                return;
            }
            m_Handlers.GridApplyZoning(active);
            active.ZonePassesDone++;
            if (active.ZonePassesDone >= 2)
            {
                Finish(active, "done", null);
            }
            else
            {
                m_ZoneAt = now + 3f;
            }
        }

        private void SimulationFrame(out uint frame)
        {
            frame = World.GetOrCreateSystemManaged<Game.Simulation.SimulationSystem>().frameIndex;
        }

        private void SubmitNext(GridJob job, float now)
        {
            GridPiece piece = job.Pieces[job.NextPiece];
            if (piece.Status == "skipped")
            {
                job.NextPiece++;
                return;
            }

            var request = new BridgeRequest { Method = "GET", Path = "/build/road/connect" };
            request.Query["prefab"] = piece.Prefab;
            request.Query["x1"] = piece.X1.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            request.Query["z1"] = piece.Z1.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            request.Query["x2"] = piece.X2.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            request.Query["z2"] = piece.Z2.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            request.Query["snap"] = job.Snap.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (piece.E1 != 0f || piece.E2 != 0f)
            {
                request.Query["e1"] = piece.E1.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                request.Query["e2"] = piece.E2.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            }

            piece.Attempts++;
            BridgeResponse immediate = m_Handlers.GridConnect(request);
            if (immediate == null)
            {
                m_InFlight = piece;
                m_InFlightRequest = request;
                m_InFlightSince = now;
                return;
            }
            RecordResult(job, piece, immediate, now);
        }

        private void RecordResult(GridJob job, GridPiece piece, BridgeResponse response, float now)
        {
            m_InFlight = null;
            m_InFlightRequest = null;
            string text = Encoding.UTF8.GetString(response.Body);
            string error = null;
            int segments = 0;
            try
            {
                JObject body = JObject.Parse(text);
                error = body.Value<string>("error");
                segments = (body["segments"] as JArray)?.Count ?? 0;
            }
            catch (Exception)
            {
                error = response.Status == 200 ? null : text;
            }

            SimulationFrame(out m_LastPieceFrame);
            if (response.Status == 200 && error == null)
            {
                piece.Status = "ok";
                piece.Message = $"built, {segments} segment(s)";
                job.NextPiece++;
                m_NotBefore = now + job.GapSeconds;
                return;
            }

            bool transient = error != null
                && (error.Contains("in progress") || error.Contains("interrupted"));
            if (transient && piece.Attempts < (error.Contains("in progress") ? MaxBusyAttempts : MaxRetries))
            {
                // Another request had the road tool, or it was interrupted: retry the same piece.
                m_NotBefore = now + (error.Contains("in progress") ? 0.25f : 1f);
                return;
            }
            piece.Status = "failed";
            piece.Message = error ?? $"HTTP {response.Status}";
            job.NextPiece++;
            m_NotBefore = now + job.GapSeconds;
        }

        private void Finish(GridJob job, string state, string note)
        {
            job.State = state;
            job.Error = state == "failed" ? note : null;
            job.Summary = note;
            job.FinishedAt = UnityEngine.Time.realtimeSinceStartup;
            if (m_Active == job)
            {
                m_Active = null;
                m_InFlight = null;
                m_InFlightRequest = null;
            }
        }
    }
}
