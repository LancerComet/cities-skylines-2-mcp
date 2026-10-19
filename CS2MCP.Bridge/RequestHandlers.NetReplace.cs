using System;
using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;

namespace CS2MCP
{
    /// <summary>
    /// /build/net/replace: the in-game upgrade/replace tool for any network
    /// family, including train, tram and subway tracks. Reuses
    /// BridgeRoadToolSystem's Replace mode (queueing takes any net prefab
    /// entity), so segments keep their geometry and junctions. Every segment is
    /// checked first; if any is invalid nothing is changed and the per-segment
    /// errors are returned.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private const int kNetReplaceMaxSegments = 30;

        /// <summary>Broad network family of a prefab: road (incl. tram roads), train, subway, tram, or its raw layers.</summary>
        private static string NetFamily(NetData data)
        {
            Layer layers = data.m_RequiredLayers;
            if ((layers & Layer.TrainTrack) != 0)
            {
                return "train";
            }
            if ((layers & Layer.SubwayTrack) != 0)
            {
                return "subway";
            }
            if ((layers & Layer.Road) != 0)
            {
                return "road";
            }
            if ((layers & Layer.TramTrack) != 0)
            {
                return "tram";
            }
            return layers.ToString();
        }

        private BridgeResponse ReplaceNet(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("prefab", out string prefabName) || string.IsNullOrEmpty(prefabName))
            {
                return BridgeResponse.Error(400, "provide ?prefab=<net prefab name> (see cs2_find_prefabs category net)");
            }
            if (!request.Query.TryGetValue("nets", out string rawNets) || string.IsNullOrEmpty(rawNets))
            {
                return BridgeResponse.Error(400,
                    "provide ?nets=<list separated by ';'>, each 'index:version' of a net segment (road or track)");
            }
            string[] items = rawNets.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (items.Length == 0)
            {
                return BridgeResponse.Error(400, "no segments given in ?nets=");
            }
            if (items.Length > kNetReplaceMaxSegments)
            {
                return BridgeResponse.Error(400, $"too many segments in one replacement (max {kNetReplaceMaxSegments})");
            }
            if (!TryFindPrefabByName(NetPrefabQuery, prefabName, out Entity prefabEntity, out PrefabBase prefab)
                || !EntityManager.HasComponent<NetData>(prefabEntity))
            {
                return BridgeResponse.Error(404, $"no net prefab named '{prefabName}' (see cs2_find_prefabs category net)");
            }
            if (IsLocked(prefabEntity) && !IsForced(request))
            {
                return BridgeResponse.Error(409, $"net prefab '{prefab.name}' is locked (milestone not reached); pass force=true to use it anyway");
            }
            string targetFamily = NetFamily(EntityManager.GetComponentData<NetData>(prefabEntity));
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            var edges = new List<Entity>();
            var plan = new List<object>();
            var errors = new List<object>();
            for (int i = 0; i < items.Length; i++)
            {
                string item = items[i].Trim();
                string problem = null;
                string currentName = null;
                Entity edge = Entity.Null;
                if (!TryParseEntityRef(item, out edge) || !EntityManager.Exists(edge)
                    || !EntityManager.HasComponent<Edge>(edge) || !EntityManager.HasComponent<Curve>(edge)
                    || !EntityManager.HasComponent<Composition>(edge) || !EntityManager.HasComponent<PrefabRef>(edge))
                {
                    problem = "not an existing net segment (use index:version)";
                }
                else if (EntityManager.HasComponent<Temp>(edge) || EntityManager.HasComponent<Deleted>(edge))
                {
                    problem = "segment is being modified; retry shortly";
                }
                else if (EntityManager.HasComponent<Owner>(edge))
                {
                    problem = "segment belongs to a building or asset (e.g. an interchange); replace the owner instead";
                }
                else
                {
                    Entity currentPrefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                    PrefabBase current = prefabSystem.GetPrefab<PrefabBase>(currentPrefab);
                    currentName = current != null ? current.name : null;
                    if (!EntityManager.HasComponent<NetData>(currentPrefab))
                    {
                        problem = "segment has no net prefab";
                    }
                    else
                    {
                        string family = NetFamily(EntityManager.GetComponentData<NetData>(currentPrefab));
                        if (family != targetFamily)
                        {
                            problem = $"incompatible: segment is a {family} network ('{currentName}') but '{prefab.name}' is a {targetFamily} network";
                        }
                    }
                }

                if (problem != null)
                {
                    errors.Add(new { segment = item, error = problem });
                    continue;
                }
                if (!edges.Contains(edge))
                {
                    edges.Add(edge);
                    plan.Add(new { net = new { index = edge.Index, version = edge.Version }, from = currentName, to = prefab.name });
                }
            }
            if (errors.Count > 0)
            {
                return BridgeResponse.Json(new
                {
                    replaced = 0,
                    prefab = prefab.name,
                    family = targetFamily,
                    errors,
                    note = "nothing was changed: fix or remove the listed segments and retry",
                }, 400);
            }

            if (request.TryGetBool("dryRun", out bool dryRun) && dryRun)
            {
                return BridgeResponse.Json(new
                {
                    dryRun = true,
                    prefab = prefab.name,
                    family = targetFamily,
                    wouldReplace = plan.Count,
                    nets = plan,
                    note = "all segments are compatible; the game still validates the replacement when applied",
                });
            }
            if (TransitToolsBusy(out error))
            {
                return error;
            }
            BridgeRoadToolSystem tool = World.GetOrCreateSystemManaged<BridgeRoadToolSystem>();
            bool invert = request.TryGetBool("invert", out bool rawInvert) && rawInvert;
            if (!tool.TryQueueReplace(edges.ToArray(), prefabEntity, prefab, invert, request))
            {
                return BridgeResponse.Error(409, "another road operation is in progress, retry shortly");
            }
            // Completed asynchronously by BridgeRoadToolSystem over the next tool frames.
            return null;
        }
    }
}
