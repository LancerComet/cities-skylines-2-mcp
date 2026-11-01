using System.Collections.Generic;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// Diagnostics that turn silent placement failures into actionable hints:
    /// whether a building placed at a computed spot will actually reach a road,
    /// and which segments to remove when a notification points at a network node.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        /// <summary>
        /// Road-access check for a building about to be placed at <paramref name="position"/>
        /// with its front facing <paramref name="facing"/> (unit x/z). Buildings flagged
        /// RequireRoad only work when a road runs along their front edge; utility buildings
        /// (pumps, outlets) also take their pipe connection from the road at their front.
        /// </summary>
        private object RoadAccessHint(Entity prefabEntity, float3 position, float2 facing)
        {
            if (!EntityManager.HasComponent<BuildingData>(prefabEntity))
            {
                return null;
            }
            BuildingData data = EntityManager.GetComponentData<BuildingData>(prefabEntity);
            bool requiresRoad = (data.m_Flags & BuildingFlags.RequireRoad) != 0;
            bool waterNode = (data.m_Flags & BuildingFlags.HasWaterNode) != 0;
            bool sewageNode = (data.m_Flags & BuildingFlags.HasSewageNode) != 0;
            float lotWidth = data.m_LotSize.x * 8f;
            float lotDepth = data.m_LotSize.y * 8f;
            float2 front = position.xz + facing * (lotDepth * 0.5f);

            float best = float.MaxValue;
            Entity bestRoad = Entity.Null;
            float bestHalfWidth = 8f;
            using (NativeArray<Entity> roads = TrafficRoadQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity road in roads)
                {
                    Colossal.Mathematics.Bezier4x3 curve = EntityManager.GetComponentData<Curve>(road).m_Bezier;
                    float d = float.MaxValue;
                    for (int i = 0; i <= 16; i++)
                    {
                        d = math.min(d, math.distance(Colossal.Mathematics.MathUtils.Position(curve, i / 16f).xz, front));
                    }
                    if (d < best)
                    {
                        best = d;
                        bestRoad = road;
                        Entity roadPrefab = EntityManager.GetComponentData<PrefabRef>(road).m_Prefab;
                        bestHalfWidth = EntityManager.HasComponent<NetGeometryData>(roadPrefab)
                            ? EntityManager.GetComponentData<NetGeometryData>(roadPrefab).m_DefaultWidth * 0.5f
                            : 8f;
                    }
                }
            }

            // Gap between the lot's front edge and the nearest road's edge (sampled, so approximate).
            float gap = bestRoad == Entity.Null ? float.MaxValue : best - bestHalfWidth;
            bool reachesRoad = gap <= 3f;
            // A small road (16 m wide) whose edge touches the front: centreline 8 m out, parallel to the front.
            float2 along = new float2(facing.y, -facing.x);
            float2 centre = front + facing * 8f;
            float half = math.max(lotWidth * 0.5f + 16f, 32f);
            float2 a = centre - along * half;
            float2 b = centre + along * half;

            string advice;
            if (!requiresRoad && !waterNode && !sewageNode)
            {
                advice = "this building does not need a road connection";
            }
            else if (reachesRoad)
            {
                advice = "a road runs along the front: the building will connect" +
                         (waterNode || sewageNode ? " (make sure that road also has a water/sewage pipe along it)" : "");
            }
            else
            {
                advice = "NO road along the front" + (bestRoad == Entity.Null ? "" : $" (nearest road edge ~{gap:F0} m away)") +
                         ": after placing, build suggestedRoad with cs2_connect_road (prefab 'Small Road', snap=30 so it joins the network), " +
                         (waterNode || sewageNode ? "then lay 'Combined Small Pipe' along the same line so the pipe network reaches it, " : "") +
                         "otherwise it will report 'No Road Access' / 'Pipeline Not Connected'";
            }

            return new
            {
                requiresRoad,
                pipeConnection = waterNode ? "water" : sewageNode ? "sewage" : null,
                frontCenter = new { x = front.x, z = front.y },
                nearestRoad = bestRoad == Entity.Null ? null : new { index = bestRoad.Index, version = bestRoad.Version, edgeGapMeters = math.round(gap) },
                reachesRoad,
                suggestedRoad = reachesRoad || (!requiresRoad && !waterNode && !sewageNode)
                    ? null
                    : new { x1 = a.x, z1 = a.y, x2 = b.x, z2 = b.y },
                advice,
            };
        }

        /// <summary>When a demolish target is a network node (e.g. from a notification), list its segments.</summary>
        private BridgeResponse NodeDemolishHint(Entity node)
        {
            var edges = new List<object>();
            if (EntityManager.HasBuffer<ConnectedEdge>(node))
            {
                DynamicBuffer<ConnectedEdge> connected = EntityManager.GetBuffer<ConnectedEdge>(node, true);
                for (int i = 0; i < connected.Length; i++)
                {
                    Entity edge = connected[i].m_Edge;
                    edges.Add(new { index = edge.Index, version = edge.Version, prefab = PrefabNameOf(edge) });
                }
            }
            return BridgeResponse.Error(400,
                "entity is a network NODE (junction/end point), not a segment. Demolish the segment(s) attached to it instead: " +
                Newtonsoft.Json.JsonConvert.SerializeObject(edges));
        }
    }
}
