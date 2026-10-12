using System;
using System.Collections.Generic;
using System.Linq;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        // 一次扫描现场建筑，再按原生菜单/服务归属列出独立设施与扩建；不按英文名字猜类别。
        private BridgeResponse GetFacilityCatalog()
        {
            if (!TryGetCity(out _, out var error)) return error;
            var placed = new Dictionary<Entity, List<object>>();
            using (var entities = PlacedBuildingQuery.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities) {
                    var prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    if (!placed.TryGetValue(prefab, out var list)) placed[prefab] = list = new List<object>();
                    var transform = EntityManager.GetComponentData<Game.Objects.Transform>(entity);
                    var p = transform.m_Position;
                    var installed = new List<object>();
                    if (EntityManager.HasBuffer<Game.Buildings.InstalledUpgrade>(entity))
                        foreach (var item in EntityManager.GetBuffer<Game.Buildings.InstalledUpgrade>(entity, true)) {
                            if (!EntityManager.Exists(item.m_Upgrade)) continue;
                            var hasTransform = EntityManager.HasComponent<Game.Objects.Transform>(item.m_Upgrade);
                            var t = hasTransform ? EntityManager.GetComponentData<Game.Objects.Transform>(item.m_Upgrade) : default;
                            installed.Add(new { entity = Ref(item.m_Upgrade), name = PrefabNameOf(item.m_Upgrade),
                                position = hasTransform ? new { x = t.m_Position.x, y = t.m_Position.y, z = t.m_Position.z } : null });
                        }
                    list.Add(new { entity = Ref(entity), position = new { x = p.x, y = p.y, z = p.z },
                        rotation = new { x = transform.m_Rotation.value.x, y = transform.m_Rotation.value.y, z = transform.m_Rotation.value.z, w = transform.m_Rotation.value.w },
                        installedUpgrades = installed, isSubBuilding = EntityManager.HasComponent<Game.Common.Owner>(entity) });
                }
            var rows = new List<object>();
            using (var entities = BuildingPrefabQuery.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities) {
                    bool isSignature = EntityManager.HasComponent<SignatureBuildingData>(entity);
                    if (EntityManager.HasComponent<SpawnableBuildingData>(entity) && !isSignature) continue;
                    if (!EntityManager.HasComponent<PlaceableObjectData>(entity)) continue;
                    bool hasService = EntityManager.HasComponent<ServiceObjectData>(entity);
                    bool hasMenu = EntityManager.HasComponent<UIObjectData>(entity);
                    bool upgrade = EntityManager.HasComponent<ServiceUpgradeData>(entity);
                    if (!hasService && !hasMenu && !upgrade) continue;
                    var data = EntityManager.GetComponentData<PlaceableObjectData>(entity);
                    var footprint = EntityManager.GetComponentData<BuildingData>(entity).m_LotSize;
                    Entity service = hasService ? EntityManager.GetComponentData<ServiceObjectData>(entity).m_Service : Entity.Null;
                    Entity group = hasMenu ? EntityManager.GetComponentData<UIObjectData>(entity).m_Group : Entity.Null;
                    var upgrades = new List<object>();
                    if (EntityManager.HasBuffer<BuildingUpgradeElement>(entity))
                        foreach (var item in EntityManager.GetBuffer<BuildingUpgradeElement>(entity, true)) {
                            var u = item.m_Upgrade;
                            upgrades.Add(new { name = NativeName(u), localizedName = LocalizedAsset(u, "NAME"), locked = IsLocked(u),
                                prefabType = World.GetOrCreateSystemManaged<PrefabSystem>().GetPrefab<PrefabBase>(u).GetType().Name,
                                description = LocalizedAsset(u, "DESCRIPTION"),
                                placementFlags = EntityManager.HasComponent<PlaceableObjectData>(u) ? EntityManager.GetComponentData<PlaceableObjectData>(u).m_Flags.ToString() : null,
                                fixedExtension = EntityManager.HasComponent<BuildingExtensionData>(u),
                                forbidMultiple = EntityManager.HasComponent<ServiceUpgradeData>(u) && EntityManager.GetComponentData<ServiceUpgradeData>(u).m_ForbidMultiple,
                                repeatablePlacement = EntityManager.HasComponent<BuildingData>(u) && EntityManager.HasComponent<ServiceUpgradeData>(u) && !EntityManager.GetComponentData<ServiceUpgradeData>(u).m_ForbidMultiple,
                                lotMeters = EntityManager.HasComponent<BuildingData>(u) ? new { width = EntityManager.GetComponentData<BuildingData>(u).m_LotSize.x * 8, depth = EntityManager.GetComponentData<BuildingData>(u).m_LotSize.y * 8 } : null,
                                constructionCost = EntityManager.HasComponent<ServiceUpgradeData>(u)
                                    ? (uint?)EntityManager.GetComponentData<ServiceUpgradeData>(u).m_UpgradeCost : null,
                                nativeFacts = FacilityFacts(u) });
                        }
                    placed.TryGetValue(entity, out var actual);
                    rows.Add(new { name = NativeName(entity), localizedName = LocalizedAsset(entity, "NAME"),
                        description = LocalizedAsset(entity, "DESCRIPTION"), service = NativeName(service),
                        menuGroup = NativeName(group), locked = IsLocked(entity), hasMenu, isUpgrade = upgrade, isSignature,
                        buildingFlags = EntityManager.GetComponentData<BuildingData>(entity).m_Flags.ToString(),
                        placementFlags = data.m_Flags.ToString(), constructionCost = data.m_ConstructionCost,
                        lotMeters = new { width = footprint.x * 8, depth = footprint.y * 8 },
                        placedCount = actual?.Count ?? 0, placed = actual, upgrades, nativeFacts = FacilityFacts(entity) });
                }
            return BridgeResponse.Json(new { capturedAtUtc = DateTime.UtcNow.ToString("o"), total = rows.Count, complete = true,
                facilities = rows, note = "范围为有正常放置数据和原生菜单/服务/扩建归属的非成长设施及标志性建筑（isSignature单列）；不是全游戏装饰、网络或全部已获得内容。每项正常升级和真实已装数量一起核验，空升级数组表示当前原生菜单没有手动升级，不能按外观猜挂载。locked和实际工具资格分别核验。原生数值保持字段单位，基础配置不是扩建后运行容量；固定扩建随父设施单列，已建子建筑不等于独立设施。" });
        }

        private string LocalizedAsset(Entity entity, string field)
        {
            string value = null;
            Game.SceneFlow.GameManager.instance?.localizationManager?.activeDictionary?.TryGetValue($"Assets.{field}[{NativeName(entity)}]", out value);
            return value;
        }

        private object FacilityFacts(Entity entity)
        {
            // 港区、产业等专用系统含资源/仓储/闸门/吊装/处理字段，不限于通用服务容量。
            // 名称仅筛选应读取的原生类型，不据此推断单位或功能；字段保持原样交叉核验。
            var prefixes = new[] { "City", "Local", "School", "Hospital", "Deathcare", "Welfare", "Police", "Prison", "Fire", "Disaster", "Shelter", "Telecom", "Mail", "Post", "Garbage", "Power", "Electric", "Solar", "Water", "Sewage", "Transport", "Cargo", "Parking", "Park", "Attraction", "ServiceUpkeep", "ServiceUpgrade", "ServiceCoverage", "ServiceConsumption", "Coverage", "Workplace", "BuildingExtension", "Storage", "Resource", "InitialResource", "Gate", "Crane", "Stack", "Industrial", "Processing", "Extractor", "CommercialCompany", "EnclosedArea" };
            var result = new List<object>();
            using (var types = EntityManager.GetComponentTypes(entity, Allocator.Temp))
                foreach (var component in types) {
                    var type = component.GetManagedType();
                    if (type == null || type.Namespace != "Game.Prefabs" || !prefixes.Any(p => type.Name.StartsWith(p, StringComparison.Ordinal))) continue;
                    if (component.IsBuffer) {
                        var buffer = OptionalModApi.Buffer(EntityManager, entity, type);
                        int length = (int)OptionalModApi.Read(buffer, "Length");
                        result.Add(new { type = type.FullName, count = length, complete = length <= 128,
                            items = Enumerable.Range(0, Math.Min(length, 128)).Select(i => OptionalModApi.JsonValue(buffer.GetType().GetProperty("Item").GetValue(buffer, new object[] { i }))).ToArray() });
                    } else if (typeof(IComponentData).IsAssignableFrom(type))
                        result.Add(new { type = type.FullName, value = OptionalModApi.JsonValue(OptionalModApi.Component(EntityManager, entity, type)) });
                }
            return result;
        }
    }
}
