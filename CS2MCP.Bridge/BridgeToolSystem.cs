using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Scripting;
using AgeMask = Game.Tools.AgeMask;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    /// <summary>
    /// Headless placement tool. Activated programmatically for exactly three
    /// tool-update frames per operation:
    ///   1. CreateDefinitions — build definition entities (ported LineTool job),
    ///      applyMode=Clear lets the game generate preview Temp entities.
    ///   2. Apply — if validation passed (GetAllowApply), applyMode=Apply commits
    ///      the Temp entities to permanent ones; otherwise reject.
    ///   3. Finish — restore the previously active tool.
    /// </summary>
    public sealed partial class BridgeToolSystem : ObjectToolBaseSystem
    {
        private enum Stage
        {
            Idle,
            CreateDefinitions,
            Apply,
            Finish,
        }

        private enum OperationKind
        {
            RoadLayout,
            RoadLayoutRemoval,
            Object,
            Net,
            Demolish,
            Upgrade,
            Area,
            NodeUpgrade,
            BuildingUpgrade,
            BuildingRebuild,
            BuildingMove,
            TransitStopMove,
            Zoning,
            Terrain,
        }

        private Stage m_Stage = Stage.Idle;
        private OperationKind m_PendingKind;
        private Entity m_PendingPrefabEntity;
        private Entity m_PendingTarget;
        private PrefabBase m_PendingPrefab;
        private string m_PendingLabel;
        private float3 m_PendingPosition;
        private float3 m_PendingEnd;
        private float3 m_PendingMid;
        private bool m_PendingHasMid;
        private CompositionFlags m_PendingUpgradeFlags;
        private float3[] m_PendingAreaNodes;
        private float2 m_PendingElevations;
        private quaternion m_PendingRotation;
        private ControlPoint m_PendingAttachedPoint;
        private BridgeRequest m_PendingRequest;
        private BridgeResponse m_PendingApplyResponse;
        private ToolBaseSystem m_PreviousTool;

        private CityConfigurationSystem m_CityConfigurationSystem;
        private EntityQuery m_ValidationIconQuery;
        private EntityQuery m_PreviewCostQuery;

        public override string toolID => "CS2MCP.Bridge";

        public bool IsBusy => m_Stage != Stage.Idle;

        /// <summary>界面在首个工具更新前或施工中切换工具时，释放自有请求，保留玩家当前工具。</summary>
        public void ReconcileToolOwnership()
        {
            if (m_Stage == Stage.Idle || m_ToolSystem.activeTool == this) return;
            var stage = m_Stage;
            applyMode = ApplyMode.Clear;
            CompletePending(BridgeResponse.Json(new {
                error = "native construction interrupted because another tool became active; read the target before continuing",
                failurePhase = "tool_interrupted",
                operation = m_PendingKind.ToString(), stage = stage.ToString(),
                target = new { index = m_PendingTarget.Index, version = m_PendingTarget.Version },
                mayHaveApplied = stage == Stage.Finish,
                safeToRetry = false,
            }, 409));
            Deactivate();
        }

        [Preserve]
        protected override void OnStopRunning()
        {
            ReconcileToolOwnership();
            base.OnStopRunning();
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_CityConfigurationSystem = base.World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_ValidationIconQuery = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<Game.Notifications.Icon>(),
                ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<PrefabRef>());
            m_PreviewCostQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Temp>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() }
            });
        }

        public override PrefabBase GetPrefab()
        {
            return m_PendingPrefab;
        }

        public override bool TrySetPrefab(PrefabBase prefab)
        {
            // Never let the game UI select this tool via asset selection.
            return false;
        }

        /// <summary>Must be called on the simulation thread.</summary>
        public bool TryQueuePlacement(Entity prefabEntity, PrefabBase prefab, float3 position, quaternion rotation, BridgeRequest request, out BridgeResponse error, ControlPoint attachedPoint = default)
        {
            error = null;
            if (m_Stage != Stage.Idle)
            {
                error = BridgeResponse.Error(409, "another build operation is in progress, retry shortly");
                return false;
            }
            if (EntityManager.HasComponent<PlaceholderBuildingData>(prefabEntity))
            {
                error = BridgeResponse.Error(400, "specialized industry placeholders require their dedicated native construction workflow");
                return false;
            }
            m_PendingKind = OperationKind.Object;
            m_PendingAttachedPoint = attachedPoint;
            m_PendingPrefabEntity = prefabEntity;
            m_PendingPrefab = prefab;
            m_PendingPosition = position;
            m_PendingRotation = rotation;
            m_PendingRequest = request;
            Activate();
            return true;
        }

        /// <summary>服务扩建沿原生 ObjectToolBaseSystem 的 owner 定义、预览、校验和收费流程施工。</summary>
        public bool TryQueueBuildingUpgrade(Entity target, Entity prefabEntity, PrefabBase prefab, float3 position, quaternion rotation, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle) return false;
            m_PendingKind = OperationKind.BuildingUpgrade;
            m_PendingTarget = target;
            m_PendingPrefabEntity = prefabEntity;
            m_PendingPrefab = prefab;
            m_PendingPosition = position;
            m_PendingRotation = rotation;
            m_PendingRequest = request;
            Activate();
            return true;
        }

        /// <summary>同原生UpgradeTool：空升级预制体、既有目标和原位控制点生成正常重建预览。</summary>
        public bool TryQueueBuildingRebuild(Entity target, string label, float3 position, quaternion rotation, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle) return false;
            m_PendingKind = OperationKind.BuildingRebuild;
            m_PendingTarget = target;
            m_PendingPrefabEntity = Entity.Null;
            m_PendingPrefab = null;
            m_PendingLabel = label;
            m_PendingPosition = position;
            m_PendingRotation = rotation;
            m_PendingRequest = request;
            Activate();
            return true;
        }

        /// <summary>搬迁引用传给原生 original 参数，子物体及扩建由原生 Relocate 流程处理。</summary>
        public bool TryQueueBuildingMove(Entity target, Entity prefabEntity, PrefabBase prefab, float3 position, quaternion rotation, BridgeRequest request, ControlPoint attachedPoint = default)
        {
            if (m_Stage != Stage.Idle) return false;
            m_PendingKind = OperationKind.BuildingMove;
            m_PendingAttachedPoint = attachedPoint;
            m_PendingTarget = target;
            m_PendingPrefabEntity = prefabEntity;
            m_PendingPrefab = prefab;
            m_PendingPosition = position;
            m_PendingRotation = rotation;
            m_PendingRequest = request;
            Activate();
            return true;
        }

        /// <summary>Must be called on the simulation thread.</summary>
        public bool TryQueueRoad(Entity prefabEntity, PrefabBase prefab, float3 start, float3 end, float3 mid, bool hasMid, float2 elevations, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle)
            {
                return false;
            }
            m_PendingKind = OperationKind.Net;
            m_PendingPrefabEntity = prefabEntity;
            m_PendingPrefab = prefab;
            m_PendingPosition = start;
            m_PendingEnd = end;
            m_PendingMid = mid;
            m_PendingHasMid = hasMid;
            m_PendingElevations = elevations;
            m_PendingRotation = quaternion.identity;
            m_PendingRequest = request;
            Activate();
            return true;
        }

        /// <summary>Must be called on the simulation thread.</summary>
        public bool TryQueueUpgrade(Entity target, string label, CompositionFlags upgradeFlags, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle)
            {
                return false;
            }
            m_PendingKind = OperationKind.Upgrade;
            m_PendingTarget = target;
            m_PendingLabel = label;
            m_PendingUpgradeFlags = upgradeFlags;
            m_PendingRequest = request;
            Activate();
            return true;
        }

        /// <summary>
        /// Must be called on the simulation thread. Replaces a junction's upgrade
        /// flags (traffic lights, all-way stop...) with finalFlags, as the game's
        /// intersection tools do.
        /// </summary>
        public bool TryQueueNodeUpgrade(Entity node, string label, CompositionFlags finalFlags, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle)
            {
                return false;
            }
            m_PendingKind = OperationKind.NodeUpgrade;
            m_PendingTarget = node;
            m_PendingLabel = label;
            m_PendingUpgradeFlags = finalFlags;
            m_PendingRequest = request;
            Activate();
            return true;
        }

        /// <summary>Must be called on the simulation thread.</summary>
        public bool TryQueueDemolish(Entity target, string label, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle)
            {
                return false;
            }
            m_PendingKind = OperationKind.Demolish;
            m_PendingTarget = target;
            m_PendingLabel = label;
            m_PendingRequest = request;
            Activate();
            return true;
        }

        /// <summary>Must be called on the simulation thread.</summary>
        public bool TryQueueArea(Entity prefabEntity, PrefabBase prefab, float3[] polygonNodes, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle)
            {
                return false;
            }
            m_PendingKind = OperationKind.Area;
            m_PendingPrefabEntity = prefabEntity;
            m_PendingPrefab = prefab;
            m_PendingAreaNodes = polygonNodes;
            m_PendingRequest = request;
            Activate();
            return true;
        }

        /// <summary>读取当前原生预览实体的错误图标，保留校验规则并提供溯源依据。</summary>
        private List<string> CollectPlacementErrors()
        {
            var errors = new List<string>();
            using (NativeArray<Entity> icons = m_ValidationIconQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity icon in icons)
                {
                    Entity owner = EntityManager.GetComponentData<Owner>(icon).m_Owner;
                    if (!EntityManager.Exists(owner) || !EntityManager.HasComponent<Temp>(owner)) continue;
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(icon).m_Prefab;
                    if (!EntityManager.HasComponent<ToolErrorData>(prefab)) continue;
                    string error = EntityManager.GetComponentData<ToolErrorData>(prefab).m_Error.ToString();
                    if (!errors.Contains(error)) errors.Add(error);
                }
            }
            return errors;
        }

        // 只回读本次原生预览的错误位置，供复合道路和设施按实际冲突修正几何。
        private List<object> CollectPlacementErrorLocations()
        {
            var rows = new List<object>();
            using (var icons = m_ValidationIconQuery.ToEntityArray(Allocator.Temp))
                foreach (var icon in icons)
                {
                    var owner = EntityManager.GetComponentData<Owner>(icon).m_Owner;
                    if (!EntityManager.Exists(owner) || !EntityManager.HasComponent<Temp>(owner)) continue;
                    var prefab = EntityManager.GetComponentData<PrefabRef>(icon).m_Prefab;
                    if (!EntityManager.HasComponent<ToolErrorData>(prefab)) continue;
                    object location = null;
                    if (EntityManager.HasComponent<Game.Notifications.Icon>(icon))
                    {
                        var p = EntityManager.GetComponentData<Game.Notifications.Icon>(icon).m_Location;
                        location = new { x = p.x, y = p.y, z = p.z };
                    }
                    rows.Add(new { error = EntityManager.GetComponentData<ToolErrorData>(prefab).m_Error.ToString(),
                        temporaryOwner = new { index = owner.Index, version = owner.Version },
                        ownerGeometry = DescribePreviewOwner(owner),
                        collisionTarget = EntityManager.HasComponent<Target>(icon)
                            ? DescribePreviewOwner(EntityManager.GetComponentData<Target>(icon).m_Target) : null, location });
                    if (rows.Count >= 64) break;
                }
            return rows;
        }

        // 回读原生校验前生成的几何，不用输入曲线冒充 CourseSplit 后的道路。
        private object DescribePreviewOwner(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity))
                return new { entity = new { index = entity.Index, version = entity.Version }, exists = false };
            string prefab = null;
            if (EntityManager.HasComponent<PrefabRef>(entity))
            {
                var reference = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                prefab = World.GetOrCreateSystemManaged<PrefabSystem>().GetPrefab<PrefabBase>(reference)?.name;
            }
            object curve = null, position = null, elevation = null;
            string kind = "other";
            if (EntityManager.HasComponent<Game.Net.Curve>(entity))
            {
                var c = EntityManager.GetComponentData<Game.Net.Curve>(entity);
                curve = new { a = PreviewPoint(c.m_Bezier.a), b = PreviewPoint(c.m_Bezier.b),
                    c = PreviewPoint(c.m_Bezier.c), d = PreviewPoint(c.m_Bezier.d), length = c.m_Length };
                kind = "network";
            }
            else if (EntityManager.HasComponent<Game.Net.Node>(entity))
            {
                position = PreviewPoint(EntityManager.GetComponentData<Game.Net.Node>(entity).m_Position);
                kind = "node";
            }
            else if (EntityManager.HasComponent<Transform>(entity))
            {
                position = PreviewPoint(EntityManager.GetComponentData<Transform>(entity).m_Position);
                kind = "object";
            }
            if (EntityManager.HasComponent<Game.Net.Elevation>(entity))
            {
                var e = EntityManager.GetComponentData<Game.Net.Elevation>(entity).m_Elevation;
                elevation = new { start = e.x, end = e.y };
            }
            var temp = EntityManager.HasComponent<Temp>(entity) ? EntityManager.GetComponentData<Temp>(entity) : default;
            object attached = null;
            if (EntityManager.HasComponent<Game.Objects.Attached>(entity)) {
                var a = EntityManager.GetComponentData<Game.Objects.Attached>(entity);
                attached = new { parent = new { index = a.m_Parent.Index, version = a.m_Parent.Version }, curvePosition = a.m_CurvePosition };
            }
            object rotation = EntityManager.HasComponent<Transform>(entity)
                ? (object)EntityManager.GetComponentData<Transform>(entity).m_Rotation.value : null;
            object size = null;
            if (EntityManager.HasComponent<PrefabRef>(entity)) {
                var p = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                if (EntityManager.HasComponent<ObjectGeometryData>(p)) size = EntityManager.GetComponentData<ObjectGeometryData>(p).m_Size;
            }
            return new { entity = new { index = entity.Index, version = entity.Version }, prefab, kind, curve, position, rotation, size, attached,
                elevation, flags = EntityManager.HasComponent<Temp>(entity) ? temp.m_Flags.ToString() : null,
                original = new { index = temp.m_Original.Index, version = temp.m_Original.Version } };
        }

        private static object PreviewPoint(float3 p) => new { x = p.x, y = p.y, z = p.z };

        private object CollectNativeRoadPreview()
        {
            if (m_PendingKind != OperationKind.RoadLayout) return null;
            var rows = new List<object>();
            bool complete = true;
            using (var entities = m_PreviewCostQuery.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities)
                {
                    if (!EntityManager.HasComponent<Game.Net.Curve>(entity) || !EntityManager.HasComponent<Game.Net.Edge>(entity)) continue;
                    if (rows.Count >= 256) { complete = false; break; }
                    rows.Add(new { temporaryEntity = new { index = entity.Index, version = entity.Version },
                        geometry = DescribePreviewOwner(entity) });
                }
            return new { complete, roads = rows, note = "原生临时道路，预览结束后清除；未施工或收费" };
        }

        private void Activate()
        {
            m_Stage = Stage.CreateDefinitions;
            m_PreviousTool = m_ToolSystem.activeTool;
            m_ToolSystem.activeTool = this;
        }

        /// <summary>回读原生CostSystem算好的Temp费用；不另行扣款，不用道路单价猜总价。</summary>
        private int NativePreviewCost()
        {
            int total = 0;
            using (var entities = m_PreviewCostQuery.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities)
                {
                    var preview = EntityManager.GetComponentData<Temp>(entity);
                    if ((preview.m_Flags & TempFlags.Cancel) == 0) total = checked(total + preview.m_Cost);
                }
            return total;
        }

        [Preserve]
        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            try
            {
                switch (m_Stage)
                {
                    case Stage.CreateDefinitions:
                        applyMode = ApplyMode.Clear;
                        switch (m_PendingKind)
                        {
                            case OperationKind.RoadLayout:
                                CreateRoadLayoutDefinitions();
                                break;
                            case OperationKind.RoadLayoutRemoval:
                                foreach (var target in m_RoadRemovalTargets) {
                                    m_PendingTarget = target;
                                    CreateModifyDefinitions(CreationFlags.Delete, default);
                                }
                                break;
                            case OperationKind.Object:
                                if (m_PendingAttachedPoint.m_OriginalEntity != Entity.Null)
                                    inputDeps = CreateRoadAttachedBuildingDefinitions(inputDeps, Entity.Null);
                                else CreatePlacementDefinitions();
                                break;
                            case OperationKind.BuildingUpgrade:
                            case OperationKind.BuildingRebuild:
                                inputDeps = CreateBuildingUpgradeDefinitions(inputDeps);
                                break;
                            case OperationKind.BuildingMove:
                            case OperationKind.TransitStopMove:
                                inputDeps = CreateBuildingMoveDefinitions(inputDeps);
                                break;
                            case OperationKind.Net:
                                CreateRoadDefinitions();
                                break;
                            case OperationKind.Demolish:
                                CreateModifyDefinitions(CreationFlags.Delete, default);
                                break;
                            case OperationKind.Upgrade:
                                CreateModifyDefinitions(CreationFlags.Upgrade, m_PendingUpgradeFlags);
                                break;
                            case OperationKind.Area:
                                CreateAreaDefinitions();
                                break;
                            case OperationKind.NodeUpgrade:
                                CreateNodeUpgradeDefinition();
                                break;
                            case OperationKind.Zoning:
                                CreateZoningDefinition();
                                break;
                            case OperationKind.Terrain:
                                CreateTerrainDefinition();
                                break;
                        }
                        m_Stage = Stage.Apply;
                        break;

                    case Stage.Apply:
                        if (m_PendingKind == OperationKind.TransitStopMove) {
                            ApplyTransitStopMovePreview();
                            m_Stage = Stage.Finish;
                            break;
                        }
                        if (m_PendingKind == OperationKind.Terrain)
                        {
                            if (GetAllowApply()) { applyMode = ApplyMode.Apply; m_TerrainAppliedPasses++; }
                            else { applyMode = ApplyMode.Clear; CompletePending(BridgeResponse.Error(409,
                                "native terrain preview rejected; earlier brush passes may remain; inspect terrain before retrying",
                                mayHaveApplied: m_TerrainAppliedPasses > 0)); }
                            m_Stage = Stage.Finish;
                            break;
                        }
                        if (m_PendingKind == OperationKind.Zoning)
                        {
                            ApplyZoningPreview();
                            m_Stage = Stage.Finish;
                            break;
                        }
                        // Object预览必须在原生Validation之后清除，不能因SDK传了dryRun仍实际放置和收费。
                        if ((m_PendingKind == OperationKind.Object || m_PendingKind == OperationKind.RoadLayout || m_PendingKind == OperationKind.RoadLayoutRemoval || m_PendingKind == OperationKind.BuildingRebuild) && m_PendingRequest != null
                            && m_PendingRequest.TryGetBool("dryRun", out bool objectDryRun) && objectDryRun)
                        {
                            bool allowed = GetAllowApply();
                            applyMode = ApplyMode.Clear;
                            CompletePending(BridgeResponse.Json(new { dryRun = true, allowed, applied = false,
                                prefab = m_PendingPrefab?.name, nativePreviewCost = NativePreviewCost(), errors = CollectPlacementErrors(),
                                validationErrors = CollectPlacementErrorLocations(),
                                nativeRoadPreview = CollectNativeRoadPreview(),
                                note = "原生对象/复合资产预览已清除，未施工、未收费；实际放置须显式dryRun=false" }));
                        }
                        else if (GetAllowApply())
                        {
                            applyMode = ApplyMode.Apply;
                            if (m_PendingKind != OperationKind.BuildingMove)
                                m_PendingApplyResponse = BuildSuccessResponse();
                        }
                        else
                        {
                            applyMode = ApplyMode.Clear;
                            List<string> errors = CollectPlacementErrors();
                            CompletePending(BridgeResponse.Json(new { error =
                                "原生施工校验拒绝：" + (errors.Count > 0 ? string.Join(", ", errors)
                                : "未返回具体错误图标；请核场址、地形、网络接入，不盲重放"),
                                validationErrors = CollectPlacementErrorLocations() }, 409));
                        }
                        m_Stage = Stage.Finish;
                        break;

                    case Stage.Finish:
                        if (m_PendingKind == OperationKind.TransitStopMove) {
                            if (!CompleteTransitStopMove()) break;
                            applyMode = ApplyMode.None;
                            Deactivate();
                            break;
                        }
                        if (m_PendingKind == OperationKind.Terrain && m_PendingRequest != null)
                        {
                            if (m_TerrainAppliedPasses < m_TerrainPasses) { m_Stage = Stage.CreateDefinitions; applyMode = ApplyMode.Clear; break; }
                            CompletePending(BridgeResponse.Json(new { applied = true, passes = m_TerrainAppliedPasses,
                                note = "native terrain brush applied; GPU heightmap and water updates are asynchronous; resample terrain and environment after update, not proof of final target height" }));
                        }
                        if (m_PendingKind == OperationKind.Zoning) CompleteZoning();
                        if (m_PendingKind == OperationKind.Upgrade && m_PendingApplyResponse != null && m_PendingRequest != null)
                        {
                            CompositionFlags actual = EntityManager.Exists(m_PendingTarget) && EntityManager.HasComponent<Upgraded>(m_PendingTarget)
                                ? EntityManager.GetComponentData<Upgraded>(m_PendingTarget).m_Flags : default;
                            // 原生工具可能接受预览后过滤不适用标志；没有实际回读便不能宣称升级完成。
                            if (!EntityManager.Exists(m_PendingTarget) || actual != m_PendingUpgradeFlags)
                                m_PendingApplyResponse = BridgeResponse.Json(new { error = "原生升级已提交，但实际标志与请求不一致；先刷新现场，不重放。",
                                    failurePhase = "native_readback", mayHaveApplied = true, safeToRetry = false,
                                    entity = new { index = m_PendingTarget.Index, version = m_PendingTarget.Version },
                                    requestedFlags = new { general = m_PendingUpgradeFlags.m_General.ToString(), left = m_PendingUpgradeFlags.m_Left.ToString(), right = m_PendingUpgradeFlags.m_Right.ToString() },
                                    actualFlags = new { general = actual.m_General.ToString(), left = actual.m_Left.ToString(), right = actual.m_Right.ToString() } }, 409);
                        }
                        if (m_PendingKind == OperationKind.BuildingMove && m_PendingRequest != null)
                        {
                            if (EntityManager.Exists(m_PendingTarget) && EntityManager.HasComponent<Transform>(m_PendingTarget)
                                && math.distance(EntityManager.GetComponentData<Transform>(m_PendingTarget).m_Position.xz, m_PendingPosition.xz) < 1f)
                                CompletePending(BridgeResponse.Json(new { moved = true, building = new { index = m_PendingTarget.Index, version = m_PendingTarget.Version },
                                    prefab = m_PendingPrefab.name, position = new { x = m_PendingPosition.x, y = m_PendingPosition.y, z = m_PendingPosition.z },
                                    note = "native Relocate applied and original position verified; inspect renters, installed upgrades, utilities and access after simulation" }));
                            else CompletePending(BridgeResponse.Error(500, "native move application returned but original position not verified; refresh building list before any retry"));
                        }
                        // 等原生ToolApply阶段结束再答复，避免成功回执与下一次界面切换之间留下悬挂操作。
                        if (m_PendingApplyResponse != null) CompletePending(m_PendingApplyResponse);
                        applyMode = ApplyMode.None;
                        Deactivate();
                        break;

                    default:
                        applyMode = ApplyMode.None;
                        break;
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"BridgeToolSystem error in stage {m_Stage}: {e}");
                CompletePending(m_PendingKind == OperationKind.TransitStopMove
                    ? BridgeResponse.Json(new { error = $"stop relocation failed: {e.GetType().Name}: {e.Message}",
                        mayHaveApplied = m_StopMoveApplied, safeToRetry = false }, 500)
                    : BridgeResponse.Error(500, $"placement failed: {e.GetType().Name}: {e.Message}"));
                applyMode = ApplyMode.None;
                Deactivate();
            }
            return inputDeps;
        }

        private BridgeResponse BuildSuccessResponse()
        {
            switch (m_PendingKind)
            {
                case OperationKind.RoadLayoutRemoval:
                    return BridgeResponse.Json(new { applied = true, roads = m_RoadRemovalTargets.Length,
                        nativePreviewCost = NativePreviewCost(),
                        note = "整组独立道路经原生拆除预览与应用；退款由游戏计费。回读保留道路、实际删除与资金，不能重放旧实体。" });
                case OperationKind.RoadLayout:
                    return BridgeResponse.Json(new { applied = true, segments = m_RoadLayout.Length,
                        nativePreviewCost = NativePreviewCost(),
                        note = "原生道路布局应用；须回读实际曲线、车道、节点和收费，不代表已通车或运营验收。" });
                case OperationKind.BuildingUpgrade:
                    return BridgeResponse.Json(new { upgraded = true, building = new { index = m_PendingTarget.Index, version = m_PendingTarget.Version }, prefab = m_PendingPrefab.name, note = "native tool application accepted; verify InstalledUpgrade and actual facility capacity after simulation ticks" });
                case OperationKind.BuildingRebuild:
                    return BridgeResponse.Json(new { applied = true, building = new { index = m_PendingTarget.Index, version = m_PendingTarget.Version }, prefab = m_PendingLabel,
                        nativePreviewCost = NativePreviewCost(), note = "原生重建预览通过并提交；必须回读损毁状态、附属、端口和收费，不据此重复请求" });
                case OperationKind.Demolish:
                    return BridgeResponse.Json(new
                    {
                        demolished = true,
                        prefab = m_PendingLabel,
                        entity = new { index = m_PendingTarget.Index, version = m_PendingTarget.Version },
                        note = "deleted via the game's bulldoze pipeline (nodes/blocks/lanes cleaned up by the game)",
                    });
                case OperationKind.Upgrade:
                    return BridgeResponse.Json(new
                    {
                        upgraded = true,
                        prefab = m_PendingLabel,
                        entity = new { index = m_PendingTarget.Index, version = m_PendingTarget.Version },
                        note = "upgrade applied via the tool pipeline; the segment is recreated with the new composition",
                    });
                case OperationKind.NodeUpgrade:
                    return BridgeResponse.Json(new
                    {
                        upgraded = true,
                        junction = new { index = m_PendingTarget.Index, version = m_PendingTarget.Version },
                        mode = m_PendingLabel,
                        note = "junction upgrade applied via the tool pipeline",
                    });
                case OperationKind.Net:
                    return BridgeResponse.Json(new
                    {
                        placed = true,
                        prefab = m_PendingPrefab != null ? m_PendingPrefab.name : null,
                        start = new { x = m_PendingPosition.x, z = m_PendingPosition.z },
                        end = new { x = m_PendingEnd.x, z = m_PendingEnd.z },
                        note = "committed this frame; verify via /city/roads or /screenshot",
                    });
                case OperationKind.Area:
                    return BridgeResponse.Json(new
                    {
                        created = true,
                        prefab = m_PendingPrefab != null ? m_PendingPrefab.name : null,
                        nodes = m_PendingAreaNodes != null ? m_PendingAreaNodes.Length : 0,
                        note = "area committed this frame; list districts via /districts",
                    });
                default:
                    return BridgeResponse.Json(new
                    {
                        placed = true,
                        prefab = m_PendingPrefab != null ? m_PendingPrefab.name : null,
                        position = new
                        {
                            x = m_PendingPosition.x,
                            y = m_PendingPosition.y,
                            z = m_PendingPosition.z,
                        },
                        note = "committed this frame; verify via /city/buildings or /screenshot",
                    });
            }
        }

        private void CompletePending(BridgeResponse response)
        {
            m_PendingRequest?.Complete(response);
            m_PendingRequest = null;
        }

        private void Deactivate()
        {
            m_Stage = Stage.Idle;
            m_PendingRequest = null;
            m_PendingPrefab = null;
            m_PendingApplyResponse = null;
            m_PendingPrefabEntity = Entity.Null;
            m_StopMoveReadContract = null;
            m_StopMoveContract = null;
            m_StopMoveApplied = false;
            m_RoadLayout = null;
            m_RoadRemovalTargets = null;
            m_ZonePreview?.Clear();
            if (m_ToolSystem.activeTool == this)
            {
                m_ToolSystem.activeTool = m_PreviousTool != null ? m_PreviousTool : m_DefaultToolSystem;
            }
            m_PreviousTool = null;
        }

        /// <summary>
        /// Creates a modify definition for the pending target, faithfully
        /// mirroring BulldozeToolSystem.AddEntity (CreationDefinition with
        /// m_Original + flags plus a NetCourse/ObjectDefinition describing the
        /// original). Used for Delete (bulldoze) and Upgrade (road upgrades:
        /// grass/trees/lighting...). The game's generate/apply systems handle
        /// all related cleanup/recreation — nodes, zone blocks, lanes — which a
        /// raw component edit would skip (and skipping corrupts state).
        /// </summary>
        private void CreateModifyDefinitions(CreationFlags flags, CompositionFlags upgrades)
        {
            Entity target = m_PendingTarget;
            EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            Entity e = commandBuffer.CreateEntity();
            var definition = new CreationDefinition
            {
                m_Original = target,
                m_Flags = flags,
            };
            // 独立网络必须与原生NetTool一致标记SubElevation，才能参与Essential校验、收费和退款。
            // 资产内部附属网络仍由其母体计费，不能重复收取。
            if (EntityManager.HasComponent<Edge>(target) && !EntityManager.HasComponent<Owner>(target))
                definition.m_Flags |= CreationFlags.SubElevation;
            commandBuffer.AddComponent(e, default(Updated));
            if (upgrades != default(CompositionFlags))
            {
                commandBuffer.AddComponent(e, new Game.Net.Upgraded { m_Flags = upgrades });
            }

            if (EntityManager.HasComponent<Game.Net.Edge>(target))
            {
                Game.Net.Edge edge = EntityManager.GetComponentData<Game.Net.Edge>(target);
                NetCourse course = default;
                course.m_Curve = EntityManager.GetComponentData<Game.Net.Curve>(target).m_Bezier;
                course.m_Length = MathUtils.Length(course.m_Curve);
                course.m_FixedIndex = EntityManager.HasComponent<Game.Net.Fixed>(target)
                    ? EntityManager.GetComponentData<Game.Net.Fixed>(target).m_Index
                    : -1;
                course.m_StartPosition.m_Entity = edge.m_Start;
                course.m_StartPosition.m_Position = course.m_Curve.a;
                course.m_StartPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(course.m_Curve));
                course.m_StartPosition.m_CourseDelta = 0f;
                course.m_EndPosition.m_Entity = edge.m_End;
                course.m_EndPosition.m_Position = course.m_Curve.d;
                course.m_EndPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(course.m_Curve));
                course.m_EndPosition.m_CourseDelta = 1f;
                commandBuffer.AddComponent(e, course);
            }
            else if (EntityManager.HasComponent<Transform>(target))
            {
                Transform transform = EntityManager.GetComponentData<Transform>(target);
                var objectDefinition = new ObjectDefinition
                {
                    m_Position = transform.m_Position,
                    m_Rotation = transform.m_Rotation,
                    m_Probability = 100,
                    m_PrefabSubIndex = -1,
                    m_LocalPosition = transform.m_Position,
                    m_LocalRotation = transform.m_Rotation,
                };
                if (EntityManager.HasComponent<Game.Objects.Elevation>(target))
                {
                    Game.Objects.Elevation elevation = EntityManager.GetComponentData<Game.Objects.Elevation>(target);
                    objectDefinition.m_Elevation = elevation.m_Elevation;
                    objectDefinition.m_ParentMesh = Game.Objects.ObjectUtils.GetSubParentMesh(elevation.m_Flags);
                }
                else
                {
                    objectDefinition.m_ParentMesh = -1;
                }
                commandBuffer.AddComponent(e, objectDefinition);
            }
            else if (EntityManager.HasBuffer<Game.Areas.Node>(target))
            {
                DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(target, isReadOnly: true);
                commandBuffer.AddBuffer<Game.Areas.Node>(e).CopyFrom(nodes.AsNativeArray());
            }

            commandBuffer.AddComponent(e, definition);
        }

        /// <summary>
        /// Junction upgrade definition, mirroring NetToolSystem.CreateDefinitionsJob
        /// .CreateUpgrade for a node control point: CreationDefinition on the node
        /// (SubElevation | Align, plus Upgrade | Parent when flags change), the new
        /// Upgraded flags and a zero-length NetCourse at the node.
        /// </summary>
        private void CreateNodeUpgradeDefinition()
        {
            Entity node = m_PendingTarget;
            CompositionFlags oldFlags = EntityManager.HasComponent<Game.Net.Upgraded>(node)
                ? EntityManager.GetComponentData<Game.Net.Upgraded>(node).m_Flags
                : default;
            CompositionFlags newFlags = m_PendingUpgradeFlags;

            var definition = new CreationDefinition
            {
                m_Original = node,
                m_Prefab = EntityManager.GetComponentData<PrefabRef>(node).m_Prefab,
                m_Flags = CreationFlags.SubElevation | CreationFlags.Align,
            };
            if (newFlags != oldFlags)
            {
                definition.m_Flags |= CreationFlags.Upgrade | CreationFlags.Parent;
            }

            float3 position = EntityManager.GetComponentData<Game.Net.Node>(node).m_Position;
            float elevation = EntityManager.HasComponent<Game.Net.Elevation>(node)
                ? math.cmin(EntityManager.GetComponentData<Game.Net.Elevation>(node).m_Elevation)
                : 0f;
            NetCourse course = default;
            course.m_Curve = new Bezier4x3(position, position, position, position);
            course.m_Length = 0f;
            course.m_FixedIndex = -1;
            course.m_StartPosition = NodeCoursePos(node, position, elevation, 0f, CoursePosFlags.IsFirst);
            course.m_EndPosition = NodeCoursePos(node, position, elevation, 1f, CoursePosFlags.IsLast);

            EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            Entity e = commandBuffer.CreateEntity();
            commandBuffer.AddComponent(e, definition);
            commandBuffer.AddComponent(e, new Game.Net.Upgraded { m_Flags = newFlags });
            commandBuffer.AddComponent(e, default(Updated));
            commandBuffer.AddComponent(e, course);
        }

        private static CoursePos NodeCoursePos(Entity node, float3 position, float elevation, float delta, CoursePosFlags flags)
        {
            return new CoursePos
            {
                m_Entity = node,
                m_Position = position,
                m_Rotation = NetUtils.GetNodeRotation(float3.zero),
                m_Elevation = new float2(elevation),
                m_CourseDelta = delta,
                m_SplitPosition = 0f,
                m_Flags = flags,
                m_ParentMesh = -1,
            };
        }

        /// <summary>
        /// Creates an area definition (district/surface): CreationDefinition +
        /// polygon node buffer; elevation float.MinValue snaps nodes to terrain
        /// (mirrors the game's area definition flow).
        /// </summary>
        private void CreateAreaDefinitions()
        {
            EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            Entity e = commandBuffer.CreateEntity();
            Unity.Mathematics.Random random = RandomSeed.Next().GetRandom(0);
            commandBuffer.AddComponent(e, new CreationDefinition
            {
                m_Prefab = m_PendingPrefabEntity,
                m_RandomSeed = random.NextInt(),
                m_Flags = CreationFlags.SubElevation,
            });
            commandBuffer.AddComponent(e, default(Updated));
            DynamicBuffer<Game.Areas.Node> nodes = commandBuffer.AddBuffer<Game.Areas.Node>(e);
            foreach (float3 position in m_PendingAreaNodes)
            {
                nodes.Add(new Game.Areas.Node(position, float.MinValue));
            }
            // 原生 GenerateAreasSystem 仅在首尾位置相同时设置 Complete，
            // 并移除重复尾点；不能把未闭合的临时区域当作完成辖区应用。
            if (m_PendingAreaNodes.Length >= 3 &&
                !m_PendingAreaNodes[0].Equals(m_PendingAreaNodes[m_PendingAreaNodes.Length - 1]))
                nodes.Add(new Game.Areas.Node(m_PendingAreaNodes[0], float.MinValue));
        }

        /// <summary>
        /// Creates a standalone straight-road course definition from the pending
        /// start to end position, terrain-following (mirrors the standalone-net
        /// branch of the game's net definition flow).
        /// </summary>
        private void CreateRoadDefinitions()
        {
            TerrainHeightData terrainHeight = m_TerrainSystem.GetHeightData();

            Curve rawCurve = default;
            if (m_PendingHasMid)
            {
                // Quadratic bezier through the mid control point, elevated to cubic.
                float3 a = m_PendingPosition;
                float3 d = m_PendingEnd;
                float3 m = m_PendingMid;
                rawCurve.m_Bezier = new Bezier4x3(a, a + (m - a) * (2f / 3f), d + (m - d) * (2f / 3f), d);
            }
            else
            {
                rawCurve.m_Bezier = NetUtils.StraightCurve(m_PendingPosition, m_PendingEnd);
            }
            Bezier4x3 adjusted = NetUtils.AdjustPosition(
                rawCurve, fixedStart: false, linearMiddle: false, fixedEnd: false, ref terrainHeight).m_Bezier;

            float e1 = m_PendingElevations.x;
            float e2 = m_PendingElevations.y;
            if (e1 != 0f || e2 != 0f)
            {
                // Lift the terrain-following curve by linearly interpolated
                // elevation; the pipeline turns nonzero course elevations into
                // bridge/elevated segments with pillars.
                adjusted.a.y += e1;
                adjusted.b.y += math.lerp(e1, e2, 1f / 3f);
                adjusted.c.y += math.lerp(e1, e2, 2f / 3f);
                adjusted.d.y += e2;
            }

            NetCourse course = default;
            course.m_Curve = adjusted;
            course.m_Elevation = new float2(math.min(e1, e2), math.max(e1, e2));
            course.m_StartPosition.m_Position = course.m_Curve.a;
            course.m_StartPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(course.m_Curve));
            course.m_StartPosition.m_CourseDelta = 0f;
            course.m_StartPosition.m_ParentMesh = -1;
            course.m_StartPosition.m_Elevation = e1;
            course.m_StartPosition.m_Flags = CoursePosFlags.IsFirst | CoursePosFlags.FreeHeight;
            course.m_EndPosition.m_Position = course.m_Curve.d;
            course.m_EndPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(course.m_Curve));
            course.m_EndPosition.m_CourseDelta = 1f;
            course.m_EndPosition.m_ParentMesh = -1;
            course.m_EndPosition.m_Elevation = e2;
            course.m_EndPosition.m_Flags = CoursePosFlags.IsLast | CoursePosFlags.FreeHeight;
            course.m_Length = MathUtils.Length(course.m_Curve);
            course.m_FixedIndex = -1;

            Unity.Mathematics.Random random = RandomSeed.Next().GetRandom(0);
            EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            Entity entity = commandBuffer.CreateEntity();
            commandBuffer.AddComponent(entity, new CreationDefinition
            {
                m_Prefab = m_PendingPrefabEntity,
                m_RandomSeed = random.NextInt(),
            });
            commandBuffer.AddComponent(entity, default(Updated));
            commandBuffer.AddComponent(entity, course);
        }

        /// <summary>
        /// Fills and synchronously executes the ported LineTool definition job
        /// for a single object at the pending position/rotation.
        /// </summary>
        private void CreatePlacementDefinitions()
        {
            CreateDefinitions definitions = default;
            definitions.m_RandomizationEnabled = false;
            definitions.m_FixedRandomSeed = 0;
            definitions.m_EditorMode = m_ToolSystem.actionMode.IsEditor();
            definitions.m_LefthandTraffic = m_CityConfigurationSystem.leftHandTraffic;
            definitions.m_ObjectPrefab = m_PendingPrefabEntity;
            definitions.m_Theme = m_CityConfigurationSystem.defaultTheme;
            definitions.m_RandomSeed = RandomSeed.Next();
            definitions.m_AgeMask = AgeMask.Mature;
            definitions.m_ControlPoint = new ControlPoint
            {
                m_Position = m_PendingPosition,
                m_Rotation = m_PendingRotation,
            };
            definitions.m_AttachmentPrefab = default;
            definitions.m_OwnerData = GetComponentLookup<Owner>(true);
            definitions.m_TransformData = GetComponentLookup<Transform>(true);
            definitions.m_AttachedData = GetComponentLookup<Game.Objects.Attached>(true);
            definitions.m_LocalTransformCacheData = GetComponentLookup<LocalTransformCache>(true);
            definitions.m_ElevationData = GetComponentLookup<Game.Objects.Elevation>(true);
            definitions.m_BuildingData = GetComponentLookup<Game.Buildings.Building>(true);
            definitions.m_LotData = GetComponentLookup<Game.Buildings.Lot>(true);
            definitions.m_EdgeData = GetComponentLookup<Game.Net.Edge>(true);
            definitions.m_NodeData = GetComponentLookup<Game.Net.Node>(true);
            definitions.m_CurveData = GetComponentLookup<Game.Net.Curve>(true);
            definitions.m_NetElevationData = GetComponentLookup<Game.Net.Elevation>(true);
            definitions.m_OrphanData = GetComponentLookup<Game.Net.Orphan>(true);
            definitions.m_UpgradedData = GetComponentLookup<Game.Net.Upgraded>(true);
            definitions.m_CompositionData = GetComponentLookup<Game.Net.Composition>(true);
            definitions.m_AreaClearData = GetComponentLookup<Game.Areas.Clear>(true);
            definitions.m_AreaSpaceData = GetComponentLookup<Game.Areas.Space>(true);
            definitions.m_AreaLotData = GetComponentLookup<Game.Areas.Lot>(true);
            definitions.m_EditorContainerData = GetComponentLookup<Game.Tools.EditorContainer>(true);
            definitions.m_PrefabRefData = GetComponentLookup<PrefabRef>(true);
            definitions.m_PrefabNetObjectData = GetComponentLookup<NetObjectData>(true);
            definitions.m_PrefabBuildingData = GetComponentLookup<BuildingData>(true);
            definitions.m_PrefabAssetStampData = GetComponentLookup<AssetStampData>(true);
            definitions.m_PrefabBuildingExtensionData = GetComponentLookup<BuildingExtensionData>(true);
            definitions.m_PrefabSpawnableObjectData = GetComponentLookup<SpawnableObjectData>(true);
            definitions.m_PrefabObjectGeometryData = GetComponentLookup<ObjectGeometryData>(true);
            definitions.m_PrefabPlaceableObjectData = GetComponentLookup<PlaceableObjectData>(true);
            definitions.m_PrefabAreaGeometryData = GetComponentLookup<AreaGeometryData>(true);
            definitions.m_PrefabBuildingTerraformData = GetComponentLookup<BuildingTerraformData>(true);
            definitions.m_PrefabCreatureSpawnData = GetComponentLookup<CreatureSpawnData>(true);
            definitions.m_PlaceholderBuildingData = GetComponentLookup<PlaceholderBuildingData>(true);
            definitions.m_PrefabNetGeometryData = GetComponentLookup<NetGeometryData>(true);
            definitions.m_PrefabCompositionData = GetComponentLookup<NetCompositionData>(true);
            definitions.m_SubObjects = GetBufferLookup<Game.Objects.SubObject>(true);
            definitions.m_CachedNodes = GetBufferLookup<LocalNodeCache>(true);
            definitions.m_InstalledUpgrades = GetBufferLookup<Game.Buildings.InstalledUpgrade>(true);
            definitions.m_SubNets = GetBufferLookup<Game.Net.SubNet>(true);
            definitions.m_ConnectedEdges = GetBufferLookup<Game.Net.ConnectedEdge>(true);
            definitions.m_SubAreas = GetBufferLookup<Game.Areas.SubArea>(true);
            definitions.m_AreaNodes = GetBufferLookup<Game.Areas.Node>(true);
            definitions.m_AreaTriangles = GetBufferLookup<Game.Areas.Triangle>(true);
            definitions.m_PrefabSubObjects = GetBufferLookup<Game.Prefabs.SubObject>(true);
            definitions.m_PrefabSubNets = GetBufferLookup<Game.Prefabs.SubNet>(true);
            definitions.m_PrefabSubLanes = GetBufferLookup<Game.Prefabs.SubLane>(true);
            definitions.m_PrefabSubAreas = GetBufferLookup<Game.Prefabs.SubArea>(true);
            definitions.m_PrefabSubAreaNodes = GetBufferLookup<SubAreaNode>(true);
            definitions.m_PrefabPlaceholderElements = GetBufferLookup<PlaceholderObjectElement>(true);
            definitions.m_PrefabRequirementElements = GetBufferLookup<ObjectRequirementElement>(true);
            definitions.m_PrefabServiceUpgradeBuilding = GetBufferLookup<ServiceUpgradeBuilding>(true);
            definitions.m_WaterSurfaceData = m_WaterSystem.GetSurfaceData(out _);
            definitions.m_TerrainHeightData = m_TerrainSystem.GetHeightData();
            definitions.m_CommandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            definitions.Execute();
        }

        private JobHandle CreateBuildingUpgradeDefinitions(JobHandle inputDeps)
        {
            var points = new NativeList<ControlPoint>(1, Allocator.TempJob);
            try
            {
                points.Add(new ControlPoint { m_Position = m_PendingPosition, m_Rotation = m_PendingRotation,
                    m_ElementIndex = new int2(-1, -1) });
            }
            catch
            {
                // 尚未进入原生调度，控制点仍由本方法独占。
                points.Dispose();
                throw;
            }
            var handle = CreateDefinitions(m_PendingPrefabEntity, Entity.Null, Entity.Null, m_PendingTarget,
                Entity.Null, Entity.Null, m_CityConfigurationSystem.defaultTheme, points, default,
                false, m_CityConfigurationSystem.leftHandTraffic, false, false, 1f, 0f, 1f, 1f, 0.1f,
                RandomSeed.Next(), Snap.None, AgeMask.Mature, false, default, inputDeps);
            return points.Dispose(handle);
        }

        private JobHandle CreateBuildingMoveDefinitions(JobHandle inputDeps)
        {
            return CreateRoadAttachedBuildingDefinitions(inputDeps, m_PendingTarget);
        }

        /// <summary>保留道路吸附的原生控制点，交由 ObjectTool 生成 Attached 与道路子对象更新。</summary>
        private JobHandle CreateRoadAttachedBuildingDefinitions(JobHandle inputDeps, Entity original)
        {
            var points = new NativeList<ControlPoint>(1, Allocator.TempJob);
            try
            {
                var point = m_PendingAttachedPoint;
                point.m_Position = m_PendingPosition;
                point.m_Rotation = m_PendingRotation;
                point.m_ElementIndex = new int2(-1, -1);
                points.Add(point);
            }
            catch
            {
                // CreateDefinitions之后可能已有读作业；这里只释放调度前的失败准备。
                points.Dispose();
                throw;
            }
            var handle = CreateDefinitions(m_PendingPrefabEntity, Entity.Null, Entity.Null, Entity.Null,
                original, Entity.Null, m_CityConfigurationSystem.defaultTheme, points, default,
                false, m_CityConfigurationSystem.leftHandTraffic, false, false, 1f, 0f, 1f, 0f, 0.1f,
                RandomSeed.Next(), Snap.None, AgeMask.Mature, false, default, inputDeps);
            return points.Dispose(handle);
        }
    }
}
