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
    }
}
