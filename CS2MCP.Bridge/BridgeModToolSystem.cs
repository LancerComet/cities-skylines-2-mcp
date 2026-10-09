using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace CS2MCP
{
    /// <summary>Network Tools原生预览/应用会话。坐标与选择走模组工具API，无输入模拟；错误校验始终启用。</summary>
    public sealed class BridgeModToolSystem : GameSystemBase
    {
        public static BridgeModToolSystem Instance { get; private set; }
        private static readonly Dictionary<string, string> ToolTypes = new Dictionary<string, string>
        {
            ["shape"] = "NetworkTools.Systems.Tools.RoadShape.NT_RoadShapeToolSystem",
            ["connect"] = "NetworkTools.Systems.Tools.Connect.NT_ConnectToolSystem",
            ["parallel"] = "NetworkTools.Systems.Tools.Parallel.NT_ParallelToolSystem",
            ["generate"] = "NetworkTools.Systems.Tools.Generate.NT_GenerateToolSystem",
            ["addNode"] = "NetworkTools.Systems.Tools.NT_AddNodeToolSystem",
            ["removeNode"] = "NetworkTools.Systems.Tools.NT_RemoveNodeToolSystem",
            ["slideNode"] = "NetworkTools.Systems.Tools.NT_SlideNodeToolSystem"
        };
        private ToolSystem m_ToolSystem;
        private ToolBaseSystem m_Tool, m_PreviousTool;
        private string m_Id, m_State = "idle", m_Error;
        private JObject m_Data;
        private PrefabBase m_Prefab;
        private Dictionary<string, PrefabBase> m_ParameterPrefabs;
        private int m_Frame;
        private DateTime m_Deadline;
        private bool m_ApplyRequested;
        private Dictionary<string, bool> m_PreviousFlags;
        private Dictionary<Entity, Game.Net.Curve> m_Before;
        private object m_Result;
        private string m_TrafficAction;
        private Entity m_TrafficNode;
        private Entity[] m_TrafficHandles;
        private string m_PriorityMode;
        private bool NodeTool => m_Data != null && new[] { "addNode", "removeNode", "slideNode" }.Contains((string)m_Data["tool"]);
        internal BridgeResponse StartTraffic(string action, Entity node)
        {
            if (Busy || m_ToolSystem.ignoreErrors) return BridgeResponse.Error(409, "another construction operation is in progress or ignoreErrors is enabled");
            m_Tool = (ToolBaseSystem)OptionalModApi.System(World, "Traffic", action == "resetPriorities" || action == "preparePriority" ? "Traffic.Tools.PriorityToolSystem" : "Traffic.Tools.LaneConnectorToolSystem");
            m_PreviousTool = m_ToolSystem.activeTool; m_PreviousFlags = null;
            m_TrafficAction = action; m_TrafficNode = node;
            m_TrafficHandles = null; m_PriorityMode = null;
            m_Id = Guid.NewGuid().ToString("N"); m_State = "trafficStarting"; m_Error = null; m_Result = null; m_ApplyRequested = false;
            m_Frame = UnityEngine.Time.frameCount; m_Deadline = DateTime.UtcNow.AddSeconds(30);
            m_ToolSystem.activeTool = m_Tool;
            return BridgeResponse.Json(new { operationId = m_Id, state = m_State, mayHaveApplied = false });
        }
        public bool Busy => m_Tool != null;

        [Preserve] protected override void OnCreate()
        {
            base.OnCreate(); Instance = this; m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
        }
        [Preserve] protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            if (Busy) Finish("interrupted", "save reloaded; discard old operation and entity identities");
            m_Data = null; m_Before = null; m_Result = null;
            m_Prefab = null; m_ParameterPrefabs = null; m_TrafficHandles = null;
        }
        internal string[] PrefabParameterKeys(JObject data)
        {
            if (!(data["parameters"] is JObject values)) return Array.Empty<string>();
            string toolName = (string)data["tool"];
            if (toolName == null || !ToolTypes.TryGetValue(toolName, out var type)) throw new ArgumentException("unknown Network Tools tool");
            var tool = OptionalModApi.System(World, "NetworkTools", type);
            var parameters = (IDictionary)OptionalModApi.Read(tool, "ParametersByKey");
            return values.Properties().Where(p => parameters.Contains(p.Name) && parameters[p.Name].GetType().Name == "NetPrefabParameter").Select(p => p.Name).ToArray();
        }
        internal BridgeResponse Handle(string action, JObject data, EntityManager manager, PrefabBase prefab = null, Dictionary<string, PrefabBase> parameterPrefabs = null)
        {
            if (action == "network.prepare")
            {
                OptionalModApi.Keys(data, "tool", "nodes", "points", "parameters", "prefab", "entity", "t", "position");
                if (Busy) return BridgeResponse.Error(409, "another construction operation is in progress");
                string key = (string)data["tool"];
                if (key == null || !ToolTypes.ContainsKey(key)) return BridgeResponse.Error(400, "unknown Network Tools tool");
                bool nodeTool = new[] { "addNode", "removeNode", "slideNode" }.Contains(key);
                var nodes = data["nodes"] as JArray;
                if (key != "generate" && !nodeTool && (nodes == null || nodes.Count < 2 || nodes.Count > 24 || key == "connect" && nodes.Count != 2))
                    return BridgeResponse.Error(400, "nodes requires 2..24 exact identities; connect requires two");
                if (nodes != null) foreach (var token in nodes)
                {
                    Entity e = Identity(token);
                    if (!manager.Exists(e) || !manager.HasComponent<Node>(e) || manager.HasComponent<Temp>(e) || manager.HasComponent<Deleted>(e))
                        return BridgeResponse.Error(400, "network node identity stale");
                }
                if (key == "generate")
                {
                    if (!(data["points"] is JArray points) || points.Count != 2) return BridgeResponse.Error(400, "generate needs exactly two world-coordinate points");
                    foreach (var p in points) Point(p);
                }
                if (nodeTool)
                {
                    var entity = Identity(data["entity"]);
                    if (!manager.Exists(entity) || manager.HasComponent<Temp>(entity) || manager.HasComponent<Deleted>(entity)
                        || !(key == "addNode" ? manager.HasComponent<Edge>(entity) && manager.HasComponent<Game.Net.Curve>(entity) : manager.HasComponent<Node>(entity)))
                        throw new ArgumentException("live road edge/node required");
                    if (key == "addNode") OptionalModApi.Number(data["t"], 0.01f, 0.99f, "t");
                    else if (!manager.HasBuffer<ConnectedEdge>(entity) || manager.GetBuffer<ConnectedEdge>(entity, true).Length != 2)
                        throw new ArgumentException("remove/slide requires exactly two connected edges");
                    if (key == "slideNode") Point(data["position"]);
                }
                m_Tool = (ToolBaseSystem)OptionalModApi.System(World, "NetworkTools", ToolTypes[key]);
                m_Id = Guid.NewGuid().ToString("N"); m_State = "starting"; m_Error = null; m_Result = null;
                m_Data = (JObject)data.DeepClone(); m_Prefab = prefab; m_ParameterPrefabs = parameterPrefabs; m_ApplyRequested = false;
                m_TrafficAction = null;
                m_PreviousFlags = new Dictionary<string, bool>();
                foreach (var f in new[] { "DisableVanillaValidation", "DisableVanillaCourseSplit", "DisableVanillaNodeReduction" })
                {
                    m_PreviousFlags[f] = (bool)OptionalModApi.Read(m_Tool, f);
                    OptionalModApi.Write(m_Tool, f, false);
                }
                m_PreviousTool = m_ToolSystem.activeTool;
                m_Before = Roads(); m_Deadline = DateTime.UtcNow.AddSeconds(60); m_Frame = UnityEngine.Time.frameCount;
                OptionalModApi.Call(m_Tool, "RequestEnable");
                return BridgeResponse.Json(new { operationId = m_Id, state = m_State, applied = false });
            }
            OptionalModApi.Keys(data, "operationId");
            if ((string)data["operationId"] != m_Id || !Busy || m_TrafficAction != null) return BridgeResponse.Error(409, "operation identity expired or belongs to another tool");
            if (action == "network.cancel")
            {
                Finish("cancelled", null); return BridgeResponse.Json(Snapshot());
            }
            if (action == "network.apply")
            {
                if (m_State != "previewReady" || !CanApply()) return BridgeResponse.Error(409, "native preview not ready or validation rejected the operation");
                m_ApplyRequested = true; m_State = NodeTool ? "applyQueued" : "applying"; m_Frame = UnityEngine.Time.frameCount; m_Deadline = DateTime.UtcNow.AddSeconds(30);
                if (!NodeTool) OptionalModApi.Call(m_Tool, "RequestApply");
                return BridgeResponse.Json(new { operationId = m_Id, state = m_State, mayHaveApplied = true });
            }
            return BridgeResponse.Error(400, "unknown network action");
        }

        private static Entity Identity(JToken token)
        {
            if (!(token is JObject data)) throw new ArgumentException("entity must contain index/version");
            OptionalModApi.Keys(data, "index", "version");
            if (data["index"]?.Type != JTokenType.Integer || data["version"]?.Type != JTokenType.Integer || (int)data["index"] <= 0 || (int)data["version"] <= 0) throw new ArgumentException("positive integer identity required");
            return new Entity { Index = (int?)data["index"] ?? -1, Version = (int?)data["version"] ?? -1 };
        }
        private static float3 Point(JToken token)
        {
            if (!(token is JObject data)) throw new ArgumentException("point must contain x,y,z world coordinates");
            OptionalModApi.Keys(data, "x", "y", "z");
            return new float3(OptionalModApi.Number(data["x"], -7168, 7168, "x"), OptionalModApi.Number(data["y"], -100, 3000, "y"), OptionalModApi.Number(data["z"], -7168, 7168, "z"));
        }
        private void SetParameters()
        {
            string toolKey = (string)m_Data["tool"];
            // TrySetPrefab选择的是NT工具按钮资产；建造网络必须走SetNetPrefab参数入口。
            if (m_Prefab != null && !(bool)OptionalModApi.Call(m_Tool, "SetNetPrefab", toolKey + ".netPrefab", m_Prefab)) throw new InvalidOperationException("Network Tools rejected network prefab");
            if (toolKey == "generate" && m_Data["parameters"]?["generate.mode"] is JToken modeToken)
            {
                var modes = (IDictionary)OptionalModApi.Read(m_Tool, "ParametersByKey");
                var parameter = modes["generate.mode"];
                var property = parameter.GetType().GetProperty("Value");
                if (modeToken.Type != JTokenType.String || !Enum.GetNames(property.PropertyType).Contains((string)modeToken)) throw new ArgumentException("unknown generate mode");
                property.SetValue(parameter, Enum.Parse(property.PropertyType, (string)modeToken));
            }
            if ((string)m_Data["tool"] == "generate")
                foreach (var p in (JArray)m_Data["points"])
                {
                    var position = Point(p);
                    OptionalModApi.Call(m_Tool, "PushControlPoint", new ControlPoint { m_Position = position, m_HitPosition = position });
                }
            else if (!NodeTool) foreach (var token in (JArray)m_Data["nodes"])
            {
                var e = Identity(token);
                if ((string)m_Data["tool"] != "connect") OptionalModApi.Call(m_Tool, "HandlePathUpdate", new ControlPoint { m_OriginalEntity = e });
                if (!(bool)OptionalModApi.Call(m_Tool, "HandleAddNode", e)) throw new InvalidOperationException("Network Tools rejected node selection");
            }
            var parameters = (IDictionary)OptionalModApi.Read(m_Tool, "ParametersByKey");
            if (m_Data["parameters"] is JObject values)
                foreach (var p in values.Properties())
                {
                    if (!parameters.Contains(p.Name)) throw new ArgumentException("unknown parameter: " + p.Name);
                    var parameter = parameters[p.Name];
                    if (parameter.GetType().Name == "NetPrefabParameter")
                    {
                        if (m_ParameterPrefabs == null || !m_ParameterPrefabs.TryGetValue(p.Name, out var selected) || !(bool)OptionalModApi.Call(m_Tool, "SetNetPrefab", p.Name, selected)) throw new ArgumentException("network prefab parameter rejected: " + p.Name);
                        continue;
                    }
                    var property = parameter.GetType().GetProperty("Value");
                    if (property == null) throw new ArgumentException("parameter not a writable value: " + p.Name);
                    var type = property.PropertyType;
                    object value;
                    if (type.IsEnum)
                    {
                        if (p.Value.Type != JTokenType.String || !Enum.IsDefined(type, (string)p.Value)) throw new ArgumentException("unknown enum value for " + p.Name);
                        value = Enum.Parse(type, (string)p.Value);
                    }
                    else if (type == typeof(float)) value = OptionalModApi.Number(p.Value, (float)OptionalModApi.Read(parameter, "Min"), (float)OptionalModApi.Read(parameter, "Max"), p.Name);
                    else if (type == typeof(int))
                    {
                        var n = OptionalModApi.Number(p.Value, Convert.ToSingle(OptionalModApi.Read(parameter, "Min")), Convert.ToSingle(OptionalModApi.Read(parameter, "Max")), p.Name);
                        if (n != Math.Truncate(n)) throw new ArgumentException("integer parameter required"); value = (int)n;
                    }
                    else if (type == typeof(bool)) { if (p.Value.Type != JTokenType.Boolean) throw new ArgumentException("boolean required"); value = (bool)p.Value; }
                    else if (type == typeof(float3)) value = Point(p.Value);
                    else throw new ArgumentException("unsupported parameter type: " + type.Name);
                    property.SetValue(parameter, value);
                }
            OptionalModApi.Write(m_Tool, "m_UpdateNeeded", true);
            if (NodeTool) NodePreview(true);
        }

        private bool CanApply() => m_Tool != null && (NodeTool ? PreviewEdgeCount() > 0 : (bool)OptionalModApi.Read(m_Tool, "CanApply")) && (bool)OptionalModApi.Call(m_Tool, "GetAllowApply")
            && !m_ToolSystem.ignoreErrors && World.GetExistingSystemManaged<Game.Tools.ValidationSystem>().Enabled;

        private int PreviewEdgeCount()
        {
            var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Game.Net.Curve>());
            try { return query.CalculateEntityCount(); } finally { query.Dispose(); }
        }
        private void NodePreview(bool initial)
        {
            string key = (string)m_Data["tool"]; var entity = Identity(m_Data["entity"]);
            if (initial && !(bool)OptionalModApi.Call(m_Tool, "FilterEligibleEntity", entity)) throw new ArgumentException("Network Tools native eligibility rejected this entity");
            if (key == "addNode")
            {
                float t = OptionalModApi.Number(m_Data["t"], 0.01f, 0.99f, "t");
                var position = Colossal.Mathematics.MathUtils.Position(EntityManager.GetComponentData<Game.Net.Curve>(entity).m_Bezier, t);
                var point = new ControlPoint { m_OriginalEntity = entity, m_CurvePosition = t, m_Position = position, m_HitPosition = position };
                point = (ControlPoint)OptionalModApi.Call(m_Tool, "SnapControlPoint", point, default(Unity.Jobs.JobHandle));
                OptionalModApi.Call(m_Tool, "HandleHover", point);
            }
            else if (key == "removeNode")
            {
                OptionalModApi.Call(m_Tool, "HandleHover", new ControlPoint { m_OriginalEntity = entity });
                var reference = (NativeReference<Entity>)OptionalModApi.Read(m_Tool, "m_LastHoveredEntity"); reference.Value = entity;
            }
            else
            {
                if (initial) OptionalModApi.Call(m_Tool, "StartDrag", entity);
                OptionalModApi.Call(m_Tool, "SnapControlPoint", Point(m_Data["position"]), default(Unity.Jobs.JobHandle));
            }
            OptionalModApi.Write(m_Tool, "m_UpdateNeeded", true);
            ((Unity.Jobs.JobHandle)OptionalModApi.Call(m_Tool, "Update", default(Unity.Jobs.JobHandle))).Complete();
        }

        [Preserve] protected override void OnUpdate()
        {
            if (!Busy) return;
            try
            {
                if (m_ToolSystem.activeTool != m_Tool) { Finish("interrupted", "active tool changed; inspect before retrying"); return; }
                if (DateTime.UtcNow > m_Deadline) { Finish("expired", "operation expired; inspect before retrying"); return; }
                if (m_TrafficAction != null) { UpdateTraffic(); return; }
                OptionalModApi.Write(m_Tool, "AnarchyEnabled", false);
                OptionalModApi.Write(m_Tool, "DisableVanillaValidation", false);
                World.GetExistingSystemManaged<Game.Tools.ValidationSystem>().Enabled = true;
                if (m_State == "applyQueued")
                {
                    if (!CanApply()) { Finish("failed", "native validation changed before apply"); return; }
                    ((Unity.Jobs.JobHandle)OptionalModApi.Call(m_Tool, "Apply", default(Unity.Jobs.JobHandle))).Complete();
                    m_State = "applying"; m_Frame = UnityEngine.Time.frameCount;
                }
                if (NodeTool && (m_State == "previewing" || m_State == "previewReady")) NodePreview(false);
                if (m_State == "starting" && UnityEngine.Time.frameCount > m_Frame + 2)
                {
                    SetParameters(); m_State = "previewing"; m_Frame = UnityEngine.Time.frameCount;
                }
                else if (m_State == "previewing" && UnityEngine.Time.frameCount > m_Frame + 10)
                {
                    m_Result = Preview(); m_State = "previewReady";
                }
                else if (m_State == "applying" && UnityEngine.Time.frameCount > m_Frame + 15)
                {
                    var after = Roads();
                    var changed = after.Where(p => !m_Before.TryGetValue(p.Key, out var prior) || !prior.m_Bezier.Equals(p.Value.m_Bezier))
                        .Select(p => new { entity = OptionalModApi.JsonValue(p.Key), curve = OptionalModApi.JsonValue(p.Value.m_Bezier), length = p.Value.m_Length }).ToArray();
                    var removed = m_Before.Keys.Where(e => !after.ContainsKey(e)).Select(e => OptionalModApi.JsonValue(e)).ToArray();
                    m_Result = new { changedCount = changed.Length, changed = changed.Take(256).ToArray(), complete = changed.Length <= 256, removed,
                        nativeApplyRequested = true, geometryChanged = changed.Length > 0 || removed.Length > 0,
                        note = "原生工具已结束；须回读车道、Traffic设置、高程/净空及实际运营。零变化不能称施工成功" };
                    Finish("finished", null);
                }
            }
            catch (Exception e) { Finish("failed", e.GetBaseException().Message); }
        }
        private object Preview()
        {
            var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Game.Net.Curve>());
            using (var entities = query.ToEntityArray(Allocator.Temp))
            {
                var curves = entities.Take(256).Select(e => new { entity = OptionalModApi.JsonValue(e), temp = OptionalModApi.JsonValue(EntityManager.GetComponentData<Temp>(e)), curve = OptionalModApi.JsonValue(EntityManager.GetComponentData<Game.Net.Curve>(e).m_Bezier) }).ToArray();
                query.Dispose();
                return new { canApply = CanApply(), phase = OptionalModApi.Read(m_Tool, "Phase").ToString(), validationEnabled = true, ignoreErrors = m_ToolSystem.ignoreErrors, previewCount = entities.Length, complete = entities.Length <= 256, curves };
            }
        }
        private void UpdateTraffic()
        {
            if (m_ToolSystem.ignoreErrors) { Finish("failed", "ignoreErrors is enabled; application blocked"); return; }
            if (m_State == "trafficApplyQueued")
            {
                if (!TrafficCanApply()) { Finish("failed", "native Traffic validation changed before apply"); return; }
                TrafficDefinitions(true); m_State = "trafficApplying"; m_Frame = UnityEngine.Time.frameCount; return;
            }
            if (m_State == "trafficStarting" && UnityEngine.Time.frameCount > m_Frame + 2)
            {
                var handle = (Unity.Jobs.JobHandle)OptionalModApi.Call(m_Tool, "SelectIntersectionNode", default(Unity.Jobs.JobHandle), m_TrafficNode);
                handle.Complete();
                var field = OptionalModApi.Field(m_Tool.GetType(), "_state");
                field.SetValue(m_Tool, Enum.Parse(field.FieldType, m_TrafficAction == "resetPriorities" || m_TrafficAction == "preparePriority" ? "ChangingPriority" : "SelectingSourceConnector"));
                m_State = "trafficSelected"; m_Frame = UnityEngine.Time.frameCount;
            }
            else if (m_State == "trafficSelected" && UnityEngine.Time.frameCount > m_Frame + 10)
            {
                if (m_TrafficAction.StartsWith("prepare", StringComparison.Ordinal))
                {
                    m_Result = new { node = OptionalModApi.JsonValue(m_TrafficNode), handles = TrafficHandles(), note = "临时原生连接器/车道手柄；选择edge完整身份与laneIndex，下一步生成预览后明确apply" };
                    m_State = "trafficReady"; return;
                }
                var ui = OptionalModApi.System(World, "Traffic", "Traffic.UISystems.ModUISystem");
                var method = ui.GetType().GetMethod("SetActionOverlayPreviewState");
                var type = method.GetParameters()[0].ParameterType;
                string overlay = m_TrafficAction == "removeUnsafe" ? "RemoveUnsafe" : m_TrafficAction == "removeUTurns" ? "RemoveUTurns" : "ResetToVanilla";
                OptionalModApi.Call(ui, "SetActionOverlayPreviewState", Enum.Parse(type, overlay));
                OptionalModApi.Call(ui, "ApplyActionOverlayPreview");
                m_ApplyRequested = true; m_State = "trafficApplying"; m_Frame = UnityEngine.Time.frameCount;
            }
            else if (m_State == "trafficPreviewing" || m_State == "trafficPreviewReady")
            {
                TrafficDefinitions(false);
                if (UnityEngine.Time.frameCount > m_Frame + 10)
                {
                    m_Result = new { canApply = TrafficCanApply(), node = OptionalModApi.JsonValue(m_TrafficNode), handles = m_TrafficHandles.Select(e => OptionalModApi.JsonValue(e)), tooltip = TrafficTooltip(),
                        note = "原生Traffic临时定义与反馈校验；连接为原生toggle语义，预览显示Add/Remove后再提交，不盲目重试" };
                    m_State = "trafficPreviewReady";
                }
            }
            else if (m_State == "trafficApplying" && UnityEngine.Time.frameCount > m_Frame + 30)
            {
                m_Result = new { action = m_TrafficAction, node = OptionalModApi.JsonValue(m_TrafficNode), nativeApplyRequested = true, note = "Traffic原生双阶段应用已请求；回读连接/优先级确认结果，不能把请求当成车流验收" };
                Finish("finished", null);
            }
        }
        internal BridgeResponse HandleTraffic(string action, JObject data)
        {
            OptionalModApi.Keys(data, "operationId", "source", "target", "lane", "mode");
            if (!Busy || m_TrafficAction == null || (string)data["operationId"] != m_Id) return BridgeResponse.Error(409, "operation identity expired or no longer active");
            if (action == "cancel") { Finish("cancelled", null); return BridgeResponse.Json(Snapshot()); }
            if (action == "apply")
            {
                if (m_State != "trafficPreviewReady" || !TrafficCanApply()) return BridgeResponse.Error(409, "native Traffic preview not ready or rejected");
                m_ApplyRequested = true; m_State = "trafficApplyQueued"; m_Frame = UnityEngine.Time.frameCount;
                return BridgeResponse.Json(new { operationId = m_Id, state = m_State, mayHaveApplied = true });
            }
            if (m_State != "trafficReady" && m_State != "trafficPreviewReady") return BridgeResponse.Error(409, "wait for Traffic handles before choosing lanes");
            if (action == "previewPriority" && m_TrafficAction == "preparePriority")
            {
                if (!new[] { "Yield", "Stop", "Priority", "Reset" }.Contains((string)data["mode"])) throw new ArgumentException("priority mode must be Yield/Stop/Priority/Reset");
                m_TrafficHandles = new[] { FindTrafficHandle(data["lane"], false) }; m_PriorityMode = (string)data["mode"];
            }
            else if (action == "previewConnection" && m_TrafficAction == "prepareConnection")
            {
                m_TrafficHandles = new[] { FindTrafficHandle(data["source"], true), FindTrafficHandle(data["target"], true) };
                var type = TrafficHandleType();
                var source = OptionalModApi.Component(EntityManager, m_TrafficHandles[0], type); var target = OptionalModApi.Component(EntityManager, m_TrafficHandles[1], type);
                if (OptionalModApi.Read(source, "connectorType").ToString() != "Source" || OptionalModApi.Read(target, "connectorType").ToString() != "Target") throw new ArgumentException("source/target connector roles invalid");
                if ((Convert.ToInt32(OptionalModApi.Read(source, "vehicleGroup")) & Convert.ToInt32(OptionalModApi.Read(target, "vehicleGroup"))) == 0) throw new ArgumentException("vehicle groups incompatible");
            }
            else return BridgeResponse.Error(400, "action does not match the prepared tool");
            m_State = "trafficPreviewing"; m_Frame = UnityEngine.Time.frameCount; m_Deadline = DateTime.UtcNow.AddSeconds(60);
            return BridgeResponse.Json(new { operationId = m_Id, state = m_State, applied = false });
        }
        private Type TrafficHandleType() => OptionalModApi.Assembly("Traffic").GetType(m_TrafficAction == "preparePriority" ? "Traffic.Components.PrioritySigns.LaneHandle" : "Traffic.Components.LaneConnections.Connector", true);
        private object[] TrafficHandles()
        {
            var type = TrafficHandleType(); var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly(type));
            try { using (var entities = query.ToEntityArray(Allocator.Temp)) return entities.Where(e => (Entity)OptionalModApi.Read(OptionalModApi.Component(EntityManager, e, type), "node") == m_TrafficNode)
                .Take(128).Select(e => (object)new { entity = OptionalModApi.JsonValue(e), data = OptionalModApi.JsonValue(OptionalModApi.Component(EntityManager, e, type)) }).ToArray(); }
            finally { query.Dispose(); }
        }
        private Entity FindTrafficHandle(JToken token, bool connector)
        {
            if (!(token is JObject data)) throw new ArgumentException("lane selector requires edge and laneIndex"); OptionalModApi.Keys(data, "edge", "laneIndex");
            var edge = Identity(data["edge"]);
            if (data["laneIndex"]?.Type != JTokenType.Integer) throw new ArgumentException("integer laneIndex required");
            int lane = (int?)data["laneIndex"] ?? -1;
            if (!EntityManager.Exists(edge) || lane < 0 || lane > 255) throw new ArgumentException("edge/lane identity invalid");
            var type = TrafficHandleType(); var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly(type));
            try
            {
                using (var entities = query.ToEntityArray(Allocator.Temp)) foreach (var e in entities)
                {
                    var value = OptionalModApi.Component(EntityManager, e, type);
                    var rawLane = OptionalModApi.Read(value, "laneIndex"); int index = connector ? (int)rawLane : ((int3)rawLane).x;
                    if ((Entity)OptionalModApi.Read(value, "node") == m_TrafficNode && (Entity)OptionalModApi.Read(value, "edge") == edge && index == lane)
                        return e;
                }
            }
            finally { query.Dispose(); }
            throw new ArgumentException("native lane handle unavailable; read current handles, not nominal road lane count");
        }
        private bool TrafficCanApply()
        {
            var query = (EntityQuery)OptionalModApi.Read(m_Tool, m_TrafficAction == "preparePriority" ? "_tempPrioritiesQuery" : "_tempConnectionQuery");
            return !m_ToolSystem.ignoreErrors && (bool)OptionalModApi.Call(m_Tool, "IsApplyAllowed", false) && !query.IsEmptyIgnoreFilter;
        }
        private object TrafficTooltip()
        {
            if (m_TrafficAction != "prepareConnection") return m_PriorityMode;
            return OptionalModApi.Read(m_Tool, "tooltip").ToString();
        }
        private void TrafficDefinitions(bool apply)
        {
            var points = (NativeList<ControlPoint>)OptionalModApi.Read(m_Tool, "_controlPoints"); points.Clear();
            foreach (var e in m_TrafficHandles) points.Add(new ControlPoint { m_OriginalEntity = e });
            var field = OptionalModApi.Field(m_Tool.GetType(), "_state"); field.SetValue(m_Tool, Enum.Parse(field.FieldType, m_TrafficAction == "preparePriority" ? "ChangingPriority" : "SelectingTargetConnector"));
            if (m_TrafficAction == "prepareConnection")
            {
                var modifiers = OptionalModApi.Field(m_Tool.GetType(), "_stateModifiers"); modifiers.SetValue(m_Tool, Enum.Parse(modifiers.FieldType, "AnyConnector"));
            }
            if (m_PriorityMode != null)
            {
                var mode = OptionalModApi.Field(m_Tool.GetType(), "_toolSetMode"); mode.SetValue(m_Tool, Enum.Parse(mode.FieldType, m_PriorityMode));
                var overlay = OptionalModApi.Field(m_Tool.GetType(), "_overlayMode"); overlay.SetValue(m_Tool, Enum.Parse(overlay.FieldType, "Lane"));
            }
            OptionalModApi.Write(m_Tool, "applyMode", apply ? ApplyMode.Apply : ApplyMode.Clear);
            var handle = (Unity.Jobs.JobHandle)OptionalModApi.Call(m_Tool, "UpdateDefinitions", default(Unity.Jobs.JobHandle), apply);
            handle.Complete();
        }
        private Dictionary<Entity, Game.Net.Curve> Roads()
        {
            var query = EntityManager.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Game.Net.Curve>() }, None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() } });
            var result = new Dictionary<Entity, Game.Net.Curve>();
            using (var entities = query.ToEntityArray(Allocator.Temp)) foreach (var e in entities) result[e] = EntityManager.GetComponentData<Game.Net.Curve>(e);
            query.Dispose(); return result;
        }
        private void Finish(string state, string error)
        {
            var tool = m_Tool;
            try
            {
                if (tool != null && m_ToolSystem.activeTool == tool) m_ToolSystem.activeTool = m_PreviousTool;
                if (tool != null && m_PreviousFlags != null) foreach (var p in m_PreviousFlags) OptionalModApi.Write(tool, p.Key, p.Value);
                World.GetExistingSystemManaged<Game.Tools.ValidationSystem>().Enabled = true;
            }
            finally { m_Tool = null; m_State = state; m_Error = error; }
        }
        internal object Snapshot()
        {
            var tools = new List<object>();
            foreach (var item in ToolTypes)
            {
                try
                {
                    var tool = OptionalModApi.System(World, "NetworkTools", item.Value);
                    var parameters = ((IEnumerable)OptionalModApi.Read(tool, "Parameters")).Cast<object>().Select(p =>
                    {
                        var type = p.GetType(); var valueProperty = type.GetProperty("Value");
                        object range = type.GetProperty("Min") == null ? null : new { min = OptionalModApi.Read(p, "Min"), max = OptionalModApi.Read(p, "Max") };
                        return new { key = OptionalModApi.Read(p, "Key"), type = valueProperty?.PropertyType.Name ?? type.Name,
                            value = valueProperty == null ? (type.Name == "NetPrefabParameter" ? OptionalModApi.JsonValue(OptionalModApi.Read(p, "Prefab")) : null) : OptionalModApi.JsonValue(valueProperty.GetValue(p)), range,
                            enumValues = valueProperty?.PropertyType.IsEnum == true ? Enum.GetNames(valueProperty.PropertyType) : null };
                    }).ToArray();
                    tools.Add(new { tool = item.Key, available = true, parameters });
                }
                catch (Exception e) { tools.Add(new { tool = item.Key, available = false, error = e.GetBaseException().Message }); }
            }
            return new { operationId = m_Id, state = m_State, busy = Busy, mayHaveApplied = m_ApplyRequested, error = m_Error, result = m_Result, tools };
        }
        [Preserve] protected override void OnDestroy()
        {
            try { if (Busy) Finish("interrupted", "world disposed"); }
            finally { if (Instance == this) Instance = null; base.OnDestroy(); }
        }
    }
}
