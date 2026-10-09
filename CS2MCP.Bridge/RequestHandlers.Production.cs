using System;
using System.Collections.Generic;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Jobs;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        /// <summary>复用原生生产面板快照，资源需求先完成写任务，不扫描全城企业。</summary>
        private BridgeResponse GetProduction()
        {
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            var production = World.GetOrCreateSystemManaged<CityProductionStatisticSystem>();
            var capacity = World.GetOrCreateSystemManaged<CityProductionCapacityCalculationSystem>();
            var stored = World.GetOrCreateSystemManaged<CountCityStoredResourceSystem>().GetCityStoredResources();
            var commercial = World.GetOrCreateSystemManaged<CommercialDemandSystem>();
            var industrial = World.GetOrCreateSystemManaged<IndustrialDemandSystem>();
            JobHandle deps;
            var cCompany = commercial.GetResourceDemands(out deps); deps.Complete();
            var cBuilding = commercial.GetBuildingDemands(out deps); deps.Complete();
            var iCompany = industrial.GetResourceDemands(out deps); deps.Complete();
            var iBuilding = industrial.GetBuildingDemands(out deps); deps.Complete();
            var sCompany = industrial.GetStorageCompanyDemands(out deps); deps.Complete();
            var sBuilding = industrial.GetStorageBuildingDemands(out deps); deps.Complete();
            var consumptionProduction = production.GetConsumptionProductions();
            var usages = production.GetCityResourceUsages();
            var resourceSystem = World.GetOrCreateSystemManaged<ResourceSystem>();
            var prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            var rows = new List<object>();
            var iterator = ResourceIterator.GetIterator();
            while (iterator.Next())
            {
                Resource resource = iterator.resource;
                int index = EconomyUtils.GetResourceIndex(resource);
                var entity = resourceSystem.GetPrefab(resource);
                if (!prefabs.TryGetPrefab<ResourcePrefab>(entity, out var prefab)) continue;
                var value = consumptionProduction[index];
                var usage = usages[index];
                rows.Add(new { resource = resource.ToString(), resourceIndex = index,
                    material = prefab.m_IsMaterial, produceable = prefab.m_IsProduceable,
                    tradable = prefab.m_IsTradable, leisure = prefab.m_IsLeisure,
                    unitWeight = prefab.m_Weight, displayUnitKind = prefab.m_Weight > 0 ? "weight" : "integerRounded",
                    consumptionRaw = value.x, productionRaw = value.y, productionMinusConsumptionRaw = (long)value.y - value.x,
                    productionCapacityRaw = capacity.GetProductionCapacity(resource), storedRaw = stored[index],
                    consumerUsageRaw = new { serviceUpkeep = usage.m_ServiceUpkeep, citizens = usage.m_Citizens,
                        importExport = usage.m_ImportExport, retail = usage.m_Retail, commercial = usage.m_Commercial,
                        industrial = usage.m_Industrial, office = usage.m_Office, heating = usage.m_Heating, levelUp = usage.m_LevelUp },
                    demandRaw = new { commercialCompany = cCompany[index], commercialBuilding = cBuilding[index],
                        industrialOrOfficeCompany = iCompany[index], industrialOrOfficeBuilding = iBuilding[index],
                        storageCompany = sCompany[index], storageBuilding = sBuilding[index] } });
            }
            var chains = new List<object>();
            foreach (var item in production.GetProductionChains())
                chains.Add(new { input1 = item.Key.m_Consume1.ToString(), input2 = item.Key.m_Consume2.ToString(),
                    output = item.Key.m_Produce.ToString(), input1AmountRaw = item.Value.m_Consume1,
                    input2AmountRaw = item.Value.m_Consume2, outputAmountRaw = item.Value.m_Produce });
            return BridgeResponse.Json(new { capturedAtUtc = DateTime.UtcNow.ToString("o"), stalenessWarning = LockStalenessWarning,
                resources = rows, activeProductionChains = chains,
                note = "原生ProductionUISystem相同统计快照；产能不是实际产量，库存不是产量。分项需求为原始整数而非百分比。数量和进出口保留原生符号/单位，不擅自标吨或月量；快照有原生平滑与刷新周期，暂停/刚加载时不能当即时变化。" });
        }
    }
}
