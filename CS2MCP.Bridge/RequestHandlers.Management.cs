using System;
using System.Collections.Generic;
using Game.City;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// Management endpoints: loans (borrow/repay) and service fees
    /// (electricity/water/education... pricing), plus a generic standalone
    /// object listing (trees etc.) so placed decorations can be found and
    /// demolished again.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private EntityQuery m_StandaloneObjectQuery;
        private bool m_StandaloneObjectQueryCreated;

        private EntityQuery StandaloneObjectQuery
        {
            get
            {
                if (!m_StandaloneObjectQueryCreated)
                {
                    m_StandaloneObjectQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Game.Objects.Static>(),
                            ComponentType.ReadOnly<Game.Objects.Transform>(),
                            ComponentType.ReadOnly<PrefabRef>(),
                        },
                        Any = new[]
                        {
                            ComponentType.ReadOnly<Game.Objects.Tree>(),
                            ComponentType.ReadOnly<Game.Objects.Plant>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Game.Tools.Temp>(),
                            ComponentType.ReadOnly<Game.Common.Deleted>(),
                            ComponentType.ReadOnly<Game.Common.Owner>(),
                        },
                    });
                    m_StandaloneObjectQueryCreated = true;
                }
                return m_StandaloneObjectQuery;
            }
        }

        private BridgeResponse GetLoan()
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            LoanSystem loans = World.GetOrCreateSystemManaged<LoanSystem>();
            LoanInfo current = loans.CurrentLoan;
            return BridgeResponse.Json(new
            {
                currentLoan = new
                {
                    amount = current.m_Amount,
                    dailyInterestRate = current.m_DailyInterestRate,
                    dailyPayment = current.m_DailyPayment,
                },
                creditworthiness = loans.Creditworthiness,
                note = "set the loan principal with /city/loan/set?amount=N (0 repays fully, max = creditworthiness)",
            });
        }

        private BridgeResponse SetLoan(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.TryGetInt("amount", out int amount))
            {
                return BridgeResponse.Error(400, "provide ?amount=<int> (new loan principal; 0 repays fully)");
            }
            LoanSystem loans = World.GetOrCreateSystemManaged<LoanSystem>();
            if (amount < 0 || amount > loans.Creditworthiness) return BridgeResponse.Error(400, "loan principal must be within native creditworthiness range");
            int applied = amount;
            LoanInfo offer = loans.RequestLoanOffer(applied);
            loans.ChangeLoan(applied);
            LoanInfo current = loans.CurrentLoan;
            return BridgeResponse.Json(new
            {
                requestedAmount = amount,
                appliedAmount = applied,
                offer = new { offer.m_Amount, offer.m_DailyInterestRate, offer.m_DailyPayment },
                currentLoan = new { current.m_Amount, current.m_DailyInterestRate, current.m_DailyPayment },
            });
        }

        private BridgeResponse GetFees()
        {
            if (!TryGetCity(out Entity city, out BridgeResponse error))
            {
                return error;
            }
            ServiceFeeSystem feeSystem = World.GetOrCreateSystemManaged<ServiceFeeSystem>();
            ServiceFeeParameterData parameters;
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ServiceFeeParameterData>()))
                parameters = query.GetSingleton<ServiceFeeParameterData>();
            DynamicBuffer<ServiceFee> fees = EntityManager.GetBuffer<ServiceFee>(city, isReadOnly: true);
            var result = new Dictionary<string, object>();
            foreach (PlayerResource resource in Enum.GetValues(typeof(PlayerResource)))
            {
                if ((int)resource < 0)
                {
                    continue;
                }
                if (ServiceFeeSystem.TryGetFee(resource, fees, out float fee))
                {
                    int3 totals = feeSystem.GetServiceFees(resource);
                    FeeParameters limits = parameters.GetFeeParameters(resource);
                    result[resource.ToString()] = new
                    {
                        fee,
                        estimatedMonthlyIncome = feeSystem.GetServiceFeeIncomeEstimate(resource, fee),
                        sliderRange = new { min = 0, max = limits.m_Max, defaultValue = limits.m_Default },
                        adjustable = resource != PlayerResource.Parking && limits.m_Adjustable,
                        incomeInternal = totals.x, incomeExports = totals.y, expenseImports = -totals.z,
                    };
                }
            }
            return BridgeResponse.Json(new
            {
                note = "set with /city/fees/set?resource=<name>&fee=<float>; fees affect service income and citizen happiness",
                fees = result,
            });
        }

        private BridgeResponse SetFee(BridgeRequest request)
        {
            if (!TryGetCity(out Entity city, out BridgeResponse error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("resource", out string resourceName)
                || !Enum.TryParse(resourceName, ignoreCase: true, out PlayerResource resource)
                || !Enum.IsDefined(typeof(PlayerResource), resource))
            {
                return BridgeResponse.Error(400,
                    $"provide ?resource=<{string.Join("|", Enum.GetNames(typeof(PlayerResource)))}>");
            }
            if (!request.TryGetFloat("fee", out float fee) || float.IsNaN(fee) || float.IsInfinity(fee))
            {
                return BridgeResponse.Error(400, "provide ?fee=<float>");
            }
            FeeParameters limits;
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ServiceFeeParameterData>()))
                limits = query.GetSingleton<ServiceFeeParameterData>().GetFeeParameters(resource);
            if (resource == PlayerResource.Parking || !limits.m_Adjustable)
                return BridgeResponse.Error(400, "fee is not adjustable in the native service panel; use the relevant facility or line settings");
            if (fee < 0 || fee > limits.m_Max) return BridgeResponse.Error(400, $"fee must be within native range 0..{limits.m_Max}");
            EntityManager.CompleteDependencyBeforeRW<ServiceFee>();
            DynamicBuffer<ServiceFee> fees = EntityManager.GetBuffer<ServiceFee>(city);
            if (!ServiceFeeSystem.TryGetFee(resource, fees, out float previous))
            {
                return BridgeResponse.Error(400, $"resource '{resource}' has no adjustable fee in this city");
            }
            ServiceFeeSystem.SetFee(resource, fees, fee);
            return BridgeResponse.Json(new
            {
                resource = resource.ToString(),
                previousFee = previous,
                newFee = fee,
            });
        }

        private BridgeResponse ListObjects(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            request.Query.TryGetValue("query", out string search);
            int limit = request.TryGetInt("limit", out int rawLimit) ? math.clamp(rawLimit, 1, 500) : 100;
            bool hasCenter = request.TryGetFloat("x", out float x) & request.TryGetFloat("z", out float z);
            float radius = request.TryGetFloat("radius", out float rawRadius) ? math.max(rawRadius, 1f) : 250f;
            float2 center = new float2(x, z);

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var results = new List<object>();
            int total = 0;
            using (NativeArray<Entity> entities = StandaloneObjectQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    Game.Objects.Transform transform = EntityManager.GetComponentData<Game.Objects.Transform>(entity);
                    if (hasCenter && math.distance(transform.m_Position.xz, center) > radius)
                    {
                        continue;
                    }
                    PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    string name = prefab != null ? prefab.name : "<unknown>";
                    if (!string.IsNullOrEmpty(search) && name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    total++;
                    if (results.Count < limit)
                    {
                        results.Add(new
                        {
                            entity = new { index = entity.Index, version = entity.Version },
                            prefab = name,
                            position = new { x = transform.m_Position.x, y = transform.m_Position.y, z = transform.m_Position.z },
                        });
                    }
                }
            }
            return BridgeResponse.Json(new
            {
                totalMatches = total,
                returned = results.Count,
                note = "standalone trees/plants; use entity index+version with /build/demolish",
                objects = results,
            });
        }
    }
}
