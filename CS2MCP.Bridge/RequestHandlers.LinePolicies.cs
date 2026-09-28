using System;
using System.Collections.Generic;
using Game.Prefabs;
using Game.Routes;
using Game.SceneFlow;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    /// <summary>
    /// Transit line policies (ticket price, vehicle count and the like): listed
    /// with the route policy query of PoliciesUISystem (what the line panel
    /// shows) and set through PoliciesUISystem.SetPolicy, the call the panel uses.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private EntityQuery m_RoutePolicyQuery;
        private bool m_RoutePolicyQueryCreated;

        private EntityQuery RoutePolicyQuery
        {
            get
            {
                if (!m_RoutePolicyQueryCreated)
                {
                    m_RoutePolicyQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[] { ComponentType.ReadOnly<PolicyData>() },
                        Any = new[]
                        {
                            ComponentType.ReadOnly<RouteOptionData>(),
                            ComponentType.ReadOnly<RouteModifierData>(),
                        },
                    });
                    m_RoutePolicyQueryCreated = true;
                }
                return m_RoutePolicyQuery;
            }
        }

        private BridgeResponse ListLinePolicies(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!TryResolveLine(request, out Entity line, out error))
            {
                return error;
            }

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            bool hasActive = EntityManager.HasBuffer<Game.Policies.Policy>(line);
            DynamicBuffer<Game.Policies.Policy> active = hasActive
                ? EntityManager.GetBuffer<Game.Policies.Policy>(line, isReadOnly: true)
                : default;
            var policies = new List<object>();
            using (NativeArray<Entity> entities = RoutePolicyQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    // Route policies ("Route Vehicle Count", "Route Ticket Price") are hidden from
                    // the city policy list but are exactly what the line panel shows, so keep them.
                    PolicyPrefab prefab = prefabSystem.GetPrefab<PolicyPrefab>(entity);
                    if (prefab == null)
                    {
                        continue;
                    }
                    bool isActive = false;
                    float adjustment = 0f;
                    for (int i = 0; hasActive && i < active.Length; i++)
                    {
                        if (active[i].m_Policy == entity)
                        {
                            isActive = (active[i].m_Flags & Game.Policies.PolicyFlags.Active) != 0;
                            adjustment = active[i].m_Adjustment;
                            break;
                        }
                    }
                    object slider = null;
                    if (EntityManager.HasComponent<PolicySliderData>(entity))
                    {
                        PolicySliderData data = EntityManager.GetComponentData<PolicySliderData>(entity);
                        slider = new { min = data.m_Range.min, max = data.m_Range.max, defaultValue = data.m_Default, step = data.m_Step };
                    }
                    string title = null;
                    GameManager.instance?.localizationManager?.activeDictionary?
                        .TryGetValue($"Policy.TITLE[{prefab.name}]", out title);
                    policies.Add(new
                    {
                        name = prefab.name,
                        title,
                        active = isActive,
                        adjustment,
                        slider,
                        locked = IsLocked(entity),
                    });
                }
            }
            return BridgeResponse.Json(new
            {
                line = new { index = line.Index, version = line.Version },
                name = LabelOf(World.GetOrCreateSystemManaged<Game.UI.NameSystem>(), line),
                policies,
                note = "set with cs2_set_line_policy; slider policies take an adjustment within the slider range",
            });
        }

        private BridgeResponse SetLinePolicy(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!TryResolveLine(request, out Entity line, out error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("name", out string policyName) || string.IsNullOrEmpty(policyName))
            {
                return BridgeResponse.Error(400, "provide ?name=<policy name from cs2_line_policies>");
            }
            if (!request.TryGetBool("active", out bool active))
            {
                return BridgeResponse.Error(400, "provide ?active=true|false");
            }
            request.TryGetFloat("adjustment", out float adjustment);

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            using (NativeArray<Entity> entities = RoutePolicyQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    PolicyPrefab prefab = prefabSystem.GetPrefab<PolicyPrefab>(entity);
                    if (prefab == null || !string.Equals(prefab.name, policyName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (IsLocked(entity))
                    {
                        return BridgeResponse.Error(409, $"policy '{prefab.name}' is locked (milestone not reached)");
                    }
                    // Same call the line panel uses (applied at the end of the frame).
                    World.GetOrCreateSystemManaged<PoliciesUISystem>().SetPolicy(line, entity, active, adjustment);
                    return BridgeResponse.Json(new
                    {
                        line = new { index = line.Index, version = line.Version },
                        name = prefab.name,
                        active,
                        adjustment,
                        note = "applied at end of frame",
                    });
                }
            }
            return BridgeResponse.Error(404, $"unknown line policy '{policyName}'; list names via cs2_line_policies");
        }

        private bool TryResolveLine(BridgeRequest request, out Entity line, out BridgeResponse error)
        {
            line = Entity.Null;
            error = null;
            if (!request.TryGetInt("index", out int index) || !request.TryGetInt("version", out int version))
            {
                error = BridgeResponse.Error(400, "provide ?index=&version= of a line from cs2_list_transit_lines");
                return false;
            }
            line = new Entity { Index = index, Version = version };
            if (!EntityManager.Exists(line) || !EntityManager.HasComponent<Route>(line)
                || !EntityManager.HasComponent<TransportLine>(line)
                || EntityManager.HasComponent<Game.Tools.Temp>(line) || EntityManager.HasComponent<Game.Common.Deleted>(line))
            {
                error = BridgeResponse.Error(404, $"{index}:{version} is not an existing transit line (see cs2_list_transit_lines)");
                return false;
            }
            return true;
        }
    }
}
