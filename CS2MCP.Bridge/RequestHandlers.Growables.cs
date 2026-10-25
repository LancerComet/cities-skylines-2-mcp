using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Colossal.IO.AssetDatabase;
using Game.Common;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private sealed class GrowableContentInfo { public bool? available; public object source; }

        /// <summary>单次复制当前注册成长Prefab；不扫描现场建筑、不重评估内容缓存，不跨请求持有Native对象。</summary>
        private BridgeResponse GetGrowableCatalog()
        {
            if (!TryGetCity(out _,out BridgeResponse error)) return error;
            string guid = World.GetExistingSystemManaged<Game.Serialization.LoadGameSystem>()?.context.instigatorGuid.ToString();
            if (string.IsNullOrEmpty(guid)) return BridgeResponse.Error(409,"no loaded save identity for growable catalog");
            var prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            var rows = new List<Dictionary<string,object>>();
            var issues = new List<object>();
            var exclusions = new SortedDictionary<string,int>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var sources = new Dictionary<Entity,GrowableContentInfo>();
            var contents = new Dictionary<Entity,Tuple<bool?,object>>();
            var mods = new Dictionary<string,bool?>(StringComparer.Ordinal);
            int queryCount, activeRows = 0;
            using (var query = EntityManager.CreateEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<PrefabData>(),ComponentType.ReadOnly<BuildingData>(),ComponentType.ReadOnly<SpawnableBuildingData>() },
                Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IgnoreComponentEnabledState
            }))
            using (var entities = query.ToEntityArray(Allocator.Temp))
            {
                queryCount = entities.Length;
                foreach (var entity in entities)
                {
                    string excluded = GrowableExclusion(entity);
                    if (excluded != null) { exclusions.TryGetValue(excluded,out int n); exclusions[excluded] = n + 1; continue; }
                    var prefab = prefabs.GetPrefab<PrefabBase>(entity);
                    if (prefab == null) { exclusions.TryGetValue("unresolvedPrefab",out int n); exclusions["unresolvedPrefab"] = n + 1; issues.Add(new { entity = Ref(entity),reason = "managed prefab missing" }); continue; }
                    string id = prefab.GetPrefabID().ToString();
                    if (!keys.Add(id)) issues.Add(new { prefabId = id,reason = "duplicate stable id" });
                    var lot = EntityManager.GetComponentData<BuildingData>(entity);
                    var spawn = EntityManager.GetComponentData<SpawnableBuildingData>(entity);
                    bool enabled = EntityManager.IsComponentEnabled<PrefabData>(entity) && !EntityManager.HasComponent<Disabled>(entity);
                    bool groupPresent = EntityManager.HasComponent<BuildingSpawnGroupData>(entity);
                    if (enabled && groupPresent) activeRows++;
                    Entity zoneEntity = spawn.m_ZonePrefab;
                    var zonePrefab = zoneEntity != Entity.Null && EntityManager.Exists(zoneEntity) ? prefabs.GetPrefab<PrefabBase>(zoneEntity) : null;
                    bool hasZone = zonePrefab != null && EntityManager.HasComponent<ZoneData>(zoneEntity);
                    if (!hasZone || lot.m_LotSize.x <= 0 || lot.m_LotSize.y <= 0 || spawn.m_Level < 1)
                        issues.Add(new { prefabId = id,reason = "invalid zone/lot/level" });
                    object zone = null;
                    if (hasZone)
                    {
                        var value = EntityManager.GetComponentData<ZoneData>(zoneEntity);
                        object zoneProperties = null,density = null;
                        if (EntityManager.HasComponent<ZonePropertiesData>(zoneEntity))
                        {
                            var property = EntityManager.GetComponentData<ZonePropertiesData>(zoneEntity);
                            zoneProperties = new { scaleResidentials = property.m_ScaleResidentials,residentialProperties = property.m_ResidentialProperties,spaceMultiplier = property.m_SpaceMultiplier,ignoreLandValue = property.m_IgnoreLandValue };
                            if (value.m_AreaType != Game.Zones.AreaType.None) density = Game.Buildings.PropertyUtils.GetZoneDensity(value,property).ToString();
                        }
                        int? groupZone = groupPresent ? EntityManager.GetSharedComponent<BuildingSpawnGroupData>(entity).m_ZoneType.m_Index : (int?)null;
                        if (groupZone.HasValue && groupZone.Value != value.m_ZoneType.m_Index) issues.Add(new { prefabId = id,reason = "spawn group/zone differ" });
                        zone = new { prefabId = zonePrefab.GetPrefabID().ToString(),prefab = zonePrefab.name,nativeZoneType = value.m_ZoneType.m_Index,spawnGroupZoneType = groupZone,
                            areaType = value.m_AreaType.ToString(),office = value.IsOffice(),flags = value.m_ZoneFlags.ToString(),density,nativeLocked = GrowableNativeLocked(zoneEntity),properties = zoneProperties,theme = GrowableTheme(zonePrefab) };
                    }
                    object properties = null,workplace = null;
                    if (EntityManager.HasComponent<BuildingPropertyData>(entity))
                    {
                        var property = EntityManager.GetComponentData<BuildingPropertyData>(entity);
                        properties = new { residentialProperties = property.m_ResidentialProperties,spaceMultiplier = property.m_SpaceMultiplier,
                            allowedSold = property.m_AllowedSold.ToString(),allowedInput = property.m_AllowedInput.ToString(),allowedManufactured = property.m_AllowedManufactured.ToString(),allowedStored = property.m_AllowedStored.ToString() };
                    }
                    else issues.Add(new { prefabId = id,reason = "initialized building properties missing" });
                    if (EntityManager.HasComponent<WorkplaceData>(entity))
                    {
                        var worker = EntityManager.GetComponentData<WorkplaceData>(entity);
                        workplace = new { maxWorkers = worker.m_MaxWorkers,complexity = worker.m_Complexity,minimumWorkersLimit = worker.m_MinimumWorkersLimit,eveningShiftProbability = worker.m_EveningShiftProbability,nightShiftProbability = worker.m_NightShiftProbability,workConditions = worker.m_WorkConditions };
                    }
                    var content = GrowableContent(entity,prefab,sources,contents,mods,issues);
                    var zoneContent = zonePrefab == null ? null : GrowableContent(zoneEntity,zonePrefab,sources,contents,mods,issues);
                    bool? available = content.available == false || zoneContent?.available == false ? false : content.available == true && zoneContent?.available == true ? true : (bool?)null;
                    string label = null;
                    Game.SceneFlow.GameManager.instance?.localizationManager?.activeDictionary?.TryGetValue($"Assets.NAME[{prefab.name}]",out label);
                    rows.Add(new Dictionary<string,object> {
                        ["prefabId"] = id,["prefab"] = prefab.name,["localizedName"] = label,["type"] = prefab.GetType().Name,["entity"] = Ref(entity),
                        ["enabled"] = enabled,["spawnGroupPresent"] = groupPresent,["level"] = spawn.m_Level,
                        ["lotCells"] = new { width = lot.m_LotSize.x,depth = lot.m_LotSize.y },["lotMeters"] = new { width = lot.m_LotSize.x * 8,depth = lot.m_LotSize.y * 8 },
                        ["heightMeters"] = EntityManager.HasComponent<ObjectGeometryData>(entity) ? (object)EntityManager.GetComponentData<ObjectGeometryData>(entity).m_Size.y : null,
                        ["buildingFlags"] = lot.m_Flags.ToString(),["zone"] = zone,["theme"] = GrowableTheme(prefab),["hasBuildingPropertyOverride"] = prefab.Has<BuildingProperties>(),
                        ["properties"] = properties,["workplace"] = workplace,["nativeLocked"] = GrowableNativeLocked(entity),["declaredContentAvailable"] = available,["content"] = content,["zoneContent"] = zoneContent,
                        ["level1StructuralCandidate"] = enabled && groupPresent && hasZone && spawn.m_Level == 1 && available == true && !GrowableNativeLocked(entity) && !GrowableNativeLocked(zoneEntity)
                    });
                }
            }
            int nativeActiveCount,nativeActiveExcluded = 0;
            using (var query = EntityManager.CreateEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<PrefabData>(),ComponentType.ReadOnly<BuildingData>(),ComponentType.ReadOnly<SpawnableBuildingData>(),ComponentType.ReadOnly<BuildingSpawnGroupData>() },Options = EntityQueryOptions.IncludePrefab
            }))
            using (var entities = query.ToEntityArray(Allocator.Temp)) { nativeActiveCount = entities.Length; foreach (var entity in entities) if (GrowableExclusion(entity) != null) nativeActiveExcluded++; }
            bool activeReconciled = nativeActiveCount == activeRows + nativeActiveExcluded;
            if (!activeReconciled) issues.Add(new { reason = "independent native spawn query differs",nativeActiveCount,activeRows,nativeActiveExcluded });
            rows.Sort((a,b) => StringComparer.Ordinal.Compare((string)a["prefabId"],(string)b["prefabId"]));
            string keyHash;
            using (var sha = SHA256.Create()) keyHash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n",keys.OrderBy(key => key,StringComparer.Ordinal))))).Replace("-","").ToLowerInvariant();
            bool reconciled = queryCount == rows.Count + exclusions.Values.Sum() && keys.Count == rows.Count;
            return BridgeResponse.Json(new { schemaVersion = 1,capturedAtUtc = DateTime.UtcNow,loadedSaveGuid = guid,stalenessWarning = LockStalenessWarning,
                scope = "本次World完整注册普通成长Prefab，不代表未安装/未注册已购内容；手放标志性、升级和产业占位单列排除；只读单次快照无跨请求缓存",
                queryCount,returnedCount = rows.Count,exclusionCounts = exclusions,reconciled,nativeActiveCount,activeRows,nativeActiveExcluded,nativeActiveReconciled = activeReconciled,
                prefabKeySha256 = keyHash,completeForRegisteredScope = reconciled && activeReconciled && issues.Count == 0,issues,rows,
                note = "原生8米格和初始化后Prefab属性，不是当前住户/岗位；1级结构候选不保证任何坐标有效格/需求/高度和成长。来源缺可靠包名保持未知。" });
        }

        private string GrowableExclusion(Entity entity) => EntityManager.HasComponent<Deleted>(entity) ? "deleted" : EntityManager.HasComponent<Game.Tools.Temp>(entity) ? "temporary"
            : EntityManager.HasComponent<SignatureBuildingData>(entity) ? "manualSignature" : EntityManager.HasComponent<ServiceUpgradeData>(entity) ? "serviceUpgrade" : EntityManager.HasComponent<PlaceholderBuildingData>(entity) ? "industryPlaceholder" : null;
        private bool GrowableNativeLocked(Entity entity) => entity != Entity.Null && EntityManager.HasComponent<Locked>(entity) && EntityManager.IsComponentEnabled<Locked>(entity);
        private static object GrowableTheme(PrefabBase prefab) => prefab != null && prefab.TryGet<ThemeObject>(out var theme) && theme.m_Theme != null
            ? new { prefabId = theme.m_Theme.GetPrefabID().ToString(),name = theme.m_Theme.name } : null;

        /// <summary>本请求共享资格/来源白名单；不用PrefabSystem.IsAvailable写缓存或触发内容事件。</summary>
        private GrowableContentInfo GrowableContent(Entity entity,PrefabBase prefab,Dictionary<Entity,GrowableContentInfo> sources,
            Dictionary<Entity,Tuple<bool?,object>> contents,Dictionary<string,bool?> mods,List<object> issues)
        {
            if (sources.TryGetValue(entity,out var cached)) return cached;
            bool? available = true; object declared = null;
            try
            {
                if (EntityManager.HasComponent<ContentPrerequisiteData>(entity))
                {
                    Entity prerequisite = EntityManager.GetComponentData<ContentPrerequisiteData>(entity).m_ContentPrerequisite;
                    if (prerequisite != Entity.Null)
                    {
                        if (!contents.TryGetValue(prerequisite,out var declaration))
                        {
                            var content = EntityManager.Exists(prerequisite) ? World.GetOrCreateSystemManaged<PrefabSystem>().GetPrefab<ContentPrefab>(prerequisite) : null;
                            if (content == null) { declaration = Tuple.Create<bool?,object>(null,null); issues.Add(new { prefabId = prefab.GetPrefabID().ToString(),reason = "declared content unresolved" }); }
                            else
                            {
                                bool contentAvailable = content.IsAvailable(); object data = null;
                                if (EntityManager.HasComponent<ContentData>(prerequisite)) { var value = EntityManager.GetComponentData<ContentData>(prerequisite); data = new { dlcId = value.m_DlcID,flags = value.m_Flags.ToString() }; }
                                declaration = Tuple.Create<bool?,object>(contentAvailable,new { name = content.name,available = contentAvailable,data });
                            }
                            contents.Add(prerequisite,declaration);
                        }
                        available = declaration.Item1; declared = declaration.Item2;
                    }
                }
                string modId = prefab.asset?.GetMeta().platformID; bool? modAvailable = true;
                if (!string.IsNullOrEmpty(modId))
                {
                    if (!mods.TryGetValue(modId,out modAvailable)) { var database = AssetDatabase<ParadoxMods>.instance?.dataSource as ParadoxModsDataSource; modAvailable = database == null ? (bool?)null : database.ContainsActiveMod(modId); mods.Add(modId,modAvailable); }
                    available = available == false || modAvailable == false ? false : available == true && modAvailable == true ? true : (bool?)null;
                }
                var result = new GrowableContentInfo { available = available,source = new { declared,builtin = prefab.isBuiltin,subscribedMod = prefab.isSubscribedMod,packaged = prefab.isPackaged,
                    assetGuid = prefab.asset?.id.guid.ToString(),platformId = modId,modAvailable,packageName = (string)null,sourceConfidence = "仅声明内容、资产GUID及公开模组ID，不按命名猜包" } };
                sources.Add(entity,result); return result;
            }
            catch (Exception exception)
            {
                issues.Add(new { prefabId = prefab.GetPrefabID().ToString(),reason = "content/source unavailable",errorType = exception.GetType().Name });
                var result = new GrowableContentInfo { available = null,source = new { declared,packageName = (string)null } }; sources.Add(entity,result); return result;
            }
        }
    }
}
