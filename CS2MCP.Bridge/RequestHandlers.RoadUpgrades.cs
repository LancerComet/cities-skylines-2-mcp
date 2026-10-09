using System;
using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private EntityQuery m_RoadUpgradePrefabQuery;
        private bool m_RoadUpgradePrefabQueryCreated;
        private EntityQuery RoadUpgradePrefabQuery
        {
            get
            {
                if (!m_RoadUpgradePrefabQueryCreated)
                {
                    m_RoadUpgradePrefabQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<PlaceableNetData>());
                    m_RoadUpgradePrefabQueryCreated = true;
                }
                return m_RoadUpgradePrefabQuery;
            }
        }

        // 车道工具读取实际预制件标志；不按道路名称或猜测的公交枚举构造升级。
        private bool IsNativeLaneUpgrade(Entity entity, PrefabBase prefab)
        {
            if (prefab == null || prefab.name.IndexOf("invisible", StringComparison.OrdinalIgnoreCase) >= 0
                || !EntityManager.HasComponent<PlaceableNetData>(entity)) return false;
            PlaceableNetData data = EntityManager.GetComponentData<PlaceableNetData>(entity);
            CompositionFlags.Side lanes = CompositionFlags.Side.PrimaryLane | CompositionFlags.Side.SecondaryLane
                | CompositionFlags.Side.TertiaryLane | CompositionFlags.Side.QuaternaryLane | CompositionFlags.Side.ForbidSecondary;
            CompositionFlags flags = data.m_SetUpgradeFlags | data.m_UnsetUpgradeFlags;
            return (data.m_PlacementFlags & Game.Net.PlacementFlags.IsUpgrade) != 0
                && ((flags.m_Left | flags.m_Right) & lanes) != 0
                && ((flags.m_Left | flags.m_Right) & kTurnRestrictionFlags) == 0;
        }

        /// <summary>正常边段道路服务；节点标志、禁转与子对象替换使用各自原生工具。</summary>
        private bool IsNativeRoadServiceUpgrade(Entity entity, PrefabBase prefab)
        {
            if (prefab == null || prefab.name.IndexOf("invisible", StringComparison.OrdinalIgnoreCase) >= 0
                || !EntityManager.HasComponent<PlaceableNetData>(entity)
                || EntityManager.HasBuffer<Game.Net.SubReplacement>(entity)) return false;
            var data = EntityManager.GetComponentData<PlaceableNetData>(entity);
            var flags = data.m_SetUpgradeFlags | data.m_UnsetUpgradeFlags;
            return (data.m_PlacementFlags & Game.Net.PlacementFlags.IsUpgrade) != 0
                && (data.m_PlacementFlags & Game.Net.PlacementFlags.NodeUpgrade) == 0
                && ((flags.m_Left | flags.m_Right) & kTurnRestrictionFlags) == 0
                && flags != default;
        }

        private BridgeResponse ListNativeRoadUpgrades(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            request.Query.TryGetValue("query", out string search);
            int limit = request.TryGetInt("limit", out int raw) ? Unity.Mathematics.math.clamp(raw, 1, 200) : 100;
            var list = new List<object>(); int total = 0;
            var system = World.GetOrCreateSystemManaged<PrefabSystem>();
            using (var entities = RoadUpgradePrefabQuery.ToEntityArray(Allocator.Temp))
                foreach (Entity entity in entities)
                {
                    PrefabBase prefab = system.GetPrefab<PrefabBase>(entity);
                    var data = EntityManager.GetComponentData<PlaceableNetData>(entity);
                    if ((data.m_PlacementFlags & Game.Net.PlacementFlags.IsUpgrade) == 0 || prefab == null
                        || prefab.name.IndexOf("invisible", StringComparison.OrdinalIgnoreCase) >= 0
                        || (!string.IsNullOrEmpty(search) && prefab.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                    total++;
                    if (list.Count >= limit) continue;
                    list.Add(new { prefab = prefab.name, locked = IsLocked(entity), placementFlags = data.m_PlacementFlags.ToString(),
                        setFlags = DescribeCompositionFlags(data.m_SetUpgradeFlags), unsetFlags = DescribeCompositionFlags(data.m_UnsetUpgradeFlags),
                        laneApplicationSupported = IsNativeLaneUpgrade(entity, prefab),
                        serviceApplicationSupported = IsNativeRoadServiceUpgrade(entity, prefab),
                        defaultConstructionCostRaw = data.m_DefaultConstructionCost, defaultUpkeepCostRaw = data.m_DefaultUpkeepCost });
                }
            return BridgeResponse.Json(new { totalMatches = total, returned = list.Count, upgrades = list,
                note = "native IsUpgrade catalog includes standalone-capable upgrades such as public transport lanes; lane application preserves unrelated flags and still requires native validation; raw costs are not a quoted total" });
        }

        private BridgeResponse ApplyNativeLaneUpgrade(BridgeRequest request, bool allServices = false)
        {
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            if (!request.TryGetInt("index", out int index) || !request.TryGetInt("version", out int version)
                || !request.Query.TryGetValue("prefab", out string name)) return BridgeResponse.Error(400, "provide road index/version and exact native lane upgrade prefab");
            Entity road = new Entity { Index = index, Version = version };
            if (!EntityManager.Exists(road) || !EntityManager.HasComponent<Road>(road) || !EntityManager.HasComponent<Edge>(road)
                || EntityManager.HasComponent<Owner>(road) || EntityManager.HasComponent<Deleted>(road)) return BridgeResponse.Error(400, "requires an existing standalone road");
            if (!TryFindPrefabByName(RoadUpgradePrefabQuery, name, out Entity prefabEntity, out PrefabBase prefab)) return BridgeResponse.Error(404, "unknown native upgrade prefab");
            if (!(allServices ? IsNativeRoadServiceUpgrade(prefabEntity, prefab) : IsNativeLaneUpgrade(prefabEntity, prefab)))
                return BridgeResponse.Error(400, "unsupported native edge upgrade; use junction/turn tools for node controls, no invisible or sub-replacement assets");
            if (IsLocked(prefabEntity)) return BridgeResponse.Error(409, "native upgrade is locked");
            if (EntityManager.HasBuffer<Game.Net.SubReplacement>(prefabEntity)) return BridgeResponse.Error(400, "sub-replacement upgrades require their native object tool, unsupported here");
            PlaceableNetData data = EntityManager.GetComponentData<PlaceableNetData>(prefabEntity);
            CompositionFlags add = data.m_SetUpgradeFlags, clear = data.m_UnsetUpgradeFlags;
            if (request.TryGetBool("invert", out bool invert) && invert)
            {
                add = NetCompositionHelpers.InvertCompositionFlags(add);
                clear = NetCompositionHelpers.InvertCompositionFlags(clear);
            }
            CompositionFlags old = EntityManager.HasComponent<Upgraded>(road) ? EntityManager.GetComponentData<Upgraded>(road).m_Flags : default;
            bool remove = request.TryGetBool("remove", out bool rawRemove) && rawRemove;
            // 原生 NetToolSystem.AddControlPoints 的边段移除分支只把
            // UnsetUpgradeFlags 中的 RemoveCrosswalk 保留为新增标志。
            // 只撤销 AddCrosswalk 会让默认路口横道重新出现；其他服务仍回到默认。
            CompositionFlags removeOverrides = default;
            removeOverrides.m_Left = clear.m_Left & CompositionFlags.Side.RemoveCrosswalk;
            removeOverrides.m_Right = clear.m_Right & CompositionFlags.Side.RemoveCrosswalk;
            CompositionFlags next = remove ? (old & ~add) | removeOverrides : (old & ~clear) | add;
            if (request.TryGetBool("dryRun", out bool dryRun) && dryRun)
                return BridgeResponse.Json(new { dryRun = true, road = Ref(road), prefab = name, oldFlags = DescribeCompositionFlags(old), nextFlags = DescribeCompositionFlags(next),
                    note = "native flag resolution only, not native collision/cost preview" });
            if (old == next) return BridgeResponse.Json(new { changed = false, road = Ref(road), flags = DescribeCompositionFlags(old) });
            if (!World.GetOrCreateSystemManaged<BridgeToolSystem>().TryQueueUpgrade(road, prefab.name, next, request))
                return BridgeResponse.Error(409, "another build operation is in progress, retry shortly");
            return null;
        }
    }
}
