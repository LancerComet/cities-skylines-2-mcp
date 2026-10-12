using System;
using System.Collections.Generic;
using Game.City;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        /// <summary>一次读取全城原生统计与治安成因，不触发 UI 页面或逐栋远程查询。</summary>
        private BridgeResponse GetCityIndicators()
        {
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            var statistics = World.GetOrCreateSystemManaged<CityStatisticsSystem>();
            var current = new Dictionary<string, long>();
            var types = new[] { StatisticType.DeathRate, StatisticType.BirthRate, StatisticType.CollectedMail,
                StatisticType.DeliveredMail, StatisticType.CitizensMovedIn, StatisticType.CitizensMovedAway,
                StatisticType.CrimeCount, StatisticType.EscapedArrestCount, StatisticType.TouristCount,
                StatisticType.TouristIncome, StatisticType.LodgingUsed, StatisticType.LodgingTotal,
                StatisticType.PassengerCountBus, StatisticType.PassengerCountTaxi, StatisticType.PassengerCountTram,
                StatisticType.PassengerCountSubway, StatisticType.PassengerCountTrain, StatisticType.PassengerCountShip,
                StatisticType.PassengerCountFerry, StatisticType.PassengerCountAirplane,
                StatisticType.CargoCountTruck, StatisticType.CargoCountTrain, StatisticType.CargoCountShip, StatisticType.CargoCountAirplane };
            foreach (var type in types) current[type.ToString()] = statistics.GetStatisticValueLong(type, 0);
            var education = new Dictionary<int, long>();
            for (int level = 0; level <= 4; level++) education[level] = statistics.GetStatisticValueLong(StatisticType.EducationCount, level);
            float accumulatedCrime = 0, maximumCrime = 0; int crimeBuildings = 0, criminals = 0, arrested = 0, sentenced = 0;
            using (var q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Prefabs.PoliceConfigurationData>()))
            using (var data = q.ToComponentDataArray<Game.Prefabs.PoliceConfigurationData>(Allocator.Temp))
                if (data.Length == 1) maximumCrime = data[0].m_MaxCrimeAccumulation;
            using (var q = EntityManager.CreateEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<Game.Buildings.CrimeProducer>() },
                None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() }
            }))
            using (var data = q.ToComponentDataArray<Game.Buildings.CrimeProducer>(Allocator.Temp)) {
                crimeBuildings = data.Length;
                foreach (var item in data) accumulatedCrime += item.m_Crime;
            }
            using (var q = EntityManager.CreateEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Game.Citizens.Citizen>(), ComponentType.ReadOnly<Game.Citizens.Criminal>() },
                None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() }
            }))
            using (var data = q.ToComponentDataArray<Game.Citizens.Criminal>(Allocator.Temp)) {
                criminals = data.Length;
                foreach (var item in data) {
                    if ((item.m_Flags & (Game.Citizens.CriminalFlags.Prisoner | Game.Citizens.CriminalFlags.Sentenced)) != 0) sentenced++;
                    else if ((item.m_Flags & Game.Citizens.CriminalFlags.Arrested) != 0) arrested++;
                }
            }
            return BridgeResponse.Json(new { current, educationCountByLevel = education,
                police = new { criminals, arrested, sentenced, crimeBuildings,
                    crimeProbabilityPercent = maximumCrime > 0 && crimeBuildings > 0 ? (float?) (accumulatedCrime / crimeBuildings / maximumCrime * 100) : null,
                    escapedArrestPercent = current["CrimeCount"] > 0 ? Math.Min(100, current["EscapedArrestCount"] * 100.0 / current["CrimeCount"]) : 0 },
                note = "原生 StatisticType 值，邮政/客货运输等保留原始统计单位；零值不是故障。治安概率按原生面板最大犯罪累积量归一化。设施容量另见 facilities，幸福因素见 happiness，空间覆盖仍须按区域核验。" });
        }
    }
}
