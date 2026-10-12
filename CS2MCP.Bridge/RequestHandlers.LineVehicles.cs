using System;
using System.Collections.Generic;
using Game.Prefabs;
using Game.Routes;
using Game.Vehicles;
using Newtonsoft.Json;
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
                    m_TransitVehiclePrefabQuery = EntityManager.CreateEntityQuery(TransportVehicleSelectData.GetEntityQueryDesc());
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
            LineVehicleCandidates(lineData, out var primaryCandidates, out var secondaryCandidates, out var energies);
            var allCandidates = new List<Entity>(primaryCandidates); allCandidates.AddRange(secondaryCandidates);
            foreach (Entity prefab in allCandidates)
            {
                bool passenger = EntityManager.HasComponent<PublicTransportVehicleData>(prefab);
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
                    slot = primaryCandidates.Contains(prefab) ? "primary" : "secondary",
                    passengerCapacity = passenger ? EntityManager.GetComponentData<PublicTransportVehicleData>(prefab).m_PassengerCapacity : 0,
                    cargo = DescribeModelCargo(prefab),
                    maxSpeed = EntityManager.HasComponent<TrainData>(prefab) ? EntityManager.GetComponentData<TrainData>(prefab).m_MaxSpeed
                        : EntityManager.HasComponent<CarData>(prefab) ? EntityManager.GetComponentData<CarData>(prefab).m_MaxSpeed : (float?)null,
                    nativeFacts = new { vehicle = ModEntityComponents(prefab, "Game.Prefabs.VehicleData"), train = ModEntityComponents(prefab, "Game.Prefabs.Train"),
                        car = ModEntityComponents(prefab, "Game.Prefabs.CarData"), watercraft = ModEntityComponents(prefab, "Game.Prefabs.WatercraftData"), aircraft = ModEntityComponents(prefab, "Game.Prefabs.AircraftData") },
                    carriages,
                    locked = IsLocked(prefab),
                });
            }

            var fleet = new List<object>();
            if (EntityManager.HasBuffer<RouteVehicle>(line))
                foreach (var vehicle in EntityManager.GetBuffer<RouteVehicle>(line, true))
                    if (fleet.Count < 128 && EntityManager.Exists(vehicle.m_Vehicle)) fleet.Add(DescribeOperatingServiceVehicle(vehicle.m_Vehicle));
            return BridgeResponse.Json(new
            {
                line = new { index = line.Index, version = line.Version },
                name = LabelOf(World.GetOrCreateSystemManaged<Game.UI.NameSystem>(), line),
                transportType = TransitTypeName(lineData.m_TransportType),
                isCargo = lineData.m_CargoTransport, depotEnergyTypes = energies.ToString(),
                selected,
                candidates,
                operational = ModEntityComponents(line, "Game.Routes.TransportLine"), routeVehicles = ModEntityComponents(line, "Game.Routes.RouteVehicle"), fleet,
                note = "候选使用原生SelectVehiclesSection同一筛选；slot是原生栏，普通列车车厢primary／机车secondary。两栏是独立集合而非编组配对；空栏随机。容量、速度为原生单位；旧车保持模型，新车按新集合选择。车队限128项。",
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
            var primary = new List<Entity>();
            var secondary = new List<Entity>();
            bool hasChoice = request.Query.ContainsKey("primary") || request.Query.ContainsKey("secondary") || request.Query.ContainsKey("primaryModels") || request.Query.ContainsKey("secondaryModels");
            if (clear && hasChoice) return BridgeResponse.Error(400, "clear cannot be combined with model choices");
            if (!clear && !hasChoice) return BridgeResponse.Error(400, "provide model choices, or clear=true");
            if (!clear)
            {
                LineVehicleCandidates(lineData, out var candidatesA, out var candidatesB, out _);
                if (!ResolveLineSelection(request, "primary", "primaryModels", candidatesA, out primary, out error)
                    || !ResolveLineSelection(request, "secondary", "secondaryModels", candidatesB, out secondary, out error)) return error;
            }

            DynamicBuffer<VehicleModel> models = EntityManager.GetBuffer<VehicleModel>(line);
            models.Clear();
            // 与原生SelectVehiclesSection相同：两个栏各自是候选集合，不是逐项配对的指定编组。
            for (int i = 0; i < Math.Max(primary.Count, secondary.Count); i++)
                models.Add(new VehicleModel { m_PrimaryPrefab = i < primary.Count ? primary[i] : Entity.Null, m_SecondaryPrefab = i < secondary.Count ? secondary[i] : Entity.Null });
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            return BridgeResponse.Json(new
            {
                line = new { index = line.Index, version = line.Version },
                selected = ModEntityComponents(line, "Game.Routes.VehicleModel"),
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

        private void LineVehicleCandidates(TransportLineData lineData, out List<Entity> primary, out List<Entity> secondary, out EnergyTypes energies)
        {
            primary = new List<Entity>(); secondary = new List<Entity>(); energies = EnergyTypes.None;
            using (var depots = TransitDepotQuery.ToEntityArray(Allocator.Temp))
                foreach (var depot in depots) {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(depot).m_Prefab;
                    if (!EntityManager.HasComponent<TransportDepotData>(prefab)) continue;
                    var data = CombinedFacilityData<TransportDepotData>(depot, prefab);
                    if (data.m_TransportType == lineData.m_TransportType) energies |= data.m_EnergyTypes;
                }
            var select = new TransportVehicleSelectData(m_System);
            Unity.Jobs.JobHandle pending = default;
            try {
                select.PreUpdate(m_System, World.GetOrCreateSystemManaged<Game.City.CityConfigurationSystem>(), TransitVehiclePrefabQuery, Allocator.TempJob, out pending);
                pending.Complete();
                using (var a = new NativeList<Entity>(32, Allocator.Temp))
                using (var b = new NativeList<Entity>(16, Allocator.Temp)) {
                    select.ListVehicles(lineData.m_TransportType, energies, lineData.m_SizeClass,
                        lineData.m_CargoTransport ? (PublicTransportPurpose)0 : PublicTransportPurpose.TransportLine,
                        lineData.m_CargoTransport ? (Game.Economy.Resource)ulong.MaxValue : Game.Economy.Resource.NoResource,
                        a, b, ignoreTheme: true);
                    foreach (var entity in a) if (!IsLocked(entity) && !primary.Contains(entity)) primary.Add(entity);
                    foreach (var entity in b) if (!IsLocked(entity) && !secondary.Contains(entity)) secondary.Add(entity);
                }
            } finally {
                // PreUpdate更新lookup时也可能失败；未创建缓存的PostUpdate为原生no-op。
                // 已创建缓存仍可能被作业借用，沿真实依赖调度释放，不提前Dispose。
                select.PostUpdate(pending);
            }
        }

        private bool ResolveLineSelection(BridgeRequest request, string singleKey, string listKey, List<Entity> candidates, out List<Entity> result, out BridgeResponse error)
        {
            error = null; result = new List<Entity>(); var names = new List<string>();
            bool single = request.Query.TryGetValue(singleKey, out var name), multiple = request.Query.TryGetValue(listKey, out var json);
            if (single && multiple) { error = BridgeResponse.Error(400, $"use {singleKey} or {listKey}, not both"); return false; }
            if (multiple) {
                try { names = JsonConvert.DeserializeObject<List<string>>(json); }
                catch (JsonException) { error = BridgeResponse.Error(400, $"{listKey} must be an array of names"); return false; }
                if (names == null || names.Count > 32) { error = BridgeResponse.Error(400, $"{listKey} requires 0..32 names"); return false; }
            } else if (single) names.Add(name);
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            foreach (var requested in names) {
                if (string.IsNullOrWhiteSpace(requested)) { error = BridgeResponse.Error(400, "model names must be nonempty"); return false; }
                Entity found = Entity.Null;
                foreach (var candidate in candidates) if (string.Equals(VehiclePrefabName(prefabSystem, candidate), requested, StringComparison.OrdinalIgnoreCase)) { found = candidate; break; }
                if (found == Entity.Null) { error = BridgeResponse.Error(409, $"'{requested}' is not normally selectable in {singleKey} for this line; refresh candidates"); return false; }
                if (result.Contains(found)) { error = BridgeResponse.Error(400, "duplicate model selection"); return false; }
                result.Add(found);
            }
            return true;
        }

        private object DescribeModelCargo(Entity prefab)
        {
            if (!EntityManager.HasComponent<CargoTransportVehicleData>(prefab)) return null;
            var data = EntityManager.GetComponentData<CargoTransportVehicleData>(prefab); ulong mask = (ulong)data.m_Resources;
            var resources = new List<string>();
            foreach (Game.Economy.Resource resource in Enum.GetValues(typeof(Game.Economy.Resource))) {
                ulong flag = (ulong)resource;
                if (flag != 0 && (flag & (flag - 1)) == 0 && (mask & flag) != 0) resources.Add(resource.ToString());
            }
            return new { capacityRaw = data.m_CargoCapacity, maxResourceCount = data.m_MaxResourceCount, maintenanceRangeRaw = data.m_MaintenanceRange,
                resourceMask = "0x" + mask.ToString("X16"), resources };
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
