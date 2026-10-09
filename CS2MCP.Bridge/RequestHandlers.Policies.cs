using System;
using System.Collections.Generic;
using Game.Policies;
using Game.Prefabs;
using Game.SceneFlow;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    /// <summary>
    /// City policy endpoints. Listing mirrors PoliciesUISystem's city policy
    /// query (PolicyData + CityOptionData|CityModifierData); toggling goes
    /// through PoliciesUISystem.SetCityPolicy - the same path the game UI uses,
    /// so the change is applied via the end-of-frame command buffer.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private EntityQuery m_CityPolicyQuery;
        private bool m_CityPolicyQueryCreated;

        private EntityQuery CityPolicyQuery
        {
            get
            {
                if (!m_CityPolicyQueryCreated)
                {
                    m_CityPolicyQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[] { ComponentType.ReadOnly<PolicyData>() },
                        Any = new[]
                        {
                            ComponentType.ReadOnly<CityOptionData>(),
                            ComponentType.ReadOnly<CityModifierData>(),
                        },
                    });
                    m_CityPolicyQueryCreated = true;
                }
                return m_CityPolicyQuery;
            }
        }

        private BridgeResponse GetPolicies()
        {
            if (!TryGetCity(out Entity city, out BridgeResponse error))
            {
                return error;
            }

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            DynamicBuffer<Policy> activePolicies = EntityManager.GetBuffer<Policy>(city, isReadOnly: true);

            var policies = new List<object>();
            using (NativeArray<Entity> entities = CityPolicyQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    PolicyPrefab prefab = prefabSystem.GetPrefab<PolicyPrefab>(entity);
                    if (prefab == null || prefab.m_Visibility == PolicyVisibility.HideFromPolicyList)
                    {
                        continue;
                    }

                    bool active = false;
                    float adjustment = 0f;
                    for (int i = 0; i < activePolicies.Length; i++)
                    {
                        if (activePolicies[i].m_Policy == entity)
                        {
                            active = (activePolicies[i].m_Flags & PolicyFlags.Active) != 0;
                            adjustment = activePolicies[i].m_Adjustment;
                            break;
                        }
                    }

                    string title = null;
                    GameManager.instance?.localizationManager?.activeDictionary?
                        .TryGetValue($"Policy.TITLE[{prefab.name}]", out title);

                    policies.Add(new
                    {
                        name = prefab.name,
                        title,
                        active,
                        adjustment,
                        hasSlider = EntityManager.HasComponent<PolicySliderData>(entity),
                        slider = PolicySlider(entity),
                        locked = IsLocked(entity),
                    });
                }
            }

            return BridgeResponse.Json(new
            {
                note = "toggle with /city/policies/set?name=<name>&active=true|false (optional &adjustment=<float> for slider policies)",
                stalenessWarning = LockStalenessWarning,
                policies,
            });
        }

        private BridgeResponse HandleSetPolicy(BridgeRequest request)
        {
            if (!TryGetCity(out Entity city, out BridgeResponse error))
            {
                return error;
            }

            if (!request.Query.TryGetValue("name", out string policyName) || string.IsNullOrEmpty(policyName))
            {
                return BridgeResponse.Error(400, "provide ?name=<policy name from /city/policies>");
            }
            if (!request.TryGetBool("active", out bool active))
            {
                return BridgeResponse.Error(400, "provide ?active=true|false");
            }

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            using (NativeArray<Entity> entities = CityPolicyQuery.ToEntityArray(Allocator.Temp))
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

                    if (!TryPolicyAdjustment(request, city, entity, out float adjustment, out error)) return error;
                    World.GetOrCreateSystemManaged<PoliciesUISystem>().SetCityPolicy(entity, active, adjustment);
                    return BridgeResponse.Json(new
                    {
                        name = prefab.name,
                        active,
                        adjustment,
                        note = "applied at end of frame",
                    });
                }
            }

            return BridgeResponse.Error(404, $"unknown policy '{policyName}'; list names via /city/policies");
        }

        // 原生 SetPolicy 只入队，不能代替界面滑块的上限校验；省略数值时保留现值或原生默认值。
        private object PolicySlider(Entity policy)
        {
            if (!EntityManager.HasComponent<PolicySliderData>(policy)) return null;
            var data = EntityManager.GetComponentData<PolicySliderData>(policy);
            return new { min = data.m_Range.min, max = data.m_Range.max, defaultValue = data.m_Default, step = data.m_Step };
        }

        private bool TryPolicyAdjustment(BridgeRequest request, Entity target, Entity policy, out float adjustment, out BridgeResponse error)
        {
            adjustment = 0; error = null;
            bool provided = request.Query.ContainsKey("adjustment");
            if (provided && (!request.TryGetFloat("adjustment", out adjustment) || float.IsNaN(adjustment) || float.IsInfinity(adjustment))) {
                error = BridgeResponse.Error(400, "adjustment must be finite"); return false;
            }
            if (!EntityManager.HasComponent<PolicySliderData>(policy)) return true;
            var data = EntityManager.GetComponentData<PolicySliderData>(policy);
            if (!provided) {
                adjustment = data.m_Default;
                if (EntityManager.HasBuffer<Policy>(target)) foreach (var item in EntityManager.GetBuffer<Policy>(target, true))
                    if (item.m_Policy == policy) { adjustment = item.m_Adjustment; break; }
            }
            if (float.IsNaN(adjustment) || float.IsInfinity(adjustment) || adjustment < data.m_Range.min || adjustment > data.m_Range.max) {
                error = BridgeResponse.Error(400, $"adjustment must be within native range {data.m_Range.min}..{data.m_Range.max}"); return false;
            }
            return true;
        }
    }
}
