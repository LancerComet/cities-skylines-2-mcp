using System;
using System.Collections.Generic;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    /// <summary>
    /// Passenger airplane lines and cargo routes (airplane, train, ship): the
    /// game's "Passenger Airplane Line", "Cargo Airplane Route", "Cargo Train
    /// Route" and "Cargo Ship Route" tools. Same pipeline as
    /// /transit/lines/create (BridgeTransitToolSystem), but the line prefab and
    /// the stops are chosen by transport type AND passenger/cargo, as
    /// ValidationHelpers.ValidateRoute requires (a cargo route needs cargo
    /// stops, NoCargoAccess otherwise). Outside connections serve both.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private static readonly Dictionary<string, TransportType> kRouteTypes =
            new Dictionary<string, TransportType>(StringComparer.OrdinalIgnoreCase)
            {
                ["airplane"] = TransportType.Airplane,
                ["air"] = TransportType.Airplane,
                ["train"] = TransportType.Train,
                ["ship"] = TransportType.Ship,
            };

        private BridgeResponse CreateRoute(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("type", out string rawType) || string.IsNullOrEmpty(rawType)
                || !kRouteTypes.TryGetValue(rawType.Trim(), out TransportType type))
            {
                return BridgeResponse.Error(400, "provide ?type=airplane|train|ship");
            }
            bool cargo = request.TryGetBool("cargo", out bool rawCargo) && rawCargo;
            string typeName = (cargo ? "cargo " : "passenger ") + TransitTypeName(type);
            if (!cargo && type != TransportType.Airplane)
            {
                return BridgeResponse.Error(400, $"use cs2_create_transit_line for passenger {TransitTypeName(type)} lines");
            }
            if (!request.Query.TryGetValue("stops", out string rawStops) || string.IsNullOrEmpty(rawStops))
            {
                return BridgeResponse.Error(400,
                    "provide ?stops=<ordered list separated by ';'> of 'index:version' stops (gates, cargo terminals, " +
                    "outside connections) or buildings containing one; the route loops back to the first stop");
            }

            if (!TryResolveRoutePrefab(type, cargo, typeName, IsForced(request), out Entity prefabEntity, out PrefabBase prefab, out error))
            {
                return error;
            }

            string[] items = rawStops.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (items.Length > 100)
            {
                return BridgeResponse.Error(400, "too many stops (max 100)");
            }
            var stops = new List<Entity>();
            for (int i = 0; i < items.Length; i++)
            {
                string item = items[i].Trim();
                if (!TryParseEntityRef(item, out Entity entity) || !EntityManager.Exists(entity))
                {
                    return BridgeResponse.Error(400, $"stop #{i + 1} ('{item}'): expected an existing 'index:version'");
                }
                if (!IsValidRouteStop(entity, type, cargo) && !TryFindRouteStopInBuilding(entity, type, cargo, out entity))
                {
                    return BridgeResponse.Error(400, $"stop #{i + 1} ('{item}'): it is neither a {typeName} stop nor a building containing one");
                }
                stops.Add(entity);
            }
            if (stops.Count >= 3 && stops[stops.Count - 1] == stops[0])
            {
                stops.RemoveAt(stops.Count - 1);
            }
            if (stops.Count < 2)
            {
                return BridgeResponse.Error(400, "a route needs at least 2 different stops (the loop back to the first stop is added automatically)");
            }

            float minSpacing = EntityManager.GetComponentData<RouteData>(prefabEntity).m_SnapDistance * 0.5f;
            var lineStops = new BridgeTransitToolSystem.LineStop[stops.Count];
            for (int i = 0; i < stops.Count; i++)
            {
                lineStops[i] = new BridgeTransitToolSystem.LineStop
                {
                    Stop = stops[i],
                    Position = EntityManager.GetComponentData<Transform>(stops[i]).m_Position,
                };
            }
            for (int i = 0; i < lineStops.Length; i++)
            {
                int next = (i + 1) % lineStops.Length;
                if (lineStops[i].Stop == lineStops[next].Stop)
                {
                    return BridgeResponse.Error(400, $"stops #{i + 1} and #{next + 1} are the same stop; consecutive stops must differ");
                }
                if (math.distance(lineStops[i].Position, lineStops[next].Position) < minSpacing)
                {
                    return BridgeResponse.Error(400, $"stops #{i + 1} and #{next + 1} are closer than {minSpacing:F1}m; the game would merge them");
                }
            }

            bool hasColor = false;
            UnityEngine.Color32 color = default;
            if (request.Query.TryGetValue("color", out string rawColor) && !string.IsNullOrEmpty(rawColor))
            {
                if (!TryParseHexColor(rawColor, out color))
                {
                    return BridgeResponse.Error(400, "color must be #RRGGBB");
                }
                hasColor = true;
            }
            string name = request.Query.TryGetValue("name", out string rawName) && rawName != null ? rawName.Trim() : null;
            if (name != null && name.Length > 64)
            {
                return BridgeResponse.Error(400, "name too long (max 64 characters)");
            }
            if (TransitToolsBusy(out error))
            {
                return error;
            }

            BridgeTransitToolSystem tool = World.GetOrCreateSystemManaged<BridgeTransitToolSystem>();
            if (!tool.TryQueueLine(prefabEntity, prefab, typeName, lineStops, hasColor, color,
                    string.IsNullOrEmpty(name) ? null : name, CountDepots(type), request))
            {
                return BridgeResponse.Error(409, "another transit operation is in progress, retry shortly");
            }
            return null;
        }

        private bool TryResolveRoutePrefab(TransportType type, bool cargo, string typeName, bool force,
            out Entity prefabEntity, out PrefabBase prefab, out BridgeResponse error)
        {
            prefabEntity = Entity.Null;
            prefab = null;
            error = null;
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var candidates = new List<KeyValuePair<Entity, PrefabBase>>();
            using (NativeArray<Entity> entities = TransitLinePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    TransportLineData data = EntityManager.GetComponentData<TransportLineData>(entity);
                    if (data.m_TransportType != type || (cargo ? !data.m_CargoTransport : !data.m_PassengerTransport))
                    {
                        continue;
                    }
                    PrefabBase candidate = prefabSystem.GetPrefab<PrefabBase>(entity);
                    if (candidate != null)
                    {
                        candidates.Add(new KeyValuePair<Entity, PrefabBase>(entity, candidate));
                    }
                }
            }
            if (candidates.Count == 0)
            {
                error = BridgeResponse.Error(404, $"no {typeName} route prefab found in this game");
                return false;
            }
            candidates.Sort((a, b) => string.CompareOrdinal(a.Value.name, b.Value.name));
            foreach (KeyValuePair<Entity, PrefabBase> candidate in candidates)
            {
                if (!IsLocked(candidate.Key))
                {
                    prefabEntity = candidate.Key;
                    prefab = candidate.Value;
                    return true;
                }
            }
            if (!force)
            {
                error = BridgeResponse.Error(409, $"{typeName} routes are locked (milestone not reached); pass force=true to create anyway");
                return false;
            }
            prefabEntity = candidates[0].Key;
            prefab = candidates[0].Value;
            return true;
        }

        private bool IsValidRouteStop(Entity entity, TransportType type, bool cargo)
        {
            if (!EntityManager.HasComponent<Game.Routes.TransportStop>(entity)
                || !EntityManager.HasBuffer<Game.Routes.ConnectedRoute>(entity)
                || !EntityManager.HasComponent<PrefabRef>(entity)
                || !EntityManager.HasComponent<Transform>(entity)
                || EntityManager.HasComponent<Temp>(entity)
                || EntityManager.HasComponent<Game.Common.Deleted>(entity))
            {
                return false;
            }
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (!EntityManager.HasComponent<TransportStopData>(prefab))
            {
                return false;
            }
            TransportStopData data = EntityManager.GetComponentData<TransportStopData>(prefab);
            return data.m_TransportType == type && (cargo ? data.m_CargoTransport : data.m_PassengerTransport);
        }

        private bool TryFindRouteStopInBuilding(Entity building, TransportType type, bool cargo, out Entity stop)
        {
            stop = Entity.Null;
            using (NativeArray<Entity> stops = TransitStopQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity candidate in stops)
                {
                    if (!IsValidRouteStop(candidate, type, cargo))
                    {
                        continue;
                    }
                    Entity owner = candidate;
                    for (int depth = 0; depth < 8 && EntityManager.HasComponent<Game.Common.Owner>(owner); depth++)
                    {
                        owner = EntityManager.GetComponentData<Game.Common.Owner>(owner).m_Owner;
                        if (owner == building)
                        {
                            stop = candidate;
                            return true;
                        }
                    }
                }
            }
            return false;
        }
    }
}
