using System;
using Game.Tools;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Game.Common;
using Game.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private SignalUiLimits ReadSignalLimits()
        {
            // PDX 模组可从字节加载，Assembly.Location 为空；原生登记的资产才是当前加载版本的路径。
            var assembly = OptionalModApi.Assembly("TrafficLightManager.Code");
            var manager = Game.SceneFlow.GameManager.instance?.modManager;
            if (manager == null || !manager.TryGetExecutableAsset(assembly, out var asset)
                || string.IsNullOrWhiteSpace(asset.path))
                throw new ArgumentException("TLM loaded asset path unavailable; numeric writes disabled");
            var directory = System.IO.Path.GetDirectoryName(asset.path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("TLM loaded asset directory unavailable; numeric writes disabled");
            return SignalUiLimits.Read(System.IO.Path.Combine(directory, "TrafficLightManager.UI.mjs"));
        }

        private float SignalNumber(SignalUiLimits limits, JToken token, string key)
        {
            var range = limits.ranges[key];
            return limits.Validate(key, OptionalModApi.Number(token, range.min, range.max, key));
        }
        private object SignalsUI => OptionalModApi.System(World, "TrafficLightManager.Code", "TrafficLightManager.Code.Systems.UI.UISystem");
        private Type SignalType(string name) => OptionalModApi.Assembly("TrafficLightManager.Code").GetType("TrafficLightManager.Code.Components." + name, true);
        private Entity SignalGroup(Entity node)
        {
            var type = SignalType("CustomTrafficLights");
            if (!EntityManager.HasComponent(node, ComponentType.ReadOnly(type))) return Entity.Null;
            return (Entity)OptionalModApi.Read(OptionalModApi.Component(EntityManager, node, type), "m_TrafficLightGroupEntity");
        }
        private int PhaseCount(Entity group) => group == Entity.Null ? 0 : (int)OptionalModApi.Read(OptionalModApi.Buffer(EntityManager, group, SignalType("CustomPhaseData")), "Length");
        private object SignalSnapshot(Entity node)
        {
            var group = SignalGroup(node);
            return new { node = OptionalModApi.JsonValue(node), nodeData = ModEntityComponents(node, "TrafficLightManager.Code.Components."),
                group = OptionalModApi.JsonValue(group), groupData = group == Entity.Null ? null : ModEntityComponents(group, "TrafficLightManager.Code.Components."),
                native = EntityManager.HasComponent<TrafficLights>(node) ? (object)OptionalModApi.JsonValue(EntityManager.GetComponentData<TrafficLights>(node)) : null };
        }
        private BridgeResponse InspectSignals(JObject data)
        {
            OptionalModApi.Keys(data, "entity");
            OptionalModApi.Assembly("TrafficLightManager.Code");
            if (data["entity"] != null) return BridgeResponse.Json(SignalSnapshot(ModEntity(data["entity"], true)));
            var query = EntityManager.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly(SignalType("CustomTrafficLights")) }, None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() } });
            object[] nodes; int count;
            using (var entities = query.ToEntityArray(Allocator.Temp)) { count = entities.Length; nodes = entities.Take(128).Select(SignalSnapshot).ToArray(); }
            query.Dispose();
            var mod = OptionalModApi.Assembly("TrafficLightManager.Code").GetType("TrafficLightManager.Code.Mod", true);
            var settings = mod.GetField("m_Settings", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public).GetValue(null);
            return BridgeResponse.Json(new { loaded = true, nodeCount = count, complete = count <= 128, nodes,
                numericUiLimits = ReadSignalLimits().ranges,
                templates = OptionalModApi.JsonValue(OptionalModApi.Call(settings, "GetCustomPhaseTemplates")),
                durationUnit = "mod simulation-update ticks; one update per 64 simulation frames, not fixed wall-clock seconds",
                phaseIndex = "zero-based, masks have 16 bits", note = "相位/配时与Traffic连接分开；新增组默认复制原生LaneSignal掩码，保留当前放行关系" });
        }
        private static string SignalText(JToken token, string label)
        {
            string text = token?.Type == JTokenType.String ? (string)token : null;
            if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > 60) throw new ArgumentException(label + " requires 1..60 UTF8 bytes");
            return text;
        }
        private BridgeResponse ActSignals(string action, JObject data)
        {
            OptionalModApi.Keys(data, "entity", "member", "index", "index2", "values", "masks", "name", "template", "dryRun");
            bool dryRun = (bool?)data["dryRun"] != false;
            var numericLimits = action == "setPhase" || action == "setTemplate" ? ReadSignalLimits() : null;
            object ui = SignalsUI;
            Entity node = data["entity"] == null ? Entity.Null : ModEntity(data["entity"], true);
            if (node != Entity.Null && !EntityManager.HasComponent<TrafficLights>(node)) throw new ArgumentException("node has no native traffic lights; add normally through junction controls first");
            Entity group = node == Entity.Null ? Entity.Null : SignalGroup(node);
            int count = PhaseCount(group);
            int index = (int?)data["index"] ?? -1, index2 = (int?)data["index2"] ?? -1;
            if (action != "initialize" && action != "setTemplate" && action != "removeTemplate" && group == Entity.Null) throw new ArgumentException("initialize a signal group first");
            if (new[] { "copyPhase", "removePhase", "swapPhases", "setPhase" }.Contains(action) && (index < 0 || index >= count)) throw new ArgumentException("phase index stale");
            if (action == "swapPhases" && (index2 < 0 || index2 >= count)) throw new ArgumentException("second phase index stale");
            if ((action == "addPhase" || action == "copyPhase") && count >= 16) throw new ArgumentException("16-phase mask limit reached");
            if (action == "removePhase" && count <= 1) throw new ArgumentException("cannot remove the last phase");
            if (action == "removePhase")
            {
                var members = OptionalModApi.Buffer(EntityManager, group, SignalType("TrafficLightsMemberRef"));
                for (int i = 0; i < (int)OptionalModApi.Read(members, "Length"); i++)
                {
                    var memberData = members.GetType().GetProperty("Item").GetValue(members, new object[] { i });
                    Entity memberNode = (Entity)OptionalModApi.Read(memberData, "m_Entity");
                    var snapshot = JToken.FromObject(ModEntityComponents(memberNode, "TrafficLightManager.Code.Components."));
                    if (snapshot.SelectTokens("$..m_GoGroupMask").Any(mask => (int)mask == (1 << index))) throw new ArgumentException("removing this phase would erase a movement's only green; remap masks first");
                }
            }
            Entity member = data["member"] == null ? Entity.Null : ModEntity(data["member"], true);
            if ((action == "addMember" || action == "removeMember") && (member == Entity.Null || !EntityManager.HasComponent<TrafficLights>(member))) throw new ArgumentException("member must have native lights");
            if (action == "addMember" && SignalGroup(member) != Entity.Null) throw new ArgumentException("member already belongs to a signal group; remove explicitly first");
            if (action == "removeMember" && SignalGroup(member) != group) throw new ArgumentException("member not in the selected group");

            var callbacks = new List<(string binding, object input)>();
            if (action == "initialize")
            {
                if (node == Entity.Null || group != Entity.Null) throw new ArgumentException("initialize requires a native node without a custom group");
                int groups = EntityManager.GetComponentData<TrafficLights>(node).m_SignalGroupCount;
                if (groups < 1 || groups > 16) throw new ArgumentException("native signal groups outside 1..16");
                var masks = NativeSignalMasks(node, ui, groups);
                for (int i = 0; i < groups; i++) callbacks.Add(("AddCustomPhase", ""));
                callbacks.Add(("UpdateEdgeGroupMask", masks.edge.ToString(Formatting.None)));
                callbacks.Add(("UpdateSubLaneGroupMask", masks.lane.ToString(Formatting.None)));
            }
            if (action == "addMember")
            {
                var masks = NativeSignalMasks(member, ui, count);
                callbacks.Add(("UpdateEdgeGroupMask", masks.edge.ToString(Formatting.None)));
                callbacks.Add(("UpdateSubLaneGroupMask", masks.lane.ToString(Formatting.None)));
            }
            if (action == "addPhase") callbacks.Add(("AddCustomPhase", ""));
            if (action == "copyPhase") callbacks.Add(("CopyPhase", index));
            if (action == "removePhase") callbacks.Add(("RemoveCustomPhase", index));
            if (action == "swapPhases") callbacks.Add(("SwapCustomPhase", JsonConvert.SerializeObject(new { index1 = index, index2 })));
            if (action == "setName") callbacks.Add(("SetTrafficLightGroupName", SignalText(data["name"], "name")));
            if (action == "setPhase")
            {
                if (!(data["values"] is JObject values) || !values.HasValues) throw new ArgumentException("values required");
                string[] boolKeys = { "PrioritiseTrack", "PrioritisePublicCar", "PrioritisePedestrian", "LinkedWithNextPhase", "EndPhasePrematurely", "BindWithTemplate" };
                foreach (var p in values.Properties())
                {
                    string value;
                    if (boolKeys.Contains(p.Name)) { if (p.Value.Type != JTokenType.Boolean) throw new ArgumentException("boolean required"); value = ((bool)p.Value).ToString(); }
                    else if (p.Name == "MinimumDuration" || p.Name == "MaximumDuration")
                    {
                        float n = SignalNumber(numericLimits, p.Value, p.Name); if (n != Math.Truncate(n)) throw new ArgumentException("integer duration required"); value = ((int)n).ToString();
                    }
                    else if (new[] { "TargetDurationMultiplier", "LaneOccupiedMultiplier", "IntervalFactor" }.Contains(p.Name)) value = SignalNumber(numericLimits, p.Value, p.Name).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    else if (p.Name == "Name" || p.Name == "BindTemplate") value = SignalText(p.Value, p.Name);
                    else throw new ArgumentException("unknown phase field: " + p.Name);
                    callbacks.Add(("SetCustomPhaseData", JsonConvert.SerializeObject(new { index, key = p.Name, value })));
                }
                if (values["MinimumDuration"] != null && values["MaximumDuration"] != null && (int)values["MinimumDuration"] > (int)values["MaximumDuration"]) throw new ArgumentException("minimum duration exceeds maximum");
                var phaseBuffer = OptionalModApi.Buffer(EntityManager, group, SignalType("CustomPhaseData"));
                var phase = phaseBuffer.GetType().GetProperty("Item").GetValue(phaseBuffer, new object[] { index });
                int minDuration = (int?)values["MinimumDuration"] ?? Convert.ToInt32(OptionalModApi.Read(phase, "m_MinimumDuration"));
                int maxDuration = (int?)values["MaximumDuration"] ?? Convert.ToInt32(OptionalModApi.Read(phase, "m_MaximumDuration"));
                if (minDuration > maxDuration) throw new ArgumentException("partial duration change conflicts with the retained duration; supply a valid pair");
            }
            if (action == "setTemplate")
            {
                if (!(data["template"] is JObject template)) throw new ArgumentException("template required");
                OptionalModApi.Keys(template, "m_Name", "m_IsPrioritiseTrack", "m_IsPrioritisePublicCar", "m_IsPrioritisePedestrian", "m_MinimumDuration", "m_MaximumDuration", "m_TargetDurationMultiplier", "m_LaneOccupiedMultiplier", "m_IntervalFactor");
                SignalText(template["m_Name"], "template name");
                float min = SignalNumber(numericLimits, template["m_MinimumDuration"], "MinimumDuration"), max = SignalNumber(numericLimits, template["m_MaximumDuration"], "MaximumDuration");
                if (min != Math.Truncate(min) || max != Math.Truncate(max) || min > max) throw new ArgumentException("invalid template durations");
                foreach (var key in new[] { "m_TargetDurationMultiplier", "m_LaneOccupiedMultiplier", "m_IntervalFactor" }) SignalNumber(numericLimits, template[key], key.Substring(2));
                foreach (var key in new[] { "m_IsPrioritiseTrack", "m_IsPrioritisePublicCar", "m_IsPrioritisePedestrian" }) if (template[key]?.Type != JTokenType.Boolean) throw new ArgumentException("template priority boolean required");
                callbacks.Add(("SetTemplate", template.ToString(Formatting.None)));
            }
            if (action == "removeTemplate") callbacks.Add(("RemoveTemplate", SignalText(data["name"], "template name")));
            if (action == "setEdgeMask" || action == "setLaneMask")
            {
                if (!(data["masks"] is JArray masks) || masks.Count < 1 || masks.Count > 128) throw new ArgumentException("1..128 complete masks required; omitted movement fields would be cleared by native mod");
                bool laneMask = action == "setLaneMask";
                foreach (var m in masks.Cast<JObject>()) ValidateSignalMask(node, m, count, laneMask);
                callbacks.Add((laneMask ? "UpdateSubLaneGroupMask" : "UpdateEdgeGroupMask", new JObject { ["entity"] = JObject.FromObject(new { Index = node.Index, Version = node.Version }), ["groupMaskArray"] = masks }.ToString(Formatting.None)));
            }
            if (dryRun) return BridgeResponse.Json(new { applied = false, action, node = OptionalModApi.JsonValue(node), phaseCount = count, calls = callbacks.Select(c => c.binding),
                note = "参数预检；任意相位变更仍须检查冲突、必要运动和行人等待；不会调用选择或写入" });
            var before = node == Entity.Null ? null : SignalSnapshot(node);
            Entity previous = (Entity)OptionalModApi.Read(ui, "m_SelectedTrafficLightGroupEntity");
            var previousUiState = OptionalModApi.Call(ui, "GetToolState");
            var previousTool = World.GetExistingSystemManaged<Game.Tools.ToolSystem>().activeTool;
            try
            {
                if (action == "initialize") { OptionalModApi.Call(ui, "ChangeSelectedTrafficLightGroupEntity", node); group = SignalGroup(node); }
                else if (group != Entity.Null) OptionalModApi.Call(ui, "SetSelectedTrafficLightGroupEntity", group);
                if (action == "addMember") OptionalModApi.Call(ui, "AddTrafficLights", member);
                else if (action == "removeMember") OptionalModApi.Call(ui, "RemoveTrafficLights", member);
                foreach (var c in callbacks) OptionalModApi.Binding(ui, "TrafficLightManager", c.binding, c.input);
                return BridgeResponse.Json(new { applied = true, before, after = node == Entity.Null ? null : SignalSnapshot(node), note = "通过原模组回调执行；需运行模拟复核生成LaneSignal、相位轮转和等待，不能以配置写入代替交通验收" });
            }
            finally
            {
                OptionalModApi.Call(ui, "SetSelectedTrafficLightGroupEntity", EntityManager.Exists(previous) ? previous : Entity.Null);
                OptionalModApi.Call(ui, "SetToolState", previousUiState);
                if (World.GetExistingSystemManaged<Game.Tools.ToolSystem>().activeTool != previousTool) World.GetExistingSystemManaged<Game.Tools.ToolSystem>().activeTool = previousTool;
            }
        }

        /// <summary>读取原生实际内连接，按来源车道和转向复制掩码。TLM逐车道索引的是来源车道，不能误填路口内部连接。</summary>
        private (JObject edge, JObject lane) NativeSignalMasks(Entity node, object ui, int phaseCount)
        {
            if (!EntityManager.HasBuffer<SubLane>(node) || !EntityManager.HasBuffer<ConnectedEdge>(node)) throw new ArgumentException("generated native lanes unavailable");
            int nativeCount = EntityManager.GetComponentData<TrafficLights>(node).m_SignalGroupCount;
            if (nativeCount < 1 || nativeCount > phaseCount) throw new ArgumentException("member native phase count exceeds the group; explicit phase mapping required");
            var utility = OptionalModApi.Assembly("TrafficLightManager.Code").GetType("TrafficLightManager.Code.Utils.NodeUtils", true);
            var handles = OptionalModApi.Read(ui, "m_TypeHandle");
            object map = utility.GetMethod("GetLaneConnectionMap").Invoke(null, new object[] { Allocator.Temp, EntityManager.GetBuffer<SubLane>(node, true), EntityManager.GetBuffer<ConnectedEdge>(node, true), OptionalModApi.Read(handles, "m_SubLane"), OptionalModApi.Read(handles, "m_Lane") });
            var masks = new Dictionary<Entity, JObject>();
            JObject turn() => new JObject(new[] { "m_Left", "m_Straight", "m_Right", "m_UTurn" }.Select(k => new JProperty(k, new JObject { ["m_GoGroupMask"] = 0 })));
            JObject identity(Entity e) => JObject.FromObject(new { Index = e.Index, Version = e.Version });
            try
            {
                foreach (var entry in EntityManager.GetBuffer<SubLane>(node, true))
                {
                    var lane = entry.m_SubLane;
                    if (!EntityManager.HasComponent<LaneSignal>(lane) || EntityManager.HasComponent<MasterLane>(lane)) continue;
                    bool pedestrian = EntityManager.HasComponent<PedestrianLane>(lane);
                    bool crossing = pedestrian && (EntityManager.GetComponentData<PedestrianLane>(lane).m_Flags & PedestrianLaneFlags.Crosswalk) != 0;
                    object connection = utility.GetMethod("GetLaneConnectionFromNodeSubLane").Invoke(null, new[] { (object)lane, map, crossing });
                    var source = pedestrian ? lane : (Entity)OptionalModApi.Read(connection, "m_SourceSubLane");
                    if (source == Entity.Null) throw new ArgumentException("native source-lane mapping unavailable; initialization blocked");
                    if (!masks.TryGetValue(source, out var mask)) masks[source] = mask = new JObject { ["m_SubLane"] = identity(source), ["m_Options"] = 0, ["m_Car"] = turn(), ["m_Track"] = turn(), ["m_Pedestrian"] = new JObject { ["m_GoGroupMask"] = 0 } };
                    int bits = EntityManager.GetComponentData<LaneSignal>(lane).m_GroupMask;
                    if ((bits & ~((1 << phaseCount) - 1)) != 0) throw new ArgumentException("native mask exceeds group phases");
                    if (pedestrian) mask["m_Pedestrian"]["m_GoGroupMask"] = bits;
                    else
                    {
                        string direction = "m_Straight", category = "m_Car";
                        if (EntityManager.HasComponent<CarLane>(lane))
                        {
                            var flags = EntityManager.GetComponentData<CarLane>(lane).m_Flags;
                            direction = (flags & (CarLaneFlags.UTurnLeft | CarLaneFlags.UTurnRight)) != 0 ? "m_UTurn" : (flags & (CarLaneFlags.TurnLeft | CarLaneFlags.GentleTurnLeft)) != 0 ? "m_Left" : (flags & (CarLaneFlags.TurnRight | CarLaneFlags.GentleTurnRight)) != 0 ? "m_Right" : "m_Straight";
                        }
                        else if (EntityManager.HasComponent<TrackLane>(lane))
                        {
                            category = "m_Track"; var flags = EntityManager.GetComponentData<TrackLane>(lane).m_Flags;
                            direction = (flags & TrackLaneFlags.TurnLeft) != 0 ? "m_Left" : (flags & TrackLaneFlags.TurnRight) != 0 ? "m_Right" : "m_Straight";
                        }
                        int prior = (int)mask[category][direction]["m_GoGroupMask"];
                        if (prior != 0 && prior != bits) throw new ArgumentException("native signals cannot be represented by one source-lane turn mask; initialization blocked");
                        mask[category][direction]["m_GoGroupMask"] = bits;
                    }
                }
                if (masks.Count == 0) throw new ArgumentException("no generated native LaneSignal masks");
                var edges = new JArray();
                foreach (var edge in EntityManager.GetBuffer<ConnectedEdge>(node, true)) edges.Add(new JObject { ["m_Edge"] = identity(edge.m_Edge), ["m_Options"] = 1, ["m_Car"] = turn(), ["m_PublicCar"] = turn(), ["m_Track"] = turn(), ["m_PedestrianStopLine"] = new JObject { ["m_GoGroupMask"] = 0 }, ["m_PedestrianNonStopLine"] = new JObject { ["m_GoGroupMask"] = 0 } });
                return (new JObject { ["entity"] = identity(node), ["groupMaskArray"] = edges }, new JObject { ["entity"] = identity(node), ["groupMaskArray"] = new JArray(masks.Values) });
            }
            finally { OptionalModApi.Call(map, "Dispose"); }
        }
        private void ValidateSignalMask(Entity node, JObject mask, int phaseCount, bool laneMask)
        {
            string entityKey = laneMask ? "m_SubLane" : "m_Edge";
            var identity = mask[entityKey] as JObject ?? throw new ArgumentException("mask identity required");
            OptionalModApi.Keys(identity, "Index", "Version");
            Entity target = new Entity { Index = (int?)identity["Index"] ?? -1, Version = (int?)identity["Version"] ?? -1 };
            if (!EntityManager.Exists(target)) throw new ArgumentException("mask entity stale");
            bool attached = laneMask ? EntityManager.GetBuffer<SubLane>(node, true).Any(x => x.m_SubLane == target) || EntityManager.GetBuffer<ConnectedEdge>(node, true).Any(edge => EntityManager.HasBuffer<SubLane>(edge.m_Edge) && EntityManager.GetBuffer<SubLane>(edge.m_Edge, true).Any(x => x.m_SubLane == target)) : EntityManager.GetBuffer<ConnectedEdge>(node, true).Any(x => x.m_Edge == target);
            if (!attached) throw new ArgumentException("mask target not part of this junction");
            string[] turns = laneMask ? new[] { "m_Car", "m_Track" } : new[] { "m_Car", "m_PublicCar", "m_Track" };
            string[] pedestrians = laneMask ? new[] { "m_Pedestrian" } : new[] { "m_PedestrianStopLine", "m_PedestrianNonStopLine" };
            OptionalModApi.Keys(mask, new[] { entityKey, "m_Options" }.Concat(turns).Concat(pedestrians).ToArray());
            int options = (int?)mask["m_Options"] ?? -1;
            if (options < 0 || options > (laneMask ? 0 : 1)) throw new ArgumentException("unsupported mask options");
            var signalValues = new List<JObject>();
            foreach (string key in turns)
            {
                var turn = mask[key] as JObject ?? throw new ArgumentException("complete turn mask required: " + key);
                OptionalModApi.Keys(turn, "m_Left", "m_Straight", "m_Right", "m_UTurn");
                foreach (string direction in new[] { "m_Left", "m_Straight", "m_Right", "m_UTurn" }) signalValues.Add(turn[direction] as JObject ?? throw new ArgumentException("complete directional mask required"));
            }
            foreach (string key in pedestrians) signalValues.Add(mask[key] as JObject ?? throw new ArgumentException("complete pedestrian mask required"));
            foreach (var signal in signalValues)
            {
                OptionalModApi.Keys(signal, "m_GoGroupMask");
                int bits = (int?)signal["m_GoGroupMask"] ?? -1;
                if (bits < 0 || bits >= (1 << phaseCount)) throw new ArgumentException("mask references missing phases");
            }
        }
    }
}
