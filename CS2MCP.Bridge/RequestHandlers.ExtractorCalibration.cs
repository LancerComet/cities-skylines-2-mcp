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
        // 固定服务端预算；无放宽参数。局部计数超限抛到入口，整批拒绝，不返回截断成功。
        private const int ExCalPrefabLimit = 1024, ExCalCompanyLimit = 2048, ExCalAreaLimit = 4096;
        private const int ExCalEmployeeLimit = 100000, ExCalAreaLinkLimit = 16384, ExCalUpgradeLimit = 4096;
        private const int ExCalInventoryLimit = 65536, ExCalEfficiencyLimit = 32768;
        private static readonly Resource[] ExCalRawResources = { Resource.Coal, Resource.Cotton, Resource.Wood,
            Resource.Grain, Resource.Livestock, Resource.Oil, Resource.Ore, Resource.Stone, Resource.Vegetables, Resource.Fish };

        private sealed class ExCalBudgetException : Exception
        {
            public ExCalBudgetException(string name, int limit) : base(name + " exceeds readonly budget " + limit + "; no partial calibration returned") { }
        }
        private sealed class ExCalBudget
        {
            public int employees, areaLinks, upgrades, inventory, efficiency;
            public void Read(ref int used, int count, int limit, string field)
            {
                if (count < 0 || count > limit - used) throw new ExCalBudgetException(field, limit);
                used += count;
            }
        }
        private sealed class ExCalPrefab
        {
            public Entity entity;
            public IndustrialProcessData? process;
            public Game.Companies.StorageLimitData? storage;
            public WorkplaceData? workplace;
            public bool registeredCompanyPrefab, extractorPrefabMarker, validProcess;
            public object row;
        }
        private sealed class ExCalArea
        {
            public Entity entity;
            public bool available, lot;
            public float? surfaceArea;
            public Game.Areas.Extractor? extractor;
            public ExtractorAreaData? definition;
            public object row;
        }
        private sealed class ExCalParent
        {
            public Entity entity;
            public bool areaLinksKnown = true;
            public readonly List<ExCalArea> linkedAreas = new List<ExCalArea>();
            public float? lotCells;
            public int? storageAddition;
            public object row;
        }
        private sealed class ExCalAggregate
        {
            public int companies, employeeKnown, capacityKnown, workforceKnown, efficiencyKnown, cacheKnown, conditionalKnown;
            public long employees, capacity, lastProduceCache, conditionalDaily;
            public double workforce, efficiency;
            public readonly SortedSet<string> processKeys = new SortedSet<string>(StringComparer.Ordinal);
        }

        /// <summary>主线程单次有界十原料采掘校准。只读ECS、无跨加载缓存、无公司/区域/统计写入。</summary>
        private BridgeResponse GetExtractorCalibration(BridgeRequest request)
        {
            if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                return BridgeResponse.Error(405, "extractor calibration is GET-only");
            foreach (string key in request.Query.Keys)
                if (!string.Equals(key, "expectedLoadedSaveGuid", StringComparison.OrdinalIgnoreCase))
                    return BridgeResponse.Error(400, "unsupported extractor calibration query parameter: " + key);
            if (!TryGetCity(out Entity city, out BridgeResponse error)) return error;
            string guid = World.GetExistingSystemManaged<Game.Serialization.LoadGameSystem>()?.context.instigatorGuid.ToString();
            if (!Guid.TryParse(guid, out Guid actualGuid) || actualGuid == Guid.Empty)
                return BridgeResponse.Error(409, "no loaded save identity for extractor calibration");
            if (request.Query.TryGetValue("expectedLoadedSaveGuid", out string expected))
            {
                if (!Guid.TryParse(expected, out Guid expectedGuid) || expectedGuid == Guid.Empty)
                    return BridgeResponse.Error(400, "expectedLoadedSaveGuid must be a nonempty GUID");
                if (expectedGuid != actualGuid) return BridgeResponse.Error(409, "loaded save identity does not match expectedLoadedSaveGuid");
            }
            var prefabs = World.GetExistingSystemManaged<PrefabSystem>();
            var resources = World.GetExistingSystemManaged<ResourceSystem>();
            if (prefabs == null || resources == null)
                return BridgeResponse.Error(409, "native prefab/resource systems unavailable; no calibration returned");
            try { return ReadExtractorCalibration(city, guid, prefabs, resources); }
            catch (ExCalBudgetException e) { return BridgeResponse.Error(413, e.Message); }
        }

        private BridgeResponse ReadExtractorCalibration(Entity city, string guid, PrefabSystem prefabs, ResourceSystem resources)
        {
            int processId;
            using (var process = System.Diagnostics.Process.GetCurrentProcess()) processId = process.Id;
            var sourceAssembly = typeof(CompanyPrefab).Assembly.GetName();
            var budget = new ExCalBudget();
            // 复用084已核同步Citizen写依赖的入口，每请求重新取lookup；无雇员身份输出。
            var citizens = m_System.GetProcessingCitizenLookup();
            var prefabCache = new Dictionary<Entity, ExCalPrefab>();
            var parentCache = new Dictionary<Entity, ExCalParent>();
            var areaCache = new Dictionary<Entity, ExCalArea>();
            var registered = new List<ExCalPrefab>();
            var units = new List<object>();
            var resourceData = new Dictionary<Resource, ResourceData>();
            var resourceRows = new List<object>();
            var outputCounts = ExCalRawResources.ToDictionary(r => r, r => 0);
            var registryCounts = ExCalRawResources.ToDictionary(r => r, r => 0);
            var aggregates = ExCalRawResources.ToDictionary(r => r, r => new ExCalAggregate());
            var known = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (string field in new[] { "employeeEntries", "employeeCapacity", "workforce", "efficiency", "lastUpdateProduceCache", "inventory",
                "effectiveStorage", "fittingWorkersCondition", "concentrationCondition", "conditionalDaily", "resolvedAvailableHub", "resolvedAvailableParent", "hubLevel" }) known.Add(field, 0);
            var unknown = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int registeredCandidates, outsideRegistryScope = 0, registryUnknown = 0, candidateCount, rawCompanyCount = 0, outsideCompanyScope = 0;
            EconomyParameterData? economy = ExCalSingleton<EconomyParameterData>(out int economyCount);
            ExtractorParameterData? extraction = ExCalSingleton<ExtractorParameterData>(out int extractorParameterCount);
            foreach (Resource output in ExCalRawResources)
            {
                // 原生映射只在ResourceSystem主线程更新；GetPrefabs索引器也有长度检查，未初始化映射保留Null。
                Entity rp = resources.GetPrefabs()[output];
                ResourceData? rd = ExCalComponent<ResourceData>(rp);
                if (rd.HasValue) resourceData.Add(output, rd.Value);
                resourceRows.Add(new { resource = output.ToString(), resourceIndex = EconomyUtils.GetResourceIndex(output),
                    prefab = ExCalReference(rp), prefabName = ExCalPrefabName(rp, prefabs), resourceDataKnown = rd.HasValue,
                    neededWorkPerIndustrialUnitRaw = rd.HasValue ? (int?)rd.Value.m_NeededWorkPerUnit.x : null,
                    neededWorkPerCommercialUnitRaw = rd.HasValue ? (int?)rd.Value.m_NeededWorkPerUnit.y : null,
                    requireNaturalResource = rd.HasValue ? (bool?)rd.Value.m_RequireNaturalResource : null,
                    requireTemperature = rd.HasValue ? (bool?)rd.Value.m_RequireTemperature : null,
                    requiredTemperature = rd.HasValue ? ExCalFinite(rd.Value.m_RequiredTemperature) : null,
                    unitWeight = rd.HasValue ? ExCalFinite(rd.Value.m_Weight) : null,
                    produceable = rd.HasValue ? (bool?)rd.Value.m_IsProduceable : null,
                    tradable = rd.HasValue ? (bool?)rd.Value.m_IsTradable : null,
                    missing = rd.HasValue ? Array.Empty<string>() : new[] { "ResourceData" } });
            }
            using (var q = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<ExtractorCompanyData>() },
                // PrefabData自身是enableable；诊断注册全表必须忽略其enabled筛选并回报真实状态。
                // None未列Prefab并不能取消默认Prefab排除，因此Options也必须显式IncludePrefab。
                None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
                Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IgnoreComponentEnabledState
            }))
            {
                registeredCandidates = q.CalculateEntityCount();
                if (registeredCandidates > ExCalPrefabLimit) throw new ExCalBudgetException("registered company prefab candidates", ExCalPrefabLimit);
                using (var entities = q.ToEntityArray(Allocator.Temp)) foreach (Entity entity in entities)
                {
                    var cp = ExCalReadPrefab(entity, prefabs, prefabCache);
                    if (!cp.registeredCompanyPrefab || !cp.validProcess) { registryUnknown++; registered.Add(cp); continue; }
                    if (!registryCounts.ContainsKey(cp.process.Value.m_Output.m_Resource)) { outsideRegistryScope++; continue; }
                    registryCounts[cp.process.Value.m_Output.m_Resource]++; registered.Add(cp);
                }
            }
            using (var q = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Companies.CompanyData>(), ComponentType.ReadOnly<Game.Companies.ExtractorCompany>() },
                None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Unity.Entities.Prefab>() },
                // 诊断保留Disabled候选并单列；原生生产query的默认Disabled排除另计于eligibility。
                Options = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IgnoreComponentEnabledState
            }))
            {
                candidateCount = q.CalculateEntityCount();
                if (candidateCount > ExCalCompanyLimit) throw new ExCalBudgetException("extractor company candidates", ExCalCompanyLimit);
                using (var companies = q.ToEntityArray(Allocator.Temp)) foreach (Entity company in companies)
                {
                    var missing = new List<string>();
                    Entity cpEntity = ExCalPrefabEntity(company);
                    ExCalPrefab cp = ExCalReadPrefab(cpEntity, prefabs, prefabCache);
                    Resource? output = cp.validProcess ? (Resource?)cp.process.Value.m_Output.m_Resource : null;
                    bool conflictingMarkers = EntityManager.HasComponent<Game.Companies.StorageCompany>(company)
                        || EntityManager.HasComponent<Game.Companies.OfficeCompany>(company) || EntityManager.HasComponent<Game.Companies.CommercialCompany>(company)
                        || EntityManager.HasComponent<Game.Companies.ServiceAvailable>(company);
                    bool inScope = output.HasValue && outputCounts.ContainsKey(output.Value) && !conflictingMarkers;
                    if (output.HasValue && !outputCounts.ContainsKey(output.Value) && !conflictingMarkers) { outsideCompanyScope++; continue; }
                    if (inScope) { rawCompanyCount++; outputCounts[output.Value]++; }
                    else CountProcessingReason(unknown, conflictingMarkers ? "conflictingExtractorOtherCompanyMarkers" : "missingOrInvalidCompanyProcess");
                    if (conflictingMarkers) missing.Add("unambiguousExtractorCompanyCategory");
                    if (!cp.validProcess) missing.Add("validIndustrialProcessData");
                    if (!cp.registeredCompanyPrefab || !cp.extractorPrefabMarker) missing.Add("registeredExtractorCompanyPrefab");
                    var renter = ExCalComponent<Game.Buildings.PropertyRenter>(company);
                    Entity hub = renter.HasValue ? renter.Value.m_Property : Entity.Null;
                    if (!renter.HasValue) missing.Add("PropertyRenter");
                    bool hubAvailable = ExCalAvailable(hub);
                    if (!hubAvailable) missing.Add("availablePropertyHub");
                    var attached = ExCalComponent<Game.Objects.Attached>(hub);
                    Entity parentEntity = attached.HasValue ? attached.Value.m_Parent : Entity.Null;
                    if (!attached.HasValue) missing.Add("propertyHub.Attached");
                    ExCalParent parent = null;
                    if (ExCalAvailable(parentEntity))
                    {
                        if (!parentCache.TryGetValue(parentEntity, out parent))
                        {
                            parent = ExCalReadParent(parentEntity, prefabs, areaCache, budget);
                            parentCache.Add(parentEntity, parent);
                        }
                    }
                    else missing.Add("availableAttachedParent");
                    var capacity = ExCalComponent<Game.Companies.WorkProvider>(company);
                    if (!capacity.HasValue || capacity.Value.m_MaxWorkers < 0) missing.Add("validWorkProvider");
                    int? employeeCount = null, validCitizens = null, invalidWorkers = null, invalidLevels = null;
                    float? workforce = null;
                    long[] employeeLevels = null;
                    bool hasEmployee = EntityManager.HasBuffer<Game.Companies.Employee>(company);
                    if (hasEmployee)
                    {
                        var employees = EntityManager.GetBuffer<Game.Companies.Employee>(company, true);
                        budget.Read(ref budget.employees, employees.Length, ExCalEmployeeLimit, "employee entries");
                        employeeCount = employees.Length; validCitizens = 0; invalidWorkers = 0; invalidLevels = 0; employeeLevels = new long[5];
                        foreach (var employee in employees)
                        {
                            if (employee.m_Level >= 0 && employee.m_Level < 5) employeeLevels[employee.m_Level]++; else invalidLevels++;
                            if (citizens.HasComponent(employee.m_Worker)) validCitizens++; else invalidWorkers++;
                        }
                        if (invalidLevels == 0) workforce = ExCalFinite(EconomyUtils.GetWorkforce(employees, ref citizens));
                    }
                    else missing.Add("EmployeeBuffer");
                    if (!workforce.HasValue) missing.Add("finiteNativeWorkforce");
                    long? stock = null, totalInventory = null;
                    var inventoryRaw = new SortedDictionary<string, long>(StringComparer.Ordinal);
                    bool hasInventory = EntityManager.HasBuffer<Resources>(company);
                    if (hasInventory)
                    {
                        var inventory = EntityManager.GetBuffer<Resources>(company, true);
                        budget.Read(ref budget.inventory, inventory.Length, ExCalInventoryLimit, "inventory entries");
                        totalInventory = 0; if (output.HasValue) stock = 0;
                        foreach (var item in inventory)
                        {
                            if (item.m_Resource != Resource.Money) totalInventory += item.m_Amount;
                            if (output.HasValue && item.m_Resource == output.Value) stock += item.m_Amount;
                            if (outputCounts.ContainsKey(item.m_Resource))
                            {
                                string k = item.m_Resource.ToString(); inventoryRaw[k] = inventoryRaw.TryGetValue(k, out long old) ? old + item.m_Amount : item.m_Amount;
                            }
                        }
                    }
                    else missing.Add("ResourcesBuffer");
                    float? observedEfficiency = null; string efficiencyStatus = "unknown"; var efficiencyFactors = new List<object>();
                    if (hubAvailable)
                    {
                        if (EntityManager.HasBuffer<Game.Buildings.Efficiency>(hub))
                        {
                            var factors = EntityManager.GetBuffer<Game.Buildings.Efficiency>(hub, true);
                            budget.Read(ref budget.efficiency, factors.Length, ExCalEfficiencyLimit, "efficiency factors");
                            bool finite = true;
                            foreach (var factor in factors)
                            { finite &= ExCalFinite(factor.m_Efficiency).HasValue; efficiencyFactors.Add(new { factor = factor.m_Factor.ToString(), value = ExCalFinite(factor.m_Efficiency) }); }
                            if (finite) observedEfficiency = ExCalFinite(NativeEfficiencyRules.Round(NativeEfficiencyRules.Product(factors)));
                            efficiencyStatus = observedEfficiency.HasValue ? "observedFullNativeFactorProduct" : "unknownNonfiniteEfficiency";
                        }
                        else { observedEfficiency = 1f; efficiencyStatus = "nativeExplicitFallbackForAbsentEfficiencyBuffer"; }
                    }
                    if (!observedEfficiency.HasValue) missing.Add("finiteHubEfficiency");
                    var statistic = ExCalComponent<Game.Companies.CompanyStatisticData>(company);
                    if (!statistic.HasValue) missing.Add("CompanyStatisticData");
                    uint? updateGroup = EntityManager.HasComponent<Game.Simulation.UpdateFrame>(company)
                        ? (uint?)EntityManager.GetSharedComponent<Game.Simulation.UpdateFrame>(company).m_Index : null;
                    if (!updateGroup.HasValue) missing.Add("UpdateFrame");
                    bool companyDisabled = EntityManager.HasComponent<Unity.Entities.Disabled>(company);
                    bool nativeQueryEligible = !companyDisabled && renter.HasValue && hasInventory && EntityManager.HasComponent<PrefabRef>(company) && capacity.HasValue
                        && updateGroup.HasValue && statistic.HasValue && hasEmployee;
                    ResourceData? outputResource = inScope && resourceData.TryGetValue(output.Value, out var rd) ? (ResourceData?)rd : null;
                    if (!outputResource.HasValue) missing.Add("outputResourceData");
                    float? concentration = ExCalConcentration(parent, outputResource, extraction, out float? extractorSquareMeters);
                    int? fit = null;
                    if (cp.validProcess && parent?.lotCells != null)
                    {
                        float v = cp.process.Value.m_MaxWorkersPerCell * parent.lotCells.Value / 2f;
                        if (ExCalFinite(v).HasValue && v >= 0f && (double)v < int.MaxValue)
                            fit = Game.Simulation.CompanyUtils.GetExtractorFittingWorkers(parent.lotCells.Value, 1f, cp.process.Value);
                    }
                    long? effectiveStorage = cp.storage.HasValue && parent?.storageAddition != null
                        ? (long?)cp.storage.Value.m_Limit + parent.storageAddition.Value : null;
                    // 无Attached的公司不会走生产核；无父级InstalledUpgrade缓冲时，原生只用公司基础容量。
                    if (cp.storage.HasValue && parent != null && !EntityManager.HasBuffer<Game.Buildings.InstalledUpgrade>(parent.entity))
                        effectiveStorage = cp.storage.Value.m_Limit;
                    long? freeStorage = effectiveStorage.HasValue && totalInventory.HasValue ? effectiveStorage - totalInventory : null;
                    int? conditionalDaily = null;
                    if (cp.validProcess && outputResource.HasValue && workforce.HasValue && observedEfficiency.HasValue && economy.HasValue
                        && outputResource.Value.m_NeededWorkPerUnit.x > 0 && ExCalFinite(economy.Value.m_ExtractorProductionEfficiency).HasValue
                        && economy.Value.m_ExtractorProductionEfficiency >= 0f)
                    {
                        // 只作当前所读效率的条件理论量；生产Tick随后会重写NaturalResources/Fish因子，所以不冒充下一Tick实际量。
                        float v = cp.process.Value.m_Output.m_Amount * (observedEfficiency.Value * economy.Value.m_ExtractorProductionEfficiency
                            * workforce.Value * EconomyUtils.kCompanyUpdatesPerDay / outputResource.Value.m_NeededWorkPerUnit.x);
                        if (ExCalFinite(v).HasValue && v >= 0f && (double)v < int.MaxValue)
                        {
                            var employees = EntityManager.GetBuffer<Game.Companies.Employee>(company, true); var ep = economy.Value;
                            conditionalDaily = EconomyUtils.GetCompanyProductionPerDay(observedEfficiency.Value, true, employees,
                                cp.process.Value, outputResource.Value, ref citizens, ref ep, default(Game.Companies.ServiceAvailable), default(Game.Companies.ServiceCompanyData));
                        }
                    }
                    var hubSpawn = ExCalComponent<SpawnableBuildingData>(ExCalPrefabEntity(hub));
                    if (!cp.workplace.HasValue) missing.Add("companyPrefab.WorkplaceData");
                    if (!cp.storage.HasValue) missing.Add("companyPrefab.StorageLimitData");
                    if (!hubSpawn.HasValue) missing.Add("hubPrefab.SpawnableBuildingData");
                    if (!economy.HasValue) missing.Add("uniqueEconomyParameterData");
                    if (!extraction.HasValue) missing.Add("uniqueExtractorParameterData");
                    if (!effectiveStorage.HasValue) missing.Add("effectiveStorageLimit");
                    if (!fit.HasValue) missing.Add("fittingWorkersCondition");
                    if (!concentration.HasValue) missing.Add("concentrationCondition");
                    bool parentAttachmentKnown = parent != null && EntityManager.HasComponent<Game.Objects.Attachment>(parent.entity);
                    foreach (var field in new[] {
                        new { name = "employeeEntries", yes = employeeCount.HasValue }, new { name = "employeeCapacity", yes = capacity.HasValue && capacity.Value.m_MaxWorkers >= 0 },
                        new { name = "workforce", yes = workforce.HasValue }, new { name = "efficiency", yes = observedEfficiency.HasValue },
                        new { name = "lastUpdateProduceCache", yes = statistic.HasValue }, new { name = "inventory", yes = totalInventory.HasValue },
                        new { name = "effectiveStorage", yes = effectiveStorage.HasValue }, new { name = "fittingWorkersCondition", yes = fit.HasValue },
                        new { name = "concentrationCondition", yes = concentration.HasValue }, new { name = "conditionalDaily", yes = conditionalDaily.HasValue },
                        new { name = "resolvedAvailableHub", yes = hubAvailable }, new { name = "resolvedAvailableParent", yes = parent != null }, new { name = "hubLevel", yes = hubSpawn.HasValue } })
                        if (field.yes) known[field.name]++;
                    if (inScope)
                    {
                        var a = aggregates[output.Value]; a.companies++; a.processKeys.Add(ProcessingRecipeKey(cp.process.Value));
                        if (employeeCount.HasValue) { a.employeeKnown++; a.employees += employeeCount.Value; }
                        if (capacity.HasValue && capacity.Value.m_MaxWorkers >= 0) { a.capacityKnown++; a.capacity += capacity.Value.m_MaxWorkers; }
                        if (workforce.HasValue) { a.workforceKnown++; a.workforce += workforce.Value; }
                        if (observedEfficiency.HasValue) { a.efficiencyKnown++; a.efficiency += observedEfficiency.Value; }
                        if (statistic.HasValue) { a.cacheKnown++; a.lastProduceCache += statistic.Value.m_LastUpdateProduce; }
                        if (conditionalDaily.HasValue) { a.conditionalKnown++; a.conditionalDaily += conditionalDaily.Value; }
                    }
                    units.Add(new { company = ExCalReference(company), output = output?.ToString(), companyPrefab = ExCalReference(cpEntity),
                        classificationStatus = inScope ? "confirmedTenRawExtractor" : "unknown",
                        processKey = cp.process.HasValue ? ProcessingRecipeKey(cp.process.Value) : null,
                        propertyHub = ExCalReference(hub), propertyHubAvailable = hubAvailable, propertyHubPrefabName = ExCalPrefabName(ExCalPrefabEntity(hub), prefabs),
                        attachedParent = ExCalReference(parentEntity), parentAvailable = parent != null,
                        parentAttachmentMatchesHub = parentAttachmentKnown ? (bool?)(EntityManager.GetComponentData<Game.Objects.Attachment>(parent.entity).m_Attached == hub) : null,
                        propertyHubLevel = hubSpawn.HasValue ? (int?)hubSpawn.Value.m_Level : null,
                        employeeEntries = employeeCount, employeeCapacityRaw = capacity.HasValue && capacity.Value.m_MaxWorkers >= 0 ? (int?)capacity.Value.m_MaxWorkers : null,
                        employeeEntriesByAssignedJobLevel = employeeLevels, validCitizenEntries = validCitizens, invalidWorkerEntries = invalidWorkers, invalidAssignedJobLevels = invalidLevels,
                        effectiveWorkforce = workforce, observedNativeEfficiency = observedEfficiency, efficiencyStatus, efficiencyFactors,
                        lastUpdateProduceRaw = statistic.HasValue ? (int?)statistic.Value.m_LastUpdateProduce : null,
                        lastUpdateProduceDivisor = EconomyUtils.kCompanyUpdatesPerDay, lastProduceWriteFrameSupported = false,
                        updateGroup, nativeCompanyQueryEligibleAtSnapshot = nativeQueryEligible, companyDisabled, conflictingCompanyMarkers = conflictingMarkers,
                        companyStorageBaseRaw = cp.storage.HasValue ? (int?)cp.storage.Value.m_Limit : null, effectiveStorageLimitRaw = effectiveStorage,
                        totalNonMoneyInventoryRaw = totalInventory, outputInventoryRaw = stock, presentTenRawInventory = hasInventory ? inventoryRaw : null,
                        freeStorageRaw = freeStorage, anomalousNegativeFreeStorage = freeStorage.HasValue ? (bool?)(freeStorage < 0) : null,
                        inventoryWithinNativeInt32Range = totalInventory.HasValue ? (bool?)(totalInventory >= int.MinValue && totalInventory <= int.MaxValue) : null,
                        effectiveStorageWithinNativeInt32Range = effectiveStorage.HasValue ? (bool?)(effectiveStorage >= int.MinValue && effectiveStorage <= int.MaxValue) : null,
                        lotAreaCellsForFit = parent?.lotCells, fittingWorkersConditionRaw = fit,
                        extractorAreaSquareMetersForConcentration = extractorSquareMeters, concentrationCondition = concentration,
                        observedEfficiencyConditionalDailyRaw = conditionalDaily,
                        prerequisites = new { companyPrefabWorkplace = cp.workplace.HasValue, hubPrefabSpawnable = hubSpawn.HasValue,
                            taxPayer = EntityManager.HasComponent<Game.Agents.TaxPayer>(company), cityModifierBuffer = EntityManager.HasBuffer<Game.City.CityModifier>(city),
                            economyParameters = economy.HasValue, extractorParameters = extraction.HasValue },
                        actualActivitySupported = false, nextTickProductionSupported = false, connectionEligibilitySupported = false,
                        missing = missing.Distinct(StringComparer.Ordinal).ToArray() });
                }
            }
            int unknownCount = unknown.Values.Sum();
            if (candidateCount != rawCompanyCount + outsideCompanyScope + unknownCount
                || registeredCandidates != registryCounts.Values.Sum() + outsideRegistryScope + registryUnknown)
                return BridgeResponse.Error(500, "extractor calibration classification accounting did not close");
            return BridgeResponse.Json(new { schemaVersion = 1, capturedAtUtc = DateTime.UtcNow.ToString("o"), loadedSaveGuid = guid, processId,
                companyPrefabApiAssembly = new { name = sourceAssembly.Name, version = sourceAssembly.Version?.ToString() },
                frameIndex = World.GetExistingSystemManaged<Game.Simulation.SimulationSystem>()?.frameIndex,
                stalenessWarning = LockStalenessWarning, complete = true, scope = "registered_extractor_prefabs_and_all_live_extractor_company_candidates_ten_raw_resources",
                classificationComplete = unknownCount == 0 && registryUnknown == 0,
                registryReadOptions = "IncludePrefab|IncludeDisabledEntities|IgnoreComponentEnabledState",
                companyReadOptions = "IncludeDisabledEntities|IgnoreComponentEnabledState; explicit None Prefab/Deleted/Temp",
                candidateCount, rawCompanyCount, outsideCompanyScope, unknownCompanyCount = unknownCount, unclassifiedReasons = unknown,
                registeredPrefabCandidateCount = registeredCandidates, registeredRawPrefabCount = registryCounts.Values.Sum(), outsideRegistryScope, unknownRegistryPrefabCount = registryUnknown,
                resourceCoverage = ExCalRawResources.Select(r => new { resource = r.ToString(), confirmedRegisteredCompanyPrefabCount = registryCounts[r],
                    confirmedExtractorCompanyCount = outputCounts[r], companyCountCoverageComplete = unknownCount == 0,
                    registryCountCoverageComplete = registryUnknown == 0, resourceDataKnown = resourceData.ContainsKey(r) }).ToArray(),
                resourceCompanySummary = aggregates.Select(kv => new { resource = kv.Key.ToString(), companyCount = kv.Value.companies,
                    processKeys = kv.Value.processKeys.ToArray(), employeeEntriesKnownSum = kv.Value.employeeKnown > 0 ? (long?)kv.Value.employees : null,
                    employeeCapacityKnownSum = kv.Value.capacityKnown > 0 ? (long?)kv.Value.capacity : null,
                    effectiveWorkforceKnownSum = kv.Value.workforceKnown > 0 ? (double?)kv.Value.workforce : null,
                    observedEfficiencyKnownCompanyMean = kv.Value.efficiencyKnown > 0 ? (double?)(kv.Value.efficiency / kv.Value.efficiencyKnown) : null,
                    lastUpdateProduceCacheKnownSum = kv.Value.cacheKnown > 0 ? (long?)kv.Value.lastProduceCache : null,
                    observedEfficiencyConditionalDailyKnownSum = kv.Value.conditionalKnown > 0 ? (long?)kv.Value.conditionalDaily : null,
                    fieldKnownCompanyCounts = new { employeeEntries = kv.Value.employeeKnown, capacity = kv.Value.capacityKnown, workforce = kv.Value.workforceKnown,
                        efficiency = kv.Value.efficiencyKnown, lastUpdateProduceCache = kv.Value.cacheKnown, conditionalDaily = kv.Value.conditionalKnown } }).ToArray(),
                companyFieldCoverage = known.Select(kv => new { field = kv.Key, known = kv.Value, unknown = units.Count - kv.Value, denominator = units.Count }).ToArray(),
                parameters = new { economySingletonCount = economyCount, extractorSingletonCount = extractorParameterCount,
                    extractorProductionEfficiency = economy.HasValue ? ExCalFinite(economy.Value.m_ExtractorProductionEfficiency) : null,
                    productionStatisticsLerpFactor = economy.HasValue ? ExCalFinite(economy.Value.m_ProductionStatisticsLerpFactor) : null,
                    extraction = extraction.HasValue ? new { fullOil = ExCalFinite(extraction.Value.m_FullOil), fullOre = ExCalFinite(extraction.Value.m_FullOre),
                        fullFertility = ExCalFinite(extraction.Value.m_FullFertility), fullFish = ExCalFinite(extraction.Value.m_FullFish),
                        oilConsumption = ExCalFinite(extraction.Value.m_OilConsumption), oreConsumption = ExCalFinite(extraction.Value.m_OreConsumption),
                        fertilityConsumption = ExCalFinite(extraction.Value.m_FertilityConsumption), fishConsumption = ExCalFinite(extraction.Value.m_FishConsumption), forestConsumption = ExCalFinite(extraction.Value.m_ForestConsumption) } : null },
                resources = resourceRows, registeredCompanyPrefabs = registered.Select(p => p.row).ToArray(),
                actualCompanyPrefabs = prefabCache.Values.Where(p => !registered.Contains(p)).OrderBy(p => p.entity.Index).Select(p => p.row).ToArray(),
                companies = units, parents = parentCache.Values.OrderBy(p => p.entity.Index).Select(p => p.row).ToArray(),
                areas = areaCache.Values.OrderBy(a => a.entity.Index).Select(a => a.row).ToArray(),
                limits = new { prefabs = ExCalPrefabLimit, companies = ExCalCompanyLimit, uniqueAreas = ExCalAreaLimit, employeeEntries = ExCalEmployeeLimit,
                    areaLinks = ExCalAreaLinkLimit, upgrades = ExCalUpgradeLimit, inventoryEntries = ExCalInventoryLimit, efficiencyFactors = ExCalEfficiencyLimit },
                readCounts = new { prefabMetadata = prefabCache.Count, parents = parentCache.Count, uniqueAreas = areaCache.Count, budget.employees, budget.areaLinks, budget.upgrades, budget.inventory, budget.efficiency },
                cumulativeProducedResources = new { supported = false, values = (long[])null,
                    reason = "unsupported: shared Processing+Extractor NativeArray writer dependencies and other raw-output writer coverage not closed in this first version; no array access or drain" },
                actualProductionEventsSupported = false, currentActiveCompanyCountSupported = false, actualProductionByOperatingModeSupported = false,
                note = "原生注册数据及单次经营状态只读快照。complete只表示有界扫描未截断；unknown/missing与覆盖另列。LastUpdateProduce是最近成功走到写分支的缓存，未包含写入帧，可能跨早退保留；不等于当前活跃。区域资源/工作/采收、库存、岗位、条件理论产量互不替代。不换算吨/月，不新增公司，不改变任何原生效率或区域。" });
        }

        private T? ExCalSingleton<T>(out int count) where T : unmanaged, IComponentData
        {
            using (var q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<T>()))
            { count = q.CalculateEntityCount(); return count == 1 ? (T?)q.GetSingleton<T>() : null; }
        }
        private bool ExCalAvailable(Entity e) => e != Entity.Null && EntityManager.Exists(e)
            && !EntityManager.HasComponent<Game.Common.Deleted>(e) && !EntityManager.HasComponent<Game.Tools.Temp>(e);
        private T? ExCalComponent<T>(Entity e) where T : unmanaged, IComponentData => ExCalAvailable(e) && EntityManager.HasComponent<T>(e) ? (T?)EntityManager.GetComponentData<T>(e) : null;
        private Entity ExCalPrefabEntity(Entity e) => ExCalComponent<PrefabRef>(e)?.m_Prefab ?? Entity.Null;
        private object ExCalReference(Entity e) => e != Entity.Null ? Ref(e) : null;
        private string ExCalPrefabName(Entity p, PrefabSystem prefabs) => ExCalAvailable(p) && prefabs.TryGetPrefab<PrefabBase>(p, out var value) ? value.name : null;
        private static float? ExCalFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v) ? (float?)v : null;

        private ExCalPrefab ExCalReadPrefab(Entity entity, PrefabSystem prefabs, Dictionary<Entity, ExCalPrefab> cache)
        {
            if (cache.TryGetValue(entity, out var p)) return p;
            if (cache.Count >= ExCalPrefabLimit) throw new ExCalBudgetException("distinct company prefab metadata", ExCalPrefabLimit);
            p = new ExCalPrefab { entity = entity, process = ExCalComponent<IndustrialProcessData>(entity),
                storage = ExCalComponent<Game.Companies.StorageLimitData>(entity), workplace = ExCalComponent<WorkplaceData>(entity),
                registeredCompanyPrefab = ExCalAvailable(entity) && EntityManager.HasComponent<PrefabData>(entity) && prefabs.TryGetPrefab<CompanyPrefab>(entity, out _),
                extractorPrefabMarker = ExCalAvailable(entity) && EntityManager.HasComponent<ExtractorCompanyData>(entity) };
            if (p.process.HasValue)
            {
                var v = p.process.Value;
                p.validProcess = v.m_Output.m_Resource != Resource.NoResource && v.m_Output.m_Amount > 0 && v.m_Input1.m_Amount >= 0
                    && v.m_Input2.m_Amount >= 0 && ExCalFinite(v.m_MaxWorkersPerCell).HasValue && v.m_MaxWorkersPerCell >= 0f;
            }
            p.row = new { prefab = ExCalReference(entity), prefabName = ExCalPrefabName(entity, prefabs), p.registeredCompanyPrefab, p.extractorPrefabMarker,
                prefabDataEnabled = ExCalAvailable(entity) && EntityManager.HasComponent<PrefabData>(entity) ? (bool?)EntityManager.IsComponentEnabled<PrefabData>(entity) : null,
                unityPrefabMarker = ExCalAvailable(entity) ? (bool?)EntityManager.HasComponent<Unity.Entities.Prefab>(entity) : null,
                unityDisabled = ExCalAvailable(entity) ? (bool?)EntityManager.HasComponent<Unity.Entities.Disabled>(entity) : null,
                industrialProcessDataKnown = p.process.HasValue, p.validProcess, processKey = p.process.HasValue ? ProcessingRecipeKey(p.process.Value) : null,
                input1 = p.process?.m_Input1.m_Resource.ToString(), input1AmountRaw = p.process?.m_Input1.m_Amount,
                input2 = p.process?.m_Input2.m_Resource.ToString(), input2AmountRaw = p.process?.m_Input2.m_Amount,
                output = p.process?.m_Output.m_Resource.ToString(), outputAmountRaw = p.process?.m_Output.m_Amount,
                maxWorkersPerCell = p.process.HasValue ? ExCalFinite(p.process.Value.m_MaxWorkersPerCell) : null,
                workplaceDataKnown = p.workplace.HasValue, prefabWorkplacesRaw = p.workplace?.m_MaxWorkers,
                workplaceComplexity = p.workplace?.m_Complexity.ToString(), minimumWorkersLimitRaw = p.workplace?.m_MinimumWorkersLimit,
                storageLimitDataKnown = p.storage.HasValue, companyStorageLimitRaw = p.storage?.m_Limit };
            cache.Add(entity, p); return p;
        }

        private ExCalParent ExCalReadParent(Entity entity, PrefabSystem prefabs, Dictionary<Entity, ExCalArea> areas, ExCalBudget budget)
        {
            var p = new ExCalParent { entity = entity }; Entity prefab = ExCalPrefabEntity(entity);
            var links = new List<object>(); var upgrades = new List<object>();
            ExCalAddAreaLinks(entity, "parentDirect", p, links, prefabs, areas, budget);
            bool storageKnown = true; long addedStorage = 0;
            bool hasUpgradeBuffer = EntityManager.HasBuffer<Game.Buildings.InstalledUpgrade>(entity);
            if (hasUpgradeBuffer)
            {
                // 与原生GetBestConcentration/GetArea相同，分母与岗位区域含Inactive升级；生产资格另行判断。
                var baseStorage = ExCalComponent<Game.Companies.StorageLimitData>(prefab);
                if (baseStorage.HasValue) addedStorage += baseStorage.Value.m_Limit;
                if (prefab == Entity.Null) storageKnown = false;
                var buffer = EntityManager.GetBuffer<Game.Buildings.InstalledUpgrade>(entity, true);
                budget.Read(ref budget.upgrades, buffer.Length, ExCalUpgradeLimit, "installed upgrade entries");
                foreach (var upgrade in buffer)
                {
                    Entity up = upgrade.m_Upgrade, upPrefab = ExCalPrefabEntity(up);
                    bool inactive = Game.Buildings.BuildingUtils.CheckOption(upgrade, Game.Buildings.BuildingOption.Inactive);
                    var facility = ExCalComponent<ExtractorFacilityData>(upPrefab);
                    var storage = ExCalComponent<Game.Companies.StorageLimitData>(upPrefab);
                    if (!inactive)
                    {
                        if (!ExCalAvailable(up) || upPrefab == Entity.Null) storageKnown = false;
                        else if (storage.HasValue) addedStorage += storage.Value.m_Limit;
                    }
                    upgrades.Add(new { upgrade = ExCalReference(up), prefab = ExCalReference(upPrefab), prefabName = ExCalPrefabName(upPrefab, prefabs),
                        upgrade.m_OptionMask, inactive, available = ExCalAvailable(up), facilityDataKnown = facility.HasValue,
                        requirements = facility?.m_Requirements.ToString(), requirementsRaw = facility.HasValue ? (int?)facility.Value.m_Requirements : null,
                        storageLimitDataKnown = storage.HasValue, storageAdditionRaw = storage?.m_Limit, productionConnectionEligibility = "unknownNotEvaluated" });
                    ExCalAddAreaLinks(up, "installedUpgradeDirect", p, links, prefabs, areas, budget);
                }
            }
            if (storageKnown && addedStorage >= int.MinValue && addedStorage <= int.MaxValue) p.storageAddition = (int)addedStorage;
            float cells = 0f; bool cellsKnown = p.areaLinksKnown;
            foreach (var a in p.linkedAreas)
            {
                if (!a.available) { cellsKnown = false; continue; }
                if (a.lot) { if (a.surfaceArea.HasValue && a.surfaceArea.Value >= 0f) cells += a.surfaceArea.Value / 64f; else cellsKnown = false; }
            }
            if (cellsKnown && ExCalFinite(cells).HasValue) p.lotCells = cells;
            var attachment = ExCalComponent<Game.Objects.Attachment>(entity); var district = ExCalComponent<Game.Areas.CurrentDistrict>(entity);
            p.row = new { parent = ExCalReference(entity), prefab = ExCalReference(prefab), prefabName = ExCalPrefabName(prefab, prefabs),
                attachmentComponentKnown = attachment.HasValue, attachmentHub = attachment.HasValue ? ExCalReference(attachment.Value.m_Attached) : null,
                districtComponentKnown = district.HasValue, district = district.HasValue ? ExCalReference(district.Value.m_District) : null,
                installedUpgradeBufferPresent = hasUpgradeBuffer, upgrades, areaReferencesKnown = p.areaLinksKnown, areaLinks = links, lotAreaCellsForFit = p.lotCells,
                storageAdditionWhenUpgradeBufferPresentRaw = hasUpgradeBuffer ? p.storageAddition : null,
                actualProductionEligibilitySupported = false };
            return p;
        }

        private void ExCalAddAreaLinks(Entity owner, string relation, ExCalParent parent, List<object> links,
            PrefabSystem prefabs, Dictionary<Entity, ExCalArea> cache, ExCalBudget budget)
        {
            if (!ExCalAvailable(owner)) { parent.areaLinksKnown = false; return; }
            if (!EntityManager.HasBuffer<Game.Areas.SubArea>(owner)) return;
            var buffer = EntityManager.GetBuffer<Game.Areas.SubArea>(owner, true);
            budget.Read(ref budget.areaLinks, buffer.Length, ExCalAreaLinkLimit, "direct area references");
            foreach (var link in buffer)
            {
                Entity area = link.m_Area;
                if (!cache.TryGetValue(area, out var a))
                {
                    if (cache.Count >= ExCalAreaLimit) throw new ExCalBudgetException("unique areas", ExCalAreaLimit);
                    var geometry = ExCalComponent<Game.Areas.Geometry>(area); Entity ap = ExCalPrefabEntity(area);
                    a = new ExCalArea { entity = area, available = ExCalAvailable(area), lot = ExCalAvailable(area) && EntityManager.HasComponent<Game.Areas.Lot>(area),
                        surfaceArea = geometry.HasValue ? ExCalFinite(geometry.Value.m_SurfaceArea) : null,
                        extractor = ExCalComponent<Game.Areas.Extractor>(area), definition = ExCalComponent<ExtractorAreaData>(ap) };
                    var lotData = ExCalComponent<LotData>(ap); var ownership = ExCalComponent<Game.Common.Owner>(area);
                    a.row = new { area = ExCalReference(area), a.available, a.lot, prefab = ExCalReference(ap), prefabName = ExCalPrefabName(ap, prefabs),
                        ownerComponentKnown = ownership.HasValue, owner = ownership.HasValue ? ExCalReference(ownership.Value.m_Owner) : null,
                        geometryKnown = geometry.HasValue, surfaceAreaSquareMeters = a.surfaceArea, extractorKnown = a.extractor.HasValue,
                        resourceAmountRaw = a.extractor.HasValue ? ExCalFinite(a.extractor.Value.m_ResourceAmount) : null,
                        extractedAmountRaw = a.extractor.HasValue ? ExCalFinite(a.extractor.Value.m_ExtractedAmount) : null,
                        totalExtractedRaw = a.extractor.HasValue ? ExCalFinite(a.extractor.Value.m_TotalExtracted) : null,
                        harvestedAmountRaw = a.extractor.HasValue ? ExCalFinite(a.extractor.Value.m_HarvestedAmount) : null,
                        workAmountRaw = a.extractor.HasValue ? ExCalFinite(a.extractor.Value.m_WorkAmount) : null,
                        maxConcentrationRaw = a.extractor.HasValue ? ExCalFinite(a.extractor.Value.m_MaxConcentration) : null,
                        workType = a.extractor?.m_WorkType.ToString(), extractorAreaDataKnown = a.definition.HasValue,
                        mapFeature = a.definition?.m_MapFeature.ToString(), areaRequireNaturalResource = a.definition?.m_RequireNaturalResource,
                        workAmountFactor = a.definition.HasValue ? ExCalFinite(a.definition.Value.m_WorkAmountFactor) : null,
                        lotDataKnown = lotData.HasValue, lotMaxRadius = lotData.HasValue ? ExCalFinite(lotData.Value.m_MaxRadius) : null,
                        onWater = lotData?.m_OnWater, allowEditing = lotData?.m_AllowEditing, allowOverlap = lotData?.m_AllowOverlap };
                    cache.Add(area, a);
                }
                var actualOwner = ExCalComponent<Game.Common.Owner>(area);
                parent.linkedAreas.Add(a); // 保留原生遍历多次的引用次数，不能按唯一面积偷改分母。
                links.Add(new { area = ExCalReference(area), owner = ExCalReference(owner), relation,
                    areaOwnerMatchesLinkOwner = actualOwner.HasValue ? (bool?)(actualOwner.Value.m_Owner == owner) : null });
            }
        }

        private static float? ExCalConcentration(ExCalParent parent, ResourceData? resource, ExtractorParameterData? parameters, out float? squareMeters)
        {
            squareMeters = null;
            if (parent == null || !parent.areaLinksKnown || !resource.HasValue || !parameters.HasValue) return null;
            float size = 0f, weighted = 0f;
            foreach (var a in parent.linkedAreas)
            {
                if (!a.available) return null;
                // 已证组件不存在的非采掘区域与原生相同地跳过；存在Extractor但缺几何/定义不把未知补0。
                if (!a.extractor.HasValue) continue;
                if (!a.surfaceArea.HasValue || a.surfaceArea.Value < 0f || !a.definition.HasValue) return null;
                float c = 1f;
                if (resource.Value.m_RequireNaturalResource && a.definition.Value.m_RequireNaturalResource)
                {
                    if (!ExCalFinite(a.extractor.Value.m_MaxConcentration).HasValue) return null;
                    float denominator;
                    switch (a.definition.Value.m_MapFeature)
                    {
                        case Game.Areas.MapFeature.Oil: denominator = parameters.Value.m_FullOil; break;
                        case Game.Areas.MapFeature.Ore: denominator = parameters.Value.m_FullOre; break;
                        case Game.Areas.MapFeature.FertileLand: denominator = parameters.Value.m_FullFertility; break;
                        case Game.Areas.MapFeature.Fish: denominator = parameters.Value.m_FullFish; break;
                        default: denominator = 1f; break;
                    }
                    if (!ExCalFinite(denominator).HasValue || denominator <= 0f) return null;
                    c = Game.Simulation.ExtractorCompanySystem.GetEffectiveConcentration(parameters.Value, a.definition.Value.m_MapFeature, a.extractor.Value.m_MaxConcentration);
                }
                size += a.surfaceArea.Value; weighted += c * a.surfaceArea.Value;
            }
            if (!ExCalFinite(size).HasValue || !ExCalFinite(weighted).HasValue) return null;
            squareMeters = size; return ExCalFinite(Math.Min(1f, weighted / Math.Max(1f, size)));
        }
    }
}
