using Game.Prefabs;
using Game.Objects;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        /// <summary>仅允许原生建筑扩建清单中的正常解锁项目，不直接写 InstalledUpgrade 或绕过收费。</summary>
        private BridgeResponse UpgradeServiceBuilding(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error) || !ReadExistingEntity(request, out Entity target, out error)) return error;
            if (!EntityManager.HasComponent<Game.Buildings.Building>(target) || !EntityManager.HasComponent<Transform>(target))
                return BridgeResponse.Error(400, "target must be a placed service building");
            if (!request.Query.TryGetValue("prefab", out string name))
                return BridgeResponse.Error(400, "provide an upgrade from building_details.availableUpgrades");
            Entity parentPrefab = EntityManager.GetComponentData<PrefabRef>(target).m_Prefab;
            Entity upgrade = Entity.Null;
            if (EntityManager.HasBuffer<BuildingUpgradeElement>(parentPrefab))
                foreach (var item in EntityManager.GetBuffer<BuildingUpgradeElement>(parentPrefab, true))
                    if (NativeName(item.m_Upgrade) == name) { upgrade = item.m_Upgrade; break; }
            if (upgrade == Entity.Null || !EntityManager.HasComponent<ServiceUpgradeData>(upgrade))
                return BridgeResponse.Error(400, "prefab is not an upgrade of this building");
            // 原生扩建还包含无独立建筑模型的设施改进，不能只搜索 BuildingPrefab。
            PrefabBase prefab = World.GetOrCreateSystemManaged<PrefabSystem>().GetPrefab<PrefabBase>(upgrade);
            if (!(prefab is ObjectPrefab)) return BridgeResponse.Error(400, "upgrade is not a native object upgrade");
            if (IsLocked(upgrade)) return BridgeResponse.Error(409, "upgrade is locked; purchase normal development prerequisites first");
            var upgradeData = EntityManager.GetComponentData<ServiceUpgradeData>(upgrade);
            bool fixedExtension = EntityManager.HasComponent<BuildingExtensionData>(upgrade);
            // 同原生BuildingsValidationHelpers.ValidateUpgrade：非建筑升级或ForbidMultiple才禁重复。
            // 固定位置/楼层外观不等于重复资格；最终仍经过原生工具的完整验证。
            if ((!EntityManager.HasComponent<BuildingData>(upgrade) || upgradeData.m_ForbidMultiple) && EntityManager.HasBuffer<Game.Buildings.InstalledUpgrade>(target))
                foreach (var item in EntityManager.GetBuffer<Game.Buildings.InstalledUpgrade>(target, true))
                    if (EntityManager.Exists(item.m_Upgrade) && EntityManager.HasComponent<PrefabRef>(item.m_Upgrade) && EntityManager.GetComponentData<PrefabRef>(item.m_Upgrade).m_Prefab == upgrade)
                        return BridgeResponse.Error(409, "this upgrade is already installed");
            var transform = EntityManager.GetComponentData<Transform>(target);
            float3 position;
            quaternion rotation = transform.m_Rotation;
            if (fixedExtension)
            {
                var extension = EntityManager.GetComponentData<BuildingExtensionData>(upgrade);
                position = ObjectUtils.LocalToWorld(transform, extension.m_Position);
            }
            else if (EntityManager.HasComponent<BuildingData>(upgrade))
            {
                if (!request.TryGetFloat("x", out float x) || !request.TryGetFloat("z", out float z))
                    return BridgeResponse.Error(400, "this freestanding upgrade requires x and z near its parent");
                // 原生距离扩展的是父建筑场地范围，并非父建筑中心半径；0也允许贴边车库。
                var parentBuilding = EntityManager.GetComponentData<BuildingData>(parentPrefab);
                var upgradeBuilding = EntityManager.GetComponentData<BuildingData>(upgrade);
                Game.Buildings.BuildingUtils.CalculateUpgradeRangeValues(transform.m_Rotation, parentBuilding,
                    upgradeBuilding, upgradeData, out float3 forward, out float width, out float length,
                    out float roundness, out bool circular);
                float2 delta = new float2(x, z) - transform.m_Position.xz;
                float2 right = new float2(forward.z, -forward.x);
                float2 local = math.abs(new float2(math.dot(delta, right), math.dot(delta, forward.xz)));
                float2 outside = math.max(local - (new float2(width, length) * .5f - roundness), 0);
                if (math.length(outside) > roundness + .01f)
                    return BridgeResponse.Error(400, "upgrade is outside the native building placement range");
                var terrain = World.GetOrCreateSystemManaged<Game.Simulation.TerrainSystem>().GetHeightData();
                position = new float3(x, 0, z);
                position.y = Game.Simulation.TerrainUtils.SampleHeight(ref terrain, position);
                if (request.TryGetFloat("rotation", out float degrees)) rotation = quaternion.RotateY(math.radians(degrees));
            }
            else position = transform.m_Position;
            if (request.Query.TryGetValue("dryRun", out string dryRun) && dryRun == "true")
                return BridgeResponse.Json(new { building = Ref(target), prefab = name, fixedExtension, constructionCost = upgradeData.m_UpgradeCost, position = new { x = position.x, y = position.y, z = position.z }, note = "position only; native overlap, money and other validation occurs on actual application" });
            if (!World.GetOrCreateSystemManaged<BridgeToolSystem>().TryQueueBuildingUpgrade(target, upgrade, prefab, position, rotation, request))
                return BridgeResponse.Error(409, "another build operation is in progress, retry shortly");
            return null;
        }
    }
}
