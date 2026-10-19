using System;
using Game.City;
using Game.Economy;
using Game.Simulation;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        /// <summary>
        /// GET /city/taxes/resource?resource=Minerals[&amp;rate=-10]: reads, or with rate sets, the
        /// industrial tax rate of one produced resource (the Taxes panel's per-resource sliders).
        /// Without a resource it lists every resource's industrial rate.
        /// </summary>
        private BridgeResponse HandleResourceTax(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            TaxSystem tax = World.GetOrCreateSystemManaged<TaxSystem>();
            int2 limits = tax.GetTaxParameterData().m_TotalTaxLimits;

            if (!request.Query.TryGetValue("resource", out string name) || string.IsNullOrEmpty(name))
            {
                var rates = new System.Collections.Generic.Dictionary<string, int>();
                foreach (Resource r in Enum.GetValues(typeof(Resource)))
                {
                    if (r == Resource.NoResource || r == Resource.Last || r == Resource.Money)
                    {
                        continue;
                    }
                    rates[r.ToString()] = tax.GetIndustrialTaxRate(r);
                }
                return BridgeResponse.Json(new { allowedRange = new { min = limits.x, max = limits.y }, industrialRatesByResource = rates });
            }

            if (!Enum.TryParse(name, ignoreCase: true, out Resource resource)
                || resource == Resource.NoResource || resource == Resource.Last)
            {
                return BridgeResponse.Error(400, $"unknown resource '{name}'; use a name from /city/production, e.g. Minerals");
            }

            int before = tax.GetIndustrialTaxRate(resource);
            if (request.TryGetInt("rate", out int rate))
            {
                int applied = math.clamp(rate, limits.x, limits.y);
                tax.SetIndustrialTaxRate(resource, applied);
            }
            return BridgeResponse.Json(new
            {
                resource = resource.ToString(),
                previousRate = before,
                rate = tax.GetIndustrialTaxRate(resource),
                allowedRange = new { min = limits.x, max = limits.y },
            });
        }
    }
}
