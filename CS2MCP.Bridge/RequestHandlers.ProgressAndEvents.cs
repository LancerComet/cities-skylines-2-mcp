using System;
using System.Collections.Generic;
using Game.City;
using Game.Events;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private string NativeName(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity)) return null;
            var ps = World.GetOrCreateSystemManaged<PrefabSystem>();
            var prefab = ps.GetPrefab<PrefabBase>(entity);
            return prefab?.name;
        }

        /// <summary>读取真实发展点、前置节点和可购买状态；不直接修改点数或 Locked。</summary>
        private BridgeResponse GetDevelopment()
        {
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            int points = World.GetOrCreateSystemManaged<DevTreeSystem>().points;
            var nodes = new List<object>();
            using (var query = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<DevTreeNodeData>() },
                Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IgnoreComponentEnabledState
            }))
            using (var entities = query.ToEntityArray(Allocator.Temp))
            {
                foreach (var entity in entities)
                {
                    var data = EntityManager.GetComponentData<DevTreeNodeData>(entity);
                    var requirements = new List<object>();
                    bool hasRequirement = false, requirementSatisfied = false;
                    if (EntityManager.HasBuffer<DevTreeNodeRequirement>(entity))
                    {
                        var buffer = EntityManager.GetBuffer<DevTreeNodeRequirement>(entity, true);
                        foreach (var requirement in buffer)
                        {
                            if (requirement.m_Node == Entity.Null) continue;
                            hasRequirement = true;
                            bool unlocked = !IsLocked(requirement.m_Node);
                            requirementSatisfied |= unlocked;
                            requirements.Add(new { name = NativeName(requirement.m_Node), unlocked });
                        }
                    }
                    bool locked = IsLocked(entity);
                    bool serviceUnlocked = data.m_Service == Entity.Null || !IsLocked(data.m_Service);
                    nodes.Add(new { name = NativeName(entity), entity = new { index = entity.Index, version = entity.Version },
                        cost = data.m_Cost, locked, service = NativeName(data.m_Service), serviceUnlocked,
                        requirements, canPurchase = locked && serviceUnlocked && points >= data.m_Cost && (!hasRequirement || requirementSatisfied) });
                }
            }
            return BridgeResponse.Json(new { points, nodes, note = "requirements follow native ANY-parent semantics; purchase uses DevTreeSystem.Purchase" });
        }

        private BridgeResponse PurchaseDevelopment(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            if (!request.Query.TryGetValue("name", out string name) || string.IsNullOrWhiteSpace(name))
                return BridgeResponse.Error(400, "provide exact development node name");
            using (var query = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<DevTreeNodeData>() },
                Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IgnoreComponentEnabledState
            }))
            using (var entities = query.ToEntityArray(Allocator.Temp))
            {
                foreach (var entity in entities)
                {
                    if (!string.Equals(NativeName(entity), name, StringComparison.Ordinal)) continue;
                    var data = EntityManager.GetComponentData<DevTreeNodeData>(entity);
                    var system = World.GetOrCreateSystemManaged<DevTreeSystem>();
                    int before = system.points;
                    if (!IsLocked(entity)) return BridgeResponse.Json(new { alreadyUnlocked = true, name, points = before });
                    if (before < data.m_Cost) return BridgeResponse.Error(409, "insufficient development points");
                    if (data.m_Service != Entity.Null && IsLocked(data.m_Service)) return BridgeResponse.Error(409, "service prerequisite is locked");
                    bool hasRequirement = false, satisfied = false;
                    if (EntityManager.HasBuffer<DevTreeNodeRequirement>(entity))
                        foreach (var requirement in EntityManager.GetBuffer<DevTreeNodeRequirement>(entity, true))
                            if (requirement.m_Node != Entity.Null) { hasRequirement = true; satisfied |= !IsLocked(requirement.m_Node); }
                    if (hasRequirement && !satisfied) return BridgeResponse.Error(409, "development prerequisite is locked");
                    system.Purchase(entity);
                    int after = system.points;
                    if (before - after != data.m_Cost) return BridgeResponse.Error(409, "native purchase declined; read current development state before retrying");
                    return BridgeResponse.Json(new { name, unlockQueued = true, cost = data.m_Cost, pointsBefore = before, pointsAfter = after,
                        note = "native Unlock event queued; read development/prefab state after a frame" });
                }
            }
            return BridgeResponse.Error(404, "development node not found");
        }

        /// <summary>同时读取事件日志和正在燃烧对象，避免只读取建筑气泡漏报森林火灾。</summary>
        private BridgeResponse GetCityEvents()
        {
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            var journal = World.GetOrCreateSystemManaged<EventJournalSystem>();
            var entries = new List<object>();
            for (int i = journal.eventJournal.Length - 1; i >= 0 && entries.Count < 100; i--)
            {
                Entity entity = journal.eventJournal[i];
                if (!EntityManager.Exists(entity)) continue;
                var info = journal.GetInfo(entity);
                bool completed = EntityManager.HasComponent<EventJournalCompleted>(entity);
                bool pending = EntityManager.HasComponent<EventJournalPending>(entity);
                var tracking = new List<object>();
                if (journal.TryGetData(entity, out var data))
                    foreach (var item in data) tracking.Add(new { type = item.m_Type.ToString(), value = item.m_Value });
                var locations = new List<object>();
                int targetCount = 0;
                if (EntityManager.Exists(info.m_Event) && EntityManager.HasBuffer<TargetElement>(info.m_Event))
                {
                    var targets = EntityManager.GetBuffer<TargetElement>(info.m_Event, true);
                    targetCount = targets.Length;
                    for (int j = 0; j < targets.Length && locations.Count < 24; j++)
                    {
                        Entity target = targets[j].m_Entity;
                        if (!EntityManager.Exists(target) || !EntityManager.HasComponent<Game.Objects.Transform>(target)) continue;
                        var position = EntityManager.GetComponentData<Game.Objects.Transform>(target).m_Position;
                        locations.Add(new { target = new { index = target.Index, version = target.Version }, x = position.x, z = position.z });
                    }
                }
                entries.Add(new { name = NativeName(journal.GetPrefab(entity)), startFrame = info.m_StartFrame,
                    active = !completed && !pending && EntityManager.Exists(info.m_Event) && !EntityManager.HasComponent<Game.Common.Deleted>(info.m_Event),
                    completed, pending, targetCount, locations, tracking });
            }
            var burning = new List<object>();
            int burningBuildings = 0, burningOtherObjects = 0;
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<OnFire>(), ComponentType.ReadOnly<Game.Objects.Transform>()))
            using (var entities = query.ToEntityArray(Allocator.Temp))
            {
                foreach (var entity in entities)
                {
                    var fire = EntityManager.GetComponentData<OnFire>(entity);
                    if (fire.m_Intensity <= 0 || EntityManager.HasComponent<Game.Common.Deleted>(entity)) continue;
                    bool building = EntityManager.HasComponent<Game.Buildings.Building>(entity);
                    if (building) burningBuildings++; else burningOtherObjects++;
                    if (burning.Count >= 100 && !building) continue;
                    var pos = EntityManager.GetComponentData<Game.Objects.Transform>(entity).m_Position;
                    burning.Add(new { entity = new { index = entity.Index, version = entity.Version }, building,
                        x = pos.x, z = pos.z, intensity = fire.m_Intensity, rescueRequested = fire.m_RescueRequest != Entity.Null });
                }
            }
            var climate = World.GetOrCreateSystemManaged<Game.Simulation.ClimateSystem>();
            return BridgeResponse.Json(new { events = entries, burningBuildings, burningOtherObjects, burning,
                weather = new { temperature = (float)climate.temperature, precipitation = (float)climate.precipitation,
                    hail = climate.hail, freezingTemperature = climate.freezingTemperature,
                    season = climate.currentSeasonName, classification = climate.classification.ToString(),
                    raining = climate.isRaining, snowing = climate.isSnowing,
                    wind = new { x = climate.wind.x, z = climate.wind.y },
                    hailWarning = climate.hail > 0, note = "native climate values; not a fabricated disaster event or road-capacity measurement" },
                note = "native event journal plus current OnFire objects; active citywide events are independent of notification bubbles" });
        }
    }
}
