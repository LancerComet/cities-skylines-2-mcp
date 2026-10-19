using System;
using System.Collections.Generic;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// Read-only economy endpoints: the Economy panel's Production tab
    /// (per-resource production, consumption, balance, trade, storage) and
    /// natural-resource grids (fertility/ore/oil/fish cell map, forest from
    /// trees) with totals and richest clusters.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private EntityQuery m_ProdWoodQuery;
        private bool m_ProdWoodQueryCreated;
        private EntityQuery m_ProdDamagedTreeQuery;
        private bool m_ProdDamagedTreeQueryCreated;

        /// <summary>
        /// Same filter as NaturalResourcesInfoviewUISystem's wood query:
        /// Tree + Plant with a DISABLED Decoration (decorative trees placed by
        /// buildings keep Decoration enabled and hold no harvestable wood).
        /// </summary>
        private EntityQuery ProdWoodQuery
        {
            get
            {
                if (!m_ProdWoodQueryCreated)
                {
                    EntityQueryBuilder builder = new EntityQueryBuilder(Allocator.Temp)
                        .WithAll<Game.Objects.Tree, Game.Objects.Plant, Game.Objects.Transform, PrefabRef>()
                        .WithDisabled<Game.Objects.Decoration>()
                        .WithNone<Game.Tools.Temp, Game.Common.Deleted>();
                    m_ProdWoodQuery = builder.Build(EntityManager);
                    builder.Dispose();
                    m_ProdWoodQueryCreated = true;
                }
                return m_ProdWoodQuery;
            }
        }

        private EntityQuery ProdDamagedTreeQuery
        {
            get
            {
                if (!m_ProdDamagedTreeQueryCreated)
                {
                    EntityQueryBuilder builder = new EntityQueryBuilder(Allocator.Temp)
                        .WithAll<Game.Objects.Tree, Game.Objects.Damaged>()
                        .WithNone<Game.Tools.Temp, Game.Common.Deleted>();
                    m_ProdDamagedTreeQuery = builder.Build(EntityManager);
                    builder.Dispose();
                    m_ProdDamagedTreeQueryCreated = true;
                }
                return m_ProdDamagedTreeQuery;
            }
        }

        /// <summary>
        /// Mirrors ProductionUISystem + the panel's dye() formula in the game UI:
        /// production = consumptionProduction.y; consumption = |consumptionProduction.x|
        /// + |serviceUpkeep| + sum of |finalConsumption| over the resource's
        /// UIProductionLinks.m_FinalConsumers; balance = production - consumption.
        /// </summary>
        private BridgeResponse GetProduction(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            request.Query.TryGetValue("resource", out string resourceFilter);
            bool includeIdle = request.TryGetBool("includeIdle", out bool rawIncludeIdle) && rawIncludeIdle;

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            ResourcePrefabs resourcePrefabs = World.GetOrCreateSystemManaged<Game.Prefabs.ResourceSystem>().GetPrefabs();
            CityProductionStatisticSystem statistics = World.GetOrCreateSystemManaged<CityProductionStatisticSystem>();
            CountCityStoredResourceSystem storedSystem = World.GetOrCreateSystemManaged<CountCityStoredResourceSystem>();
            CityProductionCapacityCalculationSystem capacitySystem = World.GetOrCreateSystemManaged<CityProductionCapacityCalculationSystem>();

            // The *Temp arrays returned here are main-thread snapshots copied in
            // CityProductionStatisticSystem.OnUpdate; the vanilla UI reads them the same way.
            NativeArray<int2> consumptionProductions = statistics.GetConsumptionProductions();
            NativeArray<CityProductionStatisticSystem.CityResourceUsage> usages = statistics.GetCityResourceUsages();
            NativeArray<int> storedResources = storedSystem.GetCityStoredResources();

            var resources = new List<Dictionary<string, object>>();
            var balances = new List<KeyValuePair<string, int>>();
            ResourceIterator iterator = ResourceIterator.GetIterator();
            while (iterator.Next())
            {
                Resource resource = iterator.resource;
                Entity resourcePrefabEntity = resourcePrefabs[resource];
                if (resourcePrefabEntity == Entity.Null)
                {
                    continue;
                }
                ResourcePrefab prefab = prefabSystem.GetPrefab<ResourcePrefab>(resourcePrefabEntity);
                // ProductionUISystem.IterateResources lists leisure, material and produceable resources.
                if (prefab == null || !(prefab.m_IsLeisure || prefab.m_IsMaterial || prefab.m_IsProduceable))
                {
                    continue;
                }
                string name = resource.ToString();
                if (!string.IsNullOrEmpty(resourceFilter)
                    && !string.Equals(name, resourceFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int index = EconomyUtils.GetResourceIndex(resource);
                int2 consumptionProduction = index >= 0 && index < consumptionProductions.Length
                    ? consumptionProductions[index]
                    : default;
                CityProductionStatisticSystem.CityResourceUsage usage = index >= 0 && index < usages.Length
                    ? usages[index]
                    : default;
                int stored = index >= 0 && index < storedResources.Length ? storedResources[index] : 0;
                int maxProduction = capacitySystem.GetProductionCapacity(resource);

                int production = consumptionProduction.y;
                int companyInputs = math.abs(consumptionProduction.x);
                int serviceUpkeep = math.abs(usage[CityProductionStatisticSystem.CityResourceUsage.Consumer.ServiceUpkeep]);
                int households = math.abs(usage[CityProductionStatisticSystem.CityResourceUsage.Consumer.Citizens]);
                int industrial = math.abs(usage[CityProductionStatisticSystem.CityResourceUsage.Consumer.Industrial]);
                int commercial = math.abs(usage[CityProductionStatisticSystem.CityResourceUsage.Consumer.Commercial]);
                int retail = math.abs(usage[CityProductionStatisticSystem.CityResourceUsage.Consumer.Retail]);
                int office = math.abs(usage[CityProductionStatisticSystem.CityResourceUsage.Consumer.Office]);
                int heating = math.abs(usage[CityProductionStatisticSystem.CityResourceUsage.Consumer.Heating]);
                int levelUp = math.abs(usage[CityProductionStatisticSystem.CityResourceUsage.Consumer.LevelUp]);
                int importExport = usage[CityProductionStatisticSystem.CityResourceUsage.Consumer.ImportExport];

                // The panel only sums the final consumers the resource prefab lists.
                var panelConsumers = new List<string>();
                int finalConsumption = 0;
                if (prefab.TryGet(out UIProductionLinks links) && links.m_FinalConsumers != null)
                {
                    foreach (UIProductionLinkPrefab link in links.m_FinalConsumers)
                    {
                        if (link == null)
                        {
                            continue;
                        }
                        CityProductionStatisticSystem.CityResourceUsage.Consumer consumer = PanelConsumer(link.m_Type);
                        panelConsumers.Add(consumer.ToString());
                        finalConsumption += math.abs(usage[consumer]);
                    }
                }

                int consumption = companyInputs + serviceUpkeep + finalConsumption;
                int balance = production - consumption;

                bool idle = production == 0 && consumption == 0 && households == 0 && industrial == 0
                    && commercial == 0 && retail == 0 && office == 0 && importExport == 0 && stored == 0;
                if (idle && !includeIdle && string.IsNullOrEmpty(resourceFilter))
                {
                    continue;
                }

                balances.Add(new KeyValuePair<string, int>(name, balance));
                resources.Add(new Dictionary<string, object>
                {
                    ["resource"] = name,
                    ["unit"] = prefab.m_Weight > 0f ? "weight" : "count",
                    ["tradable"] = prefab.m_IsTradable,
                    ["production"] = production,
                    ["consumption"] = consumption,
                    ["balance"] = balance,
                    ["status"] = balance > 0 ? "surplus" : balance < 0 ? "deficit" : "balanced",
                    ["households"] = households,
                    ["companies"] = companyInputs + industrial + commercial + retail + office,
                    ["breakdown"] = new Dictionary<string, int>
                    {
                        ["companyInputs"] = companyInputs,
                        ["industrial"] = industrial,
                        ["commercial"] = commercial,
                        ["retail"] = retail,
                        ["office"] = office,
                        ["serviceUpkeep"] = serviceUpkeep,
                        ["heating"] = heating,
                        ["buildingLevelUp"] = levelUp,
                    },
                    ["panelFinalConsumers"] = panelConsumers,
                    ["importExport"] = importExport,
                    ["stored"] = stored,
                    ["maxProduction"] = maxProduction,
                });
            }

            balances.Sort((a, b) => a.Value.CompareTo(b.Value));
            var deficits = new List<object>();
            var surpluses = new List<object>();
            foreach (KeyValuePair<string, int> pair in balances)
            {
                if (pair.Value < 0 && deficits.Count < 10)
                {
                    deficits.Add(new { resource = pair.Key, balance = pair.Value });
                }
            }
            for (int i = balances.Count - 1; i >= 0 && surpluses.Count < 10; i--)
            {
                if (balances[i].Value > 0)
                {
                    surpluses.Add(new { resource = balances[i].Key, balance = balances[i].Value });
                }
            }

            return BridgeResponse.Json(new
            {
                note = "Same numbers as Economy > Production (ProductionUISystem). production = output of extractor and " +
                       "processing companies; consumption = companyInputs (raw materials used by industry) + serviceUpkeep + " +
                       "the final consumers the panel lists for the resource (households, commerce, industry, offices, " +
                       "heating, building level-up); balance = production - consumption. companies = companyInputs + " +
                       "industrial + commercial + retail + office. Values are smoothed rates updated 32 times per in-game day. " +
                       "importExport comes from TradeSystem (cargo-station storage trade with outside connections): " +
                       "negative = net import, positive = net export (sign inferred from the code; the panel itself never " +
                       "shows it). stored = resources held in city storage; maxProduction = the panel's capacity value.",
                stalenessWarning = m_System.SimulationHasTickedSinceLoad
                    ? null
                    : "simulation has not run since this save was loaded; production statistics may be zero or stale until it runs.",
                count = resources.Count,
                topDeficits = deficits,
                topSurpluses = surpluses,
                resources,
            });
        }

        /// <summary>Port of cye() in the game UI's production panel.</summary>
        private static CityProductionStatisticSystem.CityResourceUsage.Consumer PanelConsumer(ProductionChainActorType type)
        {
            switch (type)
            {
                case ProductionChainActorType.Upkeep:
                    return CityProductionStatisticSystem.CityResourceUsage.Consumer.LevelUp;
                case ProductionChainActorType.Industry:
                    return CityProductionStatisticSystem.CityResourceUsage.Consumer.Industrial;
                case ProductionChainActorType.Offices:
                    return CityProductionStatisticSystem.CityResourceUsage.Consumer.Office;
                case ProductionChainActorType.Commerce:
                    return CityProductionStatisticSystem.CityResourceUsage.Consumer.Commercial;
                case ProductionChainActorType.Heating:
                    return CityProductionStatisticSystem.CityResourceUsage.Consumer.Heating;
                case ProductionChainActorType.Consumers:
                    return CityProductionStatisticSystem.CityResourceUsage.Consumer.Citizens;
                default:
                    return CityProductionStatisticSystem.CityResourceUsage.Consumer.Retail;
            }
        }

        private BridgeResponse GetNaturalResources(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("resource", out string resourceName) || string.IsNullOrEmpty(resourceName))
            {
                return BridgeResponse.Error(400, "provide ?resource=fertility|ore|oil|fish|forest");
            }
            string resourceKey = resourceName.ToLowerInvariant();
            if (resourceKey != "fertility" && resourceKey != "ore" && resourceKey != "oil"
                && resourceKey != "fish" && resourceKey != "forest")
            {
                return BridgeResponse.Error(400, $"unknown resource '{resourceName}'; use fertility|ore|oil|fish|forest");
            }

            int resolution = request.TryGetInt("resolution", out int rawResolution)
                ? math.clamp(rawResolution, 8, 256)
                : 64;
            float thresholdFraction = request.TryGetFloat("threshold", out float rawThreshold)
                ? math.clamp(rawThreshold, 0.01f, 1f)
                : 0.25f;
            int clusterLimit = request.TryGetInt("clusters", out int rawClusters) ? math.clamp(rawClusters, 0, 50) : 10;

            float worldSize = kWorldHalfSize * 2f;
            float cellSize = worldSize / resolution;
            var sums = new double[resolution * resolution];
            var values = new float[resolution * resolution];
            object totals;
            string approach;
            string unit;

            if (resourceKey == "forest")
            {
                SampleForest(resolution, cellSize, sums, out int treeCount, out double totalWood);
                for (int i = 0; i < sums.Length; i++)
                {
                    values[i] = (float)sums[i];
                }
                approach = "trees: sum of ObjectUtils.CalculateWoodAmount over every non-decoration tree in each cell " +
                           "(the same per-tree formula NaturalResourcesInfoviewUISystem uses for the Forest total); " +
                           "there is no forest layer in the natural-resource cell map";
                unit = "harvestable wood per output cell";
                totals = new
                {
                    trees = treeCount,
                    wood = Math.Round(totalWood, 1),
                };
            }
            else
            {
                NaturalResourceSystem naturalResources = World.GetOrCreateSystemManaged<NaturalResourceSystem>();
                NativeArray<NaturalResourceCell> map = naturalResources.GetMap(readOnly: true, out JobHandle deps);
                deps.Complete();
                int sourceSize = (int)math.round(math.sqrt(map.Length));
                var counts = new int[resolution * resolution];
                double totalBase = 0;
                double totalUsed = 0;
                double totalAvailable = 0;
                int cellsWithResource = 0;
                for (int row = 0; row < sourceSize; row++)
                {
                    int outRow = math.min(row * resolution / sourceSize, resolution - 1);
                    for (int col = 0; col < sourceSize; col++)
                    {
                        int outCol = math.min(col * resolution / sourceSize, resolution - 1);
                        NaturalResourceAmount amount = SelectNaturalResource(map[row * sourceSize + col], resourceKey);
                        float available = math.max(0f, (float)amount.m_Base - amount.m_Used);
                        totalBase += amount.m_Base;
                        totalUsed += amount.m_Used;
                        totalAvailable += available;
                        if (available > 0f)
                        {
                            cellsWithResource++;
                        }
                        int outIndex = outRow * resolution + outCol;
                        sums[outIndex] += available;
                        counts[outIndex]++;
                    }
                }
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = counts[i] > 0 ? (float)(sums[i] / counts[i]) : 0f;
                }
                float nativeCellSize = worldSize / sourceSize;
                approach = $"NaturalResourceSystem cell map ({sourceSize}x{sourceSize} cells of {nativeCellSize:0.#} m), " +
                           "available = m_Base - m_Used per cell, averaged into the output grid";
                unit = "average available amount per native cell (0-10000, 10000 = full density)";
                totals = new
                {
                    baseAmount = Math.Round(totalBase),
                    usedAmount = Math.Round(totalUsed),
                    available = Math.Round(totalAvailable),
                    availableArea = Math.Round(naturalResources.ResourceAmountToArea((float)totalAvailable)),
                    availableAreaUnit = "m2 of full-density deposit (NaturalResourceSystem.ResourceAmountToArea)",
                    nativeCellsWithResource = cellsWithResource,
                    nativeCellSize,
                };
            }

            List<object> clusters = FindResourceClusters(values, sums, resolution, cellSize, thresholdFraction, clusterLimit);

            var rounded = new List<float>(values.Length);
            foreach (float v in values)
            {
                rounded.Add((float)Math.Round(v, 1));
            }

            return BridgeResponse.Json(new
            {
                resource = resourceKey,
                approach,
                resolution,
                worldMin = -kWorldHalfSize,
                worldMax = kWorldHalfSize,
                cellSize,
                unit,
                note = "row-major: index = row*resolution + col; world x = worldMin+(col+0.5)*cellSize, world z = worldMin+(row+0.5)*cellSize. " +
                       "clusters = 4-connected groups of cells >= threshold * the richest cell, largest amount first; " +
                       "check tile ownership with cs2_list_map_tiles before building there.",
                totals,
                clusterThreshold = thresholdFraction,
                clusters,
                values = rounded,
            });
        }

        private static NaturalResourceAmount SelectNaturalResource(NaturalResourceCell cell, string resourceKey)
        {
            switch (resourceKey)
            {
                case "fertility":
                    return cell.m_Fertility;
                case "ore":
                    return cell.m_Ore;
                case "oil":
                    return cell.m_Oil;
                default:
                    return cell.m_Fish;
            }
        }

        private void SampleForest(int resolution, float cellSize, double[] sums, out int treeCount, out double totalWood)
        {
            treeCount = 0;
            totalWood = 0;

            var damagedByEntity = new Dictionary<Entity, Game.Objects.Damaged>();
            using (NativeArray<Entity> damagedEntities = ProdDamagedTreeQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Game.Objects.Damaged> damaged = ProdDamagedTreeQuery.ToComponentDataArray<Game.Objects.Damaged>(Allocator.Temp))
            {
                for (int i = 0; i < damagedEntities.Length; i++)
                {
                    damagedByEntity[damagedEntities[i]] = damaged[i];
                }
            }

            var treeDataByPrefab = new Dictionary<Entity, TreeData>();
            using (NativeArray<Entity> entities = ProdWoodQuery.ToEntityArray(Allocator.TempJob))
            using (NativeArray<Game.Objects.Tree> trees = ProdWoodQuery.ToComponentDataArray<Game.Objects.Tree>(Allocator.TempJob))
            using (NativeArray<Game.Objects.Plant> plants = ProdWoodQuery.ToComponentDataArray<Game.Objects.Plant>(Allocator.TempJob))
            using (NativeArray<Game.Objects.Transform> transforms = ProdWoodQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.TempJob))
            using (NativeArray<PrefabRef> prefabRefs = ProdWoodQuery.ToComponentDataArray<PrefabRef>(Allocator.TempJob))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity prefab = prefabRefs[i].m_Prefab;
                    if (!treeDataByPrefab.TryGetValue(prefab, out TreeData treeData))
                    {
                        treeData = EntityManager.HasComponent<TreeData>(prefab)
                            ? EntityManager.GetComponentData<TreeData>(prefab)
                            : default;
                        treeDataByPrefab[prefab] = treeData;
                    }
                    // UpdateWoodJob skips prefabs with less than 1 wood.
                    if (treeData.m_WoodAmount < 1f)
                    {
                        continue;
                    }
                    damagedByEntity.TryGetValue(entities[i], out Game.Objects.Damaged damagedData);
                    float wood = Game.Objects.ObjectUtils.CalculateWoodAmount(trees[i], plants[i], damagedData, treeData, false);
                    if (wood <= 0f)
                    {
                        continue;
                    }
                    float3 position = transforms[i].m_Position;
                    int col = (int)math.floor((position.x + kWorldHalfSize) / cellSize);
                    int row = (int)math.floor((position.z + kWorldHalfSize) / cellSize);
                    if (col < 0 || row < 0 || col >= resolution || row >= resolution)
                    {
                        continue;
                    }
                    sums[row * resolution + col] += wood;
                    treeCount++;
                    totalWood += wood;
                }
            }
        }

        private List<object> FindResourceClusters(float[] values, double[] sums, int resolution, float cellSize,
            float thresholdFraction, int limit)
        {
            var result = new List<object>();
            if (limit <= 0)
            {
                return result;
            }
            float peak = 0f;
            double grandTotal = 0;
            for (int i = 0; i < values.Length; i++)
            {
                peak = math.max(peak, values[i]);
                grandTotal += sums[i];
            }
            if (peak <= 0f)
            {
                return result;
            }
            float threshold = peak * thresholdFraction;

            var visited = new bool[values.Length];
            var stack = new Stack<int>();
            var found = new List<(double amount, object info)>();
            for (int start = 0; start < values.Length; start++)
            {
                if (visited[start] || values[start] < threshold)
                {
                    continue;
                }
                int minCol = int.MaxValue, maxCol = int.MinValue, minRow = int.MaxValue, maxRow = int.MinValue;
                int cells = 0;
                double amount = 0;
                double weightX = 0;
                double weightZ = 0;
                float clusterPeak = 0f;
                visited[start] = true;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int index = stack.Pop();
                    int row = index / resolution;
                    int col = index % resolution;
                    cells++;
                    amount += sums[index];
                    clusterPeak = math.max(clusterPeak, values[index]);
                    double centerX = -kWorldHalfSize + (col + 0.5) * cellSize;
                    double centerZ = -kWorldHalfSize + (row + 0.5) * cellSize;
                    weightX += centerX * sums[index];
                    weightZ += centerZ * sums[index];
                    minCol = math.min(minCol, col);
                    maxCol = math.max(maxCol, col);
                    minRow = math.min(minRow, row);
                    maxRow = math.max(maxRow, row);

                    if (col > 0) TryPushCluster(index - 1, values, visited, stack, threshold);
                    if (col < resolution - 1) TryPushCluster(index + 1, values, visited, stack, threshold);
                    if (row > 0) TryPushCluster(index - resolution, values, visited, stack, threshold);
                    if (row < resolution - 1) TryPushCluster(index + resolution, values, visited, stack, threshold);
                }
                double cx = amount > 0 ? weightX / amount : -kWorldHalfSize + (minCol + maxCol + 1) * 0.5 * cellSize;
                double cz = amount > 0 ? weightZ / amount : -kWorldHalfSize + (minRow + maxRow + 1) * 0.5 * cellSize;
                found.Add((amount, new
                {
                    cells,
                    amount = Math.Round(amount, 1),
                    shareOfTotal = grandTotal > 0 ? Math.Round(amount / grandTotal, 3) : 0,
                    peak = Math.Round(clusterPeak, 1),
                    center = new { x = Math.Round(cx, 1), z = Math.Round(cz, 1) },
                    bounds = new
                    {
                        minX = -kWorldHalfSize + minCol * cellSize,
                        minZ = -kWorldHalfSize + minRow * cellSize,
                        maxX = -kWorldHalfSize + (maxCol + 1) * cellSize,
                        maxZ = -kWorldHalfSize + (maxRow + 1) * cellSize,
                    },
                }));
            }
            found.Sort((a, b) => b.amount.CompareTo(a.amount));
            for (int i = 0; i < found.Count && i < limit; i++)
            {
                result.Add(found[i].info);
            }
            return result;
        }

        private static void TryPushCluster(int index, float[] values, bool[] visited, Stack<int> stack, float threshold)
        {
            if (!visited[index] && values[index] >= threshold)
            {
                visited[index] = true;
                stack.Push(index);
            }
        }
    }
}
