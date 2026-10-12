using System;
using System.Collections.Generic;
using Game;
using Game.Notifications;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace CS2MCP
{
    /// <summary>
    /// Perception endpoints: camera control (for AI-directed screenshots),
    /// terrain/water export, cell-map grids (land value, pollution, ground
    /// water), zoning readback, warning-icon listing and entity inspection.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private const float kWorldHalfSize = 7168f; // CellMapSystem.kMapSize / 2

        private EntityQuery m_IconQuery;
        private bool m_IconQueryCreated;

        private EntityQuery IconQuery
        {
            get
            {
                if (!m_IconQueryCreated)
                {
                    m_IconQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Icon>(),
                            ComponentType.ReadOnly<PrefabRef>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Game.Tools.Temp>(),
                            ComponentType.ReadOnly<Game.Common.Deleted>(),
                        },
                    });
                    m_IconQueryCreated = true;
                }
                return m_IconQuery;
            }
        }

        private BridgeResponse GetCamera()
        {
            CameraUpdateSystem cameraSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            CameraController controller = cameraSystem.gamePlayController;
            if (controller == null)
            {
                return BridgeResponse.Error(503, "gameplay camera controller not available (still loading?)");
            }
            return BridgeResponse.Json(new
            {
                pivot = new { x = controller.pivot.x, y = controller.pivot.y, z = controller.pivot.z },
                position = new { x = controller.position.x, y = controller.position.y, z = controller.position.z },
                angle = new { x = controller.angle.x, y = controller.angle.y },
                zoom = controller.zoom,
                edgeScrolling = controller.edgeScrolling,
                controllerEnabled = controller.controllerEnabled,
                activeController = cameraSystem.activeCameraController?.GetType().FullName,
                note = "pivot = look-at point; angle.x = compass rotation deg, angle.y = tilt deg; zoom = distance",
            });
        }

        private BridgeResponse SetCamera(BridgeRequest request)
        {
            // 先完整检查，再改相机；非法后续参数不能留下已经生效的前缀。
            foreach (string key in new[] { "x", "z", "y", "angleX", "angleY", "zoom" })
                if (request.Query.ContainsKey(key) && !request.TryGetFloat(key, out _))
                    return BridgeResponse.Error(400, key + " must be finite");
            foreach (string key in new[] { "edgeScrolling", "clearInfoView" })
                if (request.Query.ContainsKey(key) && !request.TryGetBool(key, out _))
                    return BridgeResponse.Error(400, key + " must be boolean");
            if (request.Query.ContainsKey("x") != request.Query.ContainsKey("z"))
                return BridgeResponse.Error(400, "camera pivot requires both x and z");
            if (request.Query.ContainsKey("y") && !request.Query.ContainsKey("x"))
                return BridgeResponse.Error(400, "camera height requires x and z");
            if ((request.TryGetFloat("x", out float checkedX) && math.abs(checkedX) > kWorldHalfSize)
                || (request.TryGetFloat("z", out float checkedZ) && math.abs(checkedZ) > kWorldHalfSize))
                return BridgeResponse.Error(400, "camera pivot must be inside playable map");
            if (request.TryGetFloat("angleY", out float checkedTilt) && (checkedTilt < 0f || checkedTilt > 89f))
                return BridgeResponse.Error(400, "camera tilt must be 0..89");
            if (request.TryGetFloat("zoom", out float checkedZoom) && (checkedZoom < 10f || checkedZoom > 10000f))
                return BridgeResponse.Error(400, "camera distance must be 10..10000");
            CameraUpdateSystem cameraSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            CameraController controller = cameraSystem.gamePlayController;
            if (controller == null)
            {
                return BridgeResponse.Error(503, "gameplay camera controller not available (still loading?)");
            }

            bool changed = false;
            if (request.TryGetBool("clearInfoView", out bool clearInfoView) && clearInfoView)
            {
                // 原生信息视图入口；只调整当前观察视图，不修改模组或玩家持久设置。
                World.GetOrCreateSystemManaged<Game.UI.InGame.InfoviewsUISystem>().SetActiveInfoview(Entity.Null);
                changed = true;
            }
            if (request.Query.TryGetValue("edgeScrolling", out string rawEdgeScrolling))
            {
                if (!bool.TryParse(rawEdgeScrolling, out bool edgeScrolling))
                    return BridgeResponse.Error(400, "edgeScrolling must be true or false");
                // 调用游戏公开相机选项；不模拟输入、不修改玩家持久设置。
                controller.edgeScrolling = edgeScrolling;
                changed = true;
            }
            bool hasX = request.TryGetFloat("x", out float x);
            bool hasZ = request.TryGetFloat("z", out float z);
            if (hasX && hasZ)
            {
                float y;
                if (!request.TryGetFloat("y", out y))
                {
                    TerrainSystem terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
                    TerrainHeightData heightData = terrain.GetHeightData();
                    y = TerrainUtils.SampleHeight(ref heightData, new float3(x, 0f, z));
                }
                controller.pivot = new Vector3(x, y, z);
                changed = true;
            }
            if (request.TryGetFloat("angleX", out float angleX) | request.TryGetFloat("angleY", out float angleY))
            {
                float2 angle = controller.angle;
                if (request.Query.ContainsKey("angleX"))
                {
                    angle.x = angleX;
                }
                if (request.Query.ContainsKey("angleY"))
                {
                    angle.y = angleY;
                }
                controller.angle = angle;
                changed = true;
            }
            if (request.TryGetFloat("zoom", out float zoom))
            {
                controller.zoom = zoom;
                changed = true;
            }

            if (!changed)
            {
                return BridgeResponse.Error(400, "provide x&z, angleX, angleY, zoom, edgeScrolling or clearInfoView=true");
            }
            // 原生公开切换接口；选择物体后的 Orbit 控制器可能仍在驱动画面。
            // 仅改未启用的 gameplay 参数不能改变实际视角。
            cameraSystem.activeCameraController = controller;
            return GetCamera();
        }

        private BridgeResponse GetTerrain(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            int resolution = request.TryGetInt("resolution", out int rawResolution)
                ? math.clamp(rawResolution, 16, 256)
                : 64;
            bool regional = request.Query.ContainsKey("x") || request.Query.ContainsKey("z") || request.Query.ContainsKey("radius");
            float minX = -kWorldHalfSize, minZ = -kWorldHalfSize, maxX = kWorldHalfSize, maxZ = kWorldHalfSize;
            if (regional)
            {
                if (!request.TryGetFloat("x",out float x) || !request.TryGetFloat("z",out float z) || !request.TryGetFloat("radius",out float radius)
                    || !math.all(math.isfinite(new float3(x,z,radius))) || math.abs(x) > kWorldHalfSize || math.abs(z) > kWorldHalfSize || radius < 8 || radius > kWorldHalfSize)
                    return BridgeResponse.Error(400,"regional terrain needs finite x/z/radius, center inside playable map and radius 8..7168m");
                minX = math.max(-kWorldHalfSize,x-radius); maxX = math.min(kWorldHalfSize,x+radius);
                minZ = math.max(-kWorldHalfSize,z-radius); maxZ = math.min(kWorldHalfSize,z+radius);
            }

            TerrainSystem terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            TerrainHeightData heightData = terrain.GetHeightData();
            WaterSystem water = World.GetOrCreateSystemManaged<WaterSystem>();
            WaterSurfaceData<SurfaceWater> surfaceData = water.GetSurfaceData(out JobHandle waterDeps);
            waterDeps.Complete();

            float stepX = (maxX-minX)/resolution, stepZ = (maxZ-minZ)/resolution;
            var heights = new List<float>(resolution * resolution);
            var waterDepths = new List<float>(resolution * resolution);
            for (int row = 0; row < resolution; row++)
            {
                float worldZ = minZ + (row + 0.5f) * stepZ;
                for (int col = 0; col < resolution; col++)
                {
                    float worldX = minX + (col + 0.5f) * stepX;
                    var samplePosition = new float3(worldX, 0f, worldZ);
                    heights.Add((float)Math.Round(TerrainUtils.SampleHeight(ref heightData, samplePosition), 1));
                    float depth = WaterUtils.SampleDepth(ref surfaceData, samplePosition);
                    waterDepths.Add(depth > 0.05f ? (float)Math.Round(depth, 1) : 0f);
                }
            }

            return BridgeResponse.Json(new
            {
                resolution,
                regional,
                worldMin = regional ? (float?)null : -kWorldHalfSize,
                worldMax = regional ? (float?)null : kWorldHalfSize,
                cellSize = regional ? (float?)null : stepX,
                bounds = new { minX,minZ,maxX,maxZ }, cellSizeX = stepX, cellSizeZ = stepZ,
                note = "row-major: index=row*resolution+col; x=bounds.minX+(col+0.5)*cellSizeX, z=bounds.minZ+(row+0.5)*cellSizeZ; heights in metres; water depth <=0.05m reported as zero; legacy scalar worldMin/worldMax/cellSize only for whole map; water/terrain changes are asynchronous",
                heights,
                waterDepths,
            });
        }

        private BridgeResponse GetGridMap(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("layer", out string layer) || string.IsNullOrEmpty(layer))
            {
                return BridgeResponse.Error(400,
                    "provide ?layer=landValue|groundPollution|airPollution|noisePollution|groundWater|groundWaterPollution");
            }

            JobHandle deps;
            List<float> values;
            int sourceSize;
            string unit;
            switch (layer.ToLowerInvariant())
            {
                case "landvalue":
                {
                    NativeArray<LandValueCell> map = World.GetOrCreateSystemManaged<LandValueSystem>().GetMap(readOnly: true, out deps);
                    deps.Complete();
                    sourceSize = (int)math.round(math.sqrt(map.Length));
                    values = SampleGrid(map.Length, i => map[i].m_LandValue);
                    unit = "land value per cell";
                    break;
                }
                case "groundpollution":
                {
                    NativeArray<GroundPollution> map = World.GetOrCreateSystemManaged<GroundPollutionSystem>().GetMap(readOnly: true, out deps);
                    deps.Complete();
                    sourceSize = (int)math.round(math.sqrt(map.Length));
                    values = SampleGrid(map.Length, i => map[i].m_Pollution);
                    unit = "pollution amount";
                    break;
                }
                case "airpollution":
                {
                    NativeArray<AirPollution> map = World.GetOrCreateSystemManaged<AirPollutionSystem>().GetMap(readOnly: true, out deps);
                    deps.Complete();
                    sourceSize = (int)math.round(math.sqrt(map.Length));
                    values = SampleGrid(map.Length, i => map[i].m_Pollution);
                    unit = "pollution amount";
                    break;
                }
                case "noisepollution":
                {
                    NativeArray<NoisePollution> map = World.GetOrCreateSystemManaged<NoisePollutionSystem>().GetMap(readOnly: true, out deps);
                    deps.Complete();
                    sourceSize = (int)math.round(math.sqrt(map.Length));
                    values = SampleGrid(map.Length, i => map[i].m_Pollution);
                    unit = "noise amount";
                    break;
                }
                case "groundwater":
                {
                    NativeArray<GroundWater> map = World.GetOrCreateSystemManaged<GroundWaterSystem>().GetMap(readOnly: true, out deps);
                    deps.Complete();
                    sourceSize = (int)math.round(math.sqrt(map.Length));
                    values = SampleGrid(map.Length, i => map[i].m_Amount);
                    unit = "ground water amount";
                    break;
                }
                case "groundwaterpollution":
                {
                    NativeArray<GroundWater> map = World.GetOrCreateSystemManaged<GroundWaterSystem>().GetMap(readOnly: true, out deps);
                    deps.Complete();
                    sourceSize = (int)math.round(math.sqrt(map.Length));
                    values = SampleGrid(map.Length, i => map[i].m_Polluted);
                    unit = "polluted ground water amount";
                    break;
                }
                default:
                    return BridgeResponse.Error(400, $"unknown layer '{layer}'");
            }

            return BridgeResponse.Json(new
            {
                layer,
                textureSize = sourceSize,
                worldMin = -kWorldHalfSize,
                worldMax = kWorldHalfSize,
                cellSize = kWorldHalfSize * 2f / sourceSize,
                unit,
                note = "row-major over the game's native cell map; index = row*textureSize + col (row = z axis)",
                values,
            });
        }

        private static List<float> SampleGrid(int length, Func<int, float> selector)
        {
            var result = new List<float>(length);
            for (int i = 0; i < length; i++)
            {
                result.Add(selector(i));
            }
            return result;
        }

        private BridgeResponse GetZoning(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            bool hasCenter = request.TryGetFloat("x", out float x) & request.TryGetFloat("z", out float z);
            float radius = request.TryGetFloat("radius", out float rawRadius) ? math.max(rawRadius, 8f) : float.MaxValue;
            float2 center = new float2(x, z);

            // zone type index -> prefab name
            var zoneNames = new Dictionary<ushort, string>();
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            using (NativeArray<Entity> zonePrefabs = ZonePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in zonePrefabs)
                {
                    PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(entity);
                    if (prefab != null)
                    {
                        zoneNames[EntityManager.GetComponentData<ZoneData>(entity).m_ZoneType.m_Index] = prefab.name;
                    }
                }
            }

            var byZone = new Dictionary<string, int[]>(); // name -> [cells, occupied]
            int totalVisible = 0;
            int totalZoned = 0;
            using (NativeArray<Entity> blocks = ZoneBlockQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity blockEntity in blocks)
                {
                    Block block = EntityManager.GetComponentData<Block>(blockEntity);
                    if (hasCenter && radius < float.MaxValue)
                    {
                        float blockExtent = kCellSize * (math.cmax(block.m_Size) + 1) * 0.71f;
                        if (math.distance(block.m_Position.xz, center) > radius + blockExtent)
                        {
                            continue;
                        }
                    }
                    DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(blockEntity, isReadOnly: true);
                    for (int i = 0; i < cells.Length; i++)
                    {
                        Cell cell = cells[i];
                        if ((cell.m_State & CellFlags.Visible) == 0)
                        {
                            continue;
                        }
                        if (hasCenter && radius < float.MaxValue)
                        {
                            int2 cellIndex = new int2(i % block.m_Size.x, i / block.m_Size.x);
                            if (math.distance(ZoneUtils.GetCellPosition(block, cellIndex).xz, center) > radius)
                            {
                                continue;
                            }
                        }
                        totalVisible++;
                        if (cell.m_Zone.Equals(ZoneType.None))
                        {
                            continue;
                        }
                        totalZoned++;
                        string name = zoneNames.TryGetValue(cell.m_Zone.m_Index, out string zoneName) ? zoneName : $"<index {cell.m_Zone.m_Index}>";
                        if (!byZone.TryGetValue(name, out int[] counts))
                        {
                            counts = new int[2];
                            byZone[name] = counts;
                        }
                        counts[0]++;
                        if ((cell.m_State & CellFlags.Occupied) != 0)
                        {
                            counts[1]++;
                        }
                    }
                }
            }

            var zones = new Dictionary<string, object>();
            foreach (KeyValuePair<string, int[]> pair in byZone)
            {
                zones[pair.Key] = new { cells = pair.Value[0], occupied = pair.Value[1], empty = pair.Value[0] - pair.Value[1] };
            }

            return BridgeResponse.Json(new
            {
                scope = hasCenter && radius < float.MaxValue ? $"radius {radius} around ({x}, {z})" : "whole city",
                zonableCells = totalVisible,
                zonedCells = totalZoned,
                note = "empty zoned cells grow buildings while the simulation runs if demand exists",
                byZone = zones,
            });
        }

        private BridgeResponse GetNotifications(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            int limit = request.TryGetInt("limit", out int rawLimit) ? math.clamp(rawLimit, 1, 500) : 100;
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            var counts = new Dictionary<string, int>();
            var items = new List<object>();
            int total = 0;
            using (NativeArray<Entity> icons = IconQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity iconEntity in icons)
                {
                    Icon icon = EntityManager.GetComponentData<Icon>(iconEntity);
                    PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(EntityManager.GetComponentData<PrefabRef>(iconEntity).m_Prefab);
                    string type = prefab != null ? prefab.name : "<unknown>";
                    total++;
                    counts[type] = counts.TryGetValue(type, out int c) ? c + 1 : 1;
                    if (items.Count < limit)
                    {
                        var notificationPrefab = prefab as NotificationIconPrefab;
                        string description = notificationPrefab?.m_Description;
                        string targetDescription = notificationPrefab?.m_TargetDescription;
                        string localizedDescription = null, localizedTargetDescription = null;
                        var dictionary = Game.SceneFlow.GameManager.instance?.localizationManager?.activeDictionary;
                        if (!string.IsNullOrEmpty(description)) dictionary?.TryGetValue(description, out localizedDescription);
                        if (!string.IsNullOrEmpty(targetDescription)) dictionary?.TryGetValue(targetDescription, out localizedTargetDescription);
                        object target = null;
                        if (EntityManager.HasComponent<Game.Common.Owner>(iconEntity))
                        {
                            Entity owner = EntityManager.GetComponentData<Game.Common.Owner>(iconEntity).m_Owner;
                            string ownerPrefab = null;
                            if (EntityManager.HasComponent<PrefabRef>(owner))
                            {
                                PrefabBase op = prefabSystem.GetPrefab<PrefabBase>(EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab);
                                ownerPrefab = op != null ? op.name : null;
                            }
                            target = new { index = owner.Index, version = owner.Version, prefab = ownerPrefab };
                        }
                        items.Add(new
                        {
                            type,
                            description, targetDescription, localizedDescription, localizedTargetDescription,
                            priority = (int)icon.m_Priority,
                            location = new { x = icon.m_Location.x, y = icon.m_Location.y, z = icon.m_Location.z },
                            target,
                        });
                    }
                }
            }

            return BridgeResponse.Json(new
            {
                total,
                returned = items.Count,
                countsByType = counts,
                note = "in-world warning icons (no electricity/water, garbage piling, abandoned...); use target with /entity/inspect",
                notifications = items,
            });
        }

        private BridgeResponse InspectEntity(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.TryGetInt("index", out int index) || !request.TryGetInt("version", out int version))
            {
                return BridgeResponse.Error(400, "provide ?index=<int>&version=<int>");
            }
            var entity = new Entity { Index = index, Version = version };
            if (!EntityManager.Exists(entity))
            {
                return BridgeResponse.Error(404, $"entity {index}:{version} does not exist");
            }

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var result = new Dictionary<string, object>
            {
                ["entity"] = new { index, version },
                ["capturedAtUtc"] = System.DateTime.UtcNow.ToString("o"),
            };

            if (EntityManager.HasComponent<PrefabRef>(entity))
            {
                PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                result["prefab"] = prefab != null ? prefab.name : null;
            }
            if (EntityManager.HasComponent<Game.Objects.Transform>(entity))
            {
                Game.Objects.Transform transform = EntityManager.GetComponentData<Game.Objects.Transform>(entity);
                result["position"] = new { x = transform.m_Position.x, y = transform.m_Position.y, z = transform.m_Position.z };
            }

            var flags = new List<string>();
            if (EntityManager.HasComponent<Game.Buildings.Building>(entity)) flags.Add("building");
            if (EntityManager.HasComponent<Game.Net.Edge>(entity)) flags.Add("roadSegment");
            if (EntityManager.HasComponent<Game.Buildings.Abandoned>(entity)) flags.Add("abandoned");
            if (EntityManager.HasComponent<Game.Buildings.Condemned>(entity)) flags.Add("condemned");
            if (EntityManager.HasComponent<Game.Common.Destroyed>(entity)) flags.Add("destroyed");
            if (EntityManager.HasComponent<Game.Common.Owner>(entity)) flags.Add("hasOwner");
            result["flags"] = flags;
            if (EntityManager.HasComponent<Game.Net.Curve>(entity))
            {
                var curve = EntityManager.GetComponentData<Game.Net.Curve>(entity).m_Bezier;
                result["networkGeometry"] = new { a = InterchangePoint(curve.a), b = InterchangePoint(curve.b),
                    c = InterchangePoint(curve.c), d = InterchangePoint(curve.d),
                    note = "实际世界坐标曲线；与原始定义的差异须考虑地形适配、节点裁切及原生生成，不只比较端点" };
            }
            result["utilities"] = DescribeUtilityState(entity);
            result["trafficMod"] = DescribeTrafficModData(entity);
            result["nativeConnections"] = DescribeNativeObjectConnections(entity);
            if (EntityManager.HasComponent<Game.Citizens.Citizen>(entity)) result["citizen"] = DescribeCitizenState(entity);
            if (EntityManager.HasComponent<Game.Objects.Attached>(entity))
            {
                var attached = EntityManager.GetComponentData<Game.Objects.Attached>(entity);
                result["attachedTo"] = new { entity = Ref(attached.m_Parent), prefab = PrefabNameOf(attached.m_Parent) };
            }
            if (EntityManager.HasComponent<Game.Net.Road>(entity)) result["turnRestrictions"] = DescribeTurnRestrictions(entity);
            if (EntityManager.HasComponent<Game.Common.Owner>(entity)) {
                Entity owner = EntityManager.GetComponentData<Game.Common.Owner>(entity).m_Owner;
                result["owner"] = new { entity = Ref(owner), prefab = PrefabNameOf(owner) };
            }
            if (EntityManager.HasComponent<Game.Net.Lane>(entity)) result["lane"] = DescribeTransitLane(entity);
            if (EntityManager.HasComponent<Game.Routes.Connected>(entity))
                result["routeConnected"] = Ref(EntityManager.GetComponentData<Game.Routes.Connected>(entity).m_Connected);
            if (EntityManager.HasBuffer<Game.Net.SubLane>(entity)) {
                var lanes = EntityManager.GetBuffer<Game.Net.SubLane>(entity, true);
                int laneLimit = request.TryGetInt("laneLimit", out int requestedLimit) ? requestedLimit : 0;
                int laneOffset = request.TryGetInt("laneOffset", out int requestedOffset) ? requestedOffset : 0;
                if (laneLimit < 0 || laneLimit > 256 || laneOffset < 0 || laneOffset > 65535)
                    return BridgeResponse.Error(400, "laneLimit must be 0..256 and laneOffset must be 0..65535");
                var details = new List<object>();
                for (int i = laneOffset; i < lanes.Length && i < laneOffset + laneLimit; i++)
                    details.Add(DescribeTransitLane(lanes[i].m_SubLane, lanes[i].m_PathMethods.ToString()));
                result["subLaneCount"] = lanes.Length;
                result["subLanes"] = details;
                result["subLaneOffset"] = laneOffset;
                result["subLanesReturned"] = details.Count;
                result["subLanesComplete"] = laneOffset == 0 && details.Count == lanes.Length;
                result["nextLaneOffset"] = laneOffset + details.Count < lanes.Length ? (object)(laneOffset + details.Count) : null;
                result["laneDetailsNote"] = "summary by default; use laneLimit and laneOffset to page native lanes without dropping utility or Traffic diagnostics";
                if (laneLimit > 0) result["parkingOccupancy"] = DescribeParkingOccupancy(entity);
            }

            if (EntityManager.HasBuffer<Game.Buildings.Renter>(entity))
            {
                DynamicBuffer<Game.Buildings.Renter> renters = EntityManager.GetBuffer<Game.Buildings.Renter>(entity, isReadOnly: true);
                var renterInfos = new List<object>();
                for (int i = 0; i < renters.Length && i < 20; i++)
                {
                    Entity renter = renters[i].m_Renter;
                    string renterPrefab = null;
                    if (EntityManager.HasComponent<PrefabRef>(renter))
                    {
                        PrefabBase rp = prefabSystem.GetPrefab<PrefabBase>(EntityManager.GetComponentData<PrefabRef>(renter).m_Prefab);
                        renterPrefab = rp != null ? rp.name : null;
                    }
                    int citizens = EntityManager.HasBuffer<Game.Citizens.HouseholdCitizen>(renter)
                        ? EntityManager.GetBuffer<Game.Citizens.HouseholdCitizen>(renter, isReadOnly: true).Length
                        : 0;
                    int employees = EntityManager.HasBuffer<Game.Companies.Employee>(renter)
                        ? EntityManager.GetBuffer<Game.Companies.Employee>(renter, isReadOnly: true).Length
                        : 0;
                    renterInfos.Add(new { prefab = renterPrefab, citizens, employees });
                }
                result["renterCount"] = renters.Length;
                result["renters"] = renterInfos;
            }

            if (EntityManager.HasBuffer<Game.Companies.Employee>(entity))
            {
                result["employees"] = EntityManager.GetBuffer<Game.Companies.Employee>(entity, isReadOnly: true).Length;
            }

            return BridgeResponse.Json(result);
        }
    }
}
