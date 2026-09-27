using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Areas;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// Map tile listing and purchasing. Purchases go through the game's own
    /// MapTilePurchaseSystem.PurchaseSelection (what the Map Tiles panel's
    /// Purchase button calls), so the game prices the tiles, checks permits
    /// and funds, charges the treasury and unlocks them.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private EntityQuery m_MapTileAreaQuery;
        private bool m_MapTileAreaQueryCreated;
        private EntityQuery m_SelectionBufferQuery;
        private bool m_SelectionBufferQueryCreated;

        private EntityQuery MapTileAreaQuery
        {
            get
            {
                if (!m_MapTileAreaQueryCreated)
                {
                    m_MapTileAreaQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<MapTile>(),
                            ComponentType.ReadOnly<Geometry>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Temp>(),
                            ComponentType.ReadOnly<Game.Common.Deleted>(),
                        },
                    });
                    m_MapTileAreaQueryCreated = true;
                }
                return m_MapTileAreaQuery;
            }
        }

        /// <summary>The selection singleton MapTilePurchaseSystem reads (SelectionElement buffer).</summary>
        private EntityQuery SelectionBufferQuery
        {
            get
            {
                if (!m_SelectionBufferQueryCreated)
                {
                    m_SelectionBufferQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SelectionElement>());
                    m_SelectionBufferQueryCreated = true;
                }
                return m_SelectionBufferQuery;
            }
        }

        private BridgeResponse ListMapTiles(BridgeRequest request)
        {
            if (!TryGetCity(out Entity city, out BridgeResponse error))
            {
                return error;
            }
            string ownership = request.Query.TryGetValue("owned", out string rawOwned) && !string.IsNullOrEmpty(rawOwned)
                ? rawOwned.ToLowerInvariant()
                : "all";
            if (ownership != "all" && ownership != "owned" && ownership != "unowned")
            {
                return BridgeResponse.Error(400, "owned must be all, owned or unowned");
            }
            bool hasCenter = request.TryGetFloat("x", out float x) & request.TryGetFloat("z", out float z);
            float radius = request.TryGetFloat("radius", out float rawRadius) ? math.max(rawRadius, 1f) : 3000f;
            int limit = request.TryGetInt("limit", out int rawLimit) ? math.clamp(rawLimit, 1, 1000) : 100;
            bool includeFeatures = !request.TryGetBool("features", out bool rawFeatures) || rawFeatures;
            float2 center = new float2(x, z);

            int total = 0;
            int owned = 0;
            var matches = new List<KeyValuePair<float, Entity>>();
            using (NativeArray<Entity> tiles = MapTileAreaQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity tile in tiles)
                {
                    total++;
                    bool isOwned = !EntityManager.HasComponent<Game.Common.Native>(tile);
                    if (isOwned)
                    {
                        owned++;
                    }
                    if ((ownership == "owned" && !isOwned) || (ownership == "unowned" && isOwned))
                    {
                        continue;
                    }
                    float distance = math.distance(EntityManager.GetComponentData<Geometry>(tile).m_CenterPosition.xz, center);
                    if (hasCenter && distance > radius)
                    {
                        continue;
                    }
                    matches.Add(new KeyValuePair<float, Entity>(hasCenter ? distance : 0f, tile));
                }
            }
            if (hasCenter)
            {
                matches.Sort((a, b) => a.Key.CompareTo(b.Key));
            }
            var results = new List<object>();
            for (int i = 0; i < matches.Count && results.Count < limit; i++)
            {
                results.Add(DescribeMapTile(matches[i].Value, includeFeatures));
            }

            MapTilePurchaseSystem purchase = World.GetOrCreateSystemManaged<MapTilePurchaseSystem>();
            return BridgeResponse.Json(new
            {
                totalTiles = total,
                ownedTiles = owned,
                permitsRemaining = purchase.GetAvailableTiles(),
                moreMilestonesGrantPermits = purchase.IsMilestonesLeft(),
                money = EntityManager.GetComponentData<Game.City.PlayerMoney>(city).money,
                totalMatches = matches.Count,
                returned = results.Count,
                note = "buy with cs2_buy_map_tiles (tile ids or points inside tiles); the game sets the price from each tile's " +
                       "features and it rises with the number of tiles already owned",
                tiles = results,
            });
        }

        private object DescribeMapTile(Entity tile, bool includeFeatures)
        {
            Geometry geometry = EntityManager.GetComponentData<Geometry>(tile);
            Dictionary<string, float> features = null;
            if (includeFeatures && EntityManager.HasBuffer<MapFeatureElement>(tile))
            {
                features = new Dictionary<string, float>();
                DynamicBuffer<MapFeatureElement> buffer = EntityManager.GetBuffer<MapFeatureElement>(tile, isReadOnly: true);
                for (int i = 0; i < buffer.Length && i < (int)MapFeature.Count; i++)
                {
                    if (buffer[i].m_Amount != 0f)
                    {
                        features[((MapFeature)i).ToString()] = buffer[i].m_Amount;
                    }
                }
            }
            return new
            {
                entity = new { index = tile.Index, version = tile.Version },
                center = new { x = geometry.m_CenterPosition.x, z = geometry.m_CenterPosition.z },
                bounds = new
                {
                    minX = geometry.m_Bounds.min.x,
                    minZ = geometry.m_Bounds.min.z,
                    maxX = geometry.m_Bounds.max.x,
                    maxZ = geometry.m_Bounds.max.z,
                },
                owned = !EntityManager.HasComponent<Game.Common.Native>(tile),
                features,
            };
        }

        private BridgeResponse BuyMapTiles(BridgeRequest request)
        {
            if (!TryGetCity(out Entity city, out BridgeResponse error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("tiles", out string rawTiles) || string.IsNullOrEmpty(rawTiles))
            {
                return BridgeResponse.Error(400,
                    "provide ?tiles=<list separated by ';'>, each 'index:version' (from /city/tiles/list) or 'x,z' (the tile containing that point)");
            }
            string[] items = rawTiles.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (items.Length > 50)
            {
                return BridgeResponse.Error(400, "too many tiles in one purchase (max 50)");
            }

            var tiles = new List<Entity>();
            for (int i = 0; i < items.Length; i++)
            {
                string item = items[i].Trim();
                Entity tile;
                if (item.IndexOf(':') >= 0)
                {
                    if (!TryParseEntityRef(item, out tile) || !EntityManager.Exists(tile) || !EntityManager.HasComponent<MapTile>(tile))
                    {
                        return BridgeResponse.Error(404, $"tile #{i + 1} ('{item}') is not an existing map tile");
                    }
                }
                else
                {
                    string[] parts = item.Split(',');
                    if (parts.Length != 2
                        || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float px)
                        || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float pz))
                    {
                        return BridgeResponse.Error(400, $"tile #{i + 1} ('{item}'): expected 'index:version' or 'x,z'");
                    }
                    tile = FindMapTileAt(new float2(px, pz));
                    if (tile == Entity.Null)
                    {
                        return BridgeResponse.Error(404, $"tile #{i + 1}: no map tile contains ({px:F0}, {pz:F0})");
                    }
                }
                if (!EntityManager.HasComponent<Game.Common.Native>(tile))
                {
                    return BridgeResponse.Error(409, $"tile #{i + 1} ({tile.Index}:{tile.Version}) is already owned");
                }
                if (!tiles.Contains(tile))
                {
                    tiles.Add(tile);
                }
            }

            ToolSystem toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            SelectionToolSystem selectionTool = World.GetOrCreateSystemManaged<SelectionToolSystem>();
            if (toolSystem.activeTool == selectionTool || !SelectionBufferQuery.IsEmptyIgnoreFilter)
            {
                return BridgeResponse.Error(409, "the game's own selection tool is open (Map Tiles or service district panel); close it and retry");
            }
            if (TransitToolsBusy(out error))
            {
                return error;
            }

            MapTilePurchaseSystem purchase = World.GetOrCreateSystemManaged<MapTilePurchaseSystem>();
            int permitsBefore = purchase.GetAvailableTiles();
            int moneyBefore = EntityManager.GetComponentData<Game.City.PlayerMoney>(city).money;

            ToolBaseSystem previousTool = toolSystem.activeTool;
            SelectionType previousType = selectionTool.selectionType;
            Entity previousOwner = selectionTool.selectionOwner;
            Entity selection = EntityManager.CreateEntity();
            DynamicBuffer<SelectionElement> selected = EntityManager.AddBuffer<SelectionElement>(selection);
            foreach (Entity tile in tiles)
            {
                selected.Add(new SelectionElement(tile));
            }

            TilePurchaseErrorFlags status;
            int quotedCost;
            try
            {
                // What the Map Tiles panel does: switch the selection tool to map
                // tiles (the player then clicks tiles into the SelectionElement
                // buffer, filled here instead) and press Purchase, which calls
                // PurchaseSelection: the game prices the selection, checks permits
                // and funds, charges the treasury and unlocks the tiles. The tool
                // switch is reverted within this frame, so it never actually runs.
                purchase.selecting = true;
                purchase.PurchaseSelection();
                status = purchase.status;
                quotedCost = purchase.cost;
            }
            finally
            {
                if (EntityManager.Exists(selection))
                {
                    EntityManager.DestroyEntity(selection);
                }
                selectionTool.selectionType = previousType;
                selectionTool.selectionOwner = previousOwner;
                toolSystem.activeTool = previousTool != null
                    ? previousTool
                    : World.GetOrCreateSystemManaged<DefaultToolSystem>();
            }

            var bought = new List<object>();
            var boughtTiles = new List<Entity>();
            foreach (Entity tile in tiles)
            {
                if (!EntityManager.HasComponent<Game.Common.Native>(tile))
                {
                    boughtTiles.Add(tile);
                    Geometry geometry = EntityManager.GetComponentData<Geometry>(tile);
                    bought.Add(new
                    {
                        index = tile.Index,
                        version = tile.Version,
                        center = new { x = geometry.m_CenterPosition.x, z = geometry.m_CenterPosition.z },
                    });
                }
            }
            int moneyAfter = EntityManager.GetComponentData<Game.City.PlayerMoney>(city).money;
            if (status != TilePurchaseErrorFlags.None || bought.Count == 0)
            {
                return BridgeResponse.Error(409,
                    "purchase refused by the game: " + DescribeTilePurchaseStatus(status, permitsBefore, moneyBefore, quotedCost, tiles.Count));
            }

            // UnlockTile flagged the tiles from this (UI) phase, too late for this
            // frame's border redraw; re-flag them at the start of the next frame.
            World.GetOrCreateSystemManaged<BridgeMapTileRefreshSystem>().Enqueue(boughtTiles);

            return BridgeResponse.Json(new
            {
                purchased = bought.Count,
                tiles = bought,
                cost = moneyBefore - moneyAfter,
                moneyAfter,
                permitsRemaining = purchase.GetAvailableTiles(),
                note = "bought through the game's own map tile purchase (same logic as the Map Tiles panel); " +
                       "monthly tile upkeep applies if enabled (see cs2_tiles_info)",
            });
        }

        private Entity FindMapTileAt(float2 point)
        {
            Entity best = Entity.Null;
            float bestDistance = float.MaxValue;
            using (NativeArray<Entity> tiles = MapTileAreaQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity tile in tiles)
                {
                    Geometry geometry = EntityManager.GetComponentData<Geometry>(tile);
                    if (point.x < geometry.m_Bounds.min.x || point.x > geometry.m_Bounds.max.x
                        || point.y < geometry.m_Bounds.min.z || point.y > geometry.m_Bounds.max.z)
                    {
                        continue;
                    }
                    float distance = math.distance(geometry.m_CenterPosition.xz, point);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = tile;
                    }
                }
            }
            return best;
        }

        private static string DescribeTilePurchaseStatus(TilePurchaseErrorFlags status, int permits, int money, int cost, int requested)
        {
            var reasons = new List<string>();
            if ((status & TilePurchaseErrorFlags.NoCurrentlyAvailable) != 0)
            {
                reasons.Add("no tile permits left right now (reach the next milestone for more)");
            }
            if ((status & TilePurchaseErrorFlags.NoAvailable) != 0)
            {
                reasons.Add("all tile permits are used up (no milestones left)");
            }
            if ((status & TilePurchaseErrorFlags.NoSelection) != 0)
            {
                reasons.Add("no purchasable tile in the selection");
            }
            if ((status & TilePurchaseErrorFlags.InsufficientPermits) != 0)
            {
                reasons.Add($"{requested} tiles requested but only {permits} permits left");
            }
            if ((status & TilePurchaseErrorFlags.InsufficientFunds) != 0)
            {
                reasons.Add($"not enough money (price {cost}, treasury {money})");
            }
            if (reasons.Count == 0)
            {
                reasons.Add("the game did not unlock the tiles");
            }
            return string.Join("; ", reasons);
        }
    }
}
