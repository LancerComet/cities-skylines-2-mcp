using System;
using System.Collections.Generic;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        /// <summary>当前产业母体的作业线声明与真实关系；不排队、不激活工具、不修改ECS。</summary>
        private BridgeResponse GetIndustryWorkRoutes(BridgeRequest request)
        {
            if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                return BridgeResponse.Error(405, "industry work routes are GET-only");
            foreach (string key in request.Query.Keys)
                if (key != "index" && key != "version" && key != "expectedLoadedSaveGuid")
                    return BridgeResponse.Error(400, "unsupported work-route query parameter: " + key);
            if (!request.Query.TryGetValue("expectedLoadedSaveGuid", out string expected)
                || !Guid.TryParse(expected, out Guid expectedGuid) || expectedGuid == Guid.Empty)
                return BridgeResponse.Error(400, "provide the nonempty current expectedLoadedSaveGuid");
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            string guid = World.GetExistingSystemManaged<Game.Serialization.LoadGameSystem>()?.context.instigatorGuid.ToString();
            if (!Guid.TryParse(guid, out Guid actualGuid) || actualGuid == Guid.Empty || expectedGuid != actualGuid)
                return BridgeResponse.Error(409, "loaded save identity does not match expectedLoadedSaveGuid");
            if (!request.TryGetInt("index", out int index) || index < 1
                || !request.TryGetInt("version", out int version) || version < 1)
                return BridgeResponse.Error(400, "provide a positive complete mother building index/version");
            var mother = new Entity { Index = index, Version = version };
            if (!EntityManager.Exists(mother) || EntityManager.HasComponent<Temp>(mother)
                || EntityManager.HasComponent<Deleted>(mother))
                return BridgeResponse.Error(404, "mother building is stale, temporary or deleted");
            if (!EntityManager.HasComponent<Game.Buildings.Building>(mother)
                || !EntityManager.HasComponent<Game.Objects.Transform>(mother)
                || !EntityManager.HasComponent<PrefabRef>(mother))
                return BridgeResponse.Error(400, "mother must be a normal placed building with a prefab and transform");
            if (World.GetExistingSystemManaged<PrefabSystem>() == null)
                return BridgeResponse.Error(409, "native prefab system unavailable");
            try { return ReadIndustryWorkRoutes(mother, guid); }
            catch (WorkRouteReadBudgetException e) { return BridgeResponse.Error(413, e.Message); }
        }

        private BridgeResponse ReadIndustryWorkRoutes(Entity mother, string guid)
        {
            var budget = new WorkRouteReadBudget();
            var problems = new List<string>();
            Entity motherPrefab = EntityManager.GetComponentData<PrefabRef>(mother).m_Prefab;
            if (!EntityManager.Exists(motherPrefab))
                return BridgeResponse.Error(409, "mother prefab is missing; declarations cannot be determined");
            bool declarationKnown = EntityManager.HasBuffer<BuildingUpgradeElement>(motherPrefab);
            var upgrades = new List<object>();
            var declaredWorkRoutes = new HashSet<Entity>();
            if (declarationKnown)
            {
                var elements = EntityManager.GetBuffer<BuildingUpgradeElement>(motherPrefab, true);
                WorkRouteReadBudget.Check(elements.Length, WorkRouteReadBudget.DeclaredUpgrades);
                budget.Take(elements.Length);
                for (int i = 0; i < elements.Length; i++)
                {
                    Entity prefab = elements[i].m_Upgrade;
                    bool exists = EntityManager.Exists(prefab);
                    bool work = exists && EntityManager.HasComponent<WorkRouteData>(prefab);
                    bool service = exists && EntityManager.HasComponent<ServiceUpgradeData>(prefab);
                    bool connection = exists && EntityManager.HasComponent<RouteConnectionData>(prefab);
                    if (work) declaredWorkRoutes.Add(prefab);
                    if (!exists) problems.Add("declared upgrade prefab missing at order " + i);
                    var native = exists ? World.GetExistingSystemManaged<PrefabSystem>().GetPrefab<PrefabBase>(prefab) : null;
                    bool registrationKnown = exists && native != null && (!work || (service && connection
                        && EntityManager.HasComponent<RouteData>(prefab)));
                    if (!registrationKnown) problems.Add("declared upgrade registration incomplete at order " + i);
                    upgrades.Add(new {
                        order = i, prefabEntity = Ref(prefab), prefab = exists ? NativeName(prefab) : null,
                        nativeType = native?.GetType().Name, exists, registrationKnown, isWorkRoute = work,
                        normallyLocked = exists ? (object)IsLocked(prefab) : null,
                        workRouteData = work ? OptionalModApi.JsonValue(EntityManager.GetComponentData<WorkRouteData>(prefab)) : null,
                        routeConnectionData = connection ? OptionalModApi.JsonValue(EntityManager.GetComponentData<RouteConnectionData>(prefab)) : null,
                        serviceUpgradeData = service ? OptionalModApi.JsonValue(EntityManager.GetComponentData<ServiceUpgradeData>(prefab)) : null,
                    });
                }
            }
            else problems.Add("mother has no native upgrade declaration buffer; declarations unknown");

            var routes = new List<object>();
            int candidateCount, unresolvedOwnerChains = 0, ownerlessRoutes = 0;
            using (var query = EntityManager.CreateEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<WorkRoute>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            }))
            {
                candidateCount = query.CalculateEntityCount();
                WorkRouteReadBudget.Check(candidateCount, WorkRouteReadBudget.CandidateRoutes);
                using (var entities = query.ToEntityArray(Allocator.Temp))
                {
                    for (int i = 0; i < entities.Length; i++)
                    {
                        Entity route = entities[i];
                        if (!EntityManager.HasComponent<Owner>(route)
                            || EntityManager.GetComponentData<Owner>(route).m_Owner == Entity.Null)
                        { ownerlessRoutes++; continue; }
                        var trace = TransitPathTrace.WalkOwners(route, Entity.Null, EntityManager.Exists,
                            e => EntityManager.HasComponent<Owner>(e) ? EntityManager.GetComponentData<Owner>(e).m_Owner : Entity.Null, 32);
                        budget.Take(trace.Entities.Count);
                        if (trace.Termination == "ownerDepthLimit")
                            throw new WorkRouteReadBudgetException("work-route owner depth budget exceeded; no partial result returned");
                        if (!trace.Complete) unresolvedOwnerChains++;
                        if (!trace.Entities.Contains(mother)) continue;
                        var owners = new List<object>();
                        foreach (Entity e in trace.Entities) owners.Add(Ref(e));
                        routes.Add(ReadIndustryWorkRoute(route, mother, owners, trace.Complete, trace.Termination, declarationKnown,
                            declaredWorkRoutes, budget, problems));
                    }
                }
            }
            if (unresolvedOwnerChains > 0) problems.Add("some global work-route owner chains are damaged; association coverage is incomplete");
            if (ownerlessRoutes > 0) problems.Add("global ownerless work routes exist and cannot be attributed to this mother");
            int processId;
            using (var process = System.Diagnostics.Process.GetCurrentProcess()) processId = process.Id;
            return BridgeResponse.Json(new {
                schemaVersion = 1, capturedAtUtc = DateTime.UtcNow.ToString("o"), loadedSaveGuid = guid, processId,
                readOnly = true, mother = Ref(mother), motherPrefab = NativeName(motherPrefab),
                motherPosition = WorkRouteEntityPosition(mother), declarationKnown,
                declaredWorkRouteCount = declarationKnown ? (object)declaredWorkRoutes.Count : null,
                declaredUpgrades = upgrades, routes, matchedRouteCount = routes.Count,
                globalWorkRouteCount = candidateCount, globalOwnerlessWorkRouteCount = ownerlessRoutes,
                globalUnresolvedOwnerChainCount = unresolvedOwnerChains, complete = problems.Count == 0,
                incompleteReasons = problems, budgetUsed = budget.Used,
                lockStalenessWarning = LockStalenessWarning, creationSupported = false, operatingAcceptanceEvaluated = false,
                note = "native declarations, association, paths and route vehicle references only; no creation or production/transport success inferred",
            });
        }

        private object ReadIndustryWorkRoute(Entity route, Entity mother, List<object> owners, bool ownerChainComplete,
            string ownerTermination, bool declarationKnown, HashSet<Entity> declared, WorkRouteReadBudget budget, List<string> problems)
        {
            bool prefabKnown = EntityManager.HasComponent<PrefabRef>(route);
            Entity prefab = prefabKnown ? EntityManager.GetComponentData<PrefabRef>(route).m_Prefab : Entity.Null;
            bool routeKnown = EntityManager.HasComponent<Route>(route);
            bool waypointsKnown = EntityManager.HasBuffer<RouteWaypoint>(route);
            bool segmentsKnown = EntityManager.HasBuffer<RouteSegment>(route);
            bool vehiclesKnown = EntityManager.HasBuffer<RouteVehicle>(route);
            var points = new List<object>(); var segments = new List<object>(); var vehicles = new List<object>();
            if (!prefabKnown || !EntityManager.Exists(prefab) || !routeKnown || !waypointsKnown || !segmentsKnown || !vehiclesKnown)
                problems.Add("matched route missing required native data: " + route.Index + ":" + route.Version);
            if (waypointsKnown)
            {
                var buffer = EntityManager.GetBuffer<RouteWaypoint>(route, true); budget.Take(buffer.Length);
                for (int i = 0; i < buffer.Length; i++)
                {
                    Entity e = buffer[i].m_Waypoint; bool exists = EntityManager.Exists(e);
                    bool connected = exists && EntityManager.HasComponent<Connected>(e);
                    Entity connection = connected ? EntityManager.GetComponentData<Connected>(e).m_Connected : Entity.Null;
                    bool lanes = exists && EntityManager.HasComponent<RouteLane>(e);
                    bool access = exists && EntityManager.HasComponent<AccessLane>(e);
                    if (!exists || WorkRouteEntityPosition(e) == null || !lanes || !access
                        || (connected && connection != Entity.Null && !EntityManager.Exists(connection)))
                        problems.Add("matched route waypoint data incomplete: " + e.Index + ":" + e.Version);
                    points.Add(new { order = i, waypoint = Ref(e), exists, position = WorkRouteEntityPosition(e),
                        connectedComponentKnown = connected, connection = connected ? Ref(connection) : null,
                        connectionExists = connected ? (object)EntityManager.Exists(connection) : null,
                        connectionPosition = connected ? WorkRouteEntityPosition(connection) : null,
                        routeLane = lanes ? OptionalModApi.JsonValue(EntityManager.GetComponentData<RouteLane>(e)) : null,
                        accessLane = access ? OptionalModApi.JsonValue(EntityManager.GetComponentData<AccessLane>(e)) : null });
                }
            }
            if (segmentsKnown)
            {
                var buffer = EntityManager.GetBuffer<RouteSegment>(route, true); budget.Take(buffer.Length);
                for (int i = 0; i < buffer.Length; i++)
                {
                    Entity e = buffer[i].m_Segment; bool exists = EntityManager.Exists(e);
                    bool info = exists && EntityManager.HasComponent<Game.Pathfind.PathInformation>(e);
                    bool targets = exists && EntityManager.HasComponent<PathTargets>(e);
                    bool path = exists && EntityManager.HasBuffer<Game.Pathfind.PathElement>(e);
                    var elements = new List<object>();
                    if (!exists || !info || !targets || !path) problems.Add("matched route segment data incomplete: " + e.Index + ":" + e.Version);
                    if (path)
                    {
                        var pathBuffer = EntityManager.GetBuffer<Game.Pathfind.PathElement>(e, true); budget.Take(pathBuffer.Length);
                        for (int k = 0; k < pathBuffer.Length; k++)
                            elements.Add(new { order = k, data = OptionalModApi.JsonValue(pathBuffer[k]),
                                targetExists = EntityManager.Exists(pathBuffer[k].m_Target) });
                    }
                    segments.Add(new { order = i, segment = Ref(e), exists,
                        information = info ? OptionalModApi.JsonValue(EntityManager.GetComponentData<Game.Pathfind.PathInformation>(e)) : null,
                        targets = targets ? OptionalModApi.JsonValue(EntityManager.GetComponentData<PathTargets>(e)) : null,
                        pathElementsKnown = path, pathElementCount = path ? (object)elements.Count : null, pathElements = elements });
                }
            }
            if (vehiclesKnown)
            {
                var buffer = EntityManager.GetBuffer<RouteVehicle>(route, true); budget.Take(buffer.Length);
                for (int i = 0; i < buffer.Length; i++)
                {
                    Entity e = buffer[i].m_Vehicle; bool exists = EntityManager.Exists(e);
                    if (!exists) problems.Add("matched route vehicle reference is stale: " + e.Index + ":" + e.Version);
                    vehicles.Add(new { vehicle = Ref(e), exists, position = WorkRouteEntityPosition(e),
                        prefab = exists && EntityManager.HasComponent<PrefabRef>(e)
                            ? NativeName(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab) : null });
                }
            }
            return new { route = Ref(route), directOwner = Ref(EntityManager.GetComponentData<Owner>(route).m_Owner),
                associatedMother = Ref(mother), ownerChain = owners, ownerChainComplete, ownerTermination,
                prefab = EntityManager.Exists(prefab) ? NativeName(prefab) : null,
                declaredByMother = declarationKnown && prefabKnown && EntityManager.Exists(prefab) ? (object)declared.Contains(prefab) : null,
                nativeRoute = routeKnown ? OptionalModApi.JsonValue(EntityManager.GetComponentData<Route>(route)) : null,
                nativeCompleteFlag = routeKnown ? (object)((EntityManager.GetComponentData<Route>(route).m_Flags & RouteFlags.Complete) != 0) : null,
                waypointsKnown, waypointCount = waypointsKnown ? (object)points.Count : null, waypoints = points,
                segmentsKnown, segmentCount = segmentsKnown ? (object)segments.Count : null, segments,
                vehiclesKnown, vehicleCount = vehiclesKnown ? (object)vehicles.Count : null, vehicles };
        }

        private object WorkRouteEntityPosition(Entity entity)
        {
            if (!EntityManager.Exists(entity)) return null;
            float3 p;
            if (EntityManager.HasComponent<Position>(entity)) p = EntityManager.GetComponentData<Position>(entity).m_Position;
            else if (EntityManager.HasComponent<Game.Objects.Transform>(entity)) p = EntityManager.GetComponentData<Game.Objects.Transform>(entity).m_Position;
            else return null;
            return math.all(math.isfinite(p)) ? new { x = p.x, y = p.y, z = p.z } : null;
        }
    }
}
