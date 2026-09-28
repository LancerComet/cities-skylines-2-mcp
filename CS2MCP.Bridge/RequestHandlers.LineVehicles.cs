using System;
using System.Collections.Generic;
using Game.Prefabs;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    /// <summary>
    /// Vehicle models of a transit line (the line panel's "select vehicles"
    /// section): candidates come from the public transport vehicle prefabs of the
    /// line's transport type, and a choice is written to the line's VehicleModel
    /// buffer exactly as SelectVehiclesSection does. An empty buffer means the
    /// game picks a random model for each new vehicle.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private EntityQuery m_TransitVehiclePrefabQuery;
        private bool m_TransitVehiclePrefabQueryCreated;

        private EntityQuery TransitVehiclePrefabQuery
        {
            get
            {
                if (!m_TransitVehiclePrefabQueryCreated)
                {
                    m_TransitVehiclePrefabQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PublicTransportVehicleData>(),
                        ComponentType.ReadOnly<PrefabData>());
                    m_TransitVehiclePrefabQueryCreated = true;
                }
                return m_TransitVehiclePrefabQuery;
            }
        }

        private BridgeResponse ListLineVehicles(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!TryResolveLine(request, out Entity line, out error))
            {
                return error;
            }
            if (!TryGetLineTransportData(line, out TransportLineData lineData, out error))
            {
                return error;
            }

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var selected = new List<object>();
            if (EntityManager.HasBuffer<VehicleModel>(line))
            {
                DynamicBuffer<VehicleModel> models = EntityManager.GetBuffer<VehicleModel>(line, isReadOnly: true);
                for (int i = 0; i < models.Length; i++)
                {
                    selected.Add(new
                    {
                        primary = VehiclePrefabName(prefabSystem, models[i].m_PrimaryPrefab),
                        secondary = VehiclePrefabName(prefabSystem, models[i].m_SecondaryPrefab),
                    });
                }
            }

            var candidates = new List<object>();
            foreach (Entity prefab in LineVehicleCandidates(lineData.m_TransportType))
            {
                PublicTransportVehicleData data = EntityManager.GetComponentData<PublicTransportVehicleData>(prefab);
                object carriages = null;
                if (EntityManager.HasBuffer<VehicleCarriageElement>(prefab))
                {
                    var list = new List<object>();
                    DynamicBuffer<VehicleCarriageElement> buffer = EntityManager.GetBuffer<VehicleCarriageElement>(prefab, isReadOnly: true);
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        Entity carriage = buffer[i].m_Prefab;
                        list.Add(new
                        {
                            prefab = VehiclePrefabName(prefabSystem, carriage),
                            countMin = buffer[i].m_Count.x,
                            countMax = buffer[i].m_Count.y,
                            passengerCapacity = carriage != Entity.Null && EntityManager.HasComponent<PublicTransportVehicleData>(carriage)
                                ? EntityManager.GetComponentData<PublicTransportVehicleData>(carriage).m_PassengerCapacity
                                : 0,
                        });
                    }
                    carriages = list;
                }
                candidates.Add(new
                {
                    name = VehiclePrefabName(prefabSystem, prefab),
                    role = VehicleRole(prefab),
                    passengerCapacity = data.m_PassengerCapacity,
                    maxSpeed = EntityManager.HasComponent<TrainData>(prefab) ? EntityManager.GetComponentData<TrainData>(prefab).m_MaxSpeed : (float?)null,
                    carriages,
                    locked = IsLocked(prefab),
                });
            }

            return BridgeResponse.Json(new
            {
                line = new { index = line.Index, version = line.Version },
                name = LabelOf(World.GetOrCreateSystemManaged<Game.UI.NameSystem>(), line),
                transportType = TransitTypeName(lineData.m_TransportType),
                selected,
                candidates,
                note = "selected empty = the game picks a random model per vehicle; set one with cs2_set_line_vehicles " +
                       "(primary = engine or single vehicle, secondary = carriage); only new vehicles use it",
            });
        }

        private BridgeResponse SetLineVehicles(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!TryResolveLine(request, out Entity line, out error))
            {
                return error;
            }
            if (!TryGetLineTransportData(line, out TransportLineData lineData, out error))
            {
                return error;
            }
            if (!EntityManager.HasBuffer<VehicleModel>(line))
            {
                return BridgeResponse.Error(409, "this line has no vehicle model list");
            }

            bool clear = request.TryGetBool("clear", out bool rawClear) && rawClear;
            Entity primary = Entity.Null;
            Entity secondary = Entity.Null;
            if (!clear)
            {
                if (!request.Query.TryGetValue("primary", out string primaryName) || string.IsNullOrEmpty(primaryName))
                {
                    return BridgeResponse.Error(400, "provide ?primary=<vehicle name from cs2_line_vehicles> (and optionally secondary), or clear=true");
                }
                if (!TryFindLineVehicle(lineData.m_TransportType, primaryName, out primary, out error))
                {
                    return error;
                }
                if (request.Query.TryGetValue("secondary", out string secondaryName) && !string.IsNullOrEmpty(secondaryName)
                    && !TryFindLineVehicle(lineData.m_TransportType, secondaryName, out secondary, out error))
                {
                    return error;
                }
                if (VehicleRole(primary) == "carriage")
                {
                    return BridgeResponse.Error(400, $"'{primaryName}' is a carriage; use it as secondary with an engine as primary");
                }
                if (secondary != Entity.Null && VehicleRole(secondary) != "carriage")
                {
                    return BridgeResponse.Error(400, "secondary must be a carriage");
                }
                if (!IsForced(request) && (IsLocked(primary) || (secondary != Entity.Null && IsLocked(secondary))))
                {
                    return BridgeResponse.Error(409, "vehicle model is locked (milestone not reached)");
                }
            }

            DynamicBuffer<VehicleModel> models = EntityManager.GetBuffer<VehicleModel>(line);
            models.Clear();
            if (!clear)
            {
                models.Add(new VehicleModel { m_PrimaryPrefab = primary, m_SecondaryPrefab = secondary });
            }
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            return BridgeResponse.Json(new
            {
                line = new { index = line.Index, version = line.Version },
                primary = VehiclePrefabName(prefabSystem, primary),
                secondary = VehiclePrefabName(prefabSystem, secondary),
                cleared = clear,
                note = "vehicles already running keep their model; new vehicles from the depot use this one",
            });
        }

        private bool TryGetLineTransportData(Entity line, out TransportLineData data, out BridgeResponse error)
        {
            data = default;
            error = null;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!EntityManager.HasComponent<TransportLineData>(prefab))
            {
                error = BridgeResponse.Error(409, "line prefab has no transport data");
                return false;
            }
            data = EntityManager.GetComponentData<TransportLineData>(prefab);
            return true;
        }

        private List<Entity> LineVehicleCandidates(TransportType type)
        {
            var result = new List<Entity>();
            using (NativeArray<Entity> prefabs = TransitVehiclePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity prefab in prefabs)
                {
                    PublicTransportVehicleData data = EntityManager.GetComponentData<PublicTransportVehicleData>(prefab);
                    if (data.m_TransportType == type && (data.m_PurposeMask & PublicTransportPurpose.TransportLine) != 0)
                    {
                        result.Add(prefab);
                    }
                }
            }
            return result;
        }

        private bool TryFindLineVehicle(TransportType type, string name, out Entity prefab, out BridgeResponse error)
        {
            error = null;
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var names = new List<string>();
            foreach (Entity candidate in LineVehicleCandidates(type))
            {
                string candidateName = VehiclePrefabName(prefabSystem, candidate);
                if (string.Equals(candidateName, name, StringComparison.OrdinalIgnoreCase))
                {
                    prefab = candidate;
                    return true;
                }
                names.Add(candidateName);
            }
            prefab = Entity.Null;
            error = BridgeResponse.Error(404, $"unknown {TransitTypeName(type)} vehicle '{name}'; available: {string.Join(", ", names)}");
            return false;
        }

        private string VehicleRole(Entity prefab)
        {
            if (EntityManager.HasComponent<TrainEngineData>(prefab))
            {
                return EntityManager.HasComponent<MultipleUnitTrainData>(prefab) ? "multipleUnit" : "engine";
            }
            if (EntityManager.HasComponent<TrainCarriageData>(prefab))
            {
                return "carriage";
            }
            return "vehicle";
        }

        private static string VehiclePrefabName(PrefabSystem prefabSystem, Entity prefab)
        {
            if (prefab == Entity.Null)
            {
                return null;
            }
            PrefabBase prefabBase = prefabSystem.GetPrefab<PrefabBase>(prefab);
            return prefabBase != null ? prefabBase.name : $"{prefab.Index}:{prefab.Version}";
        }
    }
}
