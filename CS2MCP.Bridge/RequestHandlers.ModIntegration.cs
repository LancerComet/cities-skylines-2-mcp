using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        // 可选模组只通过已加载程序集和已有系统读取，不加载DLL、不创建其系统或更改设置。
        private object ReadAnarchyState()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Anarchy");
            if (assembly == null) return new { loaded = false, inspected = true, enabled = false, allowableErrors = new string[0] };
            try
            {
                var type = assembly.GetType("Anarchy.Systems.Common.AnarchyUISystem", true);
                var system = World.GetExistingSystemManaged(type);
                if (system == null) return new { loaded = true, inspected = false, error = "existing Anarchy UI system not found" };
                var enabled = (bool)type.GetProperty("AnarchyEnabled").GetValue(system);
                var errors = (IEnumerable)type.GetMethod("GetAllowableErrorTypes").Invoke(system, null);
                var values = new List<string>();
                foreach (var value in errors) values.Add(value.ToString());
                var settings = ExistingAnarchySettings(assembly);
                bool autoBulldoze = (bool)settings.GetType().GetProperty("AnarchicBulldozer").GetValue(settings);
                var helperSettings = new Dictionary<string, object>();
                foreach (string key in AnarchyHelperSettings.Concat(new[] { "AllowPlacingMultipleUniqueBuildings", "MinimumClearanceBelowElevatedNetworks", "PreventOverrideInEditor" }))
                    helperSettings[key] = OptionalModApi.Read(settings, key);
                var network = World.GetExistingSystemManaged(assembly.GetType("Anarchy.Systems.NetworkAnarchy.NetworkAnarchyUISystem", true));
                if (network == null) throw new InvalidOperationException("Anarchy network system unavailable");
                var networkState = new Dictionary<string, object>();
                foreach (string key in new[] { "LeftUpgrade", "RightUpgrade", "NetworkComposition", "ReplaceLeftUpgrade", "ReplaceRightUpgrade", "ReplaceComposition" })
                    networkState[key] = OptionalModApi.JsonValue(OptionalModApi.Read(network, key));
                var rawComposition = OptionalModApi.Read(OptionalModApi.Read(network, "m_Composition"), "Value");
                networkState["rawComposition"] = OptionalModApi.JsonValue(rawComposition);
                bool expandedRange = (Convert.ToInt32(rawComposition) & 256) != 0;
                var errorChecks = OptionalModApi.Read(OptionalModApi.Read(system, "m_ErrorChecksBinding"), "Value");
                var nativeRanges = ReadAnarchyRanges(assembly);
                return new { loaded = true, inspected = true, enabled, allowableErrors = values,
                    collisionBypassActive = enabled || values.Count > 0, automaticAnarchyOnBulldoze = autoBulldoze,
                    uniqueBuildingBypass = (bool)helperSettings["AllowPlacingMultipleUniqueBuildings"], expandedElevationRangeActive = expandedRange,
                    helperSettings, networkState, nativeRanges, errorChecks = OptionalModApi.JsonValue(errorChecks),
                    normalHelpers = new { elevationStepSlider = settings.GetType().GetProperty("ElevationStepSlider").GetValue(settings),
                        networkUpgrades = settings.GetType().GetProperty("NetworkUpgradesToolOptions").GetValue(settings) },
                    note = "Anarchy 1.8校验绕过由AnarchyEnabled和GetAllowableErrorTypes判断；不虚构独立NoCollision开关，正常辅助显示与施工参数分开。" };
            }
            catch (Exception e) { return new { loaded = true, inspected = false, error = e.GetBaseException().Message }; }
        }

        private object ReadAnarchyRanges(Assembly assembly)
        {
            var recordType = assembly.GetType("Anarchy.Components.HeightRangeRecord", true);
            var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly(recordType));
            var items = new List<object>(); int modified = 0, count;
            try
            {
                using (var entities = query.ToEntityArray(Allocator.Temp))
                {
                    count = entities.Length;
                    foreach (var entity in entities)
                    {
                        var record = OptionalModApi.Component(EntityManager, entity, recordType);
                        float min = Convert.ToSingle(OptionalModApi.Read(record, "min")), max = Convert.ToSingle(OptionalModApi.Read(record, "max"));
                        Colossal.Mathematics.Bounds1? current = null;
                        if (EntityManager.HasComponent<Game.Prefabs.PlaceableNetData>(entity)) current = EntityManager.GetComponentData<Game.Prefabs.PlaceableNetData>(entity).m_ElevationRange;
                        else if (EntityManager.HasComponent<Game.Prefabs.NetCompositionData>(entity)) current = EntityManager.GetComponentData<Game.Prefabs.NetCompositionData>(entity).m_HeightRange;
                        bool changed = current.HasValue && (Math.Abs(current.Value.min - min) > 0.0001f || Math.Abs(current.Value.max - max) > 0.0001f);
                        if (changed) modified++;
                        if (items.Count < 128) items.Add(new { entity = OptionalModApi.JsonValue(entity), original = new { min, max }, current = current.HasValue ? (object)new { min = current.Value.min, max = current.Value.max } : null, changed });
                    }
                }
            }
            finally { query.Dispose(); }
            return new { recordCount = count, modifiedRangeCount = modified, complete = count <= 128, items };
        }

        private static object ExistingAnarchySettings(Assembly assembly)
        {
            var type = assembly.GetType("Anarchy.AnarchyMod", true);
            var instance = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            return type.GetProperty("Settings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);
        }

        private static readonly string[] AnarchyHelperSettings = { "ElevationStepSlider", "NetworkUpgradesToolOptions", "NetworkUpgradesPrefabs",
            "ShowElevationToolOption", "ResetElevationWhenChangingPrefab", "ResetNetworkToolOptionsWhenChangingPrefab", "ReplaceUpgradesBehavior",
            "PreventAccidentalPropCulling", "PropRefreshFrequency", "ConstrainBrush", "DisableAnarchyWhileBrushing", "ShowTooltip", "ToolIcon" };

        /// <summary>辅助偏好与施工参数分离；允许名单依据Anarchy 1.8契约。禁止把任意设置当远程写入口。</summary>
        private BridgeResponse SetAnarchyHelpers(BridgeRequest request)
        {
            var data = OptionalModApi.Payload(request);
            OptionalModApi.Keys(data, AnarchyHelperSettings);
            var assembly = OptionalModApi.Assembly("Anarchy");
            var settings = ExistingAnarchySettings(assembly);
            var before = ReadAnarchyState();
            foreach (var p in data.Properties())
            {
                if (p.Name == "PropRefreshFrequency") { var n = OptionalModApi.Number(p.Value, 1, 600, p.Name); if (n != Math.Truncate(n)) return BridgeResponse.Error(400, "integer frequency required"); }
                else if (p.Value.Type != Newtonsoft.Json.Linq.JTokenType.Boolean) return BridgeResponse.Error(400, p.Name + " must be boolean");
            }
            foreach (var p in data.Properties()) OptionalModApi.Write(settings, p.Name, p.Name == "PropRefreshFrequency" ? (object)(int)p.Value : (bool)p.Value);
            if (data["ShowElevationToolOption"] != null) OptionalModApi.Call(settings, "SetShowElevationToolOption", (bool)data["ShowElevationToolOption"]);
            OptionalModApi.Call(settings, "ApplyAndSave");
            return BridgeResponse.Json(new { before, after = ReadAnarchyState(), note = "only helper preferences changed; road parameters remain explicit per construction request" });
        }

        private BridgeResponse RefreshAnarchyProps()
        {
            var settings = ExistingAnarchySettings(OptionalModApi.Assembly("Anarchy"));
            OptionalModApi.Write(settings, "RefreshProps", true);
            return BridgeResponse.Json(new { requested = true, completed = false, note = "Anarchy refresh scheduled; verify prop visibility after its system update" });
        }

        /// <summary>只关闭已核实的自动绕过入口，不提供启用Anarchy或忽略碰撞的写接口。</summary>
        private BridgeResponse SetAnarchyNormalConstruction()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Anarchy");
            if (assembly == null) return BridgeResponse.Json(ReadAnarchyState());
            try
            {
                var settings = ExistingAnarchySettings(assembly);
                settings.GetType().GetProperty("AnarchicBulldozer").SetValue(settings, false);
                OptionalModApi.Write(settings, "AllowPlacingMultipleUniqueBuildings", false);
                var ui = OptionalModApi.System(World, "Anarchy", "Anarchy.Systems.Common.AnarchyUISystem");
                if ((bool)OptionalModApi.Read(ui, "AnarchyEnabled")) OptionalModApi.Call(ui, "AnarchyToggled");
                var checks = ((IEnumerable)OptionalModApi.Read(OptionalModApi.Read(ui, "m_ErrorChecksBinding"), "Value")).Cast<object>().ToArray();
                for (int i = 0; i < checks.Length; i++) if (Convert.ToInt32(OptionalModApi.Read(checks[i], "DisabledState")) == 2) OptionalModApi.Call(ui, "ChangeDisabledState", i, 0);
                var network = OptionalModApi.System(World, "Anarchy", "Anarchy.Systems.NetworkAnarchy.NetworkAnarchyUISystem");
                var binding = OptionalModApi.Read(network, "m_Composition"); var raw = OptionalModApi.Read(binding, "Value");
                OptionalModApi.Write(binding, "Value", Enum.ToObject(raw.GetType(), Convert.ToInt32(raw) & ~256));
                OptionalModApi.Call(network, "ResetExpandedElevationRanges");
                var ranges = Newtonsoft.Json.Linq.JObject.FromObject(ReadAnarchyRanges(assembly));
                if ((int?)ranges["modifiedRangeCount"] > 0)
                    ((Game.GameSystemBase)OptionalModApi.System(World, "Anarchy", "Anarchy.Systems.ClearanceViolation.ResetNetCompositionDataSystem")).Enabled = true;
                settings.GetType().GetMethod("ApplyAndSave", BindingFlags.Public | BindingFlags.Instance).Invoke(settings, null);
                return BridgeResponse.Json(ReadAnarchyState());
            }
            catch (Exception e) { return BridgeResponse.Error(409, "normal Anarchy configuration failed: " + e.GetBaseException().Message); }
        }

        private bool CheckNormalModConstruction(out BridgeResponse error)
        {
            error = null;
            // 独立于UI当前选中的工具，保证MCP施工不在已知校验屏蔽设置下继续。
            var state = Newtonsoft.Json.Linq.JObject.FromObject(ReadAnarchyState());
            if ((bool?)state["loaded"] == true && ((bool?)state["inspected"] != true || (bool?)state["enabled"] == true
                || (bool?)state["automaticAnarchyOnBulldoze"] == true || (bool?)state["uniqueBuildingBypass"] == true
                || (bool?)state["expandedElevationRangeActive"] == true || (state["allowableErrors"] as Newtonsoft.Json.Linq.JArray)?.Count > 0))
            {
                error = BridgeResponse.Error(409, "normal construction blocked: Anarchy state is unavailable or native validation bypass is enabled; inspect tools/capabilities before continuing");
                return false;
            }
            if ((int?)state["nativeRanges"]?["modifiedRangeCount"] > 0)
            {
                error = BridgeResponse.Error(409, "normal construction blocked: native height/clearance ranges still differ from Anarchy original records; reset normally and read again"); return false;
            }
            return true;
        }

        private object DescribeModIntegrations()
        {
            string[] names = { "Anarchy", "Traffic", "TrafficLightManager.Code", "RoadBuilder", "MoveIt", "BetterBulldozer", "FindIt", "NetworkTools", "NodeController",
                "ExtraAssetsImporter", "ExtraDetailingTools", "ExtraLib", "ExtraNetworksAndAreas", "AchievementFixer", "Skyve", "I18nEverywhere", "UnifiedIconLibrary", "AssetIconLibrary" };
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var loaded = new List<object>();
            foreach (var name in names)
            {
                var matches = assemblies.Where(a => a.GetName().Name.Replace(".", "").Replace("_", "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                loaded.Add(new { name, assemblyMatches = matches.Select(a => new { name = a.GetName().Name, version = a.GetName().Version.ToString() }).ToArray(),
                    status = matches.Length > 0 ? "assembly loaded; integration capabilities listed separately" : "no matching loaded assembly; verify mod log, not proof of uninstall" });
            }
            return new { loadedAssemblies = loaded, anarchy = ReadAnarchyState(),
                connectedCapabilities = ModActionNames,
                restrictions = new[] { "MoveIt/NodeController原始几何直写不经过原生施工校验；搬迁与曲线整形使用原生或NetworkTools校验流程", "Traffic逐车道操作先选择原生手柄再生成预览，原生连接为toggle语义，应用后必须回读", "不提供任意反射、校验绕过或不可见独立资产" },
                note = "optional read-only integrations use loaded mod API/data; loaded assemblies do not imply all mod tools are controllable; do not use invisible standalone assets or validation bypass" };
        }

        private static object TrafficFieldValue(object value)
        {
            if (value == null) return null;
            if (value is Entity entity) return new { index = entity.Index, version = entity.Version };
            var type = value.GetType();
            return type.IsPrimitive || value is string || value is decimal ? value : value.ToString();
        }

        private static object TrafficFields(object value)
        {
            if (value == null) return null;
            var fields = new Dictionary<string, object>();
            foreach (var field in value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Take(24))
                fields[field.Name] = TrafficFieldValue(field.GetValue(value));
            return fields;
        }

        private object DescribeTrafficModData(Entity entity)
        {
            var result = new List<object>();
            using (var components = EntityManager.GetComponentTypes(entity, Allocator.Temp))
            {
                foreach (var component in components)
                {
                    var type = component.GetManagedType();
                    if (type == null || !(type.FullName?.StartsWith("Traffic.Components.", StringComparison.Ordinal) ?? false)) continue;
                    try
                    {
                        if (component.IsBuffer)
                        {
                            var method = typeof(EntityManager).GetMethods().First(m => m.Name == "GetBuffer" && m.IsGenericMethodDefinition
                                && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(bool));
                            var buffer = method.MakeGenericMethod(type).Invoke(EntityManager, new object[] { entity, true });
                            int length = (int)buffer.GetType().GetProperty("Length").GetValue(buffer);
                            var items = new List<object>();
                            for (int i = 0; i < length && i < 32; i++) items.Add(TrafficFields(buffer.GetType().GetProperty("Item").GetValue(buffer, new object[] { i })));
                            result.Add(new { type = type.FullName, buffer = true, count = length, items, complete = length <= 32 });
                        }
                        else if (typeof(IComponentData).IsAssignableFrom(type))
                        {
                            var method = typeof(EntityManager).GetMethods().First(m => m.Name == "GetComponentData" && m.IsGenericMethodDefinition
                                && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Entity));
                            var value = method.MakeGenericMethod(type).Invoke(EntityManager, new object[] { entity });
                            result.Add(new { type = type.FullName, buffer = false, fields = TrafficFields(value) });
                        }
                    }
                    catch (Exception e) { result.Add(new { type = type.FullName, error = e.GetBaseException().Message }); }
                }
            }
            return new { components = result, note = "read-only Traffic mod data on this entity, empty means no attached Traffic.Components data; editing uses a separate native-tool preview/apply session" };
        }
    }
}
