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
    /// In-place road changes through BridgeRoadToolSystem (the road tool's
    /// Replace mode): change the road type of existing segments, e.g. widen a
    /// highway or turn a two-way street into a one-way one, keeping the nodes,
    /// connections and (where the width allows) the buildings along it.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private BridgeResponse ReplaceRoad(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("prefab", out string prefabName) || string.IsNullOrEmpty(prefabName))
            {
                return BridgeResponse.Error(400, "provide ?prefab=<road prefab name> (see cs2_find_prefabs category road)");
            }
            if (!request.Query.TryGetValue("roads", out string rawRoads) || string.IsNullOrEmpty(rawRoads))
            {
                return BridgeResponse.Error(400,
                    "provide ?roads=<list separated by ';'>, each 'index:version' of a road segment from cs2_list_roads");
            }
            string[] items = rawRoads.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (items.Length > 50)
            {
                return BridgeResponse.Error(400, "too many segments in one replacement (max 50)");
            }

            var edges = new List<Entity>();
            for (int i = 0; i < items.Length; i++)
            {
                string item = items[i].Trim();
                if (!TryParseEntityRef(item, out Entity edge) || !EntityManager.Exists(edge)
                    || !EntityManager.HasComponent<Edge>(edge) || !EntityManager.HasComponent<Curve>(edge)
                    || !EntityManager.HasComponent<Composition>(edge))
                {
                    return BridgeResponse.Error(404, $"segment #{i + 1} ('{item}') is not an existing road segment (use index:version from cs2_list_roads)");
                }
                if (EntityManager.HasComponent<Temp>(edge) || EntityManager.HasComponent<Deleted>(edge))
                {
                    return BridgeResponse.Error(409, $"segment #{i + 1} ('{item}') is being modified; retry shortly");
                }
                if (EntityManager.HasComponent<Owner>(edge))
                {
                    return BridgeResponse.Error(409,
                        $"segment #{i + 1} ('{item}') belongs to a building or asset (e.g. an interchange); replace the owner instead");
                }
                if (!edges.Contains(edge))
                {
                    edges.Add(edge);
                }
            }

            if (!TryFindPrefabByName(RoadPrefabQuery, prefabName, out Entity prefabEntity, out PrefabBase prefab))
            {
                return BridgeResponse.Error(404, $"no road prefab named '{prefabName}' (see cs2_find_prefabs category road)");
            }
            if (IsLocked(prefabEntity) && !IsForced(request))
            {
                return BridgeResponse.Error(409, $"road prefab '{prefab.name}' is locked (milestone not reached); pass force=true to use it anyway");
            }
            bool invert = request.TryGetBool("invert", out bool rawInvert) && rawInvert;
            if (TransitToolsBusy(out error))
            {
                return error;
            }

            BridgeRoadToolSystem tool = World.GetOrCreateSystemManaged<BridgeRoadToolSystem>();
            if (!tool.TryQueueReplace(edges.ToArray(), prefabEntity, prefab, invert, request))
            {
                return BridgeResponse.Error(409, "another road operation is in progress, retry shortly");
            }
            // Completed asynchronously by BridgeRoadToolSystem over the next tool frames.
            return null;
        }
    }
}
