using System;
using System.Collections.Generic;
using Game.City;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// Economy endpoints: budget breakdown (read), tax rates (read/write),
    /// per-service budget sliders (read/write). Write paths call the same
    /// game APIs the vanilla UI triggers use.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private EntityQuery m_ServicePrefabQuery;
        private bool m_ServicePrefabQueryCreated;

        private static readonly TaxAreaType[] kTaxAreas =
        {
            TaxAreaType.Residential,
            TaxAreaType.Commercial,
            TaxAreaType.Industrial,
            TaxAreaType.Office,
        };

        private EntityQuery ServicePrefabQuery
        {
            get
            {
                if (!m_ServicePrefabQueryCreated)
                {
                    m_ServicePrefabQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<ServiceData>(),
                        ComponentType.ReadOnly<UIObjectData>());
                    m_ServicePrefabQueryCreated = true;
                }
                return m_ServicePrefabQuery;
            }
        }

        private BridgeResponse GetBudget()
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            CityServiceBudgetSystem budget = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();

            var income = new Dictionary<string, int>();
            for (int i = 0; i < (int)IncomeSource.Count; i++)
            {
                income[((IncomeSource)i).ToString()] = budget.GetIncome((IncomeSource)i);
            }
            var expenses = new Dictionary<string, int>();
            for (int i = 0; i < (int)ExpenseSource.Count; i++)
            {
                expenses[((ExpenseSource)i).ToString()] = budget.GetExpense((ExpenseSource)i);
            }

            return BridgeResponse.Json(new
            {
                note = "hourly-updated monthly rates; detailed expense entries are positive costs, totalExpenses uses native signed total; balance is native net income",
                totalIncome = budget.GetTotalIncome(),
                totalExpenses = budget.GetTotalExpenses(),
                totalTaxIncome = budget.GetTotalTaxIncome(),
                balance = budget.GetBalance(),
                moneyDelta = budget.GetMoneyDelta(),
                income,
                expenses,
            });
        }

        private BridgeResponse GetTaxes()
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            TaxSystem tax = World.GetOrCreateSystemManaged<TaxSystem>();
            int2 limits = tax.GetTaxParameterData().m_TotalTaxLimits;
            var areas = new Dictionary<string, object>();
            foreach (TaxAreaType area in kTaxAreas)
            {
                // Span of effective rates across job levels / resources — NOT the settable range.
                int2 span = tax.GetTaxRateRange(area);
                areas[area.ToString()] = new
                {
                    rate = tax.GetTaxRate(area),
                    effectiveRateSpan = new { min = span.x, max = span.y },
                };
            }
            return BridgeResponse.Json(new
            {
                capturedAtUtc = DateTime.UtcNow.ToString("o"),
                allowedRange = new { min = limits.x, max = limits.y },
                stalenessWarning = LockStalenessWarning,
                taxRates = areas,
                individualRates = ReadIndividualTaxes(tax),
            });
        }

        private bool TaxResourceMatches(ResourcePrefab prefab, TaxAreaType area)
        {
            if (!prefab.TryGet<TaxableResource>(out var taxable)) return false;
            return taxable.m_TaxAreas == null || taxable.m_TaxAreas.Length == 0 || Array.IndexOf(taxable.m_TaxAreas, area) >= 0;
        }

        private object ReadIndividualTaxes(TaxSystem tax)
        {
            var levels = new List<object>();
            var resources = new List<object>();
            var parameters = tax.GetTaxParameterData();
            for (int level = 0; level <= 4; level++)
                levels.Add(new { educationLevel = level, rate = tax.GetResidentialTaxRate(level),
                    allowedRange = new { min = parameters.m_JobLevelTaxLimits.x, max = parameters.m_JobLevelTaxLimits.y } });
            var prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            using (var query = EntityManager.CreateEntityQuery(typeof(ResourceData), typeof(TaxableResourceData)))
            using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (Entity entity in entities)
                {
                    var prefab = prefabSystem.GetPrefab<ResourcePrefab>(entity);
                    Resource resource = EconomyUtils.GetResource(prefab.m_Resource);
                    foreach (var area in new[] { TaxAreaType.Commercial, TaxAreaType.Office })
                        if (TaxResourceMatches(prefab, area))
                        {
                            int rate = area == TaxAreaType.Commercial ? tax.GetCommercialTaxRate(resource)
                                : tax.GetOfficeTaxRate(resource);
                            resources.Add(new { area = area.ToString(), resource = resource.ToString(), rate,
                                allowedRange = new { min = parameters.m_ResourceTaxLimits.x, max = parameters.m_ResourceTaxLimits.y } });
                        }
                }
            return new { residentialByEducation = levels, resourceRates = resources,
                note = "税收面板相同TaxableResource类别；分项设置可能使大类基准归一化，修改后须重读全部分项。" };
        }

        private BridgeResponse HandleSetTax(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            if (!request.Query.TryGetValue("area", out string areaName)
                || !Enum.TryParse(areaName, ignoreCase: true, out TaxAreaType area)
                || Array.IndexOf(kTaxAreas, area) < 0)
            {
                return BridgeResponse.Error(400, "provide ?area=Residential|Commercial|Industrial|Office");
            }
            if (!request.TryGetInt("rate", out int rate))
            {
                return BridgeResponse.Error(400, "provide ?rate=<integer percent>");
            }

            TaxSystem tax = World.GetOrCreateSystemManaged<TaxSystem>();
            bool hasEducation = request.Query.ContainsKey("educationLevel");
            bool hasResource = request.Query.ContainsKey("resource");
            if (hasEducation || hasResource)
            {
                if (hasEducation && hasResource) return BridgeResponse.Error(400, "educationLevel and resource are mutually exclusive");
                if (hasEducation)
                {
                    if (area != TaxAreaType.Residential || !request.TryGetInt("educationLevel", out int level) || level < 0 || level > 4)
                        return BridgeResponse.Error(400, "educationLevel=0..4 requires Residential");
                    var range = tax.GetTaxParameterData().m_JobLevelTaxLimits;
                    if (rate < range.x || rate > range.y) return BridgeResponse.Error(400, "individual residential rate outside native allowed range");
                    tax.Readers.Complete();
                    tax.SetResidentialTaxRate(level, rate);
                }
                else
                {
                    if (area == TaxAreaType.Residential || area == TaxAreaType.Industrial || !Enum.TryParse(request.Query["resource"], true, out Resource resource)
                        || resource == Resource.NoResource || !Enum.IsDefined(typeof(Resource), resource))
                        return BridgeResponse.Error(400, "resource requires a taxable Commercial/Office product");
                    var prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
                    var entity = World.GetOrCreateSystemManaged<ResourceSystem>().GetPrefab(resource);
                    if (!prefabSystem.TryGetPrefab<ResourcePrefab>(entity, out var prefab) || !TaxResourceMatches(prefab, area))
                        return BridgeResponse.Error(400, "resource is not taxable in this area");
                    var range = tax.GetTaxParameterData().m_ResourceTaxLimits;
                    if (rate < range.x || rate > range.y) return BridgeResponse.Error(400, "individual resource rate outside native allowed range");
                    tax.Readers.Complete();
                    if (area == TaxAreaType.Commercial) tax.SetCommercialTaxRate(resource, rate);
                    else tax.SetOfficeTaxRate(resource, rate);
                }
                return GetTaxes();
            }
            int2 limits = tax.GetTaxParameterData().m_TotalTaxLimits;
            if (rate < limits.x || rate > limits.y) return BridgeResponse.Error(400, "total tax rate outside native allowed range");
            int applied = rate;
            tax.Readers.Complete();
            tax.SetTaxRate(area, applied);

            return BridgeResponse.Json(new
            {
                area = area.ToString(),
                requestedRate = rate,
                appliedRate = applied,
                newRate = tax.GetTaxRate(area),
                allowedRange = new { min = limits.x, max = limits.y },
            });
        }

        private BridgeResponse GetServiceBudgets()
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            CityServiceBudgetSystem budgetSystem = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            var services = new List<object>();
            using (NativeArray<Entity> entities = ServicePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    ServicePrefab prefab = prefabSystem.GetPrefab<ServicePrefab>(entity);
                    if (prefab == null)
                    {
                        continue;
                    }
                    int budget = budgetSystem.GetServiceBudget(entity);
                    budgetSystem.GetEstimatedServiceBudget(entity, out int upkeep);
                    // 原生计数方法接受建筑预制体；传服务类别会恒为零。
                    int buildingCount = 0;
                    foreach (Entity buildingPrefab in budgetSystem.GetServiceBuildings(entity))
                        buildingCount += budgetSystem.GetNumberOfServiceBuildings(buildingPrefab);
                    services.Add(new
                    {
                        name = prefab.name,
                        budgetPercent = budget,
                        efficiencyPercent = budgetSystem.GetServiceEfficiency(entity, budget),
                        estimatedUpkeep = upkeep,
                        buildings = buildingCount,
                    });
                }
            }

            return BridgeResponse.Json(new
            {
                note = "budgetPercent range 50-150, 100 = default; lower budget saves money but reduces efficiency",
                services,
            });
        }

        private BridgeResponse HandleSetServiceBudget(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            if (!request.Query.TryGetValue("service", out string serviceName) || string.IsNullOrEmpty(serviceName))
            {
                return BridgeResponse.Error(400, "provide ?service=<name from /city/service-budgets>");
            }
            if (!request.TryGetInt("percentage", out int percentage))
            {
                return BridgeResponse.Error(400, "provide ?percentage=<50-150>");
            }

            CityServiceBudgetSystem budgetSystem = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            using (NativeArray<Entity> entities = ServicePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    ServicePrefab prefab = prefabSystem.GetPrefab<ServicePrefab>(entity);
                    if (prefab == null || !string.Equals(prefab.name, serviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (percentage < 50 || percentage > 150) return BridgeResponse.Error(400, "budget percentage must be within native range 50..150");
                    int applied = percentage;
                    budgetSystem.SetServiceBudget(entity, applied);
                    return BridgeResponse.Json(new
                    {
                        service = prefab.name,
                        requestedPercent = percentage,
                        appliedPercent = applied,
                        efficiencyPercent = budgetSystem.GetServiceEfficiency(entity, applied),
                    });
                }
            }

            return BridgeResponse.Error(404, $"unknown service '{serviceName}'; list names via /city/service-budgets");
        }
    }
}
