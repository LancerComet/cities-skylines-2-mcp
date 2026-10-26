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
        private double m_LastResourceTime;
        private int m_LastResourceFrame;
        private readonly BackgroundRenderingPolicy m_BackgroundRendering = new BackgroundRenderingPolicy();
        private readonly BackgroundSceneRenderingPolicy m_BackgroundSceneRendering = new BackgroundSceneRenderingPolicy();
        private readonly BackgroundFrameOutputPolicy m_BackgroundFrameOutput = new BackgroundFrameOutputPolicy();
        // 实载零回调已反证仅绑定目标不足以恢复隐藏渲染；改为独立显式诊断能力。
        private bool m_OffscreenSceneOutput = false;
        private readonly BackgroundSceneFrameLoop m_BackgroundSceneFrames = new BackgroundSceneFrameLoop();
        private bool m_RenderBackgroundScene;
        private readonly BackgroundCommandSubmission m_BackgroundCommandSubmission = new BackgroundCommandSubmission();
        private bool m_SubmitBackgroundCommands;
        // 尚无实际回调/内存收益证明，不默认改变原生场景绘制；独立显式能力保留。
        private bool m_SuspendSceneRendering = false;
        private bool m_RenderingWasReady;

        private bool BackgroundMode => Environment.GetEnvironmentVariable(LaunchFramePolicy.EnvironmentName) == "background";
        private bool SceneRenderingRequested => BackgroundMode && m_SuspendSceneRendering;
        private bool FrameOutputRequested => BackgroundMode && (m_OffscreenSceneOutput || m_RenderBackgroundScene) && !m_SuspendSceneRendering;
        private bool SceneFramesRequested => BackgroundMode && m_RenderBackgroundScene && !m_SuspendSceneRendering;
        private bool CommandSubmissionRequested => BackgroundMode && m_SubmitBackgroundCommands;

        private bool CommandSubmissionReady()
        {
            var manager = Game.SceneFlow.GameManager.instance;
            return World.IsCreated && m_RuntimeSettingsLoadComplete && m_FrameLimitRestored
                && manager != null && !manager.isGameLoading && manager.gameMode == Game.GameMode.Game;
        }

        private void StopCommandSubmission()
        {
            m_BackgroundCommandSubmission.SetRequested(false);
            BackgroundCommandSubmissionRunner.Release(m_BackgroundCommandSubmission);
        }

        private bool SceneFrameReady() => CommandSubmissionReady()
            && ReadClockGate(out _, out _, out bool barrier) && !barrier;

        private void StopSceneFrames()
        {
            m_BackgroundSceneFrames.SetRequested(false);
            BackgroundSceneFrameRunner.Release(m_BackgroundSceneFrames);
        }

        private void RestoreSceneFrameOutput()
        {
            if (!m_BackgroundFrameOutput.Restore()) throw new InvalidOperationException("native_scene_frame_output_restore_failed");
        }

        private void ReconcileSceneFrames()
        {
            if (!SceneFramesRequested) { StopSceneFrames(); return; }
            if (m_BackgroundSceneFrames.Requested) return;
            m_BackgroundSceneFrames.SetRequested(true);
            try { BackgroundSceneFrameRunner.Bind(m_BackgroundSceneFrames, SceneFrameReady,
                m_BackgroundFrameOutput.RenderFrameIfNeeded, RestoreSceneFrameOutput); }
            catch (Exception e) { m_BackgroundSceneFrames.FailAndRelease(e, RestoreSceneFrameOutput); }
        }

        private void ReconcileCommandSubmission()
        {
            if (!CommandSubmissionRequested) { StopCommandSubmission(); return; }
            if (m_BackgroundCommandSubmission.Requested) return;
            m_BackgroundCommandSubmission.SetRequested(true);
            try { BackgroundCommandSubmissionRunner.Bind(m_BackgroundCommandSubmission, CommandSubmissionReady); }
            catch (Exception e) { m_BackgroundCommandSubmission.SetupFailed(e); }
        }

        /// <summary>只在离开已就绪场景时释放一次；失败保留所有权，不在普通帧重试。</summary>
        public void ReconcileRenderingLifecycle()
        {
            var manager = Game.SceneFlow.GameManager.instance;
            bool ready = m_RuntimeSettingsLoadComplete && manager != null
                && !manager.isGameLoading && manager.gameMode == Game.GameMode.Game;
            if (ready) { m_RenderingWasReady = true; return; }
            if (!m_RenderingWasReady) return;
            m_RenderingWasReady = false;
            m_RuntimeSettingsLoadComplete = false;
            m_FrameLimitRestored = false;
            NativeCaptureOperation.Shared.CancelAndClearResult("scene_no_longer_ready");
            ReleaseSceneRendering();
        }

        public void ReleaseSceneRendering()
        {
            StopSceneFrames();
            StopCommandSubmission();
            if (!m_BackgroundSceneRendering.Restore())
                Mod.Log.Warn("后台自有场景渲染委托释放失败；保留所有权和错误，不自动重试");
            if (!m_BackgroundFrameOutput.Restore())
                Mod.Log.Warn("后台自有输出恢复失败；保留所有权和错误，不自动重试");
        }

        private int? LaunchFrameLimit()
        {
            return LaunchFramePolicy.Resolve(Environment.GetEnvironmentVariable(LaunchFramePolicy.EnvironmentName));
        }

        /// <summary>桥接创建即应用显式启动限帧，避免后台加载及等待接口期间继承前台80帧。</summary>
        public void ApplyLaunchFrameLimit()
        {
            // 原生模式切换及加载会重置限帧；这里只改运行时两个参数，不重做整套画质/分辨率应用。
            int? requested = LaunchFrameLimit();
            if (!requested.HasValue) return;
            int limit = requested.Value;
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            if (Application.targetFrameRate != limit) Application.targetFrameRate = limit;
            if (SharedSettings.instance?.graphics != null) SharedSettings.instance.graphics.vSync = false;
        }

        /// <summary>初始模式切换/加载期间维持本次启动限帧；就绪后允许显式运行时切换。</summary>
        public void ReconcileLaunchFrameLimit()
        {
            var manager = Game.SceneFlow.GameManager.instance;
            if (!m_FrameLimitRestored || manager != null && (manager.isGameLoading || manager.gameMode != Game.GameMode.Game))
                ApplyLaunchFrameLimit();
        }

        public void ResetRuntimeSettingsAfterLoad()
        {
            m_FrameLimitRestored = false;
            m_RuntimeSettingsLoadComplete = true;
            // 原生加载回调可能先于isGameLoading清零，不能把新加载误判成离开旧场景。
            m_RenderingWasReady = false;
            m_LastResourceTime = 0;
            m_LastResourceFrame = 0;
            StopSceneFrames();
            m_RenderBackgroundScene = false;
            StopCommandSubmission();
            m_SubmitBackgroundCommands = false;
            m_BackgroundRendering.ResetAfterLoad();
            m_BackgroundSceneRendering.ResetAfterLoad();
            if (!m_BackgroundFrameOutput.Restore())
                Mod.Log.Warn("新场景自有输出恢复失败；保留旧对象所有权，不向新相机重复分配");
            NativeGameCameraCapture.ResetAfterLoad();
            NativeCaptureOperation.Shared.CancelAndClearResult("scene_loaded_or_replaced");
        }

        /// <summary>按请求只读原生资源计数；不启用Profiler、不收集堆、不强制GC或卸载资源。</summary>
        private BridgeResponse GetResourceDiagnostics()
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            double now = Time.realtimeSinceStartupAsDouble;
            int frame = Time.frameCount;
            double elapsed = now - m_LastResourceTime;
            double? renderFrameRate = m_LastResourceTime > 0 && elapsed > 0 && frame >= m_LastResourceFrame
                ? (double?)(frame - m_LastResourceFrame) / elapsed : null;
            m_LastResourceTime = now;
            m_LastResourceFrame = frame;
            int temporary, deleted;
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Tools.Temp>()))
                temporary = query.CalculateEntityCount();
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Deleted>()))
                deleted = query.CalculateEntityCount();
            return BridgeResponse.Json(new {
                capturedAtUtc = DateTime.UtcNow.ToString("o"), processId = process.Id,
                memory = RuntimeMemory.Read(),
                unityAllocatedBytes = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong(),
                unityReservedBytes = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong(),
                unityUnusedReservedBytes = UnityEngine.Profiling.Profiler.GetTotalUnusedReservedMemoryLong(),
                graphicsDriverAllocatedBytes = UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(),
                monoHeapBytes = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong(),
                monoUsedBytes = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong(),
                temporaryEntities = temporary, deletedEntities = deleted, renderFrame = frame,
                renderFrameRateSincePreviousRead = renderFrameRate, sampleSeconds = renderFrameRate.HasValue ? (double?)elapsed : null,
                applicationFocused = Application.isFocused, applicationRunInBackground = Application.runInBackground,
                uiReadiness = ReadNativeUiLoadReadiness().state,
                graphics = new { targetFrameRate = Application.targetFrameRate, vSyncCount = QualitySettings.vSyncCount },
                backgroundFrameOutput = m_BackgroundFrameOutput.Read(FrameOutputRequested),
                backgroundSceneFrames = m_BackgroundSceneFrames.Read(),
                backgroundCommandSubmission = m_BackgroundCommandSubmission.Read(),
                graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
                note = "只读主线程计数；Unity/驱动统计不等于进程提交量，0可能表示平台未提供；帧率是两次读取间基础Unity帧数，不包含驱动插帧；不是根因或泄漏修复证明"
            });
        }

        /// <summary>加载完成后重新应用本次启动预设；持久快照不决定下次普通启动。</summary>
        public void RestoreFrameLimit()
        {
            // 图形设置无需等待解锁重放或模拟推进；暂停加载也必须恢复限帧。
            // GameManager 在完成加载时会先重置 targetFrameRate，之后才应用本地限帧。
            var manager = Game.SceneFlow.GameManager.instance;
            if (m_FrameLimitRestored || !m_RuntimeSettingsLoadComplete || SharedSettings.instance?.graphics == null
                || manager == null || manager.isGameLoading || manager.gameMode != Game.GameMode.Game
                || !ReadNativeUiLoadReadiness().ready) return;
            ApplyLaunchFrameLimit();
            if (LaunchFrameLimit() == 30) m_BackgroundRendering.Apply(SharedSettings.instance.graphics);
            else m_BackgroundRendering.Restore(SharedSettings.instance.graphics);
            if (FrameOutputRequested) m_BackgroundFrameOutput.Apply(() => NativeBackgroundFrameOutput.Resolve(World));
            if (SceneRenderingRequested) m_BackgroundSceneRendering.Apply(() => NativeBackgroundSceneRenderTarget.Resolve(World));
            else m_BackgroundSceneRendering.Restore();
            m_FrameLimitRestored = true;
            ReconcileSceneFrames();
            ReconcileCommandSubmission();
            // 明确模式优先；普通启动保留本地快照。IO失败不产生每帧重试。
            try {
                int? explicitLimit = LaunchFrameLimit();
                if (explicitLimit.HasValue) {
                    ApplyLaunchFrameLimit();
                    Directory.CreateDirectory(Path.GetDirectoryName(FrameLimitPath));
                    File.WriteAllText(FrameLimitPath, explicitLimit.Value.ToString());
                } else if (File.Exists(FrameLimitPath) && int.TryParse(File.ReadAllText(FrameLimitPath), out int saved) && saved >= 10 && saved <= 240) {
                    SharedSettings.instance.graphics.vSync = false;
                    QualitySettings.vSyncCount = 0;
                    Application.targetFrameRate = saved;
                }
            }
            catch (IOException e) { Mod.Log.Warn($"启动限帧已应用，但本地快照保存失败：{e.Message}"); }
            catch (UnauthorizedAccessException e) { Mod.Log.Warn($"启动限帧已应用，但本地快照无法写入：{e.Message}"); }
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
                uiReadiness = ReadNativeUiLoadReadiness().state,
                launchMode = LaunchFrameLimit().HasValue ? Environment.GetEnvironmentVariable(LaunchFramePolicy.EnvironmentName) : null,
                launchFrameLimit = LaunchFrameLimit(),
                driverDisplayFps = (float?)null,
                backgroundRendering = m_BackgroundRendering.Read(SharedSettings.instance?.graphics,
                    Environment.GetEnvironmentVariable(LaunchFramePolicy.EnvironmentName) == "background"),
                backgroundSceneRendering = m_BackgroundSceneRendering.Read(SceneRenderingRequested),
                backgroundFrameOutput = m_BackgroundFrameOutput.Read(FrameOutputRequested),
                backgroundSceneFrames = m_BackgroundSceneFrames.Read(),
                backgroundCommandSubmission = m_BackgroundCommandSubmission.Read(),
                nativeGameCapture = NativeGameCameraCapture.Read(),
                nativeGameCaptureOperation = NativeCaptureOperation.Shared.ReadLatest(),
                note = "基础限帧与后台原生渲染分别回读；驱动插帧后帧率及实际功耗未由游戏接口测得"
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
                // 接口允许范围独立于30/90启动预设；仍禁止0及无限帧率值。
                if (!int.TryParse(raw, out int limit) || limit < 10 || limit > 240)
                    return BridgeResponse.Error(400, "maxFps must be an integer from 10 to 240");
                requestedLimit = limit;
            }
            string requestedMode = null;
            if (request.Query.TryGetValue("launchMode", out string mode)) {
                if (mode != "background" && mode != "foreground")
                    return BridgeResponse.Error(400, "launchMode must be background or foreground");
                int modeLimit = LaunchFramePolicy.Resolve(mode).Value;
                if (requestedLimit.HasValue && requestedLimit.Value != modeLimit)
                    return BridgeResponse.Error(400, "maxFps conflicts with the explicit launchMode preset");
                requestedMode = mode;
                requestedLimit = modeLimit;
            }
            if (requestedLimit.HasValue && (requestedVSync ?? SharedSettings.instance.graphics.vSync))
                return BridgeResponse.Error(400, "maxFps requires vSync=false; Unity ignores the rendering limit while vertical sync is active");
            bool? requestedSceneSuspension = null;
            if (request.Query.ContainsKey("suspendSceneRendering")) {
                if (!request.TryGetBool("suspendSceneRendering", out bool suspend))
                    return BridgeResponse.Error(400, "suspendSceneRendering must be true or false");
                if (suspend && !(requestedMode == "background" || requestedMode == null && BackgroundMode))
                    return BridgeResponse.Error(400, "suspendSceneRendering=true requires background mode");
                requestedSceneSuspension = suspend;
            }
            bool? requestedFrameOutput = null;
            if (request.Query.ContainsKey("offscreenSceneOutput")) {
                if (!request.TryGetBool("offscreenSceneOutput", out bool output))
                    return BridgeResponse.Error(400, "offscreenSceneOutput must be true or false");
                if (output && !(requestedMode == "background" || requestedMode == null && BackgroundMode))
                    return BridgeResponse.Error(400, "offscreenSceneOutput=true requires background mode");
                requestedFrameOutput = output;
            }
            bool? requestedCommandSubmission = null;
            bool? requestedSceneFrames = null;
            if (request.Query.ContainsKey("renderBackgroundScene")) {
                if (!request.TryGetBool("renderBackgroundScene", out bool render))
                    return BridgeResponse.Error(400, "renderBackgroundScene must be true or false");
                if (render && !(requestedMode == "background" || requestedMode == null && BackgroundMode))
                    return BridgeResponse.Error(400, "renderBackgroundScene=true requires background mode");
                if (render && requestedFrameOutput == false)
                    return BridgeResponse.Error(400, "renderBackgroundScene=true conflicts with explicit output restoration");
                requestedSceneFrames = render;
            }
            // 恢复原输出也必须停止借用该输出的循环，即使被动输出从未启用。
            if (requestedFrameOutput == false) requestedSceneFrames = false;
            if (request.Query.ContainsKey("submitBackgroundCommands")) {
                if (!request.TryGetBool("submitBackgroundCommands", out bool submit))
                    return BridgeResponse.Error(400, "submitBackgroundCommands must be true or false");
                if (submit && !(requestedMode == "background" || requestedMode == null && BackgroundMode))
                    return BridgeResponse.Error(400, "submitBackgroundCommands=true requires background mode");
                requestedCommandSubmission = submit;
            }
            bool modeChanged = requestedMode != null && requestedMode != Environment.GetEnvironmentVariable(LaunchFramePolicy.EnvironmentName);
            bool sceneChanged = requestedSceneSuspension.HasValue && requestedSceneSuspension.Value != m_SuspendSceneRendering;
            bool outputChanged = requestedFrameOutput.HasValue && requestedFrameOutput.Value != m_OffscreenSceneOutput;
            bool framesChanged = requestedSceneFrames.HasValue && requestedSceneFrames.Value != m_RenderBackgroundScene;
            bool nativeSettingsReset = requestedVSync.HasValue && SharedSettings.instance.graphics.vSync != requestedVSync.Value;
            if (modeChanged || sceneChanged || outputChanged || framesChanged || nativeSettingsReset) StopSceneFrames();
            // 同模式限帧不反复恢复/禁用全部效果，也不重新注册场景委托。
            if ((modeChanged || sceneChanged) && !m_BackgroundSceneRendering.Restore())
                return BridgeResponse.Error(409, "native scene rendering restore failed; ownership retained; no mode change applied");
            if ((modeChanged || outputChanged || framesChanged || sceneChanged || nativeSettingsReset) && !m_BackgroundFrameOutput.Restore())
                return BridgeResponse.Error(409, "native frame output restore failed; ownership retained; no mode change applied");
            if (modeChanged || requestedCommandSubmission.HasValue && requestedCommandSubmission.Value != m_SubmitBackgroundCommands)
                StopCommandSubmission();
            // 所有参数先校验，再持久化及应用；非法布尔值不能留下半套设置。
            if (requestedLimit.HasValue) {
                Directory.CreateDirectory(Path.GetDirectoryName(FrameLimitPath));
                File.WriteAllText(FrameLimitPath, requestedLimit.Value.ToString());
            }
            if (requestedVSync.HasValue) {
                // FPS/模式切换常重复传false；相同值不再触发分辨率、整套画质和SettingsApplied回调。
                if (SharedSettings.instance.graphics.vSync != requestedVSync.Value) {
                    m_BackgroundRendering.Restore(SharedSettings.instance.graphics);
                    SharedSettings.instance.graphics.vSync = requestedVSync.Value;
                    SharedSettings.instance.graphics.ApplyAndSave();
                }
                QualitySettings.vSyncCount = requestedVSync.Value ? 1 : 0;
            }
            if (requestedLimit.HasValue) Application.targetFrameRate = requestedLimit.Value;
            if (requestedMode != null)
                Environment.SetEnvironmentVariable(LaunchFramePolicy.EnvironmentName, requestedMode, EnvironmentVariableTarget.Process);
            if (requestedSceneSuspension.HasValue) m_SuspendSceneRendering = requestedSceneSuspension.Value;
            if (requestedFrameOutput.HasValue) m_OffscreenSceneOutput = requestedFrameOutput.Value;
            if (requestedSceneFrames.HasValue) m_RenderBackgroundScene = requestedSceneFrames.Value;
            if (requestedCommandSubmission.HasValue) m_SubmitBackgroundCommands = requestedCommandSubmission.Value;
            var manager = Game.SceneFlow.GameManager.instance;
            bool renderingReady = m_RuntimeSettingsLoadComplete && manager != null
                && !manager.isGameLoading && manager.gameMode == Game.GameMode.Game
                && ReadNativeUiLoadReadiness().ready;
            if (renderingReady && modeChanged) {
                if (requestedMode == "foreground") m_BackgroundRendering.Restore(SharedSettings.instance.graphics);
                else {
                    m_BackgroundRendering.Restore(SharedSettings.instance.graphics);
                    m_BackgroundRendering.Apply(SharedSettings.instance.graphics);
                }
            } else if (renderingReady && Environment.GetEnvironmentVariable(LaunchFramePolicy.EnvironmentName) == "background")
                m_BackgroundRendering.Apply(SharedSettings.instance.graphics);
            if (renderingReady && FrameOutputRequested)
                m_BackgroundFrameOutput.Apply(() => NativeBackgroundFrameOutput.Resolve(World));
            if (renderingReady && SceneRenderingRequested)
                m_BackgroundSceneRendering.Apply(() => NativeBackgroundSceneRenderTarget.Resolve(World));
            if (renderingReady) { ReconcileSceneFrames(); ReconcileCommandSubmission(); }
            return GetGraphicsSettings();
        }

        private T CombinedFacilityData<T>(Entity building, Entity prefab) where T : unmanaged, IComponentData, ICombineData<T>
        {
            T data = EntityManager.GetComponentData<T>(prefab);
            // 同原生设施AI，按InstalledUpgrade的Inactive选项合并；不用理论挂载总数冒充生效容量。
            if (EntityManager.HasBuffer<Game.Buildings.InstalledUpgrade>(building))
                UpgradeUtils.CombineStats(EntityManager, ref data, EntityManager.GetBuffer<Game.Buildings.InstalledUpgrade>(building, true));
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
