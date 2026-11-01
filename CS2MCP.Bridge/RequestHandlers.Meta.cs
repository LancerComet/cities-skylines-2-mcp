using System;
using System.Collections.Generic;
using Game.Areas;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Simulation;
using Game.UI.InGame;
using Game.UI.Menu;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// Meta / time / district endpoints: timed simulation runs with auto-pause,
    /// triggering saves (AI safety net), map tile info, district creation and
    /// district policies.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private const float kFramesPerHour = 262144f / 24f;

        private EntityQuery m_DistrictPrefabQuery;
        private bool m_DistrictPrefabQueryCreated;
        private EntityQuery m_DistrictQuery;
        private bool m_DistrictQueryCreated;
        private EntityQuery m_DistrictPolicyQuery;
        private bool m_DistrictPolicyQueryCreated;
        private EntityQuery m_MapTileQuery;
        private bool m_MapTileQueryCreated;

        private EntityQuery DistrictPrefabQuery
        {
            get
            {
                if (!m_DistrictPrefabQueryCreated)
                {
                    m_DistrictPrefabQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<DistrictData>());
                    m_DistrictPrefabQueryCreated = true;
                }
                return m_DistrictPrefabQuery;
            }
        }

        private EntityQuery DistrictQuery
        {
            get
            {
                if (!m_DistrictQueryCreated)
                {
                    m_DistrictQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<District>(),
                            ComponentType.ReadOnly<Geometry>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Game.Tools.Temp>(),
                            ComponentType.ReadOnly<Game.Common.Deleted>(),
                        },
                    });
                    m_DistrictQueryCreated = true;
                }
                return m_DistrictQuery;
            }
        }

        private EntityQuery DistrictPolicyQuery
        {
            get
            {
                if (!m_DistrictPolicyQueryCreated)
                {
                    m_DistrictPolicyQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[] { ComponentType.ReadOnly<PolicyData>() },
                        Any = new[]
                        {
                            ComponentType.ReadOnly<DistrictOptionData>(),
                            ComponentType.ReadOnly<DistrictModifierData>(),
                        },
                    });
                    m_DistrictPolicyQueryCreated = true;
                }
                return m_DistrictPolicyQuery;
            }
        }

        private EntityQuery MapTileQuery
        {
            get
            {
                if (!m_MapTileQueryCreated)
                {
                    m_MapTileQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<MapTile>());
                    m_MapTileQueryCreated = true;
                }
                return m_MapTileQuery;
            }
        }

        private BridgeResponse SimRun(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            SimulationSystem sim = World.GetOrCreateSystemManaged<SimulationSystem>();

            if (request.TryGetBool("cancel", out bool cancel) && cancel)
            {
                m_System.SetAutoPause(0);
                sim.selectedSpeed = 0f;
                return BridgeResponse.Json(new { cancelled = true, paused = true });
            }

            if (!request.TryGetFloat("hours", out float hours))
            {
                return BridgeResponse.Error(400, "provide ?hours=<in-game hours 0.1-96> (or ?cancel=true)");
            }
            hours = math.clamp(hours, 0.1f, 96f);
            float speed = request.TryGetFloat("speed", out float rawSpeed) ? math.clamp(rawSpeed, 0.5f, 8f) : 4f;

            uint targetFrame = sim.frameIndex + (uint)(hours * kFramesPerHour);
            bool dismissPopups = request.TryGetBool("dismissPopups", out bool rawDismiss) && rawDismiss;
            m_System.SetAutoPause(targetFrame, dismissPopups);
            int dismissedNow = dismissPopups ? DismissBlockingPopups() : 0;
            sim.selectedSpeed = speed;
            string forcedPause = ForcedPauseReason();

            return BridgeResponse.Json(new
            {
                running = true,
                inGameHours = hours,
                speed,
                startFrame = sim.frameIndex,
                targetFrame,
                dismissPopups,
                dismissedNow,
                forcedPause,
                note = forcedPause == "popup"
                    ? "a blocking popup is holding the simulation at speed 0: pass dismissPopups=true or call /ui/popups/dismiss"
                    : "simulation auto-pauses at targetFrame; poll /state (frameIndex) to track progress; cancel with /sim/run?cancel=true",
            });
        }

        private BridgeResponse SaveGame(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            string name = request.Query.TryGetValue("name", out string rawName) && !string.IsNullOrEmpty(rawName)
                ? rawName
                : $"CS2MCP {DateTime.Now:yyyy-MM-dd HH-mm-ss}";

            MenuUISystem menu = World.GetExistingSystemManaged<MenuUISystem>();
            if (menu == null)
            {
                return BridgeResponse.Error(503, "menu system unavailable");
            }
            var saveInfo = menu.GetSaveInfo(autoSave: false);
            // The save pipeline requires a preview texture (null crashes it) —
            // capture one exactly like AutoSaveSystem does.
            UnityEngine.RenderTexture preview = Game.UI.ScreenCaptureHelper.CreateRenderTarget("PreviewSaveGame-CS2MCP", 680, 383);
            Game.UI.ScreenCaptureHelper.CaptureScreenshot(UnityEngine.Camera.main, preview, new MenuHelpers.SaveGamePreviewSettings());
            _ = GameManager.instance.Save(name, saveInfo, Colossal.IO.AssetDatabase.AssetDatabase.user, preview);

            return BridgeResponse.Json(new
            {
                saving = true,
                name,
                note = "save runs asynchronously; it appears in the game's load menu when finished",
            });
        }

        private BridgeResponse GetTilesInfo()
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            MapTilePurchaseSystem tiles = World.GetOrCreateSystemManaged<MapTilePurchaseSystem>();
            int total = MapTileQuery.CalculateEntityCount();
            int owned = 0;
            using (NativeArray<Entity> entities = MapTileQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    if (!EntityManager.HasComponent<Game.Common.Native>(entity))
                    {
                        owned++;
                    }
                }
            }
            return BridgeResponse.Json(new
            {
                totalTiles = total,
                ownedTiles = owned,
                availableToPurchase = tiles.GetAvailableTiles(),
                upkeepEnabled = tiles.GetMapTileUpkeepEnabled(),
                upkeepCostMultiplier = tiles.GetMapTileUpkeepCostMultiplier(owned),
                note = "tile purchasing via API is planned (v0.9); buy tiles in the game UI for now",
            });
        }

        private BridgeResponse GetDistricts()
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var districts = new List<object>();
            using (NativeArray<Entity> entities = DistrictQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    Geometry geometry = EntityManager.GetComponentData<Geometry>(entity);
                    int nodeCount = EntityManager.HasBuffer<Game.Areas.Node>(entity)
                        ? EntityManager.GetBuffer<Game.Areas.Node>(entity, isReadOnly: true).Length
                        : 0;
                    int activePolicies = 0;
                    if (EntityManager.HasBuffer<Game.Policies.Policy>(entity))
                    {
                        DynamicBuffer<Game.Policies.Policy> policies = EntityManager.GetBuffer<Game.Policies.Policy>(entity, isReadOnly: true);
                        for (int i = 0; i < policies.Length; i++)
                        {
                            if ((policies[i].m_Flags & Game.Policies.PolicyFlags.Active) != 0)
                            {
                                activePolicies++;
                            }
                        }
                    }
                    districts.Add(new
                    {
                        entity = new { index = entity.Index, version = entity.Version },
                        center = new { x = geometry.m_CenterPosition.x, z = geometry.m_CenterPosition.z },
                        polygonNodes = nodeCount,
                        activePolicies,
                    });
                }
            }
            return BridgeResponse.Json(new
            {
                count = districts.Count,
                note = "create with /build/district; delete with /build/demolish; set policies with /district/policies/set",
                districts,
            });
        }

        private BridgeResponse CreateDistrict(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("nodes", out string nodesRaw) || string.IsNullOrEmpty(nodesRaw))
            {
                return BridgeResponse.Error(400,
                    "provide ?nodes=x1,z1;x2,z2;x3,z3;... (3+ polygon corners, counter-clockwise, in world meters)");
            }

            string[] pairs = nodesRaw.Split(';');
            if (pairs.Length < 3)
            {
                return BridgeResponse.Error(400, "polygon needs at least 3 corners");
            }
            if (pairs.Length > 32)
            {
                return BridgeResponse.Error(400, "polygon too complex (max 32 corners)");
            }

            TerrainSystem terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            TerrainHeightData heightData = terrain.GetHeightData();
            var nodes = new float3[pairs.Length];
            for (int i = 0; i < pairs.Length; i++)
            {
                string[] parts = pairs[i].Split(',');
                if (parts.Length != 2
                    || !float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                    || !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                {
                    return BridgeResponse.Error(400, $"cannot parse corner '{pairs[i]}'; expected x,z");
                }
                var position = new float3(x, 0f, z);
                position.y = TerrainUtils.SampleHeight(ref heightData, position);
                nodes[i] = position;
            }

            Entity prefabEntity;
            PrefabBase prefab;
            if (request.Query.TryGetValue("prefab", out string prefabName) && !string.IsNullOrEmpty(prefabName))
            {
                if (!TryFindPrefabByName(DistrictPrefabQuery, prefabName, out prefabEntity, out prefab))
                {
                    return BridgeResponse.Error(404, $"unknown district prefab '{prefabName}'");
                }
            }
            else
            {
                using NativeArray<Entity> prefabs = DistrictPrefabQuery.ToEntityArray(Allocator.Temp);
                if (prefabs.Length == 0)
                {
                    return BridgeResponse.Error(500, "no district prefab found");
                }
                prefabEntity = prefabs[0];
                prefab = World.GetOrCreateSystemManaged<PrefabSystem>().GetPrefab<PrefabBase>(prefabEntity);
            }

            BridgeToolSystem tool = World.GetOrCreateSystemManaged<BridgeToolSystem>();
            if (!tool.TryQueueArea(prefabEntity, prefab, nodes, request))
            {
                return BridgeResponse.Error(409, "another build operation is in progress, retry shortly");
            }
            return null;
        }

        private BridgeResponse GetDistrictPolicies(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!TryResolveDistrict(request, out Entity district, out BridgeResponse districtError))
            {
                return districtError;
            }

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            DynamicBuffer<Game.Policies.Policy> active = EntityManager.GetBuffer<Game.Policies.Policy>(district, isReadOnly: true);
            var policies = new List<object>();
            using (NativeArray<Entity> entities = DistrictPolicyQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    PolicyPrefab prefab = prefabSystem.GetPrefab<PolicyPrefab>(entity);
                    if (prefab == null || prefab.m_Visibility == PolicyVisibility.HideFromPolicyList)
                    {
                        continue;
                    }
                    bool isActive = false;
                    float adjustment = 0f;
                    for (int i = 0; i < active.Length; i++)
                    {
                        if (active[i].m_Policy == entity)
                        {
                            isActive = (active[i].m_Flags & Game.Policies.PolicyFlags.Active) != 0;
                            adjustment = active[i].m_Adjustment;
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
                        active = isActive,
                        adjustment,
                        locked = IsLocked(entity),
                    });
                }
            }
            return BridgeResponse.Json(new
            {
                district = new { index = district.Index, version = district.Version },
                policies,
            });
        }

        private BridgeResponse SetDistrictPolicy(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!TryResolveDistrict(request, out Entity district, out BridgeResponse districtError))
            {
                return districtError;
            }
            if (!request.Query.TryGetValue("name", out string policyName) || string.IsNullOrEmpty(policyName))
            {
                return BridgeResponse.Error(400, "provide ?name=<policy name from /district/policies>");
            }
            if (!request.TryGetBool("active", out bool active))
            {
                return BridgeResponse.Error(400, "provide ?active=true|false");
            }
            request.TryGetFloat("adjustment", out float adjustment);

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            using (NativeArray<Entity> entities = DistrictPolicyQuery.ToEntityArray(Allocator.Temp))
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
                    World.GetOrCreateSystemManaged<PoliciesUISystem>().SetPolicy(district, entity, active, adjustment);
                    return BridgeResponse.Json(new
                    {
                        district = new { index = district.Index, version = district.Version },
                        name = prefab.name,
                        active,
                        adjustment,
                    });
                }
            }
            return BridgeResponse.Error(404, $"unknown district policy '{policyName}'");
        }

        private bool TryResolveDistrict(BridgeRequest request, out Entity district, out BridgeResponse error)
        {
            district = Entity.Null;
            error = null;
            if (!request.TryGetInt("index", out int index) || !request.TryGetInt("version", out int version))
            {
                error = BridgeResponse.Error(400, "provide ?index=&version= of a district from /districts");
                return false;
            }
            var entity = new Entity { Index = index, Version = version };
            if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<District>(entity))
            {
                error = BridgeResponse.Error(404, $"entity {index}:{version} is not an existing district");
                return false;
            }
            district = entity;
            return true;
        }
    }
}
