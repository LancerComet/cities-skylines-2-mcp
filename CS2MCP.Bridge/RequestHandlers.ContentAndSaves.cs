using System.Collections.Generic;
using Colossal.PSI.Common;
using Game.Prefabs;
using Game.Settings;
using Game.UI.Menu;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        // 内容资格使用游戏自己的检查；磁盘存在资产不等于拥有对应数据包。
        private bool IsContentAvailable(Entity prefabEntity)
        {
            if (!EntityManager.HasComponent<ContentPrerequisiteData>(prefabEntity)) return true;
            var prerequisite = EntityManager.GetComponentData<ContentPrerequisiteData>(prefabEntity).m_ContentPrerequisite;
            return prerequisite == Entity.Null || World.GetOrCreateSystemManaged<PrefabSystem>().GetPrefab<ContentPrefab>(prerequisite).IsAvailable();
        }

        private BridgeResponse GetSaveSettings()
        {
            if (SharedSettings.instance == null) return BridgeResponse.Error(409, "native settings are not ready");
            var settings = SharedSettings.instance.general;
            return BridgeResponse.Json(new {
                enabled = settings.autoSave, intervalSeconds = (int)settings.autoSaveInterval,
                retentionCount = (int)settings.autoSaveCount, retentionScope = "all local native automatic saves",
                manualSaveAutomaticallyOverwritten = false,
                note = "native AutoSaveSystem uses real elapsed seconds and separate autoSave metadata; its retention is global, not per map"
            });
        }

        private BridgeResponse SetSaveSettings(BridgeRequest request)
        {
            if (SharedSettings.instance == null) return BridgeResponse.Error(409, "native settings are not ready");
            var settings = SharedSettings.instance.general;
            if (request.Query.ContainsKey("intervalSeconds") && (!request.TryGetInt("intervalSeconds", out int seconds) || !System.Enum.IsDefined(typeof(GeneralSettings.AutoSaveInterval), seconds)))
                return BridgeResponse.Error(400, "intervalSeconds must be a native menu option: 60, 120, 300, 600, 1800 or 3600");
            if (request.Query.ContainsKey("retentionCount") && (!request.TryGetInt("retentionCount", out int count) || (count != 1 && count != 2 && count != 3 && count != 10)))
                return BridgeResponse.Error(400, "retentionCount must be 1, 2, 3 or 10");
            if (request.Query.ContainsKey("enabled") && !request.TryGetBool("enabled", out _)) return BridgeResponse.Error(400, "enabled must be boolean");
            if (request.TryGetInt("intervalSeconds", out int interval)) settings.autoSaveInterval = (GeneralSettings.AutoSaveInterval)interval;
            if (request.TryGetInt("retentionCount", out int retention)) settings.autoSaveCount = (GeneralSettings.AutoSaveCount)retention;
            if (request.TryGetBool("enabled", out bool enabled)) settings.autoSave = enabled;
            settings.ApplyAndSave();
            return GetSaveSettings();
        }

        /// <summary>只读原生资格、内容授权与当前实际引用，不修改成就或DLC状态。</summary>
        private BridgeResponse GetContentStatus()
        {
            var prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var contents = new List<object>();
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ContentData>()))
            using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities) {
                    var prefab = prefabSystem.GetPrefab<ContentPrefab>(entity);
                    var data = EntityManager.GetComponentData<ContentData>(entity);
                    contents.Add(new { name = prefab.name, available = prefab.IsAvailable(), dlcId = data.m_DlcID, flags = data.m_Flags.ToString() });
                }
            var unavailable = new Dictionary<string, int>();
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrefabRef>()))
            using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities) {
                    var prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    if (!EntityManager.Exists(prefab) || IsContentAvailable(prefab)) continue;
                    string name = NativeName(prefab);
                    unavailable.TryGetValue(name, out int count);
                    unavailable[name] = count + 1;
                }
            var menu = World.GetExistingSystemManaged<MenuUISystem>();
            return BridgeResponse.Json(new {
                achievementsEnabled = PlatformManager.instance?.achievementsEnabled,
                normalGameplayOptions = Game.SceneFlow.GameManager.instance.gameMode == Game.GameMode.Game ? menu?.GetSaveInfo(false).options : null,
                contents, unavailableReferences = unavailable,
                note = "eligibility is not proof of an individual Steam achievement being awarded"
            });
        }
    }
}
