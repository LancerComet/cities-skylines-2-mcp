using System;
using Game.Tools;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private static readonly Dictionary<string, string> ModPurposes = new Dictionary<string, string>
        {
            ["Anarchy"] = "施工错误控制、高程辅助、道路形态/两侧升级、道具防覆盖/防剔除；正常施工禁用错误放行与重复独特建筑",
            ["Traffic"] = "车道连接、移除Unsafe/掉头、连接重置与车道优先级；重建道路后重验",
            ["TrafficLightManager.Code"] = "信号组、自定义相位/模板、配时、车流感应及公交/轨道/行人优先；不替代车道连接",
            ["RoadBuilder"] = "由车道/路缘/中央带横断面生成道路资产；分类、速度、设施和分区独立配置",
            ["NetworkTools"] = "增删/滑动节点、曲线连接、圆/网格等生成、平行网络、坡度/拱形/平滑整形；保持原生Validation",
            ["NodeController"] = "节点类型及端部偏移/旋转/宽度/扭转/坡度/高度、主轴、标线与过街控制",
            ["MoveIt"] = "对象选择、移动/旋转/对齐、节点及控制柄操作、撤销/重做；直接几何变换不是原生建造预检",
            ["BetterBulldozer"] = "精细选择子对象/子网络/表面、同类删除与防重生记录；母体及经营/路径子对象需保护",
            ["FindIt"] = "搜索、分类、收藏及选择已有资产；找到不等于解锁、可用或适合独立放置",
            ["ExtraAssetsImporter"] = "导入资产注册、材质/分类及导入状态；已注册可用资产走原生放置",
            ["ExtraLib"] = "导入资产与分类/图标等共用注册服务，非独立施工工具",
            ["UnifiedIconLibrary"] = "统一模组图标资源服务，非施工工具",
            ["AssetIconLibrary"] = "资产图标资源与兼容服务，非施工工具",
            ["I18NEverywhere"] = "模组及资产本地化服务，搜索时读活本地化名称",
            ["AchievementFixer"] = "模组环境下成就统计/触发修正；不自动授予成就或修改解锁",
            ["Skyve Mod"] = "模组/配组管理、依赖及兼容性审查缓存；以运行程序集核加载，不把缓存当实时保证"
        };

        private BridgeResponse GetModCatalog()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var items = ModPurposes.Select(p =>
            {
                var assembly = assemblies.FirstOrDefault(a => a.GetName().Name == p.Key);
                object[] systems = Array.Empty<object>();
                string error = null;
                if (assembly != null)
                {
                    try
                    {
                        systems = assembly.GetTypes().Where(t => !t.IsAbstract && typeof(GameSystemBase).IsAssignableFrom(t)).Select(t =>
                        {
                            var system = World.GetExistingSystemManaged(t);
                            return (object)new { type = t.FullName, exists = system != null, enabled = system?.Enabled };
                        }).ToArray();
                    }
                    catch (Exception e) { error = e.GetBaseException().Message; }
                }
                return new { assembly = p.Key, purpose = p.Value, loaded = assembly != null, version = assembly?.GetName().Version.ToString(), systems, error };
            }).ToArray();
            return BridgeResponse.Json(new { capturedAtUtc = DateTime.UtcNow, bridgeVersion = Mod.Version, items,
                read = new[] { "Anarchy", "RoadBuilder", "NetworkTools", "NodeController", "MoveIt", "Traffic", "TrafficLightManager.Code", "BetterBulldozer", "all loaded library systems" },
                actions = ModActionNames,
                policy = "明确名单调用已加载模组API；库经状态/资产/本地化接入；原始几何直写、错误放行、虚假资产与成就授予不提供" });
        }

        private static readonly string[] ModActionNames = { "roadBuilder.createTemplate", "network.prepare", "network.apply", "network.cancel",
            "nodeController.crosswalk", "betterBulldozer.removeDecoration", "betterBulldozer.restoreDecorations",
            "traffic.removeUnsafe", "traffic.removeUTurns", "traffic.resetConnections", "traffic.resetPriorities",
            "traffic.prepareConnection", "traffic.previewConnection", "traffic.preparePriority", "traffic.previewPriority", "traffic.apply", "traffic.cancel",
            "signals.initialize", "signals.addMember", "signals.removeMember", "signals.addPhase", "signals.copyPhase", "signals.removePhase",
            "signals.swapPhases", "signals.setPhase", "signals.setEdgeMask", "signals.setLaneMask", "signals.setName", "signals.setTemplate", "signals.removeTemplate" };

        private Entity ModEntity(JToken value, bool requireNode = false)
        {
            if (!(value is JObject o)) throw new ArgumentException("entity must contain index and version");
            OptionalModApi.Keys(o, "index", "version");
            if (o["index"]?.Type != JTokenType.Integer || o["version"]?.Type != JTokenType.Integer) throw new ArgumentException("integer entity identity required");
            var e = new Entity { Index = (int?)o["index"] ?? -1, Version = (int?)o["version"] ?? -1 };
            if (e.Index <= 0 || e.Version <= 0 || !EntityManager.Exists(e) || EntityManager.HasComponent<Deleted>(e) || EntityManager.HasComponent<Temp>(e))
                throw new ArgumentException("entity identity stale or not a live city entity");
            if (requireNode && !EntityManager.HasComponent<Node>(e)) throw new ArgumentException("entity must be a network node");
            return e;
        }

        private object ModEntityComponents(Entity e, string prefix)
        {
            var results = new List<object>();
            using (var types = EntityManager.GetComponentTypes(e, Allocator.Temp))
                foreach (var component in types)
                {
                    var type = component.GetManagedType();
                    if (type == null || !type.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (component.IsBuffer)
                    {
                        var buffer = OptionalModApi.Buffer(EntityManager, e, type);
                        int length = (int)OptionalModApi.Read(buffer, "Length");
                        var items = Enumerable.Range(0, Math.Min(length, 128)).Select(i => OptionalModApi.JsonValue(buffer.GetType().GetProperty("Item").GetValue(buffer, new object[] { i }))).ToArray();
                        results.Add(new { type = type.FullName, count = length, complete = length <= 128, items });
                    }
                    else if (typeof(IComponentData).IsAssignableFrom(type)) results.Add(new { type = type.FullName, value = OptionalModApi.JsonValue(OptionalModApi.Component(EntityManager, e, type)) });
                }
            return results;
        }

        private BridgeResponse InspectOptionalMod(BridgeRequest request)
        {
            if (!TryGetCity(out _, out var cityError)) return cityError;
            if (!request.Query.TryGetValue("mod", out var mod)) return BridgeResponse.Error(400, "mod required");
            var data = OptionalModApi.Payload(request);
            if (data["dryRun"] != null && data["dryRun"].Type != JTokenType.Boolean) throw new ArgumentException("dryRun must be a JSON boolean");
            if (mod == "Anarchy") return BridgeResponse.Json(ReadAnarchyState());
            if (mod == "NetworkTools") return BridgeResponse.Json(BridgeModToolSystem.Instance?.Snapshot() ?? new { available = false });
            if (mod == "RoadBuilder")
            {
                var rb = OptionalModApi.System(World, "RoadBuilder", "RoadBuilder.Systems.RoadBuilderSystem");
                var dictionary = (IDictionary)OptionalModApi.Read(rb, "Configurations");
                var templates = new List<object>();
                foreach (DictionaryEntry item in dictionary) templates.Add(new { id = item.Key, config = OptionalModApi.JsonValue(OptionalModApi.Read(item.Value, "Config")) });
                var generation = OptionalModApi.System(World, "RoadBuilder", "RoadBuilder.Systems.RoadBuilderGenerationDataSystem");
                var generationData = OptionalModApi.Read(generation, "RoadGenerationData");
                var sections = generationData == null ? null : OptionalModApi.JsonValue(OptionalModApi.Read(generationData, "NetSectionPrefabs"));
                var groups = new Dictionary<string, object>();
                if (generationData != null) foreach (DictionaryEntry item in (IDictionary)OptionalModApi.Read(generationData, "LaneGroupPrefabs"))
                    if (groups.Count < 128) groups[item.Key.ToString()] = OptionalModApi.JsonValue(OptionalModApi.Read(item.Value, "Options"));
                return BridgeResponse.Json(new { templates, sections, groups, generationReady = generationData != null,
                    note = "配置完整返回；截面目录最多128项，资产详情继续用prefab_info；只生成独立模板，再正常建造，不覆盖全城实例" });
            }
            if (mod == "NodeController")
            {
                var tool = OptionalModApi.System(World, "NodeController", "NodeController.Main.Tools.NodeControllerTool");
                var manager = OptionalModApi.Read(tool, "Manager");
                if (data["entity"] == null) return BridgeResponse.Json(new { selected = OptionalModApi.JsonValue(OptionalModApi.Read(tool, "SelectedNode")), nodes = OptionalModApi.JsonValue(OptionalModApi.Read(manager, "Nodes")) });
                var e = ModEntity(data["entity"], true);
                // 只读不能调用GetOrCreate；查现有节点记录与实际曲线。
                var nodes = (IDictionary)OptionalModApi.Read(manager, "Nodes");
                object node = nodes.Contains(e) ? nodes[e] : null;
                var ends = node == null ? null : OptionalModApi.Call(manager, "GetSegmentEnds", e);
                return BridgeResponse.Json(new { entity = OptionalModApi.JsonValue(e), stored = node != null, node = OptionalModApi.JsonValue(node), ends = OptionalModApi.JsonValue(ends) });
            }
            if (mod == "MoveIt")
            {
                var tool = OptionalModApi.System(World, "MoveIt", "MoveIt.Tool.MIT");
                var queue = OptionalModApi.Read(tool, "Queue");
                if (queue == null) return BridgeResponse.Error(409, "Move It queue not initialized; wait for native city preload");
                return BridgeResponse.Json(new { manipulating = OptionalModApi.Read(tool, "IsManipulating"), canUndo = OptionalModApi.Call(queue, "CanUndo"), canRedo = OptionalModApi.Call(queue, "CanRedo"),
                    note = "Move It变换直接修改几何，正常施工使用网络原生预览；原生建筑搬迁用building/move" });
            }
            if (mod == "Traffic" || mod == "BetterBulldozer")
            {
                if (data["entity"] == null) return GetModCatalog();
                var e = ModEntity(data["entity"]);
                return BridgeResponse.Json(new { entity = OptionalModApi.JsonValue(e), data = ModEntityComponents(e, mod == "Traffic" ? "Traffic.Components." : "Better_Bulldozer.Components.") });
            }
            if (mod == "TrafficLightManager.Code") return InspectSignals(data);
            if (mod == "FindIt")
            {
                var categories = (IDictionary)OptionalModApi.Static("FindIt", "FindIt.Utilities.FindItUtil", "CategorizedPrefabs");
                var counts = new Dictionary<string, int>();
                foreach (DictionaryEntry category in categories)
                    foreach (DictionaryEntry subcategory in (IDictionary)category.Value)
                        if (subcategory.Key.ToString() == "Any") counts[category.Key.ToString()] = Convert.ToInt32(OptionalModApi.Read(subcategory.Value, "Count"));
                return BridgeResponse.Json(new { ready = OptionalModApi.Static("FindIt", "FindIt.Utilities.FindItUtil", "IsReady"), categories = counts, note = "搜索读活PrefabSystem与本地化；不更改FindIt收藏/筛选或解锁" });
            }
            if (mod == "ExtraAssetsImporter")
            {
                object count(string name) => OptionalModApi.Read(OptionalModApi.Static("ExtraAssetsImporter", "ExtraAssetsImporter.AssetImporter.AssetsImporterManager", name), "Count");
                return BridgeResponse.Json(new { preImporters = count("PreImporters"), importers = count("Importers"), componentImporters = count("ComponentImporters"), databaseLoaded = OptionalModApi.Static("ExtraAssetsImporter", "ExtraAssetsImporter.DataBase.EAIDataBaseManager", "eaiDataBase") != null,
                    note = "只读已注册导入器与数据库就绪状态；不在运行中重新导入/改缓存或数据库" });
            }
            if (ModPurposes.ContainsKey(mod)) return GetModCatalog();
            return BridgeResponse.Error(400, "unknown mod identifier; read mods/catalog");
        }

        private BridgeResponse ActOptionalMod(BridgeRequest request)
        {
            if (!request.Query.TryGetValue("action", out var action) || !ModActionNames.Contains(action)) return BridgeResponse.Error(400, "unknown mod action; read mods/catalog");
            var data = OptionalModApi.Payload(request);
            if (!TryGetCity(out _, out var cityError)) return cityError;
            if (data["dryRun"] != null && data["dryRun"].Type != JTokenType.Boolean) throw new ArgumentException("dryRun must be a JSON boolean");
            if (action.StartsWith("network.", StringComparison.Ordinal))
            {
                PrefabBase prefab = null;
                var parameterPrefabs = new Dictionary<string, PrefabBase>();
                if (action == "network.prepare")
                {
                    if (TransitToolsBusy(out var busy)) return busy;
                    if (data["prefab"] != null)
                    {
                        string name = (string)data["prefab"];
                        if (name.IndexOf("Invisible", StringComparison.OrdinalIgnoreCase) >= 0 || !TryFindPrefabByName(NetPrefabQuery, name, out var entity, out prefab) || IsLocked(entity))
                            return BridgeResponse.Error(400, "prefab unavailable, locked or prohibited");
                    }
                    foreach (string key in BridgeModToolSystem.Instance.PrefabParameterKeys(data))
                    {
                        var value = data["parameters"][key];
                        if (value.Type != JTokenType.String) return BridgeResponse.Error(400, "network prefab parameter must be an exact name");
                        string name = (string)value;
                        if (name.IndexOf("Invisible", StringComparison.OrdinalIgnoreCase) >= 0 || !TryFindPrefabByName(NetPrefabQuery, name, out var entity, out var selected) || IsLocked(entity))
                            return BridgeResponse.Error(400, "network prefab parameter unavailable, locked or prohibited");
                        parameterPrefabs.Add(key, selected);
                    }
                }
                return BridgeModToolSystem.Instance.Handle(action, data, EntityManager, prefab, parameterPrefabs);
            }
            if (action.StartsWith("traffic.", StringComparison.Ordinal)) return ActTraffic(action.Substring(8), data);
            if (BridgeModToolSystem.Instance?.Busy == true) return BridgeResponse.Error(409, "another construction operation is in progress");
            if (action == "roadBuilder.createTemplate") return CreateRoadBuilderTemplate(data);
            if (action.StartsWith("signals.", StringComparison.Ordinal)) return ActSignals(action.Substring(8), data);
            if (action.StartsWith("betterBulldozer.", StringComparison.Ordinal)) return ActDecoration(action.Substring(16), data);
            if (action == "nodeController.crosswalk")
            {
                OptionalModApi.Keys(data, "entity", "edge", "mode", "dryRun");
                var node = ModEntity(data["entity"], true); var edge = ModEntity(data["edge"]);
                if (!EntityManager.HasComponent<Edge>(edge)) throw new ArgumentException("edge required");
                var end = EntityManager.GetComponentData<Edge>(edge);
                if (end.m_Start != node && end.m_End != node) throw new ArgumentException("edge not connected to node");
                // 关闭过街必须另核可达替代；接口默认dryRun，调用方明确选择。
                int mode = (int?)data["mode"] ?? -1;
                if (mode < 0 || mode > 2) throw new ArgumentException("crosswalk mode outside enum");
                if ((bool?)data["dryRun"] != false) return BridgeResponse.Json(new { validated = true, applied = false, node = OptionalModApi.JsonValue(node), edge = OptionalModApi.JsonValue(edge), mode });
                var tool = OptionalModApi.System(World, "NodeController", "NodeController.Main.Tools.NodeControllerTool");
                var manager = OptionalModApi.Read(tool, "Manager");
                var segment = OptionalModApi.Call(manager, "GetOrCreateSegmentEndData", edge, node);
                var property = segment.GetType().GetProperty("CrosswalkMode");
                property.SetValue(segment, Enum.ToObject(property.PropertyType, mode));
                OptionalModApi.Call(tool, "ApplyCrosswalkToEcs", segment);
                return BridgeResponse.Json(new { applied = true, segment = OptionalModApi.JsonValue(segment), note = "回读真实步行路径和替代过街后才能验收" });
            }
            return BridgeResponse.Error(400, "action not implemented");
        }

        private BridgeResponse CreateRoadBuilderTemplate(JObject data)
        {
            OptionalModApi.Keys(data, "sourceEntity", "name", "speedKmh", "addons", "lanes", "category", "dryRun");
            var entity = ModEntity(data["sourceEntity"]);
            if (!EntityManager.HasComponent<Edge>(entity)) throw new ArgumentException("sourceEntity must be a road edge");
            if (!EntityManager.HasComponent<Game.Prefabs.PrefabRef>(entity) || IsLocked(EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(entity).m_Prefab)) throw new ArgumentException("source road prefab unavailable or locked");
            var rb = OptionalModApi.System(World, "RoadBuilder", "RoadBuilder.Systems.RoadBuilderSystem");
            var source = OptionalModApi.Call(rb, "GetOrGenerateConfiguration", entity) ?? throw new InvalidOperationException("Road Builder cannot generate this road's config");
            var config = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(source), source.GetType());
            if (config.GetType().FullName != "RoadBuilder.Domain.Configurations.RoadConfig") throw new ArgumentException("only ordinary road configurations supported");
            string name = (string)data["name"];
            if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.IndexOf("Invisible", StringComparison.OrdinalIgnoreCase) >= 0) throw new ArgumentException("valid template name required");
            OptionalModApi.Write(config, "Name", name);
            OptionalModApi.Write(config, "ID", null);
            // RoadOptionsUtil显示km/h = 原始SpeedLimit / 2，不能当m/s或直接填显示值。
            if (data["speedKmh"] != null) OptionalModApi.Write(config, "SpeedLimit", OptionalModApi.Number(data["speedKmh"], 5, 140, "speedKmh") * 2f);
            foreach (string key in new[] { "Addons", "Category" })
            {
                var token = data[char.ToLowerInvariant(key[0]) + key.Substring(1)];
                if (token == null) continue;
                var property = config.GetType().GetProperty(key);
                if (token.Type != JTokenType.String) throw new ArgumentException(key + " must use enum names");
                var parsed = Enum.Parse(property.PropertyType, (string)token, false);
                // Enum.Parse也接受任意数字，显式按已定义位校验。
                long allowed = Enum.GetValues(property.PropertyType).Cast<object>().Aggregate(0L, (x, v) => x | Convert.ToInt64(v));
                if ((Convert.ToInt64(parsed) & ~allowed) != 0 || ((string)token).Any(char.IsDigit)) throw new ArgumentException("unknown " + key + " enum");
                property.SetValue(config, parsed);
            }
            if (data["lanes"] is JArray lanes)
            {
                if (lanes.Count < 2 || lanes.Count > 32) throw new ArgumentException("lanes count outside 2..32");
                var generation = OptionalModApi.System(World, "RoadBuilder", "RoadBuilder.Systems.RoadBuilderGenerationDataSystem");
                var generationData = OptionalModApi.Read(generation, "RoadGenerationData") ?? throw new InvalidOperationException("Road Builder generation not ready");
                var sections = (IDictionary)OptionalModApi.Read(generationData, "NetSectionPrefabs"); var groups = (IDictionary)OptionalModApi.Read(generationData, "LaneGroupPrefabs");
                foreach (var lane in lanes.Cast<JObject>())
                {
                    OptionalModApi.Keys(lane, "SectionPrefabName", "GroupPrefabName", "GroupOptions", "Invert", "Version");
                    if (lane["Invert"]?.Type != JTokenType.Boolean) throw new ArgumentException("lane Invert boolean required");
                    string section = (string)lane["SectionPrefabName"], groupName = (string)lane["GroupPrefabName"];
                    if (!string.IsNullOrEmpty(groupName))
                    {
                        if (!groups.Contains(groupName)) throw new ArgumentException("unknown lane group: " + groupName);
                        var options = (IEnumerable)OptionalModApi.Read(groups[groupName], "Options");
                        if (!(lane["GroupOptions"] is JObject groupOptions)) throw new ArgumentException("GroupOptions object required");
                        foreach (var option in groupOptions.Properties())
                        {
                            var definition = options.Cast<object>().FirstOrDefault(o => (string)OptionalModApi.Read(o, "Name") == option.Name);
                            if (definition == null) throw new ArgumentException("unknown group option: " + option.Name);
                            if (option.Value.Type != JTokenType.Null && option.Value.Type != JTokenType.String) throw new ArgumentException("group option value must be string or null");
                            var choices = OptionalModApi.Read(definition, "Options") as IEnumerable;
                            string selected = (string)option.Value;
                            if (choices != null && !choices.Cast<object>().Any(choice => (string)OptionalModApi.Read(choice, "Value") == selected)) throw new ArgumentException("unknown value for group option: " + option.Name);
                            if (choices == null && selected != (string)OptionalModApi.Read(definition, "DefaultValue")) throw new ArgumentException("option without declared choices only accepts its native default");
                        }
                    }
                    else if (string.IsNullOrEmpty(section) || !sections.Contains(section) || section.IndexOf("Invisible", StringComparison.OrdinalIgnoreCase) >= 0) throw new ArgumentException("unknown/prohibited lane section");
                }
                var property = config.GetType().GetProperty("Lanes");
                property.SetValue(config, lanes.ToObject(property.PropertyType));
            }
            if ((bool?)data["dryRun"] != false) return BridgeResponse.Json(new { applied = false, config = OptionalModApi.JsonValue(config), note = "模板配置预览，不证明该道路已生成或可在场址施工" });
            var prefab = OptionalModApi.Call(rb, "AddPrefab", config, true) ?? throw new InvalidOperationException("Road Builder refused template generation; no road placement requested");
            return BridgeResponse.Json(new { created = true, config = OptionalModApi.JsonValue(OptionalModApi.Read(prefab, "Config")),
                note = "资产生成已请求，待PrefabSystem更新后核prefab_info；没有更改既有道路，没有自动覆盖Traffic连接" });
        }
    }
}
