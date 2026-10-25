using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Buildings;
using Game.Citizens;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private const int HousingBuildingLimit = 200000;
        private const int HousingHouseholdLimit = 500000;
        private const int HousingMemberVisitLimit = 2000000;
        private const int HousingRenterVisitLimit = 2000000;
        private const int HousingPrefabGroupLimit = 4096;
        private const int HousingDistrictGroupLimit = 256;
        private const int HousingDensityGroupLimit = 128;
        private const int HousingWorkMillisecondsLimit = 2000;

        private sealed class HousingReadBudgetException : Exception
        {
            internal HousingReadBudgetException(string reason) : base(reason) { }
        }

        private sealed class HousingGroup
        {
            internal string key;
            internal object identity;
            internal long buildings, unitKnownBuildings, units, verifiedHouseholds, memberEntries;
            internal long nativeEligibleCitizens, scoredCitizens, fallbackCitizens, apartmentNotEvaluatedCitizens, scoreUnknownCitizens;
            internal long wellbeingSum, contributionAtOrBelowMinus2, roundedContributionAtOrBelowMinus2;
            internal readonly SortedDictionary<string, long> exclusions = new SortedDictionary<string, long>(StringComparer.Ordinal);
            internal readonly SortedDictionary<string, long> unknown = new SortedDictionary<string, long>(StringComparer.Ordinal);
            internal readonly SortedDictionary<string, long> memberEntryStatuses = new SortedDictionary<string, long>(StringComparer.Ordinal);
            internal readonly SortedDictionary<int, long> familySizes = new SortedDictionary<int, long>();
            internal readonly SortedDictionary<int, long> nativeRoundedWellbeingCounts = new SortedDictionary<int, long>();
            internal readonly long[] updateFrameCitizens = new long[16];

            internal object Snapshot(int factorBaseLevel, bool suppressed)
            {
                float? contribution = scoredCitizens == 0 ? (float?)null : suppressed ? 0f : wellbeingSum / (2f * scoredCitizens) - factorBaseLevel;
                return new {
                    key, identity, buildingCount = buildings,
                    residentialUnitKnownBuildingCount = unitKnownBuildings,
                    residentialUnitsKnownSum = unitKnownBuildings > 0 ? (long?)units : null,
                    residentialUnitCoverageComplete = unitKnownBuildings == buildings,
                    verifiedHouseholdRenterCount = verifiedHouseholds,
                    unoccupiedByVerifiedHouseholdsKnownCapacity = unitKnownBuildings == buildings ? (long?)(units - verifiedHouseholds) : null,
                    unoccupiedCapacityIsMarketVacancy = false,
                    memberBufferEntries = memberEntries, nativeEligibleCitizens, scoredCitizens, fallbackCitizens,
                    apartmentNotEvaluatedCitizens, scoreUnknownCitizens,
                    nativeRoundedWellbeingKnownSum = scoredCitizens > 0 ? (long?)wellbeingSum : null,
                    nativeHealthKnownSum = scoredCitizens > 0 ? (long?)0 : null,
                    instantaneousResidentialCitizenContribution = contribution,
                    contributionStatus = scoredCitizens == 0 ? "noScoredCitizens"
                        : scoreUnknownCitizens > 0 || apartmentNotEvaluatedCitizens > 0 || unknown.Count > 0
                            ? "knownScoredSubset" : "scoredVerifiedMemberEntries",
                    completeForActualCitizenPopulation = false,
                    contributionPopulationWeighting = "sum(native rounded wellbeing)/(2*scored citizens)-native Apartment base; locked factor => 0",
                    contributionAtOrBelowMinus2, roundedContributionAtOrBelowMinus2,
                    familyMemberBufferLengthHouseholdCounts = familySizes,
                    nativeRoundedWellbeingCounts, nativeEligibleCitizensByUpdateFrame = updateFrameCitizens,
                    memberEntryStatuses, exclusions, unknown
                };
            }
        }

        private sealed class HousingBuildingInfo
        {
            internal Entity building, prefab, district;
            internal bool hasPrefabRef, requiredPrefabComponents, unitsKnown;
            internal int width, depth, units, level;
            internal float multiplier;
            internal HousingGroup densityGroup, prefabGroup, districtGroup;
        }

        private static void HousingCount(SortedDictionary<string, long> counters, string reason, long amount = 1)
        {
            counters.TryGetValue(reason, out long count);
            counters[reason] = checked(count + amount);
        }

        private static void HousingReason(HousingGroup[] groups, string reason, bool unknown, long amount = 1)
        {
            foreach (var group in groups) HousingCount(unknown ? group.unknown : group.exclusions, reason, amount);
        }

        private static void HousingMemberStatus(HousingGroup[] groups, string reason, bool unknown)
        {
            foreach (var group in groups) HousingCount(group.memberEntryStatuses, reason);
            if (reason != "nativeEligibleCitizen") HousingReason(groups, reason, unknown);
        }

        private static HousingGroup HousingGetGroup(SortedDictionary<string, HousingGroup> groups, string key, object identity, int limit)
        {
            if (groups.TryGetValue(key, out var existing)) return existing;
            if (groups.Count >= limit) throw new HousingReadBudgetException("group limit " + limit);
            var created = new HousingGroup { key = key, identity = identity };
            groups.Add(key, created);
            return created;
        }

        private static void HousingCheckTime(Stopwatch watch)
        {
            if (watch.ElapsedMilliseconds > HousingWorkMillisecondsLimit)
                throw new HousingReadBudgetException("work time exceeds " + HousingWorkMillisecondsLimit + " ms");
        }

        private bool HousingUnavailable(Entity entity)
        {
            return entity == Entity.Null || !EntityManager.Exists(entity)
                || EntityManager.HasComponent<Game.Common.Deleted>(entity)
                || EntityManager.HasComponent<Game.Tools.Temp>(entity)
                || EntityManager.HasComponent<Unity.Entities.Prefab>(entity);
        }

        private static bool HousingGuid(string raw, out Guid guid)
        {
            guid = Guid.Empty;
            return raw != null && raw.Length <= 36
                && Regex.IsMatch(raw, "\\A(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                && Guid.TryParse(raw, out guid) && guid != Guid.Empty;
        }

        // 纯HTTP/身份入参校验在任何World/ECS访问之前完成，可独立核验只读边界。
        private static BridgeResponse ValidateHousingReadRequest(BridgeRequest request, out Guid expected)
        {
            expected = Guid.Empty;
            if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)) return BridgeResponse.Error(405, "housing snapshot only accepts GET");
            if (request.Query.Keys.Any(key => !string.Equals(key, "expectedLoadedSaveGuid", StringComparison.OrdinalIgnoreCase)))
                return BridgeResponse.Error(400, "only expectedLoadedSaveGuid is accepted; readonly budgets cannot be changed");
            if (!request.Query.TryGetValue("expectedLoadedSaveGuid", out string expectedRaw) || !HousingGuid(expectedRaw, out expected))
                return BridgeResponse.Error(400, "provide nonempty current expectedLoadedSaveGuid in 32-digit or standard GUID form");
            return null;
        }

        /// <summary>当前住宅→真实家庭回指→完整成员的单次有界聚合；没有全城Citizen查询、写入或逐人输出。</summary>
        private BridgeResponse GetHousingSnapshot(BridgeRequest request)
        {
            var validationError = ValidateHousingReadRequest(request, out Guid expected);
            if (validationError != null) return validationError;
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            var loader = World.GetExistingSystemManaged<Game.Serialization.LoadGameSystem>();
            string loadedRaw = loader?.context.instigatorGuid.ToString();
            if (!HousingGuid(loadedRaw, out Guid loaded)) return BridgeResponse.Error(409, "loaded save identity is not ready");
            if (loaded != expected) return BridgeResponse.Error(409, "loaded save identity differs; refresh before housing read");
            var happiness = World.GetExistingSystemManaged<CitizenHappinessSystem>();
            var simulation = World.GetExistingSystemManaged<SimulationSystem>();
            var prefabs = World.GetExistingSystemManaged<PrefabSystem>();
            var names = World.GetExistingSystemManaged<Game.UI.NameSystem>();
            if (happiness == null || simulation == null || prefabs == null) return BridgeResponse.Error(409, "native housing dependencies are not ready");
            uint frameStart = simulation.frameIndex;
            DateTime capturedAt = DateTime.UtcNow;
            var watch = Stopwatch.StartNew();
            var coverage = new SortedDictionary<string, long>(StringComparer.Ordinal);
            int? buildingQueryTotal = null, householdQueryTotal = null;
            long renterVisits = 0, memberVisits = 0;
            try
            {
                var lookups = m_System.GetHousingReadLookups();
                int parameterBase;
                bool suppressed;
                float3 nativeCityApartment;
                using (var parameterQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<HappinessFactorParameterData>()))
                {
                    if (parameterQuery.CalculateEntityCount() != 1) return BridgeResponse.Error(409, "Apartment parameter singleton is absent or ambiguous");
                    var parameters = EntityManager.GetBuffer<HappinessFactorParameterData>(parameterQuery.GetSingletonEntity(), true);
                    int index = (int)CitizenHappinessSystem.HappinessFactor.Apartment;
                    if (parameters.Length <= index) return BridgeResponse.Error(409, "Apartment parameter buffer is incomplete");
                    var parameter = parameters[index];
                    parameterBase = parameter.m_BaseLevel;
                    suppressed = parameter.m_LockedEntity != Entity.Null && lookups.locked.HasComponent(parameter.m_LockedEntity)
                        && lookups.locked.IsComponentEnabled(parameter.m_LockedEntity);
                    nativeCityApartment = happiness.GetHappinessFactor(CitizenHappinessSystem.HappinessFactor.Apartment, parameters, ref lookups.locked);
                    if (!math.all(math.isfinite(nativeCityApartment))) return BridgeResponse.Error(409, "native rolling Apartment factor is nonfinite");
                }
                HousingCheckTime(watch);
                var densityGroups = new SortedDictionary<string, HousingGroup>(StringComparer.Ordinal);
                var prefabGroups = new SortedDictionary<string, HousingGroup>(StringComparer.Ordinal);
                var districtGroups = new SortedDictionary<string, HousingGroup>(StringComparer.Ordinal);
                var total = new HousingGroup { key = "allCurrentResidentialBuildings", identity = null };
                var buildingCache = new Dictionary<Entity, HousingBuildingInfo>();
                var acceptedHouseholds = new HashSet<Entity>();
                var memberSet = new HashSet<Entity>();
                var prefabNames = new Dictionary<Entity, Tuple<string, string>>();
                var districtNames = new Dictionary<Entity, string>();
                float rawFallback = CitizenHappinessSystem.GetApartmentWellbeing(0.01f, 1);
                if (!math.isfinite(rawFallback)) return BridgeResponse.Error(409, "native fallback Apartment formula is not ready");
                int fallbackWellbeing = Mathf.RoundToInt(rawFallback);

                using (var buildingQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc {
                    All = new[] { ComponentType.ReadOnly<ResidentialProperty>(), ComponentType.ReadOnly<Game.Buildings.Building>() },
                    None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Unity.Entities.Prefab>() },
                    Options = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IgnoreComponentEnabledState
                }))
                using (var householdQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc {
                    All = new[] { ComponentType.ReadOnly<Household>() },
                    None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Unity.Entities.Prefab>() },
                    Options = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IgnoreComponentEnabledState
                }))
                {
                    buildingQueryTotal = buildingQuery.CalculateEntityCount();
                    householdQueryTotal = householdQuery.CalculateEntityCount();
                    if (buildingQueryTotal > HousingBuildingLimit) throw new HousingReadBudgetException("residential building query exceeds " + HousingBuildingLimit);
                    if (householdQueryTotal > HousingHouseholdLimit) throw new HousingReadBudgetException("household query exceeds " + HousingHouseholdLimit);
                    using (var buildings = buildingQuery.ToEntityArray(Allocator.Temp))
                    {
                        foreach (Entity building in buildings)
                        {
                            HousingCheckTime(watch);
                            var info = ReadHousingBuilding(building, ref lookups, prefabs, names, densityGroups, prefabGroups, districtGroups, prefabNames, districtNames);
                            buildingCache.Add(building, info);
                            var groups = new[] { total, info.densityGroup, info.prefabGroup, info.districtGroup };
                            foreach (var group in groups) {
                                group.buildings++;
                                if (info.unitsKnown) { group.unitKnownBuildings++; group.units += info.units; }
                            }
                            if (!info.unitsKnown) HousingReason(groups, "initializedResidentialUnitsMissingOrInvalid", true);
                            if (EntityManager.HasComponent<Game.Buildings.Abandoned>(building)) HousingCount(coverage, "abandonedResidentialBuildings");
                            if (EntityManager.HasComponent<Game.Buildings.Condemned>(building)) HousingCount(coverage, "condemnedResidentialBuildings");
                            if (EntityManager.HasComponent<Game.Common.Destroyed>(building)) HousingCount(coverage, "destroyedResidentialBuildings");
                            if (EntityManager.HasComponent<Unity.Entities.Disabled>(building)) HousingCount(coverage, "disabledResidentialBuildingsRetained");
                            if (!lookups.renters.HasBuffer(building)) { HousingReason(groups, "buildingRenterBufferMissing", true); continue; }
                            var renters = lookups.renters[building];
                            if (renters.Length > HousingRenterVisitLimit - renterVisits) throw new HousingReadBudgetException("renter entries exceed " + HousingRenterVisitLimit);
                            renterVisits += renters.Length;
                            foreach (var entry in renters)
                            {
                                HousingCheckTime(watch);
                                Entity household = entry.m_Renter;
                                if (HousingUnavailable(household)) { HousingReason(groups, "renterUnavailable", true); HousingCount(coverage, "renterUnavailable"); continue; }
                                if (!lookups.households.HasComponent(household)) {
                                    string reason = EntityManager.HasComponent<Game.Companies.CompanyData>(household) ? "companyRenter" : "otherNonHouseholdRenter";
                                    HousingReason(groups, reason, false); HousingCount(coverage, reason); continue;
                                }
                                if (!lookups.properties.HasComponent(household)) { HousingReason(groups, "householdPropertyRenterMissing", true); HousingCount(coverage, "householdPropertyRenterMissing"); continue; }
                                if (lookups.properties[household].m_Property != building) { HousingReason(groups, "householdPropertyBacklinkMismatch", true); HousingCount(coverage, "householdPropertyBacklinkMismatch"); continue; }
                                if (!acceptedHouseholds.Add(household)) { HousingReason(groups, "duplicateHouseholdRenterEntry", false); HousingCount(coverage, "duplicateHouseholdRenterEntry"); continue; }
                                if (acceptedHouseholds.Count > HousingHouseholdLimit) throw new HousingReadBudgetException("accepted households exceed " + HousingHouseholdLimit);
                                foreach (var group in groups) group.verifiedHouseholds++;
                                if (!lookups.householdCitizens.HasBuffer(household)) { HousingReason(groups, "householdCitizenBufferMissing", true); HousingCount(coverage, "acceptedHouseholdCitizenBufferMissing"); continue; }
                                var members = lookups.householdCitizens[household];
                                int originalLength = members.Length;
                                if (originalLength > HousingMemberVisitLimit - memberVisits) throw new HousingReadBudgetException("member entries exceed " + HousingMemberVisitLimit);
                                memberVisits += originalLength;
                                foreach (var group in groups) {
                                    group.memberEntries += originalLength;
                                    group.familySizes.TryGetValue(originalLength, out long previous);
                                    group.familySizes[originalLength] = previous + 1;
                                }
                                if (originalLength == 0) { HousingReason(groups, "emptyHouseholdCitizenBuffer", true); HousingCount(coverage, "acceptedEmptyHouseholdCitizenBuffer"); continue; }
                                memberSet.Clear();
                                bool normalCalculated = false, normalValid = false;
                                int normalWellbeing = 0;
                                string normalUnknownReason = null;
                                bool movedIn = (lookups.households[household].m_Flags & HouseholdFlags.MovedIn) != 0;
                                foreach (var memberEntry in members)
                                {
                                    HousingCheckTime(watch);
                                    Entity member = memberEntry.m_Citizen;
                                    if (!memberSet.Add(member)) { HousingMemberStatus(groups, "duplicateMemberBufferEntry", false); continue; }
                                    if (HousingUnavailable(member)) { HousingMemberStatus(groups, "memberUnavailable", true); continue; }
                                    if (EntityManager.HasComponent<Unity.Entities.Disabled>(member)) { HousingMemberStatus(groups, "nativeCitizenQueryDisabledExcluded", false); continue; }
                                    if (!lookups.citizens.HasComponent(member)) { HousingMemberStatus(groups, "citizenComponentMissing", true); continue; }
                                    if (!lookups.householdMembers.HasComponent(member) || lookups.householdMembers[member].m_Household != household) { HousingMemberStatus(groups, "memberHouseholdBacklinkMismatch", true); continue; }
                                    if (!EntityManager.HasComponent<UpdateFrame>(member)) { HousingMemberStatus(groups, "nativeCitizenQueryUpdateFrameMissing", true); continue; }
                                    uint updateFrame = EntityManager.GetSharedComponent<UpdateFrame>(member).m_Index;
                                    if (updateFrame >= 16) { HousingMemberStatus(groups, "nativeCitizenQueryUpdateFrameOutOfRange", true); continue; }
                                    if (lookups.healthProblems.HasComponent(member) && CitizenUtils.IsDead(lookups.healthProblems[member])) { HousingMemberStatus(groups, "nativeCitizenDeadExcluded", false); continue; }
                                    var citizen = lookups.citizens[member];
                                    bool tourist = (citizen.m_State & CitizenFlags.Tourist) != 0;
                                    if (!movedIn && !tourist) { HousingMemberStatus(groups, "nativeCitizenUnmovedNonTouristExcluded", false); continue; }
                                    HousingMemberStatus(groups, "nativeEligibleCitizen", false);
                                    foreach (var group in groups) { group.nativeEligibleCitizens++; group.updateFrameCitizens[updateFrame]++; }
                                    if (tourist) HousingCount(coverage, "eligibleTouristCitizensInResidentialScope");
                                    if (!info.hasPrefabRef) { foreach (var group in groups) group.apartmentNotEvaluatedCitizens++; continue; }
                                    bool fallback = (citizen.m_State & CitizenFlags.Homeless) != 0 || !info.requiredPrefabComponents;
                                    if (!fallback && !normalCalculated) {
                                        normalValid = TryHousingNormalWellbeing(info, originalLength, out normalWellbeing, out normalUnknownReason);
                                        normalCalculated = true;
                                    }
                                    if (!fallback && !normalValid) { foreach (var group in groups) group.scoreUnknownCitizens++; HousingReason(groups, normalUnknownReason, true); continue; }
                                    int wellbeing = fallback ? fallbackWellbeing : normalWellbeing;
                                    float contribution = suppressed ? 0f : wellbeing / 2f - parameterBase;
                                    foreach (var group in groups) {
                                        group.scoredCitizens++; group.wellbeingSum += wellbeing;
                                        if (fallback) group.fallbackCitizens++;
                                        if (contribution <= -2f) group.contributionAtOrBelowMinus2++;
                                        if (Mathf.RoundToInt(contribution) <= -2) group.roundedContributionAtOrBelowMinus2++;
                                        group.nativeRoundedWellbeingCounts.TryGetValue(wellbeing, out long count);
                                        group.nativeRoundedWellbeingCounts[wellbeing] = count + 1;
                                    }
                                }
                            }
                        }
                    }

                    // 只查询Household用于反向覆盖；不再访问成员内容，不创建全城Citizen query。
                    using (var households = householdQuery.ToEntityArray(Allocator.Temp))
                    {
                        foreach (Entity household in households)
                        {
                            HousingCheckTime(watch);
                            var h = lookups.households[household];
                            if ((h.m_Flags & HouseholdFlags.Tourist) != 0) HousingCount(coverage, "allHouseholdQueryTouristFlag");
                            if ((h.m_Flags & HouseholdFlags.Commuter) != 0) HousingCount(coverage, "allHouseholdQueryCommuterFlag");
                            if ((h.m_Flags & HouseholdFlags.MovedIn) == 0) HousingCount(coverage, "allHouseholdQueryNotMovedInFlag");
                            if (acceptedHouseholds.Contains(household)) { HousingCount(coverage, "householdsAcceptedViaResidentialRenter"); continue; }
                            string reason;
                            if (!lookups.properties.HasComponent(household)) reason = "allHouseholdsMissingPropertyRenter";
                            else {
                                Entity property = lookups.properties[household].m_Property;
                                if (property == Entity.Null) reason = "allHouseholdsNullHome";
                                else if (HousingUnavailable(property)) reason = "allHouseholdsUnavailableHome";
                                else if (!buildingCache.ContainsKey(property)) reason = "allHouseholdsOutsideResidentialBuildingScope";
                                else reason = "residentialHouseholdsMissingRenterReverseLink";
                            }
                            HousingCount(coverage, reason);
                            if (reason == "residentialHouseholdsMissingRenterReverseLink") {
                                var homeInfo = buildingCache[lookups.properties[household].m_Property];
                                HousingReason(new[] { total, homeInfo.densityGroup, homeInfo.prefabGroup, homeInfo.districtGroup }, "householdReverseRenterLinkMissing", true);
                            }
                            if (lookups.householdCitizens.HasBuffer(household)) {
                                int length = lookups.householdCitizens[household].Length;
                                HousingCount(coverage, reason + "MemberBufferEntries", length);
                            } else HousingCount(coverage, reason + "MemberBufferMissing");
                        }
                    }
                }
                HousingCheckTime(watch);
                long acceptedCoverage = HousingValue(coverage, "householdsAcceptedViaResidentialRenter");
                long householdPartitions = acceptedCoverage + HousingValue(coverage, "allHouseholdsMissingPropertyRenter") + HousingValue(coverage, "allHouseholdsNullHome")
                    + HousingValue(coverage, "allHouseholdsUnavailableHome") + HousingValue(coverage, "allHouseholdsOutsideResidentialBuildingScope") + HousingValue(coverage, "residentialHouseholdsMissingRenterReverseLink");
                bool groupsReconciled = HousingGroupsMatch(total, densityGroups.Values) && HousingGroupsMatch(total, prefabGroups.Values) && HousingGroupsMatch(total, districtGroups.Values);
                bool memberScorePartitionReconciled = total.nativeEligibleCitizens == total.scoredCitizens + total.scoreUnknownCitizens + total.apartmentNotEvaluatedCitizens;
                long renterPartitions = acceptedHouseholds.Count + HousingValue(coverage, "renterUnavailable") + HousingValue(coverage, "companyRenter")
                    + HousingValue(coverage, "otherNonHouseholdRenter") + HousingValue(coverage, "householdPropertyRenterMissing")
                    + HousingValue(coverage, "householdPropertyBacklinkMismatch") + HousingValue(coverage, "duplicateHouseholdRenterEntry");
                bool renterPartitionReconciled = renterPartitions == renterVisits;
                bool memberEntryPartitionReconciled = total.memberEntryStatuses.Values.Sum() == memberVisits;
                if (total.buildings != buildingQueryTotal || total.verifiedHouseholds != acceptedHouseholds.Count || acceptedCoverage != acceptedHouseholds.Count
                    || householdPartitions != householdQueryTotal || !groupsReconciled || !memberScorePartitionReconciled || !renterPartitionReconciled || !memberEntryPartitionReconciled)
                    return BridgeResponse.Error(500, "housing count/group reconciliation failed; no summary returned");
                string loadedAfter = loader.context.instigatorGuid.ToString();
                if (!HousingGuid(loadedAfter, out Guid after) || after != loaded || simulation.frameIndex != frameStart)
                    return BridgeResponse.Error(409, "save identity or simulation frame changed during housing read; no summary returned");
                bool residentLinksComplete = HousingValue(coverage, "residentialHouseholdsMissingRenterReverseLink") == 0
                    && HousingValue(coverage, "acceptedHouseholdCitizenBufferMissing") == 0 && HousingValue(coverage, "acceptedEmptyHouseholdCitizenBuffer") == 0
                    && !total.unknown.Keys.Any(key => key == "buildingRenterBufferMissing" || key == "householdPropertyRenterMissing" || key == "householdPropertyBacklinkMismatch"
                        || key == "renterUnavailable" || key == "memberUnavailable" || key == "citizenComponentMissing" || key == "memberHouseholdBacklinkMismatch"
                        || key == "nativeCitizenQueryUpdateFrameMissing" || key == "nativeCitizenQueryUpdateFrameOutOfRange");
                var densityResult = densityGroups.Values.Select(group => group.Snapshot(parameterBase, suppressed)).ToArray();
                var prefabResult = prefabGroups.Values.Select(group => group.Snapshot(parameterBase, suppressed)).ToArray();
                var districtResult = districtGroups.Values.Select(group => group.Snapshot(parameterBase, suppressed)).ToArray();
                HousingCheckTime(watch);
                var response = BridgeResponse.Json(new {
                    schemaVersion = 1, capturedAtUtc = capturedAt, loadedSaveGuid = loaded.ToString("N"), expectedLoadedSaveGuid = expected.ToString("N"),
                    bridgeVersion = Mod.Version, simulationFrameStart = frameStart, simulationFrameEnd = simulation.frameIndex,
                    stalenessWarning = LockStalenessWarning, elapsedMsBeforeSerialization = watch.ElapsedMilliseconds,
                    scope = "all current ResidentialProperty+Building, including disabled/abandoned/destroyed diagnostics; excludes deleted/temp/prefab objects; no whole-city Citizen query",
                    completeForBoundedResidentialBuildingScan = true, visitedResidentialRenterMemberLinksComplete = residentLinksComplete,
                    observedResidentialApartmentScoresComplete = residentLinksComplete && total.scoreUnknownCitizens == 0 && total.apartmentNotEvaluatedCitizens == 0,
                    completeForActualResidentialCitizenPopulation = false,
                    populationCoverageBoundary = "本次仅遍历住宅Renter可达的HouseholdCitizen缓冲；未查询全城Citizen反向成员，因此缓冲漏项的人口覆盖未验证",
                    completeForCityApartmentFactor = false,
                    budget = new { buildings = HousingBuildingLimit, households = HousingHouseholdLimit, renterEntries = HousingRenterVisitLimit,
                        memberEntries = HousingMemberVisitLimit, prefabGroups = HousingPrefabGroupLimit, densityGroups = HousingDensityGroupLimit,
                        districtGroups = HousingDistrictGroupLimit, workMilliseconds = HousingWorkMillisecondsLimit },
                    coverage = new { buildingQueryTotal, scannedBuildings = total.buildings, allHouseholdsQueryTotal = householdQueryTotal,
                        acceptedHouseholds = acceptedHouseholds.Count, renterEntriesVisited = renterVisits, memberEntriesVisited = memberVisits,
                        householdPartitionReconciled = true, threeMarginalGroupSetsReconciled = groupsReconciled, memberScorePartitionReconciled,
                        renterPartitionReconciled, memberEntryPartitionReconciled, reasons = coverage },
                    nativeApartmentParameter = new { baseLevel = parameterBase, suppressedByLocked = suppressed },
                    nativeCityApartmentRollingReference = new { contribution = nativeCityApartment.x, healthContribution = nativeCityApartment.y,
                        wellbeingContribution = nativeCityApartment.z, roundedContribution = Mathf.RoundToInt(nativeCityApartment.x),
                        source = "existing native CitizenHappinessSystem.GetHappinessFactor", shards = 16, nominalFullCycleSimulationFrames = 256 },
                    total = total.Snapshot(parameterBase, suppressed), densityGroups = densityResult, prefabGroups = prefabResult, districtGroups = districtResult,
                    note = "瞬时住宅人口加权与全城16分片滚动不同。单位U按楼一次，成员H保原缓冲Length；公司租户不作家庭，空置率不替代U。未知及非住宅/无家家庭未补0；不含逐家庭/市民身份，不承诺全城Apartment覆盖。"
                });
                HousingCheckTime(watch);
                return response;
            }
            catch (HousingReadBudgetException exceeded)
            {
                return BridgeResponse.Json(new { error = "housing readonly budget exceeded; no partial groups returned", reason = exceeded.Message,
                    complete = false, loadedSaveGuid = loaded.ToString("N"), buildingQueryTotal, householdQueryTotal,
                    renterEntriesVisited = renterVisits, memberEntriesVisited = memberVisits, elapsedMs = watch.ElapsedMilliseconds }, 413);
            }
        }

        private static long HousingValue(SortedDictionary<string, long> counters, string key) => counters.TryGetValue(key, out long value) ? value : 0;

        // 同户正常成员共用q和W，按合格人口分别计权；请求内计算一次，不持久缓存。
        private static bool TryHousingNormalWellbeing(HousingBuildingInfo info, int originalLength, out int wellbeing, out string reason)
        {
            wellbeing = 0;
            reason = null;
            if (info.units <= 0 || originalLength <= 0 || originalLength > int.MaxValue / info.units || !math.isfinite(info.multiplier)
                || info.multiplier <= 0f || info.width <= 0 || info.depth <= 0 || info.level < 1 || info.level > 5) {
                reason = "invalidInitializedApartmentInput"; return false;
            }
            float q = info.multiplier * info.width * info.depth / (originalLength * info.units);
            if (!math.isfinite(q) || q <= 0f) { reason = "invalidSizePerMember"; return false; }
            float raw = CitizenHappinessSystem.GetApartmentWellbeing(q, info.level);
            if (!math.isfinite(raw)) { reason = "nativeApartmentResultNonfinite"; return false; }
            wellbeing = Mathf.RoundToInt(raw);
            return true;
        }

        private static bool HousingGroupsMatch(HousingGroup total, IEnumerable<HousingGroup> values)
        {
            var groups = values.ToArray();
            return groups.Sum(g => g.buildings) == total.buildings && groups.Sum(g => g.unitKnownBuildings) == total.unitKnownBuildings
                && groups.Sum(g => g.units) == total.units && groups.Sum(g => g.verifiedHouseholds) == total.verifiedHouseholds
                && groups.Sum(g => g.memberEntries) == total.memberEntries && groups.Sum(g => g.nativeEligibleCitizens) == total.nativeEligibleCitizens
                && groups.Sum(g => g.scoredCitizens) == total.scoredCitizens && groups.Sum(g => g.wellbeingSum) == total.wellbeingSum
                && groups.Sum(g => g.fallbackCitizens) == total.fallbackCitizens && groups.Sum(g => g.scoreUnknownCitizens) == total.scoreUnknownCitizens
                && groups.Sum(g => g.apartmentNotEvaluatedCitizens) == total.apartmentNotEvaluatedCitizens
                && groups.Sum(g => g.contributionAtOrBelowMinus2) == total.contributionAtOrBelowMinus2
                && groups.Sum(g => g.roundedContributionAtOrBelowMinus2) == total.roundedContributionAtOrBelowMinus2
                && Enumerable.Range(0, 16).All(index => groups.Sum(g => g.updateFrameCitizens[index]) == total.updateFrameCitizens[index])
                && HousingDictionaryGroupsMatch(total.exclusions, groups.Select(g => g.exclusions))
                && HousingDictionaryGroupsMatch(total.unknown, groups.Select(g => g.unknown))
                && HousingDictionaryGroupsMatch(total.memberEntryStatuses, groups.Select(g => g.memberEntryStatuses))
                && HousingDictionaryGroupsMatch(total.familySizes, groups.Select(g => g.familySizes))
                && HousingDictionaryGroupsMatch(total.nativeRoundedWellbeingCounts, groups.Select(g => g.nativeRoundedWellbeingCounts));
        }

        private static bool HousingDictionaryGroupsMatch<TKey>(SortedDictionary<TKey, long> total, IEnumerable<SortedDictionary<TKey, long>> groups)
        {
            var summed = new Dictionary<TKey, long>();
            foreach (var group in groups) foreach (var item in group) {
                summed.TryGetValue(item.Key, out long value);
                summed[item.Key] = value + item.Value;
            }
            return summed.Count == total.Count && total.All(item => summed.TryGetValue(item.Key, out long value) && value == item.Value);
        }

        private HousingBuildingInfo ReadHousingBuilding(Entity building, ref BridgeSystem.HousingReadLookups l, PrefabSystem prefabSystem,
            Game.UI.NameSystem names, SortedDictionary<string, HousingGroup> densities, SortedDictionary<string, HousingGroup> prefabs,
            SortedDictionary<string, HousingGroup> districts, Dictionary<Entity, Tuple<string, string>> prefabNames, Dictionary<Entity, string> districtNames)
        {
            var info = new HousingBuildingInfo { building = building, hasPrefabRef = l.prefabs.HasComponent(building) };
            if (info.hasPrefabRef) info.prefab = l.prefabs[building].m_Prefab;
            bool hasProperties = l.buildingProperties.HasComponent(info.prefab);
            bool hasLot = l.buildingData.HasComponent(info.prefab);
            bool hasSpawn = l.spawnableBuildings.HasComponent(info.prefab);
            info.requiredPrefabComponents = hasProperties && hasLot && hasSpawn;
            if (hasProperties) {
                var property = l.buildingProperties[info.prefab];
                info.units = property.m_ResidentialProperties; info.multiplier = property.m_SpaceMultiplier; info.unitsKnown = info.units >= 0;
            }
            if (hasLot) { var lot = l.buildingData[info.prefab].m_LotSize; info.width = lot.x; info.depth = lot.y; }
            string density = "unknown";
            string densityStatus = "nativeZoneDataMissing";
            Entity zone = Entity.Null;
            if (hasSpawn) {
                var spawn = l.spawnableBuildings[info.prefab]; info.level = spawn.m_Level; zone = spawn.m_ZonePrefab;
                if (l.zoneData.HasComponent(zone) && l.zoneProperties.HasComponent(zone)) {
                    var zd = l.zoneData[zone];
                    if (zd.m_AreaType == Game.Zones.AreaType.Residential) { density = PropertyUtils.GetZoneDensity(zd, l.zoneProperties[zone]).ToString(); densityStatus = "nativeResidentialZone"; }
                    else densityStatus = "nativeNonResidentialZoneWithResidentialProperty";
                }
            }
            info.densityGroup = HousingGetGroup(densities, densityStatus + ":" + density, new { density, status = densityStatus }, HousingDensityGroupLimit);
            if (!prefabNames.TryGetValue(info.prefab, out var prefabName)) {
                if (info.prefab != Entity.Null && EntityManager.Exists(info.prefab) && prefabSystem.TryGetPrefab<PrefabBase>(info.prefab, out var value) && value != null)
                    prefabName = Tuple.Create(value.GetPrefabID().ToString(), value.name);
                else prefabName = Tuple.Create<string, string>(null, null);
                prefabNames.Add(info.prefab, prefabName);
            }
            string prefabKey = prefabName.Item1 ?? "unknownPrefab:" + info.prefab.Index + ":" + info.prefab.Version;
            info.prefabGroup = HousingGetGroup(prefabs, prefabKey, new { prefabId = prefabName.Item1, prefab = prefabName.Item2,
                nativeLevel = hasSpawn ? (int?)info.level : null,
                lotCells = hasLot ? new { width = info.width, depth = info.depth } : null,
                initializedResidentialUnits = hasProperties ? (int?)info.units : null,
                initializedSpaceMultiplier = hasProperties && math.isfinite(info.multiplier) ? (float?)info.multiplier : null,
                requiredNativePrefabComponentsPresent = info.requiredPrefabComponents }, HousingPrefabGroupLimit);
            bool hasDistrict = l.districts.HasComponent(building);
            if (hasDistrict) info.district = l.districts[building].m_District;
            string status = !hasDistrict ? "currentDistrictMissing" : info.district == Entity.Null ? "outsideNativeDistrict"
                : HousingUnavailable(info.district) ? "staleNativeDistrict"
                : !EntityManager.HasComponent<Game.Areas.District>(info.district) ? "nativeDistrictMarkerMissing" : "nativeDistrict";
            string districtName = null;
            if (status == "nativeDistrict") {
                if (!districtNames.TryGetValue(info.district, out districtName)) { districtName = names?.GetRenderedLabelName(info.district); districtNames.Add(info.district, districtName); }
            }
            string districtKey = status + ":" + info.district.Index + ":" + info.district.Version;
            info.districtGroup = HousingGetGroup(districts, districtKey, new { status, name = districtName,
                entity = status == "nativeDistrict" ? new { index = info.district.Index, version = info.district.Version } : null }, HousingDistrictGroupLimit);
            return info;
        }
    }
}
