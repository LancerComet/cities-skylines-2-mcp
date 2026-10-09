using System.Collections.Generic;
using System;
using Game.Prefabs;
using Game.Simulation;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        // 与交通信息页使用同一配置、统计和线路计数；不按图标猜测类型或把横线当零线路。
        private BridgeResponse GetTransportOverview()
        {
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            var prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            var statistics = World.GetOrCreateSystemManaged<CityStatisticsSystem>();
            using (var configQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<UITransportConfigurationData>()))
            using (var lines = TransportUIUtils.GetSortedLines(TransitLineQuery, EntityManager, prefabs))
            using (var linePrefabs = TransitLinePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                if (configQuery.IsEmptyIgnoreFilter) return BridgeResponse.Error(409, "原生交通信息页配置未就绪");
                var config = prefabs.GetSingletonPrefab<UITransportConfigurationPrefab>(configQuery);
                var passengers = new List<object>();
                var cargo = new List<object>();
                foreach (var item in config.m_PassengerSummaryItems)
                {
                    var unlockable = prefabs.GetEntity(item.m_Unlockable);
                    passengers.Add(new {
                        type = TransitTypeName(item.m_Type), nativeType = item.m_Type.ToString(), icon = item.m_Icon,
                        displayed = TransportUIUtils.ShouldBindTransportType(EntityManager, prefabs, item.m_Type, linePrefabs),
                        locked = IsLocked(unlockable), unlockable = item.m_Unlockable.name,
                        showsLines = item.m_ShowLines,
                        lineCount = item.m_ShowLines ? (int?)TransportUIUtils.CountLines(lines, item.m_Type) : null,
                        statistic = item.m_Statistic.ToString(),
                        touristCount = statistics.GetStatisticValue(item.m_Statistic, 1),
                        citizenCount = statistics.GetStatisticValue(item.m_Statistic)
                    });
                }
                foreach (var item in config.m_CargoSummaryItems)
                {
                    var unlockable = prefabs.GetEntity(item.m_Unlockable);
                    cargo.Add(new {
                        type = item.m_Statistic.ToString() == "CargoCountTruck" ? "truck" : TransitTypeName(item.m_Type), nativeType = item.m_Type.ToString(), icon = item.m_Icon,
                        displayed = true, locked = IsLocked(unlockable), unlockable = item.m_Unlockable.name,
                        showsLines = item.m_ShowLines,
                        lineCount = item.m_ShowLines ? (int?)TransportUIUtils.CountLines(lines, item.m_Type, cargo: true) : null,
                        statistic = item.m_Statistic.ToString(), cargoCountRaw = statistics.GetStatisticValue(item.m_Statistic)
                    });
                }
                return BridgeResponse.Json(new { capturedAtUtc = DateTime.UtcNow.ToString("o"), passengers, cargo, stalenessWarning = LockStalenessWarning,
                    note = "原生交通信息页同源；showsLines表示配置是否统计线路，零值可能显示横线，非没有服务。CargoCountTruck的nativeType=Bus是原生配置复用，type明确为truck。统计值保持原始单位，货运不冒充人数或未经核实的吨。配置中displayed=false的类型仍保留审计；航空线路由原生自动管理。" });
            }
        }
    }
}
