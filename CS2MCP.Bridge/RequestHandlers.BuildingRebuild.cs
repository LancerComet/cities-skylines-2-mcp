using Game.Common;
using Game.Prefabs;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        /// <summary>只重建已由原生灾害救援清障完成的服务设施；收费、碰撞及网络恢复交给原生工具。</summary>
        private BridgeResponse RebuildServiceBuilding(BridgeRequest request)
        {
            if (request.Query.ContainsKey("dryRun") && !request.TryGetBool("dryRun", out _))
                return BridgeResponse.Error(400, "dryRun must be true or false; no native preview was queued");
            if (!request.Query.ContainsKey("dryRun")) request.Query["dryRun"] = "true";
            if (!TryGetCity(out _, out BridgeResponse error) || !ReadExistingEntity(request, out Entity target, out error)) return error;
            if (!EntityManager.HasComponent<Game.Buildings.Building>(target)
                || !EntityManager.HasComponent<Game.Objects.Transform>(target)
                || !EntityManager.HasComponent<PrefabRef>(target)
                || !EntityManager.HasComponent<Destroyed>(target))
                return BridgeResponse.Error(400, "target must be a currently destroyed service building; refresh details before retrying");
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(target).m_Prefab;
            if (!EntityManager.HasComponent<ServiceObjectData>(prefab) && !EntityManager.HasComponent<ServiceUpgradeData>(prefab))
                return BridgeResponse.Error(400, "only native service buildings and their service upgrades can use this rebuild endpoint");
            if (EntityManager.HasComponent<Owner>(target) && !EntityManager.HasComponent<Game.Buildings.ServiceUpgrade>(target))
                return BridgeResponse.Error(400, "internal owned buildings cannot be rebuilt independently");
            var destroyed = EntityManager.GetComponentData<Destroyed>(target);
            if (!Unity.Mathematics.math.isfinite(destroyed.m_Cleared) || destroyed.m_Cleared < 1f)
                return BridgeResponse.Error(409, "native disaster cleanup is incomplete; provide disaster-response service and wait for actual clearance");
            if (IsLocked(prefab)) return BridgeResponse.Error(409, "native facility is currently locked; do not bypass normal prerequisites");
            var transform = EntityManager.GetComponentData<Game.Objects.Transform>(target);
            if (!World.GetOrCreateSystemManaged<BridgeToolSystem>().TryQueueBuildingRebuild(target, NativeName(prefab),
                transform.m_Position, transform.m_Rotation, request))
                return BridgeResponse.Error(409, "another build operation is in progress, retry shortly");
            return null;
        }

        /// <summary>
        /// DestroyedBuildingSection permits rebuilding a cleared Attached spawnable building.
        /// Limit this entry to an extractor's actual Hub with a matching Attachment backlink;
        /// the native UpgradeTool definitions still decide preview, cost and application.
        /// </summary>
        private BridgeResponse RebuildIndustryHub(BridgeRequest request)
        {
            foreach (string key in request.Query.Keys)
                if (key != "index" && key != "version" && key != "parentIndex" && key != "parentVersion"
                    && key != "expectedLoadedSaveGuid" && key != "dryRun")
                    return BridgeResponse.Error(400, "unsupported industry rebuild parameter: " + key);
            if (!request.Query.TryGetValue("expectedLoadedSaveGuid", out string expected)
                || !System.Guid.TryParse(expected, out var expectedGuid) || expectedGuid == System.Guid.Empty)
                return BridgeResponse.Error(400, "provide the nonempty current expectedLoadedSaveGuid");
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            string loaded = World.GetExistingSystemManaged<Game.Serialization.LoadGameSystem>()?.context.instigatorGuid.ToString();
            if (!System.Guid.TryParse(loaded, out var actualGuid) || actualGuid != expectedGuid)
                return BridgeResponse.Error(409, "loaded save identity does not match expectedLoadedSaveGuid");
            if (!request.TryGetInt("parentIndex", out int parentIndex) || parentIndex < 1
                || !request.TryGetInt("parentVersion", out int parentVersion) || parentVersion < 1)
                return BridgeResponse.Error(400, "provide the complete positive extractor parent index/version");
            if (request.Query.ContainsKey("dryRun") && !request.TryGetBool("dryRun", out _))
                return BridgeResponse.Error(400, "dryRun must be true or false; no native preview was queued");
            if (!request.Query.ContainsKey("dryRun")) request.Query["dryRun"] = "true";
            if (!ReadExistingEntity(request, out Entity hub, out error)) return error;
            var parent = new Entity { Index = parentIndex, Version = parentVersion };
            if (!EntityManager.Exists(parent) || EntityManager.HasComponent<Deleted>(parent)
                || EntityManager.HasComponent<Game.Tools.Temp>(parent) || EntityManager.HasComponent<Destroyed>(parent)
                || !EntityManager.HasComponent<Game.Buildings.Building>(parent)
                || !EntityManager.HasComponent<PrefabRef>(parent)
                || !EntityManager.HasComponent<Game.Objects.Attachment>(parent))
                return BridgeResponse.Error(409, "extractor parent is stale, destroyed or lacks its normal Attachment");
            if (EntityManager.HasComponent<Deleted>(hub) || EntityManager.HasComponent<Game.Tools.Temp>(hub)
                || !EntityManager.HasComponent<Game.Buildings.Building>(hub)
                || !EntityManager.HasComponent<Game.Objects.Transform>(hub)
                || !EntityManager.HasComponent<PrefabRef>(hub) || !EntityManager.HasComponent<Destroyed>(hub)
                || !EntityManager.HasComponent<Game.Objects.Attached>(hub) || EntityManager.HasComponent<Owner>(hub)
                || EntityManager.GetComponentData<Game.Objects.Attached>(hub).m_Parent != parent
                || EntityManager.GetComponentData<Game.Objects.Attachment>(parent).m_Attached != hub)
                return BridgeResponse.Error(409, "target must be the parent's currently destroyed Attached industry Hub");
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(hub).m_Prefab;
            if (!EntityManager.HasComponent<SpawnableBuildingData>(prefab)
                || EntityManager.HasComponent<PlacedSignatureBuildingData>(prefab))
                return BridgeResponse.Error(400, "target is not a normal attached spawnable industry Hub");
            bool hasOwnedExtractorLot = false;
            if (EntityManager.HasBuffer<Game.Areas.SubArea>(parent))
            {
                var areas = EntityManager.GetBuffer<Game.Areas.SubArea>(parent, true);
                if (areas.Length > 128) return BridgeResponse.Error(409, "extractor area ownership exceeds the bounded rebuild read");
                foreach (var sub in areas)
                {
                    Entity area = sub.m_Area;
                    if (EntityManager.Exists(area) && !EntityManager.HasComponent<Deleted>(area)
                        && !EntityManager.HasComponent<Game.Tools.Temp>(area)
                        && EntityManager.HasComponent<Game.Areas.Lot>(area)
                        && EntityManager.HasComponent<Game.Areas.Extractor>(area)
                        && EntityManager.HasComponent<Owner>(area)
                        && EntityManager.GetComponentData<Owner>(area).m_Owner == parent)
                        hasOwnedExtractorLot = true;
                }
            }
            if (!hasOwnedExtractorLot) return BridgeResponse.Error(400, "parent has no normal owned extractor Lot");
            var destroyed = EntityManager.GetComponentData<Destroyed>(hub);
            if (!Unity.Mathematics.math.isfinite(destroyed.m_Cleared) || destroyed.m_Cleared < 1f)
                return BridgeResponse.Error(409, "native disaster cleanup is incomplete");
            Entity parentPrefab = EntityManager.GetComponentData<PrefabRef>(parent).m_Prefab;
            if (IsLocked(prefab) || IsLocked(parentPrefab))
                return BridgeResponse.Error(409, "native Hub or extractor parent is currently locked");
            var transform = EntityManager.GetComponentData<Game.Objects.Transform>(hub);
            if (!World.GetOrCreateSystemManaged<BridgeToolSystem>().TryQueueBuildingRebuild(hub, NativeName(prefab),
                    transform.m_Position, transform.m_Rotation, request))
                return BridgeResponse.Error(409, "another build operation is in progress, retry shortly");
            return null;
        }
    }
}
