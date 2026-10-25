using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Economy;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private const int ProcessingCompanyReadLimit = 8192;
        private const int ProcessingEmployeeReadLimit = 400000;
        private const int ProcessingGroupReadLimit = 1024;

        private sealed class ProcessingPropertyInfo
        {
            public bool industrial, office, abandoned, condemned, destroyed, hasDistrict;
            public Entity district;
            public int? level;
            public float? efficiency, efficiencyWithoutLackResources;
        }

        private sealed class ProcessingRecipeInfo
        {
            public IndustrialProcessData data;
            public string key, prefabName;
        }

        private sealed class ProcessingGroupInfo
        {
            public string processKey, districtStatus, districtName;
            public Entity district;
            public int? level;
            public int companyCount, employeeKnown, capacityKnown, workforceKnown, efficiencyKnown;
            public long employeeEntries, capacity, validCitizenEntries, invalidWorkerEntries, invalidJobLevels;
            public long[] employeeEntriesByAssignedJobLevel = new long[5];
            public double workforce, efficiency, efficiencyWithoutLackResources;
            public int abandonedProperties, condemnedProperties, destroyedProperties;
        }

        /// <summary>单次有界公司扫描，严格制造分类并按原生配方/产权地区/等级汇总；不写公司或统计、不跨重载保留ECS对象。</summary>
        private BridgeResponse GetProcessingSummary()
        {
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            string guid = World.GetExistingSystemManaged<Game.Serialization.LoadGameSystem>()?.context.instigatorGuid.ToString();
            if (string.IsNullOrEmpty(guid)) return BridgeResponse.Error(409, "no loaded save identity for processing summary");
            var prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            var names = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            var citizens = m_System.GetProcessingCitizenLookup();
            var propertyCache = new Dictionary<Entity, ProcessingPropertyInfo>();
            var recipeCache = new Dictionary<Entity, ProcessingRecipeInfo>();
            var recipes = new SortedDictionary<string, ProcessingRecipeInfo>(StringComparer.Ordinal);
            var recipePrefabs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var groups = new SortedDictionary<string, ProcessingGroupInfo>(StringComparer.Ordinal);
            var excluded = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var unknown = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int candidateCount, manufacturingCount = 0, employeeEntriesRead = 0;

            using (var query = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Companies.CompanyData>(), ComponentType.ReadOnly<Game.Companies.ProcessingCompany>() },
                None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Unity.Entities.Prefab>() }
            }))
            {
                candidateCount = query.CalculateEntityCount();
                if (candidateCount > ProcessingCompanyReadLimit)
                    return BridgeResponse.Error(413, $"processing candidate count {candidateCount} exceeds readonly budget {ProcessingCompanyReadLimit}; no partial summary returned");
                using (var companies = query.ToEntityArray(Allocator.Temp))
                foreach (Entity company in companies)
                {
                    // 采掘/仓储也可能有Industrial和Processing标记，必须先排除，不能按楼名或输出资源猜类型。
                    if (EntityManager.HasComponent<Game.Companies.ExtractorCompany>(company)) { CountProcessingReason(excluded, "extractor"); continue; }
                    if (EntityManager.HasComponent<Game.Companies.StorageCompany>(company)) { CountProcessingReason(excluded, "storage"); continue; }
                    if (EntityManager.HasComponent<Game.Companies.OfficeCompany>(company)) { CountProcessingReason(excluded, "officeCompany"); continue; }
                    bool industrial = EntityManager.HasComponent<Game.Companies.IndustrialCompany>(company);
                    bool service = EntityManager.HasComponent<Game.Companies.CommercialCompany>(company) || EntityManager.HasComponent<Game.Companies.ServiceAvailable>(company);
                    if (service) { CountProcessingReason(industrial ? unknown : excluded, industrial ? "conflictingIndustrialServiceMarkers" : "commercialOrService"); continue; }
                    if (!industrial) { CountProcessingReason(unknown, "missingIndustrialCompanyMarker"); continue; }
                    if (!EntityManager.HasComponent<Game.Buildings.PropertyRenter>(company)) { CountProcessingReason(unknown, "missingPropertyRenter"); continue; }
                    Entity property = EntityManager.GetComponentData<Game.Buildings.PropertyRenter>(company).m_Property;
                    if (!EntityManager.Exists(property) || !EntityManager.HasComponent<Game.Buildings.Building>(property)
                        || EntityManager.HasComponent<Game.Common.Deleted>(property) || EntityManager.HasComponent<Game.Tools.Temp>(property))
                    { CountProcessingReason(unknown, "unavailableProperty"); continue; }
                    if (!propertyCache.TryGetValue(property, out var pi))
                    {
                        pi = ReadProcessingProperty(property);
                        propertyCache.Add(property, pi);
                    }
                    if (pi.office) { CountProcessingReason(excluded, "officeProperty"); continue; }
                    if (!EntityManager.HasComponent<PrefabRef>(company)) { CountProcessingReason(unknown, "missingCompanyPrefab"); continue; }
                    Entity cp = EntityManager.GetComponentData<PrefabRef>(company).m_Prefab;
                    if (!recipeCache.TryGetValue(cp, out var recipe))
                    {
                        recipe = null;
                        if (EntityManager.Exists(cp) && EntityManager.HasComponent<IndustrialProcessData>(cp))
                        {
                            var process = EntityManager.GetComponentData<IndustrialProcessData>(cp);
                            if (process.m_Output.m_Resource != Resource.NoResource && process.m_Output.m_Amount > 0
                                && process.m_Input1.m_Amount >= 0 && process.m_Input2.m_Amount >= 0
                                && process.m_MaxWorkersPerCell >= 0f
                                && !float.IsNaN(process.m_MaxWorkersPerCell) && !float.IsInfinity(process.m_MaxWorkersPerCell))
                            {
                                prefabs.TryGetPrefab<PrefabBase>(cp, out var companyPrefab);
                                recipe = new ProcessingRecipeInfo { data = process, key = ProcessingRecipeKey(process), prefabName = companyPrefab?.name };
                            }
                        }
                        recipeCache.Add(cp, recipe);
                    }
                    if (recipe == null) { CountProcessingReason(unknown, "missingOrInvalidCompanyProcess"); continue; }
                    if (EconomyUtils.IsOfficeResource(recipe.data.m_Output.m_Resource)) { CountProcessingReason(excluded, "officeOutput"); continue; }
                    if (!pi.industrial) { CountProcessingReason(unknown, "missingIndustrialPropertyMarker"); continue; }
                    string districtStatus = !pi.hasDistrict ? "unknown" : pi.district == Entity.Null ? "outsideNativeDistrict" : "nativeDistrict";
                    string groupKey = recipe.key + "|" + districtStatus + "|" + pi.district.Index + ":" + pi.district.Version + "|" + (pi.level?.ToString(CultureInfo.InvariantCulture) ?? "unknown");
                    if (!groups.TryGetValue(groupKey, out var group))
                    {
                        if (groups.Count >= ProcessingGroupReadLimit)
                            return BridgeResponse.Error(413, $"processing groups exceed readonly budget {ProcessingGroupReadLimit}; no partial summary returned");
                        group = new ProcessingGroupInfo { processKey = recipe.key, district = pi.district, districtStatus = districtStatus,
                            districtName = pi.district != Entity.Null && EntityManager.Exists(pi.district) ? names.GetRenderedLabelName(pi.district) : null, level = pi.level };
                        groups.Add(groupKey, group);
                    }
                    if (!recipes.ContainsKey(recipe.key)) { recipes.Add(recipe.key, recipe); recipePrefabs.Add(recipe.key, new HashSet<string>(StringComparer.Ordinal)); }
                    if (recipe.prefabName != null) recipePrefabs[recipe.key].Add(recipe.prefabName);
                    manufacturingCount++; group.companyCount++;
                    if (pi.abandoned) group.abandonedProperties++;
                    if (pi.condemned) group.condemnedProperties++;
                    if (pi.destroyed) group.destroyedProperties++;
                    if (EntityManager.HasComponent<Game.Companies.WorkProvider>(company))
                    {
                        int capacity = EntityManager.GetComponentData<Game.Companies.WorkProvider>(company).m_MaxWorkers;
                        if (capacity >= 0) { group.capacityKnown++; group.capacity += capacity; }
                    }
                    if (EntityManager.HasBuffer<Game.Companies.Employee>(company))
                    {
                        var employees = EntityManager.GetBuffer<Game.Companies.Employee>(company, true);
                        if (employees.Length > ProcessingEmployeeReadLimit - employeeEntriesRead)
                            return BridgeResponse.Error(413, $"processing employee entries exceed readonly budget {ProcessingEmployeeReadLimit}; no partial summary returned");
                        employeeEntriesRead += employees.Length; group.employeeKnown++; group.employeeEntries += employees.Length;
                        bool validLevels = true;
                        foreach (var employee in employees)
                        {
                            if (employee.m_Level >= 0 && employee.m_Level < 5) group.employeeEntriesByAssignedJobLevel[employee.m_Level]++;
                            else { group.invalidJobLevels++; validLevels = false; }
                            if (citizens.HasComponent(employee.m_Worker)) group.validCitizenEntries++; else group.invalidWorkerEntries++;
                        }
                        if (validLevels)
                        {
                            float workforce = EconomyUtils.GetWorkforce(employees, ref citizens);
                            if (!float.IsNaN(workforce) && !float.IsInfinity(workforce)) { group.workforceKnown++; group.workforce += workforce; }
                        }
                    }
                    if (pi.efficiency.HasValue && pi.efficiencyWithoutLackResources.HasValue)
                    { group.efficiencyKnown++; group.efficiency += pi.efficiency.Value; group.efficiencyWithoutLackResources += pi.efficiencyWithoutLackResources.Value; }
                }
            }

            var rows = groups.Values.Select(g => new
            {
                g.processKey, district = g.district != Entity.Null ? Ref(g.district) : null, g.districtStatus, g.districtName, g.level, g.companyCount,
                actualEmployeeEntriesSum = g.employeeKnown > 0 ? (long?)g.employeeEntries : null,
                employeeEntriesByAssignedJobLevel = g.employeeKnown > 0 ? g.employeeEntriesByAssignedJobLevel : null,
                employeeCapacityRawSum = g.capacityKnown > 0 ? (long?)g.capacity : null,
                effectiveWorkforceKnownSum = g.workforceKnown > 0 ? (double?)g.workforce : null,
                validCitizenEmployeeEntriesSum = g.employeeKnown > 0 ? (long?)g.validCitizenEntries : null,
                invalidWorkerEntries = g.employeeKnown > 0 ? (long?)g.invalidWorkerEntries : null,
                invalidAssignedJobLevelEntries = g.employeeKnown > 0 ? (long?)g.invalidJobLevels : null,
                nativeEfficiencyCompanyMean = g.efficiencyKnown > 0 ? (double?)(g.efficiency / g.efficiencyKnown) : null,
                nativeEfficiencyExcludingLackResourcesCompanyMean = g.efficiencyKnown > 0 ? (double?)(g.efficiencyWithoutLackResources / g.efficiencyKnown) : null,
                fieldKnownCompanyCounts = new { employeeEntries = g.employeeKnown, employeeCapacity = g.capacityKnown, workforce = g.workforceKnown, efficiency = g.efficiencyKnown,
                    propertyLevel = g.level.HasValue ? g.companyCount : 0, propertyDistrict = g.districtStatus != "unknown" ? g.companyCount : 0 },
                missingCompanyCounts = new { employeeEntries = g.companyCount - g.employeeKnown, employeeCapacity = g.companyCount - g.capacityKnown,
                    workforce = g.companyCount - g.workforceKnown, efficiency = g.companyCount - g.efficiencyKnown,
                    propertyLevel = g.level.HasValue ? 0 : g.companyCount, propertyDistrict = g.districtStatus != "unknown" ? 0 : g.companyCount },
                propertyConditionCompanyCounts = new { abandoned = g.abandonedProperties, condemned = g.condemnedProperties, destroyed = g.destroyedProperties }
            }).ToArray();
            var processRows = recipes.Values.Select(r => new { processKey = r.key,
                input1 = r.data.m_Input1.m_Resource.ToString(), input1AmountRaw = r.data.m_Input1.m_Amount,
                input2 = r.data.m_Input2.m_Resource.ToString(), input2AmountRaw = r.data.m_Input2.m_Amount,
                output = r.data.m_Output.m_Resource.ToString(), outputAmountRaw = r.data.m_Output.m_Amount,
                maxWorkersPerCell = r.data.m_MaxWorkersPerCell, distinctCompanyPrefabNames = recipePrefabs[r.key].OrderBy(x => x, StringComparer.Ordinal).ToArray() }).ToArray();
            int excludedCount = excluded.Values.Sum(), unknownCount = unknown.Values.Sum();
            if (candidateCount != manufacturingCount + excludedCount + unknownCount)
                return BridgeResponse.Error(500, "processing classification accounting did not close; no complete summary returned");
            return BridgeResponse.Json(new { schemaVersion = 1, capturedAtUtc = DateTime.UtcNow.ToString("o"), loadedSaveGuid = guid,
                frameIndex = World.GetExistingSystemManaged<Game.Simulation.SimulationSystem>()?.frameIndex,
                stalenessWarning = LockStalenessWarning, scope = "whole_city_strict_manufacturing", complete = true,
                classificationComplete = unknownCount == 0, candidateCount, manufacturingCompanyCount = manufacturingCount,
                excludedCompanyCount = excludedCount, unclassifiedCompanyCount = unknownCount, excluded, unclassifiedReasons = unknown,
                limits = new { companies = ProcessingCompanyReadLimit, employeeEntries = ProcessingEmployeeReadLimit, groups = ProcessingGroupReadLimit },
                employeeEntriesRead, processes = processRows, groups = rows,
                actualProductionByCompanySupported = false, actualProductionByProcessKeySupported = false, currentActiveCompanyCountSupported = false,
                note = "一次只读ECS扫描的严格制造配方/产权地区/等级/真实工位；缺字段单列覆盖，不补0。员工项与岗位容量不等于在产数，Workforce是原生幸福及岗位等级加权。效率为公司均值。配方系数不是实际流量；每公司/完整配方当前产量与活跃数无原生公开依据，全城实际供料另读production快照；不换算吨或月量、不写公司。" });
        }

        private ProcessingPropertyInfo ReadProcessingProperty(Entity property)
        {
            var pi = new ProcessingPropertyInfo {
                industrial = EntityManager.HasComponent<Game.Buildings.IndustrialProperty>(property), office = EntityManager.HasComponent<Game.Buildings.OfficeProperty>(property),
                abandoned = EntityManager.HasComponent<Game.Buildings.Abandoned>(property), condemned = EntityManager.HasComponent<Game.Buildings.Condemned>(property), destroyed = EntityManager.HasComponent<Game.Common.Destroyed>(property),
                hasDistrict = EntityManager.HasComponent<Game.Areas.CurrentDistrict>(property) };
            if (pi.hasDistrict) pi.district = EntityManager.GetComponentData<Game.Areas.CurrentDistrict>(property).m_District;
            if (EntityManager.HasComponent<PrefabRef>(property))
            {
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
                if (EntityManager.Exists(prefab) && EntityManager.HasComponent<SpawnableBuildingData>(prefab))
                {
                    var spawn = EntityManager.GetComponentData<SpawnableBuildingData>(prefab); pi.level = spawn.m_Level;
                    if (EntityManager.Exists(spawn.m_ZonePrefab) && EntityManager.HasComponent<ZoneData>(spawn.m_ZonePrefab))
                        pi.office |= (EntityManager.GetComponentData<ZoneData>(spawn.m_ZonePrefab).m_ZoneFlags & ZoneFlags.Office) != 0;
                }
            }
            if (EntityManager.HasBuffer<Game.Buildings.Efficiency>(property))
            {
                var efficiency = EntityManager.GetBuffer<Game.Buildings.Efficiency>(property, true);
                foreach (var factor in efficiency)
                    if (float.IsNaN(factor.m_Efficiency) || float.IsInfinity(factor.m_Efficiency)) return pi;
                // 已核当前原生API存在；桥接net472的Span类型引用无法绑定，沿既有详情使用同一原生乘积规则。
                float all = NativeEfficiencyRules.Round(NativeEfficiencyRules.Product(efficiency));
                float excluding = NativeEfficiencyRules.Round(NativeEfficiencyRules.Product(efficiency, Game.Buildings.EfficiencyFactor.LackResources));
                if (!float.IsNaN(all) && !float.IsInfinity(all) && !float.IsNaN(excluding) && !float.IsInfinity(excluding))
                { pi.efficiency = all; pi.efficiencyWithoutLackResources = excluding; }
            }
            return pi;
        }

        private static string ProcessingRecipeKey(IndustrialProcessData p) =>
            p.m_Input1.m_Resource + ":" + p.m_Input1.m_Amount.ToString(CultureInfo.InvariantCulture) + "|" +
            p.m_Input2.m_Resource + ":" + p.m_Input2.m_Amount.ToString(CultureInfo.InvariantCulture) + "|" +
            p.m_Output.m_Resource + ":" + p.m_Output.m_Amount.ToString(CultureInfo.InvariantCulture) + "|" + p.m_MaxWorkersPerCell.ToString("R", CultureInfo.InvariantCulture);

        private static void CountProcessingReason(IDictionary<string, int> counts, string reason) => counts[reason] = counts.TryGetValue(reason, out int count) ? count + 1 : 1;
    }
}
