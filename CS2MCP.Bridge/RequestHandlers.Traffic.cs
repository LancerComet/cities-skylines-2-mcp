using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// Traffic diagnostics with the numbers the game shows: the city-wide flow
    /// and volume as TrafficFlowSystem.RefreshCityTrafficAverages computes them,
    /// per-road flow and volume like the road info panel (NetUtils
    /// .GetTrafficFlowSpeed over the four daily periods), and the lanes
    /// TrafficBottleneckSystem marks with Bottleneck (the in-world traffic
    /// bottleneck icons). Read-only.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private struct RoadTraffic
        {
            public Entity Edge;
            public float Flow;
            public float4 Periods;
            public float Volume;
            public Bezier4x3 Curve;
            public float Length;
        }

        private sealed class BottleneckGroup
        {
            public int Lanes;
            public float3 PositionSum;
        }

        // Road.m_TrafficFlowDistance0 + 1 to the road info panel's volume scale (16 * 4 / 24).
        private const float kTrafficVolumeScale = 2.666667f;

        private EntityQuery m_CityTrafficQuery;
        private bool m_CityTrafficQueryCreated;
        private EntityQuery m_TrafficRoadQuery;
        private bool m_TrafficRoadQueryCreated;
        private EntityQuery m_BottleneckLaneQuery;
        private bool m_BottleneckLaneQueryCreated;

        /// <summary>Same roads TrafficFlowSystem averages for the city: named road edges in owned tiles.</summary>
        private EntityQuery CityTrafficQuery
        {
            get
            {
                if (!m_CityTrafficQueryCreated)
                {
                    m_CityTrafficQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Road>(),
                            ComponentType.ReadOnly<Aggregated>(),
                            ComponentType.ReadOnly<Edge>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Deleted>(),
                            ComponentType.ReadOnly<Temp>(),
                            ComponentType.ReadOnly<Native>(),
                        },
                    });
                    m_CityTrafficQueryCreated = true;
                }
                return m_CityTrafficQuery;
            }
        }

        private EntityQuery TrafficRoadQuery
        {
            get
            {
                if (!m_TrafficRoadQueryCreated)
                {
                    m_TrafficRoadQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Edge>(),
                            ComponentType.ReadOnly<Road>(),
                            ComponentType.ReadOnly<Curve>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Deleted>(),
                            ComponentType.ReadOnly<Temp>(),
                        },
                    });
                    m_TrafficRoadQueryCreated = true;
                }
                return m_TrafficRoadQuery;
            }
        }

        private EntityQuery BottleneckLaneQuery
        {
            get
            {
                if (!m_BottleneckLaneQueryCreated)
                {
                    m_BottleneckLaneQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Bottleneck>(),
                            ComponentType.ReadOnly<Curve>(),
                            ComponentType.ReadOnly<Owner>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Deleted>(),
                            ComponentType.ReadOnly<Temp>(),
                        },
                    });
                    m_BottleneckLaneQueryCreated = true;
                }
                return m_BottleneckLaneQuery;
            }
        }

        private BridgeResponse CityTraffic(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            bool hasCenter = request.TryGetFloat("x", out float x) & request.TryGetFloat("z", out float z);
            float radius = request.TryGetFloat("radius", out float rawRadius) ? math.max(rawRadius, 1f) : 1000f;
            float2 center = new float2(x, z);
            int limit = request.TryGetInt("limit", out int rawLimit) ? math.clamp(rawLimit, 1, 200) : 25;
            float minVolume = request.TryGetFloat("minVolume", out float rawMinVolume) ? math.max(rawMinVolume, 0f) : 1f;

            // City-wide averages, computed like TrafficFlowSystem.RefreshCityTrafficAverages.
            float4 flowSum = float4.zero;
            float4 volumeSum = float4.zero;
            int cityRoads;
            using (NativeArray<Road> roads = CityTrafficQuery.ToComponentDataArray<Road>(Allocator.Temp))
            {
                foreach (Road road in roads)
                {
                    flowSum += NetUtils.GetTrafficFlowSpeed(road) * 100f;
                    volumeSum += (road.m_TrafficFlowDistance0 + road.m_TrafficFlowDistance1) * kTrafficVolumeScale;
                }
                cityRoads = roads.Length;
            }
            float divisor = math.max(1, cityRoads) * 4f;

            // Per-road flow and volume like the road info panel.
            var ranked = new List<RoadTraffic>();
            int roadsInArea = 0;
            int below50 = 0;
            int below25 = 0;
            using (NativeArray<Entity> edges = TrafficRoadQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Road> roads = TrafficRoadQuery.ToComponentDataArray<Road>(Allocator.Temp))
            using (NativeArray<Curve> curves = TrafficRoadQuery.ToComponentDataArray<Curve>(Allocator.Temp))
            {
                for (int i = 0; i < edges.Length; i++)
                {
                    Bezier4x3 bezier = curves[i].m_Bezier;
                    if (hasCenter && math.distance(MathUtils.Position(bezier, 0.5f).xz, center) > radius)
                    {
                        continue;
                    }
                    roadsInArea++;
                    float4 periods = NetUtils.GetTrafficFlowSpeed(roads[i]) * 100f;
                    float volume = math.csum((roads[i].m_TrafficFlowDistance0 + roads[i].m_TrafficFlowDistance1) * kTrafficVolumeScale) * 0.25f;
                    if (volume < minVolume)
                    {
                        continue;
                    }
                    float flow = math.csum(periods) * 0.25f;
                    if (flow < 50f)
                    {
                        below50++;
                    }
                    if (flow < 25f)
                    {
                        below25++;
                    }
                    ranked.Add(new RoadTraffic
                    {
                        Edge = edges[i],
                        Flow = flow,
                        Periods = periods,
                        Volume = volume,
                        Curve = bezier,
                        Length = curves[i].m_Length,
                    });
                }
            }
            ranked.Sort((a, b) => a.Flow != b.Flow ? a.Flow.CompareTo(b.Flow) : b.Volume.CompareTo(a.Volume));

            var worstRoads = new List<object>();
            for (int i = 0; i < ranked.Count && i < limit; i++)
            {
                RoadTraffic road = ranked[i];
                worstRoads.Add(new
                {
                    road = new { index = road.Edge.Index, version = road.Edge.Version },
                    prefab = PrefabNameOf(road.Edge),
                    start = new { x = road.Curve.a.x, z = road.Curve.a.z },
                    end = new { x = road.Curve.d.x, z = road.Curve.d.z },
                    length = road.Length,
                    flowPercent = math.round(road.Flow),
                    worstPeriodFlowPercent = math.round(math.cmin(road.Periods)),
                    flowByPeriod = new[]
                    {
                        math.round(road.Periods.x), math.round(road.Periods.y),
                        math.round(road.Periods.z), math.round(road.Periods.w),
                    },
                    volume = math.round(road.Volume),
                });
            }

            // Bottleneck lanes, grouped by the road edge or intersection that owns them.
            var groups = new Dictionary<Entity, BottleneckGroup>();
            int bottleneckLanes = 0;
            using (NativeArray<Entity> lanes = BottleneckLaneQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity lane in lanes)
                {
                    Bottleneck bottleneck = EntityManager.GetComponentData<Bottleneck>(lane);
                    Bezier4x3 laneCurve = EntityManager.GetComponentData<Curve>(lane).m_Bezier;
                    float3 position = MathUtils.Position(laneCurve, bottleneck.m_Position / 255f);
                    if (hasCenter && math.distance(position.xz, center) > radius)
                    {
                        continue;
                    }
                    Entity owner = EntityManager.GetComponentData<Owner>(lane).m_Owner;
                    if (!groups.TryGetValue(owner, out BottleneckGroup group))
                    {
                        group = new BottleneckGroup();
                        groups.Add(owner, group);
                    }
                    group.Lanes++;
                    group.PositionSum += position;
                    bottleneckLanes++;
                }
            }
            var bottlenecks = new List<object>();
            foreach (KeyValuePair<Entity, BottleneckGroup> pair in groups)
            {
                Entity owner = pair.Key;
                float3 position = pair.Value.PositionSum / pair.Value.Lanes;
                float? ownerFlow = null;
                if (EntityManager.HasComponent<Road>(owner))
                {
                    ownerFlow = math.round(math.csum(NetUtils.GetTrafficFlowSpeed(EntityManager.GetComponentData<Road>(owner)) * 100f) * 0.25f);
                }
                bottlenecks.Add(new
                {
                    owner = new { index = owner.Index, version = owner.Version },
                    kind = EntityManager.HasComponent<Node>(owner) ? "intersection" : "road",
                    prefab = PrefabNameOf(owner),
                    position = new { x = position.x, z = position.z },
                    lanes = pair.Value.Lanes,
                    flowPercent = ownerFlow,
                });
            }

            return BridgeResponse.Json(new
            {
                cityTrafficFlowPercent = (int)math.round(math.csum(flowSum) / divisor),
                cityTrafficVolume = (int)math.round(math.csum(volumeSum) / divisor),
                cityRoadsAveraged = cityRoads,
                area = hasCenter ? (object)new { x, z, radius } : null,
                roadsInArea,
                roadsWithTraffic = ranked.Count,
                congestedRoads = new { below50Percent = below50, below25Percent = below25 },
                bottlenecks = new { count = bottlenecks.Count, lanes = bottleneckLanes, items = bottlenecks },
                worstRoads,
                note = "flow is the game's traffic flow (100% = free-flowing, lower = congested), as the traffic info view " +
                       "and road panel show it; flowByPeriod = the four daily periods; volume uses the road panel's scale; " +
                       "bottlenecks = lanes the game flags as traffic bottlenecks (the red car icons), grouped by road or " +
                       "intersection. Values update 32 times per in-game day, so let the simulation run between checks.",
            });
        }
    }
}
