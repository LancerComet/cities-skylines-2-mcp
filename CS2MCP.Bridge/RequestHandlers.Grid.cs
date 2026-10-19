using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Zones;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// Server-side grid builder. POST/GET /build/grid plans a whole street grid
    /// (and optionally its zoning), returns a job id at once and lets
    /// BridgeGridBuilderSystem build it piece by piece through the same code as
    /// /build/road/connect; /build/grid/status and /build/grid/cancel follow the
    /// job. Planning mirrors the scratchpad scripts edges.js (skip edges already
    /// covered by a road within 9 m at 0.25/0.5/0.75) and bridge_edges.js
    /// (water crossings become ramp / bridge / ramp).
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private const float kGridCoverDistance = 9f;
        private const float kGridWaterDepth = 0.05f;
        private const int kGridMaxPieces = 6000;

        private struct GridEdge
        {
            public int Class; // 0 major, 1 medium, 2 minor
            public string Prefab;
            public string Group;
            public float X1, Z1, X2, Z2;
        }

        // Entry points used by BridgeGridBuilderSystem (same thread, same code paths as the HTTP handlers).
        internal BridgeResponse GridConnect(BridgeRequest request)
        {
            return ConnectRoad(request);
        }

        internal bool GridToolsBusy()
        {
            return TransitToolsBusy(out _)
                || World.GetOrCreateSystemManaged<BridgeAreaToolSystem>().IsBusy;
        }

        private static bool TryParseFloatList(string raw, out List<float> values)
        {
            values = new List<float>();
            foreach (string part in raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                {
                    return false;
                }
                values.Add(value);
            }
            return true;
        }

        private static List<float> GridAxis(float min, float max, float step)
        {
            var values = new List<float>();
            for (float v = min; v <= max + 0.001f; v += step)
            {
                values.Add((float)Math.Round(v, 2));
            }
            return values;
        }

        private BridgeResponse BuildGrid(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.TryGetFloat("x0", out float x0) || !request.TryGetFloat("z0", out float z0)
                || !request.TryGetFloat("x1", out float x1) || !request.TryGetFloat("z1", out float z1))
            {
                return BridgeResponse.Error(400, "provide ?x0=&z0=&x1=&z1= bounds");
            }
            if (x1 < x0) { (x0, x1) = (x1, x0); }
            if (z1 < z0) { (z0, z1) = (z1, z0); }

            List<float> xs;
            if (request.Query.TryGetValue("xs", out string rawXs) && !string.IsNullOrWhiteSpace(rawXs))
            {
                if (!TryParseFloatList(rawXs, out xs))
                {
                    return BridgeResponse.Error(400, "xs must be a comma separated list of numbers");
                }
                xs.Sort();
            }
            else if (request.TryGetFloat("dx", out float dx) && dx >= 8f)
            {
                xs = GridAxis(x0, x1, dx);
            }
            else
            {
                return BridgeResponse.Error(400, "provide dx= (N-S street spacing, >= 8) or xs=<list>");
            }
            List<float> zs;
            if (request.Query.TryGetValue("zs", out string rawZs) && !string.IsNullOrWhiteSpace(rawZs))
            {
                if (!TryParseFloatList(rawZs, out zs))
                {
                    return BridgeResponse.Error(400, "zs must be a comma separated list of numbers");
                }
                zs.Sort();
            }
            else if (request.TryGetFloat("dz", out float dz) && dz >= 8f)
            {
                zs = GridAxis(z0, z1, dz);
            }
            else
            {
                return BridgeResponse.Error(400, "provide dz= (E-W street spacing, >= 8) or zs=<list>");
            }
            if (xs.Count < 2 || zs.Count < 2 || xs.Count > 200 || zs.Count > 200)
            {
                return BridgeResponse.Error(400, $"the grid needs 2-200 lines per axis (got {xs.Count} x and {zs.Count} z lines)");
            }

            string major = request.Query.TryGetValue("major", out string rawMajor) && rawMajor.Length > 0 ? rawMajor : null;
            string medium = request.Query.TryGetValue("medium", out string rawMedium) && rawMedium.Length > 0 ? rawMedium : null;
            string minor = request.Query.TryGetValue("minor", out string rawMinor) && rawMinor.Length > 0 ? rawMinor : null;
            if (major == null && medium == null && minor == null)
            {
                return BridgeResponse.Error(400, "provide minor=<prefab> (and optionally major=, medium=; see cs2_find_prefabs category road)");
            }
            foreach (string name in new[] { major, medium, minor })
            {
                if (name == null)
                {
                    continue;
                }
                if (!TryFindPrefabByName(NetPrefabQuery, name, out Entity prefabEntity, out PrefabBase prefab))
                {
                    return BridgeResponse.Error(404, $"unknown network prefab '{name}' (see cs2_find_prefabs category road)");
                }
                if (IsLocked(prefabEntity) && !IsForced(request))
                {
                    return BridgeResponse.Error(409, $"prefab '{prefab.name}' is locked (milestone not reached); pass force=true to build anyway");
                }
            }
            List<float> majorX = new List<float>();
            List<float> majorZ = new List<float>();
            if ((request.Query.TryGetValue("majorX", out string rawMajorX) && !TryParseFloatList(rawMajorX, out majorX))
                || (request.Query.TryGetValue("majorZ", out string rawMajorZ) && !TryParseFloatList(rawMajorZ, out majorZ)))
            {
                return BridgeResponse.Error(400, "majorX / majorZ must be comma separated lists of numbers");
            }
            int mediumEvery = request.TryGetInt("mediumEvery", out int rawEvery) ? Math.Max(0, rawEvery) : 0;

            var exclusions = new List<float[]>();
            if (request.Query.TryGetValue("exclude", out string rawExclude) && !string.IsNullOrWhiteSpace(rawExclude))
            {
                foreach (string circle in rawExclude.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!TryParseFloatList(circle, out List<float> parts) || parts.Count != 3 || parts[2] < 0f)
                    {
                        return BridgeResponse.Error(400, $"cannot parse exclude circle '{circle}'; expected x,z,r");
                    }
                    exclusions.Add(parts.ToArray());
                }
            }

            bool skipExisting = request.TryGetBool("skipExisting", out bool rawSkip) && rawSkip;
            string water = request.Query.TryGetValue("water", out string rawWater) ? rawWater.Trim().ToLowerInvariant() : "skip";
            if (water != "bridge" && water != "skip")
            {
                return BridgeResponse.Error(400, "water must be 'bridge' or 'skip'");
            }
            float snap = request.TryGetFloat("snap", out float rawSnap) ? math.clamp(rawSnap, 0f, 50f) : 12f;
            float gapSeconds = request.TryGetFloat("gapMs", out float rawGap) ? math.clamp(rawGap, 0f, 5000f) / 1000f : 0.2f;
            bool dryRun = request.TryGetBool("dryRun", out bool rawDry) && rawDry;

            // Zoning plan.
            var zoneRects = new List<GridZoneRect>();
            string zoneRaw = request.Query.TryGetValue("zone", out string z) && !string.IsNullOrWhiteSpace(z) ? z.Trim() : null;
            if (zoneRaw == null && !string.IsNullOrWhiteSpace(request.Body))
            {
                zoneRaw = request.Body.Trim();
            }
            if (zoneRaw != null)
            {
                if (zoneRaw.StartsWith("[") || zoneRaw.StartsWith("{"))
                {
                    try
                    {
                        JToken token = JToken.Parse(zoneRaw);
                        JArray items = token as JArray ?? (token as JObject)?["zones"] as JArray;
                        if (items == null)
                        {
                            return BridgeResponse.Error(400, "zone JSON must be an array of {x0,z0,x1,z1,zone} (or {\"zones\":[...]})");
                        }
                        foreach (JToken item in items)
                        {
                            var rect = new GridZoneRect
                            {
                                X0 = item.Value<float>("x0"),
                                Z0 = item.Value<float>("z0"),
                                X1 = item.Value<float>("x1"),
                                Z1 = item.Value<float>("z1"),
                                ZoneName = item.Value<string>("zone"),
                            };
                            if (string.IsNullOrEmpty(rect.ZoneName))
                            {
                                return BridgeResponse.Error(400, "every zone rect needs a 'zone' name");
                            }
                            zoneRects.Add(rect);
                        }
                    }
                    catch (Exception e)
                    {
                        return BridgeResponse.Error(400, $"cannot parse zone JSON: {e.Message}");
                    }
                }
                else
                {
                    zoneRects.Add(new GridZoneRect { X0 = x0, Z0 = z0, X1 = x1, Z1 = z1, ZoneName = zoneRaw });
                }
                foreach (GridZoneRect rect in zoneRects)
                {
                    if (rect.X1 < rect.X0) { (rect.X0, rect.X1) = (rect.X1, rect.X0); }
                    if (rect.Z1 < rect.Z0) { (rect.Z0, rect.Z1) = (rect.Z1, rect.Z0); }
                    if (string.Equals(rect.ZoneName, "None", StringComparison.OrdinalIgnoreCase))
                    {
                        rect.Zone = ZoneType.None;
                        rect.ZoneName = "None";
                        continue;
                    }
                    if (!TryFindPrefabByName(ZonePrefabQuery, rect.ZoneName, out Entity zoneEntity, out PrefabBase zonePrefab))
                    {
                        return BridgeResponse.Error(404, $"unknown zone '{rect.ZoneName}'; list via /zones");
                    }
                    if (IsLocked(zoneEntity) && !IsForced(request))
                    {
                        return BridgeResponse.Error(409, $"zone '{zonePrefab.name}' is locked (milestone not reached); pass force=true to zone anyway");
                    }
                    rect.Zone = EntityManager.GetComponentData<ZoneData>(zoneEntity).m_ZoneType;
                    rect.ZoneName = zonePrefab.name;
                }
            }

            // Edges: N-S lines at each x between consecutive zs, E-W lines at each z between consecutive xs.
            var edges = new List<GridEdge>();
            for (int i = 0; i < xs.Count; i++)
            {
                int cls = ClassifyGridLine(xs[i], i, majorX, mediumEvery, major != null, medium != null);
                string prefab = cls == 0 ? major : cls == 1 ? medium : minor;
                for (int j = 0; j + 1 < zs.Count; j++)
                {
                    edges.Add(new GridEdge { Class = cls, Prefab = prefab, Group = $"N-S x={xs[i]:0.##}", X1 = xs[i], Z1 = zs[j], X2 = xs[i], Z2 = zs[j + 1] });
                }
            }
            for (int i = 0; i < zs.Count; i++)
            {
                int cls = ClassifyGridLine(zs[i], i, majorZ, mediumEvery, major != null, medium != null);
                string prefab = cls == 0 ? major : cls == 1 ? medium : minor;
                for (int j = 0; j + 1 < xs.Count; j++)
                {
                    edges.Add(new GridEdge { Class = cls, Prefab = prefab, Group = $"E-W z={zs[i]:0.##}", X1 = xs[j], Z1 = zs[i], X2 = xs[j + 1], Z2 = zs[i] });
                }
            }
            if (edges.Count > kGridMaxPieces)
            {
                return BridgeResponse.Error(400, $"the grid has {edges.Count} edges; split it (max {kGridMaxPieces})");
            }
            // Major first so minor streets snap onto them; stable within a class (N-S before E-W).
            edges = edges.OrderBy(e => e.Class).ToList();

            List<Bezier4x3> existing = skipExisting ? CollectExistingRoadCurves() : null;
            WaterSurfaceData<SurfaceWater> surfaceData = default;
            if (water == "bridge" || water == "skip")
            {
                surfaceData = World.GetOrCreateSystemManaged<WaterSystem>().GetSurfaceData(out JobHandle waterDeps);
                waterDeps.Complete();
            }

            var pieces = new List<GridPiece>();
            foreach (GridEdge edge in edges)
            {
                PlanGridEdge(pieces, edge, exclusions, existing, water, ref surfaceData);
            }
            for (int i = 0; i < pieces.Count; i++)
            {
                pieces[i].Index = i;
            }

            var job = new GridJob
            {
                Pieces = pieces,
                Zones = zoneRects,
                Exclusions = exclusions,
                Snap = snap,
                GapSeconds = gapSeconds,
            };
            int skipped = job.Count("skipped");
            var plan = new
            {
                pieces = pieces.Count,
                toBuild = pieces.Count - skipped,
                skipped,
                lines = new { northSouth = xs.Count, eastWest = zs.Count },
                zoning = zoneRects.Select(r => new { r.X0, r.Z0, r.X1, r.Z1, zone = r.ZoneName }).ToList(),
            };

            if (dryRun)
            {
                return BridgeResponse.Json(new
                {
                    dryRun = true,
                    plan,
                    pieces = pieces.Select(DescribeGridPiece).ToList(),
                    note = "nothing was built; repeat without dryRun=true to start the job",
                });
            }

            World.GetOrCreateSystemManaged<BridgeGridBuilderSystem>().Submit(job, this);
            return BridgeResponse.Json(new
            {
                jobId = job.Id,
                state = job.State,
                plan,
                note = "building in the background, one piece at a time through the road tool; follow with /build/grid/status?id=" +
                       job.Id + ", stop with /build/grid/cancel?id=" + job.Id +
                       ". Pieces already built stay; the game must keep running (not saving) while it works.",
            });
        }

        private static int ClassifyGridLine(float coordinate, int index, List<float> majors, int mediumEvery, bool hasMajor, bool hasMedium)
        {
            if (hasMajor && majors.Any(m => Math.Abs(m - coordinate) < 0.5f))
            {
                return 0;
            }
            if (hasMedium && mediumEvery > 0 && index % mediumEvery == 0)
            {
                return 1;
            }
            return 2;
        }

        private List<Bezier4x3> CollectExistingRoadCurves()
        {
            var curves = new List<Bezier4x3>();
            using (NativeArray<Entity> entities = NetEdgeQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    if (!EntityManager.HasComponent<Road>(entity) || EntityManager.HasComponent<Owner>(entity))
                    {
                        continue;
                    }
                    curves.Add(EntityManager.GetComponentData<Curve>(entity).m_Bezier);
                }
            }
            return curves;
        }

        private static bool NearExistingRoad(List<Bezier4x3> roads, float2 point)
        {
            foreach (Bezier4x3 curve in roads)
            {
                float2 min = math.min(math.min(curve.a.xz, curve.b.xz), math.min(curve.c.xz, curve.d.xz));
                float2 max = math.max(math.max(curve.a.xz, curve.b.xz), math.max(curve.c.xz, curve.d.xz));
                if (math.any(point < min - kGridCoverDistance) || math.any(point > max + kGridCoverDistance))
                {
                    continue;
                }
                if (MathUtils.Distance(curve.xz, point, out _) < kGridCoverDistance)
                {
                    return true;
                }
            }
            return false;
        }

        private void PlanGridEdge(List<GridPiece> pieces, GridEdge edge, List<float[]> exclusions, List<Bezier4x3> existing,
            string water, ref WaterSurfaceData<SurfaceWater> surfaceData)
        {
            float2 a = new float2(edge.X1, edge.Z1);
            float2 b = new float2(edge.X2, edge.Z2);
            float length = math.distance(a, b);

            GridPiece Make(float2 from, float2 to, float e1, float e2)
            {
                return new GridPiece
                {
                    Prefab = edge.Prefab, Group = edge.Group,
                    X1 = (float)Math.Round(from.x, 2), Z1 = (float)Math.Round(from.y, 2),
                    X2 = (float)Math.Round(to.x, 2), Z2 = (float)Math.Round(to.y, 2),
                    E1 = e1, E2 = e2,
                };
            }

            GridPiece Skip(string reason)
            {
                GridPiece piece = Make(a, b, 0f, 0f);
                piece.Status = "skipped";
                piece.Message = reason;
                return piece;
            }

            if (edge.Prefab == null)
            {
                pieces.Add(Skip("no prefab for this line (give minor=, or major=/medium= covering it)"));
                return;
            }
            if (length < 4f || length > 1500f)
            {
                pieces.Add(Skip($"length {length:0.#}m is outside 4-1500m"));
                return;
            }

            int steps = Math.Max(1, (int)Math.Ceiling(length / 8f));
            for (int k = 0; k <= steps; k++)
            {
                float2 p = math.lerp(a, b, k / (float)steps);
                for (int c = 0; c < exclusions.Count; c++)
                {
                    float[] circle = exclusions[c];
                    if (math.distance(p, new float2(circle[0], circle[1])) <= circle[2])
                    {
                        pieces.Add(Skip($"excluded: sample ({p.x:0.#},{p.y:0.#}) is inside exclude circle #{c + 1}"));
                        return;
                    }
                }
            }

            if (existing != null)
            {
                bool covered = true;
                foreach (float f in new[] { 0.25f, 0.5f, 0.75f })
                {
                    if (!NearExistingRoad(existing, math.lerp(a, b, f)))
                    {
                        covered = false;
                        break;
                    }
                }
                if (covered)
                {
                    pieces.Add(Skip($"already covered by an existing road within {kGridCoverDistance:0}m"));
                    return;
                }
            }

            float first = -1f;
            float last = -1f;
            int waterSteps = Math.Max(1, (int)Math.Ceiling(length / 4f));
            for (int k = 0; k <= waterSteps; k++)
            {
                float s = length * k / waterSteps;
                float2 p = math.lerp(a, b, k / (float)waterSteps);
                if (WaterUtils.SampleDepth(ref surfaceData, new float3(p.x, 0f, p.y)) > kGridWaterDepth)
                {
                    if (first < 0f)
                    {
                        first = s;
                    }
                    last = s;
                }
            }
            if (first < 0f)
            {
                pieces.Add(Make(a, b, 0f, 0f));
                return;
            }
            if (water != "bridge")
            {
                pieces.Add(Skip($"crosses water ({first:0}-{last:0}m along the edge); use water=bridge to bridge it"));
                return;
            }

            // Ramp (0->5), bridge (5->5), ramp (5->0), as bridge_edges.js.
            float s1 = Math.Max(20f, first - 45f);
            float s2 = Math.Min(length - 20f, last + 45f);
            if (s1 < 4f || s2 - s1 < 4f || length - s2 < 4f)
            {
                pieces.Add(Skip("water crossing too close to the edge ends to fit ramps"));
                return;
            }
            float2 pa = a + (b - a) * (s1 / length);
            float2 pb = a + (b - a) * (s2 / length);
            pieces.Add(Make(a, pa, 0f, 5f));
            pieces.Add(Make(pa, pb, 5f, 5f));
            pieces.Add(Make(pb, b, 5f, 0f));
        }

        private static object DescribeGridPiece(GridPiece piece)
        {
            return new
            {
                index = piece.Index,
                group = piece.Group,
                prefab = piece.Prefab,
                from = new { x = piece.X1, z = piece.Z1 },
                to = new { x = piece.X2, z = piece.Z2 },
                e1 = piece.E1,
                e2 = piece.E2,
                status = piece.Status == "pending" ? "planned" : piece.Status,
                result = piece.Status == "ok" ? "OK" : piece.Message,
            };
        }

        private BridgeResponse GridStatus(BridgeRequest request)
        {
            BridgeGridBuilderSystem system = World.GetOrCreateSystemManaged<BridgeGridBuilderSystem>();
            if (!request.TryGetInt("id", out int id))
            {
                return BridgeResponse.Json(new
                {
                    jobs = system.Jobs.Select(j => new { id = j.Id, state = j.State, pieces = j.Pieces.Count, ok = j.Count("ok"), failed = j.Count("failed") }).ToList(),
                    note = "pass ?id= for one job",
                });
            }
            GridJob job = system.Find(id);
            if (job == null)
            {
                return BridgeResponse.Error(404, $"no grid job {id} (only the last jobs are kept, and jobs vanish on game restart)");
            }

            int ok = job.Count("ok");
            int failed = job.Count("failed");
            int skipped = job.Count("skipped");
            int cancelled = job.Count("cancelled");
            string filter = request.Query.TryGetValue("pieces", out string rawFilter) ? rawFilter.ToLowerInvariant() : "all";
            int offset = request.TryGetInt("offset", out int rawOffset) ? Math.Max(0, rawOffset) : 0;
            int limit = request.TryGetInt("limit", out int rawLimit) ? math.clamp(rawLimit, 1, 6000) : 1500;
            IEnumerable<GridPiece> listed = job.Pieces;
            if (filter == "none")
            {
                listed = Enumerable.Empty<GridPiece>();
            }
            else if (filter == "failed")
            {
                listed = job.Pieces.Where(p => p.Status == "failed");
            }
            else if (filter == "problems")
            {
                listed = job.Pieces.Where(p => p.Status == "failed" || p.Status == "skipped");
            }
            return BridgeResponse.Json(new
            {
                id = job.Id,
                state = job.State,
                error = job.Error,
                summary = job.Summary,
                counts = new
                {
                    total = job.Pieces.Count,
                    done = ok + failed + skipped,
                    ok,
                    failed,
                    skipped,
                    pending = job.Pieces.Count - ok - failed - skipped - cancelled,
                    cancelled,
                },
                cancelRequested = job.CancelRequested,
                zoning = job.Zones.Count == 0 ? null : new
                {
                    passesDone = job.ZonePassesDone,
                    rects = job.Zones.Select(r => new
                    {
                        r.X0, r.Z0, r.X1, r.Z1, zone = r.ZoneName,
                        cellsChanged = r.CellsChanged,
                        skippedOccupied = r.CellsSkippedOccupied,
                        skippedExcluded = r.CellsSkippedExcluded,
                    }).ToList(),
                },
                pieces = listed.Skip(offset).Take(limit).Select(DescribeGridPiece).ToList(),
            });
        }

        private BridgeResponse GridCancel(BridgeRequest request)
        {
            if (!request.TryGetInt("id", out int id))
            {
                return BridgeResponse.Error(400, "provide ?id= of a grid job");
            }
            BridgeGridBuilderSystem system = World.GetOrCreateSystemManaged<BridgeGridBuilderSystem>();
            GridJob job = system.Find(id);
            if (job == null)
            {
                return BridgeResponse.Error(404, $"no grid job {id}");
            }
            if (!system.Cancel(job))
            {
                return BridgeResponse.Error(409, $"grid job {id} already finished ({job.State})");
            }
            return BridgeResponse.Json(new
            {
                id = job.Id,
                state = job.State,
                note = "the piece in flight (if any) still completes; remaining pieces are not built; zoning is not applied",
            });
        }

        /// <summary>
        /// One zoning pass of a job: like /build/zone's cell rewrite, but over
        /// rectangles, never touching Occupied cells (that handler overwrites
        /// occupied cells) nor cells inside exclude circles.
        /// </summary>
        internal void GridApplyZoning(GridJob job)
        {
            using (NativeArray<Entity> blocks = ZoneBlockQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity blockEntity in blocks)
                {
                    Block block = EntityManager.GetComponentData<Block>(blockEntity);
                    float blockExtent = kCellSize * (math.cmax(block.m_Size) + 1) * 0.71f;
                    DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(blockEntity);
                    bool blockChanged = false;
                    foreach (GridZoneRect rect in job.Zones)
                    {
                        float2 center = block.m_Position.xz;
                        if (center.x < rect.X0 - blockExtent || center.x > rect.X1 + blockExtent
                            || center.y < rect.Z0 - blockExtent || center.y > rect.Z1 + blockExtent)
                        {
                            continue;
                        }
                        for (int cellZ = 0; cellZ < block.m_Size.y; cellZ++)
                        {
                            for (int cellX = 0; cellX < block.m_Size.x; cellX++)
                            {
                                int index = cellZ * block.m_Size.x + cellX;
                                if (index >= cells.Length)
                                {
                                    continue;
                                }
                                Cell cell = cells[index];
                                if ((cell.m_State & CellFlags.Visible) == 0
                                    || (cell.m_State & (CellFlags.Blocked | CellFlags.Overridden)) != 0
                                    || cell.m_Zone.Equals(rect.Zone))
                                {
                                    continue;
                                }
                                float3 position = ZoneUtils.GetCellPosition(block, new int2(cellX, cellZ));
                                if (position.x < rect.X0 || position.x > rect.X1 || position.z < rect.Z0 || position.z > rect.Z1)
                                {
                                    continue;
                                }
                                if (InGridExclusion(job, position.xz))
                                {
                                    if (job.ZonePassesDone == 0) { rect.CellsSkippedExcluded++; }
                                    continue;
                                }
                                if ((cell.m_State & CellFlags.Occupied) != 0)
                                {
                                    if (job.ZonePassesDone == 0) { rect.CellsSkippedOccupied++; }
                                    continue;
                                }
                                cell.m_Zone = rect.Zone;
                                cells[index] = cell;
                                rect.CellsChanged++;
                                blockChanged = true;
                            }
                        }
                    }
                    if (blockChanged && !EntityManager.HasComponent<Updated>(blockEntity))
                    {
                        EntityManager.AddComponent<Updated>(blockEntity);
                    }
                }
            }
        }

        private static bool InGridExclusion(GridJob job, float2 point)
        {
            foreach (float[] circle in job.Exclusions)
            {
                if (math.distance(point, new float2(circle[0], circle[1])) <= circle[2])
                {
                    return true;
                }
            }
            return false;
        }
    }
}
