using System;
using System.Collections.Generic;
using System.IO;
using Game.Common;
using Game.Economy;
using Game.Objects;
using Game.Prefabs;
using Game.Settings;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private static string FrameLimitPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low", "Colossal Order", "Cities Skylines II", "Mods", "CS2MCP", "frame-limit.txt");
        private bool m_FrameLimitRestored;
        private bool m_RuntimeSettingsLoadComplete;

        public void ResetRuntimeSettingsAfterLoad()
        {
            m_FrameLimitRestored = false;
            m_RuntimeSettingsLoadComplete = true;
        }

        /// <summary>只恢复本桥接接口明确保存的基础帧率上限，不改变画质或显卡驱动设置。</summary>
        public void RestoreFrameLimit()
        {
            // 图形设置无需等待解锁重放或模拟推进；暂停加载也必须恢复限帧。
            // GameManager 在完成加载时会先重置 targetFrameRate，之后才应用本地限帧。
            var manager = Game.SceneFlow.GameManager.instance;
            if (m_FrameLimitRestored || !m_RuntimeSettingsLoadComplete || SharedSettings.instance?.graphics == null
                || manager == null || manager.isGameLoading || manager.gameMode != Game.GameMode.Game) return;
            m_FrameLimitRestored = true;
            try {
                if (File.Exists(FrameLimitPath) && int.TryParse(File.ReadAllText(FrameLimitPath), out int limit) && limit >= 30 && limit <= 240)
                {
                    SharedSettings.instance.graphics.vSync = false;
                    SharedSettings.instance.graphics.ApplyAndSave();
                    Application.targetFrameRate = limit;
                }
            } catch (IOException e) { Mod.Log.Warn($"限帧配置读取失败，保留当前设置：{e.Message}"); }
            catch (UnauthorizedAccessException e) { Mod.Log.Warn($"限帧配置无法读取，保留当前设置：{e.Message}"); }
        }

        private BridgeResponse GetGraphicsSettings()
        {
            return BridgeResponse.Json(new {
                vSync = SharedSettings.instance?.graphics.vSync,
                appliedVSyncCount = QualitySettings.vSyncCount,
                baseFrameRateLimit = Application.targetFrameRate,
                performancePreference = SharedSettings.instance?.general.performancePreference.ToString(),
                automaticSaveEnabled = SharedSettings.instance?.general.autoSave,
                frameLimitRestoredForCurrentLoad = m_FrameLimitRestored,
                driverDisplayFps = (float?)null,
                note = "base rendering limit only; driver frame-generation multiplier and post-generation FPS are not exposed by the game"
            });
        }

        private BridgeResponse SetGraphicsSettings(BridgeRequest request)
        {
            if (SharedSettings.instance?.graphics == null) return BridgeResponse.Error(409, "native settings are not ready");
            bool? requestedVSync = null;
            if (request.Query.ContainsKey("vSync")) {
                if (!request.TryGetBool("vSync", out bool parsedVSync))
                    return BridgeResponse.Error(400, "vSync must be true or false");
                requestedVSync = parsedVSync;
            }
            int? requestedLimit = null;
            if (request.Query.TryGetValue("maxFps", out string raw)) {
                if (!int.TryParse(raw, out int limit) || limit < 30 || limit > 240)
                    return BridgeResponse.Error(400, "maxFps must be an integer from 30 to 240");
                requestedLimit = limit;
            }
            if (requestedLimit.HasValue && (requestedVSync ?? SharedSettings.instance.graphics.vSync))
                return BridgeResponse.Error(400, "maxFps requires vSync=false; Unity ignores the rendering limit while vertical sync is active");
            // 所有参数先校验，再持久化及应用；非法布尔值不能留下半套设置。
            if (requestedLimit.HasValue) {
                Directory.CreateDirectory(Path.GetDirectoryName(FrameLimitPath));
                File.WriteAllText(FrameLimitPath, requestedLimit.Value.ToString());
            }
            if (requestedVSync.HasValue) {
                SharedSettings.instance.graphics.vSync = requestedVSync.Value;
                SharedSettings.instance.graphics.ApplyAndSave();
            }
            if (requestedLimit.HasValue) Application.targetFrameRate = requestedLimit.Value;
            return GetGraphicsSettings();
        }

        private T CombinedFacilityData<T>(Entity building, Entity prefab) where T : unmanaged, IComponentData, ICombineData<T>
        {
            T data = EntityManager.GetComponentData<T>(prefab);
            if (EntityManager.HasBuffer<Game.Buildings.InstalledUpgrade>(building))
                foreach (var installed in EntityManager.GetBuffer<Game.Buildings.InstalledUpgrade>(building, true)) {
                    Entity upgrade = installed.m_Upgrade;
                    if (!EntityManager.Exists(upgrade) || !EntityManager.HasComponent<PrefabRef>(upgrade)) continue;
                    Entity upgradePrefab = EntityManager.GetComponentData<PrefabRef>(upgrade).m_Prefab;
                    if (EntityManager.HasComponent<T>(upgradePrefab)) data.Combine(EntityManager.GetComponentData<T>(upgradePrefab));
                }
            return data;
        }

        /// <summary>读取学校、医疗、垃圾设施真实使用量及建筑等级，供经营决策使用。</summary>
        private BridgeResponse GetFacilityStatus()
        {
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            var facilities = new List<object>();
            var levels = new Dictionary<string, Dictionary<int, int>>();
            using (var query = EntityManager.CreateEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.Objects.Transform>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() }
            }))
            using (var entities = query.ToEntityArray(Allocator.Temp)) {
                foreach (Entity entity in entities) {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    // 母建筑已合并 InstalledUpgrade 的容量，扩建子对象不能再次计入全城汇总。
                    if (EntityManager.HasComponent<ServiceUpgradeData>(prefab)) continue;
                    if (EntityManager.HasComponent<SpawnableBuildingData>(prefab)) {
                        var spawn = EntityManager.GetComponentData<SpawnableBuildingData>(prefab);
                        string zone = NativeName(spawn.m_ZonePrefab) ?? "Unknown";
                        if (!levels.TryGetValue(zone, out var counts)) levels[zone] = counts = new Dictionary<int, int>();
                        counts.TryGetValue(spawn.m_Level, out int count);
                        counts[spawn.m_Level] = count + 1;
                    }
                    var row = new Dictionary<string, object>();
                    if (EntityManager.HasComponent<SchoolData>(prefab)) {
                        var data = CombinedFacilityData<SchoolData>(entity, prefab);
                        row["type"] = "school"; row["educationLevel"] = data.m_EducationLevel;
                        row["studentCapacity"] = data.m_StudentCapacity;
                        row["students"] = EntityManager.HasBuffer<Game.Buildings.Student>(entity) ? EntityManager.GetBuffer<Game.Buildings.Student>(entity, true).Length : 0;
                    } else if (EntityManager.HasComponent<HospitalData>(prefab)) {
                        var data = CombinedFacilityData<HospitalData>(entity, prefab);
                        row["type"] = "hospital"; row["patientCapacity"] = data.m_PatientCapacity;
                        row["patients"] = EntityManager.HasBuffer<Game.Buildings.Patient>(entity) ? EntityManager.GetBuffer<Game.Buildings.Patient>(entity, true).Length : 0;
                        row["ambulanceCapacity"] = data.m_AmbulanceCapacity;
                    } else if (EntityManager.HasComponent<GarbageFacilityData>(prefab)) {
                        var data = CombinedFacilityData<GarbageFacilityData>(entity, prefab);
                        row["type"] = "garbage"; row["buildingStorageCapacity"] = data.m_GarbageCapacity;
                        row["processingCapacity"] = data.m_ProcessingSpeed; row["vehicleCapacity"] = data.m_VehicleCapacity;
                        int stored = 0;
                        if (EntityManager.HasBuffer<Game.Economy.Resources>(entity))
                            foreach (var resource in EntityManager.GetBuffer<Game.Economy.Resources>(entity, true))
                                if (resource.m_Resource == Resource.Garbage) stored += resource.m_Amount;
                        row["storedGarbage"] = stored;
                        if (EntityManager.HasComponent<Game.Buildings.GarbageFacility>(entity))
                            row["processingRate"] = EntityManager.GetComponentData<Game.Buildings.GarbageFacility>(entity).m_ProcessingRate;
                    } else if (EntityManager.HasComponent<DeathcareFacilityData>(prefab)) {
                        var data = CombinedFacilityData<DeathcareFacilityData>(entity, prefab);
                        row["type"] = "deathcare"; row["longTermStorage"] = data.m_LongTermStorage;
                        row["storageCapacity"] = data.m_StorageCapacity; row["processingCapacity"] = data.m_ProcessingRate;
                        row["hearseCapacity"] = data.m_HearseCapacity;
                        if (EntityManager.HasComponent<Game.Buildings.DeathcareFacility>(entity))
                            row["storedBodies"] = EntityManager.GetComponentData<Game.Buildings.DeathcareFacility>(entity).m_LongTermStoredCount;
                    } else if (EntityManager.HasComponent<PoliceStationData>(prefab)) {
                        var data = CombinedFacilityData<PoliceStationData>(entity, prefab);
                        row["type"] = "police"; row["jailCapacity"] = data.m_JailCapacity;
                        row["patrolCarCapacity"] = data.m_PatrolCarCapacity;
                        row["occupants"] = EntityManager.HasBuffer<Game.Buildings.Occupant>(entity) ? EntityManager.GetBuffer<Game.Buildings.Occupant>(entity, true).Length : 0;
                    } else if (EntityManager.HasComponent<PrisonData>(prefab)) {
                        var data = CombinedFacilityData<PrisonData>(entity, prefab);
                        row["type"] = "prison"; row["prisonCapacity"] = data.m_PrisonerCapacity;
                        row["occupants"] = EntityManager.HasBuffer<Game.Buildings.Occupant>(entity) ? EntityManager.GetBuffer<Game.Buildings.Occupant>(entity, true).Length : 0;
                    } else if (EntityManager.HasComponent<FireStationData>(prefab)) {
                        var data = CombinedFacilityData<FireStationData>(entity, prefab);
                        row["type"] = "fire"; row["fireEngineCapacity"] = data.m_FireEngineCapacity;
                        row["fireHelicopterCapacity"] = data.m_FireHelicopterCapacity;
                        row["disasterResponseCapacity"] = data.m_DisasterResponseCapacity;
                    } else if (EntityManager.HasComponent<PostFacilityData>(prefab)) {
                        var data = CombinedFacilityData<PostFacilityData>(entity, prefab);
                        row["type"] = "post"; row["postVanCapacity"] = data.m_PostVanCapacity;
                        row["postTruckCapacity"] = data.m_PostTruckCapacity;
                        row["mailCapacity"] = data.m_MailCapacity; row["sortingCapacity"] = data.m_SortingRate;
                    } else if (EntityManager.HasComponent<TelecomFacilityData>(prefab)) {
                        var data = CombinedFacilityData<TelecomFacilityData>(entity, prefab);
                        row["type"] = "telecom"; row["rangeMeters"] = data.m_Range;
                        row["networkCapacity"] = data.m_NetworkCapacity;
                    } else continue;
                    var p = EntityManager.GetComponentData<Game.Objects.Transform>(entity).m_Position;
                    row["entity"] = new { index = entity.Index, version = entity.Version };
                    row["prefab"] = NativeName(prefab); row["position"] = new { x = p.x, z = p.z };
                    float efficiency = 1f;
                    if (EntityManager.HasBuffer<Game.Buildings.Efficiency>(entity))
                        foreach (var factor in EntityManager.GetBuffer<Game.Buildings.Efficiency>(entity, true)) efficiency *= Math.Max(0f, factor.m_Efficiency);
                    row["efficiency"] = efficiency > 0 ? Math.Max(0.01f, (float)Math.Round(100f * efficiency) / 100f) : 0f;
                    row["ownedVehicles"] = EntityManager.HasBuffer<Game.Vehicles.OwnedVehicle>(entity) ? EntityManager.GetBuffer<Game.Vehicles.OwnedVehicle>(entity, true).Length : 0;
                    row["dispatchRequests"] = EntityManager.HasBuffer<Game.Simulation.ServiceDispatch>(entity) ? EntityManager.GetBuffer<Game.Simulation.ServiceDispatch>(entity, true).Length : 0;
                    facilities.Add(row);
                }
            }
            return BridgeResponse.Json(new { facilities, buildingLevelsByZone = levels,
                note = "capacities include installed building upgrades; landfill storage-area capacity is separate from buildingStorageCapacity; efficiency is reported separately" });
        }
    }
}
