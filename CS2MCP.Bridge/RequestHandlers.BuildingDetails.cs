using System;
using System.Collections.Generic;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private object Ref(Entity entity) => new { index = entity.Index, version = entity.Version };

        private bool ReadExistingEntity(BridgeRequest request, out Entity entity, out BridgeResponse error)
        {
            entity = Entity.Null; error = null;
            if (!request.TryGetInt("index", out int index) || !request.TryGetInt("version", out int version)) {
                error = BridgeResponse.Error(400, "provide index and version"); return false;
            }
            entity = new Entity { Index = index, Version = version };
            if (!EntityManager.Exists(entity) || EntityManager.HasComponent<Game.Common.Deleted>(entity) || EntityManager.HasComponent<Game.Tools.Temp>(entity)) {
                error = BridgeResponse.Error(404, "entity does not exist; refresh live entity reference"); return false;
            }
            return true;
        }

        private object DescribeBuildingDetail(Entity entity)
        {
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            var names = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            var row = new Dictionary<string, object> { ["entity"] = Ref(entity), ["name"] = names.GetRenderedLabelName(entity), ["prefab"] = NativeName(prefab) };
            if (EntityManager.HasComponent<Game.Common.Destroyed>(entity)) {
                var destroyed = EntityManager.GetComponentData<Game.Common.Destroyed>(entity);
                row["destruction"] = new { cleared = destroyed.m_Cleared, cleanupComplete = Unity.Mathematics.math.isfinite(destroyed.m_Cleared) && destroyed.m_Cleared >= 1f,
                    eventEntity = Ref(destroyed.m_Event), note = "原生灾害救援清障完成后才允许按原生费用重建；不修改此状态" };
            } else row["destruction"] = null;
            row["serviceOperationalComponents"] = ModEntityComponents(entity, "Game.Buildings.Deathcare");
            row["fireOperationalComponents"] = ModEntityComponents(entity, "Game.Buildings.FireStation");
            if (EntityManager.HasComponent<Game.Areas.CurrentDistrict>(entity)) {
                Entity district = EntityManager.GetComponentData<Game.Areas.CurrentDistrict>(entity).m_District;
                row["district"] = district == Entity.Null ? null : new { entity = Ref(district), name = names.GetRenderedLabelName(district) };
            }
            var districts = new List<object>();
            bool canAssign = EntityManager.HasBuffer<Game.Areas.ServiceDistrict>(entity);
            if (canAssign) foreach (var district in EntityManager.GetBuffer<Game.Areas.ServiceDistrict>(entity, true))
                districts.Add(new { entity = Ref(district.m_District), name = names.GetRenderedLabelName(district.m_District) });
            row["serviceDistrictAssignmentSupported"] = canAssign;
            row["assignedServiceDistricts"] = districts;
            row["serviceScope"] = canAssign && districts.Count == 0 ? "all districts within native operating range" : "native assigned districts";
            var efficiency = new List<object>(); float product = 1;
            if (EntityManager.HasBuffer<Game.Buildings.Efficiency>(entity))
                foreach (var factor in EntityManager.GetBuffer<Game.Buildings.Efficiency>(entity, true)) {
                    product *= Math.Max(0, factor.m_Efficiency);
                    efficiency.Add(new { factor = factor.m_Factor.ToString(), multiplier = factor.m_Efficiency });
                }
            row["efficiency"] = product > 0 ? Math.Max(.01f, (float)Math.Round(product * 100) / 100) : 0;
            row["efficiencyFactors"] = efficiency;
            if (EntityManager.HasComponent<SpawnableBuildingData>(prefab)) row["level"] = EntityManager.GetComponentData<SpawnableBuildingData>(prefab).m_Level;
            if (EntityManager.HasComponent<Game.Companies.WorkProvider>(entity)) row["employeeCapacity"] = EntityManager.GetComponentData<Game.Companies.WorkProvider>(entity).m_MaxWorkers;
            row["employees"] = EntityManager.HasBuffer<Game.Companies.Employee>(entity) ? EntityManager.GetBuffer<Game.Companies.Employee>(entity, true).Length : 0;
            row["ownedVehicleCount"] = EntityManager.HasBuffer<Game.Vehicles.OwnedVehicle>(entity) ? EntityManager.GetBuffer<Game.Vehicles.OwnedVehicle>(entity, true).Length : 0;
            if (EntityManager.HasComponent<DeathcareFacilityData>(prefab)) {
                var data = CombinedFacilityData<DeathcareFacilityData>(entity, prefab);
                float immediate = EntityManager.HasBuffer<Game.Buildings.Efficiency>(entity)
                    ? Game.Buildings.BuildingUtils.GetImmediateEfficiency(EntityManager.GetBuffer<Game.Buildings.Efficiency>(entity, true)) : 1f;
                // 与原生GetEfficiency逐因子相乘后的百分位取整一致；Span重载不兼容桥接的net472引用。
                float operatingEfficiency = product > 0 ? Unity.Mathematics.math.max(.01f, Unity.Mathematics.math.round(100f * product) / 100f) : 0;
                var fleet = new List<object>();
                if (EntityManager.HasBuffer<Game.Vehicles.OwnedVehicle>(entity))
                    foreach (var owned in EntityManager.GetBuffer<Game.Vehicles.OwnedVehicle>(entity, true)) {
                        Entity vehicle = owned.m_Vehicle;
                        if (fleet.Count >= 128 || !EntityManager.Exists(vehicle)) continue;
                        fleet.Add(new { entity = Ref(vehicle), prefab = PrefabNameOf(vehicle),
                            deleted = EntityManager.HasComponent<Game.Common.Deleted>(vehicle),
                            hearse = ModEntityComponents(vehicle, "Game.Vehicles.Hearse"),
                            parked = ModEntityComponents(vehicle, "Game.Vehicles.ParkedCar"),
                            lane = ModEntityComponents(vehicle, "Game.Vehicles.CarCurrentLane"),
                            target = ModEntityComponents(vehicle, "Game.Common.Target"),
                            path = ModEntityComponents(vehicle, "Game.Pathfind.PathOwner"),
                            position = ModEntityComponents(vehicle, "Game.Objects.Transform") });
                    }
                row["deathcare"] = new { hearseCapacity = data.m_HearseCapacity,
                    effectiveHearseCapacity = Game.Buildings.BuildingUtils.GetVehicleCapacity(Math.Min(operatingEfficiency, immediate), data.m_HearseCapacity),
                    immediateEfficiency = immediate, storageCapacity = data.m_StorageCapacity,
                    processingRateRaw = data.m_ProcessingRate, longTermStorage = data.m_LongTermStorage,
                    waitingAtFacility = EntityManager.HasBuffer<Game.Buildings.Patient>(entity) ? EntityManager.GetBuffer<Game.Buildings.Patient>(entity, true).Length : 0,
                    dispatch = ModEntityComponents(entity, "Game.Simulation.ServiceDispatch"), fleet,
                    note = "容量按原生UpgradeUtils合并生效升级；processingRate为AI原始量，不冒充每月数量。车队限128项，只读真实派车/停驻/寻路，不修改调度。" };
            }
            row["transport"] = new { prefab = ModEntityComponents(prefab, "Game.Prefabs.Transport"),
                operational = ModEntityComponents(entity, "Game.Buildings.Transport"),
                note = "原生预制体与现场组件分别读取；基础容量不能代替安装扩建后的有效容量或已出车辆" };
            if (EntityManager.HasComponent<Game.Buildings.PostFacility>(entity))
            {
                var inventory = new List<object>();
                if (EntityManager.HasBuffer<Game.Economy.Resources>(entity))
                    foreach (var resource in EntityManager.GetBuffer<Game.Economy.Resources>(entity, true))
                        inventory.Add(new { resource = resource.m_Resource.ToString(), amount = resource.m_Amount });
                var post = EntityManager.GetComponentData<Game.Buildings.PostFacility>(entity);
                var requests = new List<object>();
                foreach (var target in new[] { post.m_MailDeliverRequest, post.m_MailReceiveRequest, post.m_TargetRequest })
                    if (target != Entity.Null) requests.Add(DescribePostalRequest(target));
                var fleet = new List<object>();
                if (EntityManager.HasBuffer<Game.Vehicles.OwnedVehicle>(entity))
                    foreach (var owned in EntityManager.GetBuffer<Game.Vehicles.OwnedVehicle>(entity, true))
                        if (fleet.Count < 128 && EntityManager.Exists(owned.m_Vehicle)) fleet.Add(DescribeOperatingServiceVehicle(owned.m_Vehicle));
                row["postal"] = new { operational = ModEntityComponents(entity, "Game.Buildings.Post"), inventory, requests, fleet,
                    dispatch = ModEntityComponents(entity, "Game.Simulation.ServiceDispatch"),
                    note = "原生邮件分类库存、服务请求／重试／寻路与实际车队，均只读、保留原始单位；车队及缓冲最多128项。车辆所有权不等于正在投递" };
            }
            row["serviceDemand"] = new { mail = ModEntityComponents(entity, "Game.Buildings.MailProducer"), telecom = ModEntityComponents(entity, "Game.Buildings.TelecomConsumer"),
                garbage = ModEntityComponents(entity, "Game.Buildings.GarbageProducer"), coverage = ModEntityComponents(entity, "Game.Buildings.ServiceCoverage") };
            var ports = new List<object>();
            var nodes = new HashSet<Entity>();
            if (EntityManager.HasBuffer<Game.Net.SubNet>(entity)) foreach (var subnet in EntityManager.GetBuffer<Game.Net.SubNet>(entity, true)) {
                Entity part = subnet.m_SubNet;
                if (!EntityManager.Exists(part)) continue;
                if (EntityManager.HasComponent<Game.Net.Node>(part)) nodes.Add(part);
                else if (EntityManager.HasComponent<Game.Net.Edge>(part)) {
                    var edge = EntityManager.GetComponentData<Game.Net.Edge>(part); nodes.Add(edge.m_Start); nodes.Add(edge.m_End);
                }
            }
            foreach (Entity node in nodes) if (EntityManager.Exists(node) && EntityManager.HasComponent<Game.Net.Node>(node)) {
                var p = EntityManager.GetComponentData<Game.Net.Node>(node).m_Position;
                ports.Add(new { entity = Ref(node), prefab = PrefabNameOf(node), position = new { x = p.x, y = p.y, z = p.z } });
            }
            row["subNetworkNodes"] = ports;
            row["utilities"] = DescribeUtilityState(entity);
            var upgrades = new List<object>();
            if (EntityManager.HasBuffer<BuildingUpgradeElement>(prefab)) foreach (var upgrade in EntityManager.GetBuffer<BuildingUpgradeElement>(prefab, true)) {
                uint? cost = EntityManager.HasComponent<ServiceUpgradeData>(upgrade.m_Upgrade) ? EntityManager.GetComponentData<ServiceUpgradeData>(upgrade.m_Upgrade).m_UpgradeCost : (uint?)null;
                bool fixedExtension = EntityManager.HasComponent<BuildingExtensionData>(upgrade.m_Upgrade);
                bool hasUpgradeData = EntityManager.HasComponent<ServiceUpgradeData>(upgrade.m_Upgrade);
                var upgradeData = hasUpgradeData ? EntityManager.GetComponentData<ServiceUpgradeData>(upgrade.m_Upgrade) : default;
                int installedCount = 0;
                if (EntityManager.HasBuffer<Game.Buildings.InstalledUpgrade>(entity))
                    foreach (var item in EntityManager.GetBuffer<Game.Buildings.InstalledUpgrade>(entity, true))
                        if (EntityManager.Exists(item.m_Upgrade) && EntityManager.HasComponent<PrefabRef>(item.m_Upgrade)
                            && EntityManager.GetComponentData<PrefabRef>(item.m_Upgrade).m_Prefab == upgrade.m_Upgrade) installedCount++;
                upgrades.Add(new { prefab = NativeName(upgrade.m_Upgrade), locked = IsLocked(upgrade.m_Upgrade), constructionCost = cost,
                    installedCount, forbidMultiple = hasUpgradeData ? (bool?)upgradeData.m_ForbidMultiple : null,
                    repeatablePlacement = hasUpgradeData && EntityManager.HasComponent<BuildingData>(upgrade.m_Upgrade) && !upgradeData.m_ForbidMultiple,
                    lotMeters = EntityManager.HasComponent<BuildingData>(upgrade.m_Upgrade) ? new { width = EntityManager.GetComponentData<BuildingData>(upgrade.m_Upgrade).m_LotSize.x * 8, depth = EntityManager.GetComponentData<BuildingData>(upgrade.m_Upgrade).m_LotSize.y * 8 } : null,
                    nativeFacts = FacilityFacts(upgrade.m_Upgrade),
                    fixedExtension, requiresPlacement = !fixedExtension && EntityManager.HasComponent<BuildingData>(upgrade.m_Upgrade),
                    nativePlacementDistance = EntityManager.HasComponent<ServiceUpgradeData>(upgrade.m_Upgrade)
                        ? (float?)EntityManager.GetComponentData<ServiceUpgradeData>(upgrade.m_Upgrade).m_MaxPlacementDistance : null });
            }
            row["availableUpgrades"] = upgrades;
            var installed = new List<object>();
            if (EntityManager.HasBuffer<Game.Buildings.InstalledUpgrade>(entity)) foreach (var upgrade in EntityManager.GetBuffer<Game.Buildings.InstalledUpgrade>(entity, true))
                installed.Add(new { entity = Ref(upgrade.m_Upgrade), prefab = PrefabNameOf(upgrade.m_Upgrade) });
            row["installedUpgrades"] = installed;
            var upkeep = new List<object>();
            if (EntityManager.HasBuffer<ServiceUpkeepData>(prefab)) foreach (var item in EntityManager.GetBuffer<ServiceUpkeepData>(prefab, true))
                upkeep.Add(new { resource = item.m_Upkeep.m_Resource.ToString(), amount = item.m_Upkeep.m_Amount, scaleWithUsage = item.m_ScaleWithUsage });
            row["prefabUpkeepResources"] = upkeep;
            if (EntityManager.HasComponent<PollutionData>(prefab)) {
                var pollution = CombinedFacilityData<PollutionData>(entity, prefab);
                row["prefabPollution"] = new { air = pollution.m_AirPollution, ground = pollution.m_GroundPollution, noise = pollution.m_NoisePollution, scaleWithRenters = pollution.m_ScaleWithRenters };
            }
            var companies = new List<object>();
            if (EntityManager.HasBuffer<Game.Buildings.Renter>(entity)) foreach (var renter in EntityManager.GetBuffer<Game.Buildings.Renter>(entity, true)) {
                Entity company = renter.m_Renter;
                if (!EntityManager.Exists(company) || !EntityManager.HasComponent<Game.Companies.CompanyStatisticData>(company)) continue;
                var data = EntityManager.GetComponentData<Game.Companies.CompanyStatisticData>(company);
                var inventory = new List<object>();
                if (EntityManager.HasBuffer<Game.Economy.Resources>(company)) foreach (var resource in EntityManager.GetBuffer<Game.Economy.Resources>(company, true))
                    inventory.Add(new { resource = resource.m_Resource.ToString(), amount = resource.m_Amount });
                object process = null;
                if (EntityManager.HasComponent<PrefabRef>(company)) {
                    Entity cp = EntityManager.GetComponentData<PrefabRef>(company).m_Prefab;
                    if (EntityManager.HasComponent<IndustrialProcessData>(cp)) {
                        var ip = EntityManager.GetComponentData<IndustrialProcessData>(cp);
                        process = new { input1 = ip.m_Input1.m_Resource.ToString(), input2 = ip.m_Input2.m_Resource.ToString(), output = ip.m_Output.m_Resource.ToString() };
                    }
                }
                companies.Add(new { entity = Ref(company), name = names.GetRenderedLabelName(company), income = data.m_Income, profit = data.m_Profit,
                    bankBalance = data.m_Worth, costs = new { wages = data.m_WagePaid, rent = data.m_RentPaid, electricity = data.m_ElectricityPaid,
                        water = data.m_WaterPaid, sewage = data.m_SewagePaid, garbage = data.m_GarbagePaid, tax = data.m_TaxPaid, resources = data.m_CostBuyResource },
                    employees = EntityManager.HasBuffer<Game.Companies.Employee>(company) ? EntityManager.GetBuffer<Game.Companies.Employee>(company, true).Length : 0,
                    employeeCapacity = EntityManager.HasComponent<Game.Companies.WorkProvider>(company) ? EntityManager.GetComponentData<Game.Companies.WorkProvider>(company).m_MaxWorkers : 0,
                    ownedVehicleCount = EntityManager.HasBuffer<Game.Vehicles.OwnedVehicle>(company) ? EntityManager.GetBuffer<Game.Vehicles.OwnedVehicle>(company, true).Length : 0,
                    process, inventory });
            }
            row["companies"] = companies;
            row["note"] = "native building/company facts; prefab upkeep is not total current monthly upkeep including wages; resource amounts are raw native units, not tons; vehicle count is ownership, not dispatch capacity";
            return row;
        }

        private object DescribePostalRequest(Entity entity)
        {
            bool exists = EntityManager.Exists(entity);
            return new { entity = Ref(entity), exists, data = exists ? new { service = ModEntityComponents(entity, "Game.Simulation.ServiceRequest"),
                transfer = ModEntityComponents(entity, "Game.Simulation.MailTransferRequest"), dispatched = ModEntityComponents(entity, "Game.Simulation.Dispatched"),
                path = ModEntityComponents(entity, "Game.Pathfind.PathInformation"), pathOwner = ModEntityComponents(entity, "Game.Pathfind.PathOwner"),
                pathElements = ModEntityComponents(entity, "Game.Pathfind.PathElement") } : null };
        }

        private object DescribeOperatingServiceVehicle(Entity entity)
        {
            return new { entity = Ref(entity), prefab = PrefabNameOf(entity), deleted = EntityManager.HasComponent<Game.Common.Deleted>(entity),
                postVan = ModEntityComponents(entity, "Game.Vehicles.PostVan"), delivery = ModEntityComponents(entity, "Game.Vehicles.DeliveryTruck"),
                publicTransport = ModEntityComponents(entity, "Game.Vehicles.PublicTransport"), cargoTransport = ModEntityComponents(entity, "Game.Vehicles.CargoTransport"),
                layout = ModEntityComponents(entity, "Game.Vehicles.LayoutElement"), parked = ModEntityComponents(entity, "Game.Vehicles.Parked"),
                lane = ModEntityComponents(entity, "Game.Vehicles.CarCurrentLane"), target = ModEntityComponents(entity, "Game.Common.Target"),
                path = ModEntityComponents(entity, "Game.Pathfind.PathOwner"), position = ModEntityComponents(entity, "Game.Objects.Transform") };
        }

        private BridgeResponse GetBuildingDetails(BridgeRequest request)
        {
            if (!ReadExistingEntity(request, out Entity entity, out BridgeResponse error)) return error;
            if (!EntityManager.HasComponent<Game.Buildings.Building>(entity) || !EntityManager.HasComponent<PrefabRef>(entity)) return BridgeResponse.Error(400, "entity is not a building");
            return BridgeResponse.Json(DescribeBuildingDetail(entity));
        }

        private BridgeResponse GetHappinessFactors()
        {
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            var rows = new List<object>();
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<HappinessFactorParameterData>())) {
                if (query.IsEmptyIgnoreFilter) return BridgeResponse.Error(409, "happiness parameters not ready");
                var parameters = EntityManager.GetBuffer<HappinessFactorParameterData>(query.GetSingletonEntity(), true);
                var system = World.GetOrCreateSystemManaged<Game.Simulation.CitizenHappinessSystem>();
                var locked = system.GetComponentLookup<Locked>(true);
                for (int i = 0; i < (int)Game.Simulation.CitizenHappinessSystem.HappinessFactor.Count; i++) {
                    var factor = (Game.Simulation.CitizenHappinessSystem.HappinessFactor)i;
                    var values = system.GetHappinessFactor(factor, parameters, ref locked);
                    float contribution = values.x;
                    int rounded = UnityEngine.Mathf.RoundToInt(contribution);
                    var lockedEntity = parameters[i].m_LockedEntity;
                    rows.Add(new { factor = factor.ToString(), contribution, roundedContribution = rounded,
                        healthContribution = values.y, wellbeingContribution = values.z,
                        suppressedByLockedService = lockedEntity != Entity.Null && locked.HasComponent(lockedEntity) && locked.IsComponentEnabled(lockedEntity) });
                }
            }
            return BridgeResponse.Json(new { capturedAtUtc = DateTime.UtcNow.ToString("o"), stalenessWarning = LockStalenessWarning,
                factors = rows, note = "原生城市幸福提示同一计算；health与wellbeing分开，全城平均不证明每个片区健康。分项保留原生数值，不设置玩家自定义警报阈值。" });
        }

        private BridgeResponse SetEntityName(BridgeRequest request)
        {
            if (!ReadExistingEntity(request, out Entity entity, out BridgeResponse error)) return error;
            if (!request.Query.TryGetValue("name", out string name) || string.IsNullOrWhiteSpace(name) || name.Length > 64) return BridgeResponse.Error(400, "name must contain 1..64 characters");
            if (EntityManager.HasComponent<Game.Net.Aggregated>(entity)) entity = EntityManager.GetComponentData<Game.Net.Aggregated>(entity).m_Aggregate;
            if (entity == Entity.Null || !EntityManager.Exists(entity)) return BridgeResponse.Error(409, "street aggregate is not ready");
            if (EntityManager.HasComponent<Game.Areas.District>(entity))
                return BridgeResponse.Error(400, "district renaming belongs to the district editing workflow");
            Entity target = entity;
            World.GetOrCreateSystemManaged<BridgeTransitRenameSystem>().Enqueue(target, name, request,
                warning => BridgeResponse.Json(new { entity = Ref(target), requestedName = name, nameWarning = warning,
                    actualName = World.GetOrCreateSystemManaged<Game.UI.NameSystem>().GetRenderedLabelName(target) }));
            return null;
        }

        private BridgeResponse SetBuildingDistricts(BridgeRequest request)
        {
            if (!ReadExistingEntity(request, out Entity entity, out BridgeResponse error)) return error;
            if (!EntityManager.HasComponent<Game.Buildings.Building>(entity) || !EntityManager.HasBuffer<Game.Areas.ServiceDistrict>(entity)) return BridgeResponse.Error(400, "building does not support native service district assignment");
            if (!request.Query.TryGetValue("districts", out string raw)) return BridgeResponse.Error(400, "provide districts index:version;... or empty to restore whole operating range");
            var targets = new HashSet<Entity>();
            foreach (string item in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) {
                if (!TryParseEntityRef(item, out Entity district) || !EntityManager.Exists(district) || !EntityManager.HasComponent<Game.Areas.District>(district)) return BridgeResponse.Error(400, "invalid/stale district: " + item);
                targets.Add(district);
            }
            var buffer = EntityManager.GetBuffer<Game.Areas.ServiceDistrict>(entity);
            buffer.Clear();
            foreach (Entity district in targets) buffer.Add(new Game.Areas.ServiceDistrict { m_District = district });
            return BridgeResponse.Json(DescribeBuildingDetail(entity));
        }

        private object DescribeRouteConnection(Entity prefab)
        {
            if (!EntityManager.HasComponent<RouteConnectionData>(prefab)) return null;
            var data = EntityManager.GetComponentData<RouteConnectionData>(prefab);
            return new { accessType = data.m_AccessConnectionType.ToString(), routeType = data.m_RouteConnectionType.ToString(),
                trackType = data.m_RouteTrackType.ToString(), roadType = data.m_RouteRoadType.ToString(),
                sizeClass = data.m_RouteSizeClass.ToString(), startLaneOffset = data.m_StartLaneOffset, endMargin = data.m_EndMargin };
        }

        private object DescribeRouteLane(Entity entity)
        {
            if (!EntityManager.HasComponent<Game.Routes.RouteLane>(entity)) return null;
            var lane = EntityManager.GetComponentData<Game.Routes.RouteLane>(entity);
            return new { start = DescribeTransitLane(lane.m_StartLane), end = DescribeTransitLane(lane.m_EndLane),
                startCurvePos = lane.m_StartCurvePos, endCurvePos = lane.m_EndCurvePos };
        }

        private object DescribeTransitLane(Entity lane, string pathMethods = null)
        {
            if (lane == Entity.Null || !EntityManager.Exists(lane)) return null;
            Entity owner = EntityManager.HasComponent<Game.Common.Owner>(lane) ? EntityManager.GetComponentData<Game.Common.Owner>(lane).m_Owner : Entity.Null;
            object curve = null;
            if (EntityManager.HasComponent<Game.Net.Curve>(lane)) {
                var data = EntityManager.GetComponentData<Game.Net.Curve>(lane).m_Bezier;
                curve = new { start = InterchangePoint(data.a), control1 = InterchangePoint(data.b),
                    control2 = InterchangePoint(data.c), end = InterchangePoint(data.d) };
            }
            object trackData = null, carData = null, connectionData = null, pathNodes = null, pedestrianData = null, parkingData = null;
            if (EntityManager.HasComponent<Game.Net.PedestrianLane>(lane)) {
                var data = EntityManager.GetComponentData<Game.Net.PedestrianLane>(lane);
                pedestrianData = new { flags = data.m_Flags.ToString(), accessRestriction = Ref(data.m_AccessRestriction) };
            }
            if (EntityManager.HasComponent<Game.Net.ParkingLane>(lane)) {
                var data = EntityManager.GetComponentData<Game.Net.ParkingLane>(lane);
                parkingData = new { flags = data.m_Flags.ToString(), accessRestriction = Ref(data.m_AccessRestriction),
                    freeSpaceRaw = data.m_FreeSpace, parkingFeeRaw = data.m_ParkingFee,
                    additionalStartNode = DescribePathNode(data.m_AdditionalStartNode),
                    note = "freeSpaceRaw is native lane free length, not a count of free stalls" };
            }
            if (EntityManager.HasComponent<Game.Net.CarLane>(lane)) {
                var data = EntityManager.GetComponentData<Game.Net.CarLane>(lane);
                carData = new { flags = data.m_Flags.ToString(), accessRestriction = Ref(data.m_AccessRestriction),
                    speedLimit = data.m_SpeedLimit, defaultSpeedLimit = data.m_DefaultSpeedLimit };
            }
            if (EntityManager.HasComponent<Game.Net.TrackLane>(lane)) {
                var data = EntityManager.GetComponentData<Game.Net.TrackLane>(lane);
                trackData = new { flags = data.m_Flags.ToString(), accessRestriction = Ref(data.m_AccessRestriction), speedLimit = data.m_SpeedLimit, curviness = data.m_Curviness };
            }
            // 外部连接使用 ConnectionLane；不能只看 prefab 名称判断是否允许列车/货运。
            if (EntityManager.HasComponent<Game.Net.ConnectionLane>(lane)) {
                var data = EntityManager.GetComponentData<Game.Net.ConnectionLane>(lane);
                connectionData = new { flags = data.m_Flags.ToString(), roadTypes = data.m_RoadTypes.ToString(), trackTypes = data.m_TrackTypes.ToString() };
            }
            if (EntityManager.HasComponent<Game.Net.Lane>(lane)) {
                var data = EntityManager.GetComponentData<Game.Net.Lane>(lane);
                pathNodes = new { start = DescribePathNode(data.m_StartNode), middle = DescribePathNode(data.m_MiddleNode), end = DescribePathNode(data.m_EndNode) };
            }
            object masterData = null, slaveData = null;
            if (EntityManager.HasComponent<Game.Net.MasterLane>(lane)) {
                var data = EntityManager.GetComponentData<Game.Net.MasterLane>(lane);
                masterData = new { flags = data.m_Flags.ToString(), group = data.m_Group, minIndex = data.m_MinIndex, maxIndex = data.m_MaxIndex };
            }
            if (EntityManager.HasComponent<Game.Net.SlaveLane>(lane)) {
                var data = EntityManager.GetComponentData<Game.Net.SlaveLane>(lane);
                slaveData = new { flags = data.m_Flags.ToString(), group = data.m_Group, minIndex = data.m_MinIndex,
                    maxIndex = data.m_MaxIndex, subIndex = data.m_SubIndex, masterIndex = data.m_MasterIndex };
            }
            return new { entity = Ref(lane), prefab = PrefabNameOf(lane), owner = Ref(owner), ownerPrefab = PrefabNameOf(owner), masterData, slaveData,
                masterLane = EntityManager.HasComponent<Game.Net.MasterLane>(lane),
                slaveLane = EntityManager.HasComponent<Game.Net.SlaveLane>(lane),
                track = EntityManager.HasComponent<Game.Net.TrackLane>(lane), trackData, carData, pedestrianData, parkingData, connectionData, pathNodes, curve, pathMethods };
        }

        private static object DescribePathNode(Game.Pathfind.PathNode node)
            => new { ownerIndex = node.GetOwnerIndex(), laneIndex = node.GetLaneIndex(), curvePosition = node.GetCurvePos(), secondary = node.IsSecondary() };

        private List<object> ListTransitLinePrefabs(TransportType? typeFilter)
        {
            var result = new List<object>();
            using (var entities = TransitLinePrefabQuery.ToEntityArray(Allocator.Temp)) foreach (Entity entity in entities) {
                var data = EntityManager.GetComponentData<TransportLineData>(entity);
                if (typeFilter.HasValue && typeFilter.Value != data.m_TransportType) continue;
                var requirements = new List<object>();
                if (EntityManager.HasBuffer<UnlockRequirement>(entity)) foreach (var requirement in EntityManager.GetBuffer<UnlockRequirement>(entity, true))
                    requirements.Add(new { prefab = NativeName(requirement.m_Prefab), flags = requirement.m_Flags.ToString(), locked = IsLocked(requirement.m_Prefab) });
                result.Add(new { prefab = NativeName(entity), entity = Ref(entity), type = TransitTypeName(data.m_TransportType),
                    passenger = data.m_PassengerTransport, cargo = data.m_CargoTransport, routeConnection = DescribeRouteConnection(entity),
                    locked = IsLocked(entity), requirements });
            }
            return result;
        }
    }
}
