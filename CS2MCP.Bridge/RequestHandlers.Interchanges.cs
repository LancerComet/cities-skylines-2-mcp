using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Newtonsoft.Json.Linq;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private EntityQuery m_AssetStampPrefabQuery;
        private bool m_AssetStampPrefabQueryCreated;
        private EntityQuery AssetStampPrefabQuery
        {
            get
            {
                if (!m_AssetStampPrefabQueryCreated)
                {
                    m_AssetStampPrefabQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<AssetStampData>());
                    m_AssetStampPrefabQueryCreated = true;
                }
                return m_AssetStampPrefabQuery;
            }
        }

        private static object InterchangePoint(float3 p) => new { x = p.x, y = p.y, z = p.z };

        /// <summary>读取原生复合资产的道路定义，保留节点索引、曲线、吸附和升级，供逐段复刻与运动分析。</summary>
        private BridgeResponse GetInterchangeInfo(BridgeRequest request)
        {
            if (!TryGetCity(out _, out var error)) return error;
            if (!request.Query.TryGetValue("name", out var name)
                || !TryFindPrefabByName(AssetStampPrefabQuery, name, out var entity, out var prefab))
                return BridgeResponse.Error(404, "未找到该原生复合资产，请搜索category=asset");
            int offset = request.TryGetInt("offset", out var rawOffset) ? math.clamp(rawOffset, 0, 100000) : 0;
            int limit = request.TryGetInt("limit", out var rawLimit) ? math.clamp(rawLimit, 1, 250) : 100;
            var prefabSystem = World.GetExistingSystemManaged<PrefabSystem>();
            var parts = new List<object>(); int total = 0;
            var boundsMin = new float3(float.PositiveInfinity);
            var boundsMax = new float3(float.NegativeInfinity);
            if (EntityManager.HasBuffer<Game.Prefabs.SubNet>(entity))
            {
                var subNets = EntityManager.GetBuffer<Game.Prefabs.SubNet>(entity, true);
                total = subNets.Length;
                for (int i = 0; i < total; i++)
                {
                    var n = subNets[i];
                    for (int j = 0; j <= 32; j++)
                    {
                        var p = MathUtils.Position(n.m_Curve, j / 32f);
                        boundsMin = math.min(boundsMin, p); boundsMax = math.max(boundsMax, p);
                    }
                    if (i < offset || parts.Count >= limit) continue;
                    var network = prefabSystem.GetPrefab<PrefabBase>(n.m_Prefab);
                    float? width = EntityManager.HasComponent<NetGeometryData>(n.m_Prefab)
                        ? EntityManager.GetComponentData<NetGeometryData>(n.m_Prefab).m_DefaultWidth : (float?)null;
                    parts.Add(new { segment = i, prefab = network?.name, type = network?.GetType().Name, width,
                        curve = new { a = InterchangePoint(n.m_Curve.a), b = InterchangePoint(n.m_Curve.b),
                            c = InterchangePoint(n.m_Curve.c), d = InterchangePoint(n.m_Curve.d) },
                        nodes = new { start = n.m_NodeIndex.x, end = n.m_NodeIndex.y },
                        parentMesh = new { start = n.m_ParentMesh.x, end = n.m_ParentMesh.y },
                        snap = new { start = n.m_Snapping.x, end = n.m_Snapping.y },
                        invertMode = n.m_InvertMode.ToString(),
                        upgrades = new { general = n.m_Upgrades.m_General.ToString(), left = n.m_Upgrades.m_Left.ToString(), right = n.m_Upgrades.m_Right.ToString() },
                        length = MathUtils.Length(n.m_Curve) });
                }
            }
            var stamp = EntityManager.GetComponentData<AssetStampData>(entity);
            var placeable = EntityManager.GetComponentData<PlaceableObjectData>(entity);
            string localized = null;
            Game.SceneFlow.GameManager.instance.localizationManager.activeDictionary?.TryGetValue("Assets.NAME[" + prefab.name + "]", out localized);
            return BridgeResponse.Json(new { name = prefab.name, localizedName = localized, type = prefab.GetType().Name,
                locked = IsLocked(entity), stalenessWarning = LockStalenessWarning,
                constructionCost = placeable.m_ConstructionCost, upkeepCost = stamp.m_UpKeepCost,
                sizeCells = new { x = stamp.m_Size.x, z = stamp.m_Size.y },
                bounds = total == 0 ? null : new { min = InterchangePoint(boundsMin), max = InterchangePoint(boundsMax) },
                totalSegments = total, offset, returned = parts.Count, complete = offset + parts.Count >= total, segments = parts,
                note = "局部坐标和原生节点索引；世界放置需旋转、平移及原生地形适配。曲线道路不等于实际车道连接，施工后仍须逐页检查与运营验收。" });
        }

        /// <summary>原生AssetStamp复合资产放置，使用相同ObjectTool定义、预览、Validation、收费和生成流程。</summary>
        private BridgeResponse PlaceInterchange(BridgeRequest request)
        {
            if (!TryGetCity(out _, out var error)) return error;
            if (!request.Query.TryGetValue("prefab", out var name)
                || !TryFindPrefabByName(AssetStampPrefabQuery, name, out var entity, out var prefab)
                || !(prefab is AssetStampPrefab)) return BridgeResponse.Error(404, "不是可放置的原生AssetStamp资产");
            if (IsLocked(entity)) return BridgeResponse.Error(409, "复合资产尚未正常解锁或其内容不可用");
            if (!request.TryGetFloat("x", out var x) || !request.TryGetFloat("z", out var z)
                || !math.isfinite(x) || !math.isfinite(z) || math.abs(x) > 7168 || math.abs(z) > 7168)
                return BridgeResponse.Error(400, "世界坐标必须有限且在地图范围内");
            request.TryGetFloat("rotation", out var rotation);
            request.TryGetFloat("elevation", out var elevation);
            if (!math.isfinite(rotation) || !math.isfinite(elevation) || elevation < -30 || elevation > 60)
                return BridgeResponse.Error(400, "旋转或相对高程不合法");
            if (!request.Query.ContainsKey("dryRun")) request.Query["dryRun"] = "true";
            if (!request.TryGetBool("dryRun", out _)) return BridgeResponse.Error(400, "dryRun必须为布尔值");
            if (TransitToolsBusy(out error)) return error;
            var terrain = World.GetExistingSystemManaged<TerrainSystem>().GetHeightData();
            var position = new float3(x, 0, z);
            position.y = TerrainUtils.SampleHeight(ref terrain, position) + elevation;
            if (!World.GetExistingSystemManaged<BridgeToolSystem>().TryQueuePlacement(entity, prefab, position,
                quaternion.RotateY(math.radians(rotation)), request)) return BridgeResponse.Error(409, "another build operation is in progress, retry shortly");
            return null;
        }

        private static float LayoutNumber(JToken token)
        {
            if (token == null || (token.Type != JTokenType.Float && token.Type != JTokenType.Integer)) return float.NaN;
            return token.Value<float>();
        }
        private static float3 LayoutPoint(JToken p) => new float3(LayoutNumber(p?["x"]), LayoutNumber(p?["y"]), LayoutNumber(p?["z"]));
        private static bool LayoutNode(JToken token, out int value)
        {
            value = -1;
            return token != null && token.Type == JTokenType.Integer && int.TryParse(token.ToString(), out value) && value >= -1 && value <= 4096;
        }

        /// <summary>接受明确的道路结构，不改源资产、不写活车道；引用正常解锁道路走原生CreateSubNet和统一Validation。</summary>
        private BridgeResponse RemoveRoadLayout(BridgeRequest request)
        {
            if (!TryGetCity(out _, out var error)) return error;
            JObject input;
            try { input = JObject.Parse(request.Body ?? ""); }
            catch { return BridgeResponse.Error(400, "须POST合法道路身份JSON"); }
            foreach (var property in input.Properties())
                if (property.Name != "roads" && property.Name != "dryRun") return BridgeResponse.Error(400, "批量拆路只接受roads和dryRun");
            var rows = input["roads"] as JArray;
            if (rows == null || rows.Count < 1 || rows.Count > 250) return BridgeResponse.Error(400, "须1至250条独立道路");
            if (input["dryRun"] != null && input["dryRun"].Type != JTokenType.Boolean) return BridgeResponse.Error(400, "dryRun须布尔值");
            request.Query["dryRun"] = (bool?)input["dryRun"] == false ? "false" : "true";
            var targets = new Entity[rows.Count]; var seen = new HashSet<Entity>();
            var prefabs = World.GetExistingSystemManaged<PrefabSystem>();
            for (int i = 0; i < rows.Count; i++) {
                var row = rows[i] as JObject;
                if (row == null || row.Count != 2 || row["index"]?.Type != JTokenType.Integer || row["version"]?.Type != JTokenType.Integer
                    || !int.TryParse(row["index"].ToString(), out int index) || !int.TryParse(row["version"].ToString(), out int version)
                    || index <= 0 || version <= 0) return BridgeResponse.Error(400, $"第{i}条必须提供完整有效实体身份");
                var target = new Entity { Index = index, Version = version };
                if (!EntityManager.Exists(target) || EntityManager.HasComponent<Game.Common.Deleted>(target)) return BridgeResponse.Error(404, $"第{i}条实体已过期，整批未排队");
                if (!seen.Add(target)) return BridgeResponse.Error(400, "重复实体，整批未排队");
                if (!EntityManager.HasComponent<Game.Net.Edge>(target) || EntityManager.HasComponent<Game.Common.Owner>(target)
                    || !EntityManager.HasComponent<PrefabRef>(target)) return BridgeResponse.Error(400, "只能拆独立正常道路，不能拆建筑内部网络");
                var prefab = prefabs.GetPrefab<PrefabBase>(EntityManager.GetComponentData<PrefabRef>(target).m_Prefab);
                if (!(prefab is RoadPrefab) || prefab.name.IndexOf("Invisible", StringComparison.OrdinalIgnoreCase) >= 0)
                    return BridgeResponse.Error(400, "只能拆正常可见道路，整批未排队");
                targets[i] = target;
            }
            if (TransitToolsBusy(out error)) return error;
            if (!World.GetExistingSystemManaged<BridgeToolSystem>().TryQueueRoadRemoval(targets, request))
                return BridgeResponse.Error(409, "another build operation is in progress, retry shortly");
            return null;
        }

        private BridgeResponse BuildRoadLayout(BridgeRequest request)
        {
            if (!TryGetCity(out _, out var error)) return error;
            JObject input;
            try { input = JObject.Parse(request.Body ?? ""); }
            catch { return BridgeResponse.Error(400, "须POST合法道路布局JSON"); }
            if (input["force"] != null) return BridgeResponse.Error(400, "道路布局不支持force");
            var partsJson = input["segments"] as JArray;
            if (partsJson == null || partsJson.Count < 1 || partsJson.Count > 250) return BridgeResponse.Error(400, "布局须1至250段道路");
            float x = LayoutNumber(input["x"]), z = LayoutNumber(input["z"]);
            float rotation = input["rotation"] == null ? 0 : LayoutNumber(input["rotation"]);
            float elevation = input["elevation"] == null ? 0 : LayoutNumber(input["elevation"]);
            if (!math.isfinite(x) || !math.isfinite(z) || math.abs(x) > 7168 || math.abs(z) > 7168
                || !math.isfinite(rotation) || !math.isfinite(elevation) || elevation < -30 || elevation > 60)
                return BridgeResponse.Error(400, "布局原点、旋转或高程不合法");
            bool dryRun = true;
            if (input["dryRun"] != null)
            {
                if (input["dryRun"].Type != JTokenType.Boolean) return BridgeResponse.Error(400, "dryRun须布尔值");
                dryRun = input["dryRun"].Value<bool>();
            }
            request.Query["dryRun"] = dryRun ? "true" : "false";
            var terrain = World.GetExistingSystemManaged<TerrainSystem>().GetHeightData();
            var origin = new float3(x, 0, z);
            origin.y = TerrainUtils.SampleHeight(ref terrain, origin);
            var heading = quaternion.RotateY(math.radians(rotation));
            var prefabs = new Dictionary<string, Entity>(StringComparer.Ordinal);
            var parts = new RoadLayoutPart[partsJson.Count];
            PrefabBase firstPrefab = null;
            for (int i = 0; i < parts.Length; i++)
            {
                var p = partsJson[i]; string name = p["prefab"]?.Value<string>();
                if (string.IsNullOrEmpty(name) || name.IndexOf("Invisible", StringComparison.OrdinalIgnoreCase) >= 0)
                    return BridgeResponse.Error(400, $"第{i}段必须使用正常可见道路");
                if (!prefabs.TryGetValue(name, out var prefabEntity))
                {
                    if (!TryFindPrefabByName(RoadPrefabQuery, name, out prefabEntity, out var prefab) || !(prefab is RoadPrefab))
                        return BridgeResponse.Error(404, $"第{i}段道路预制体不存在");
                    if (IsLocked(prefabEntity)) return BridgeResponse.Error(409, $"道路{name}尚未正常解锁");
                    prefabs.Add(name, prefabEntity); if (firstPrefab == null) firstPrefab = prefab;
                }
                var c = p["curve"];
                var curve = new Bezier4x3(LayoutPoint(c?["a"]), LayoutPoint(c?["b"]), LayoutPoint(c?["c"]), LayoutPoint(c?["d"]));
                if (!CheckNetElevation(prefabEntity, curve.a.y + elevation, out error) || !CheckNetElevation(prefabEntity, curve.d.y + elevation, out error)) return error;
                foreach (var local in new[] { curve.a, curve.b, curve.c, curve.d })
                {
                    if (!math.all(math.isfinite(local)) || local.y + elevation < -30 || local.y + elevation > 60)
                        return BridgeResponse.Error(400, $"第{i}段曲线点或相对高程不合法");
                    var world = origin + math.mul(heading, local);
                    if (math.abs(world.x) > 7168 || math.abs(world.z) > 7168)
                        return BridgeResponse.Error(400, $"第{i}段超出地图范围");
                }
                if (!LayoutNode(p["nodes"]?["start"], out int start) || !LayoutNode(p["nodes"]?["end"], out int end))
                    return BridgeResponse.Error(400, $"第{i}段节点编号不合法");
                int meshStart = p["parentMesh"]?["start"]?.Value<int>() ?? 0;
                int meshEnd = p["parentMesh"]?["end"]?.Value<int>() ?? 0;
                if (meshStart < -1 || meshStart > 0 || meshEnd < -1 || meshEnd > 0)
                    return BridgeResponse.Error(400, "自由道路只允许-1/0的原生高程固定方式");
                CompositionFlags upgrades = default;
                var u = p["upgrades"];
                if (!Enum.TryParse(u?["general"]?.Value<string>() ?? "0", out upgrades.m_General)
                    || !Enum.TryParse(u?["left"]?.Value<string>() ?? "0", out upgrades.m_Left)
                    || !Enum.TryParse(u?["right"]?.Value<string>() ?? "0", out upgrades.m_Right))
                    return BridgeResponse.Error(400, $"第{i}段道路升级标记不合法");
                curve.a.y += elevation; curve.b.y += elevation; curve.c.y += elevation; curve.d.y += elevation;
                parts[i] = new RoadLayoutPart { Prefab = prefabEntity, Curve = curve, Nodes = new int2(start, end),
                    ParentMesh = new int2(meshStart, meshEnd), Upgrades = upgrades };
            }
            if (TransitToolsBusy(out error)) return error;
            if (!World.GetExistingSystemManaged<BridgeToolSystem>().TryQueueRoadLayout(parts, origin, heading, firstPrefab, request))
                return BridgeResponse.Error(409, "another build operation is in progress, retry shortly");
            return null;
        }
    }
}
