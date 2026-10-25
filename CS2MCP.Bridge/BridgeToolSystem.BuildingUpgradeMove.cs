using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class BridgeToolSystem
    {
        private Entity m_UpgradeMoveOwner;
        private Entity[] m_UpgradeMoveBefore;
        private bool m_UpgradeMoveApplied;
        private int m_UpgradeMoveReadbacks;
        private int m_UpgradeMoveCost;

        /// <summary>原附属作为original；原生定义从其Owner恢复母体，不手改归属/安装列表或Transform。</summary>
        public bool TryQueueBuildingUpgradeMove(Entity child, Entity owner, Entity prefabEntity, PrefabBase prefab,
            ControlPoint point, Entity[] before, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle) return false;
            m_PendingKind = OperationKind.BuildingUpgradeMove;
            m_PendingTarget = child;
            m_UpgradeMoveOwner = owner;
            m_UpgradeMoveBefore = before;
            m_UpgradeMoveApplied = false;
            m_UpgradeMoveReadbacks = 0;
            m_PendingPrefabEntity = prefabEntity;
            m_PendingPrefab = prefab;
            m_PendingPosition = point.m_Position;
            m_PendingRotation = point.m_Rotation;
            m_PendingAttachedPoint = point;
            m_PendingRequest = request;
            Activate();
            return true;
        }

        private void ApplyBuildingUpgradeMovePreview()
        {
            var preview = new List<object>();
            int originals = 0;
            bool ownerMatches = false;
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Transform>(), ComponentType.ReadOnly<PrefabRef>()))
            using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities)
                {
                    var temp = EntityManager.GetComponentData<Temp>(entity);
                    if (temp.m_Original != m_PendingTarget || EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab != m_PendingPrefabEntity) continue;
                    originals++;
                    Entity owner = EntityManager.HasComponent<Owner>(entity) ? EntityManager.GetComponentData<Owner>(entity).m_Owner : Entity.Null;
                    Entity originalOwner = EntityManager.HasComponent<Temp>(owner) ? EntityManager.GetComponentData<Temp>(owner).m_Original : owner;
                    ownerMatches |= originalOwner == m_UpgradeMoveOwner;
                    var transform = EntityManager.GetComponentData<Transform>(entity);
                    preview.Add(new { entity = new { index = entity.Index, version = entity.Version },
                        original = new { index = temp.m_Original.Index, version = temp.m_Original.Version },
                        owner = new { index = originalOwner.Index, version = originalOwner.Version }, flags = temp.m_Flags.ToString(),
                        position = PreviewPoint(transform.m_Position), rotation = new { x = transform.m_Rotation.value.x, y = transform.m_Rotation.value.y, z = transform.m_Rotation.value.z, w = transform.m_Rotation.value.w } });
                }
            bool bindingVerified = originals == 1 && ownerMatches;
            bool allowed = GetAllowApply() && bindingVerified;
            m_UpgradeMoveCost = NativePreviewCost();
            if (m_PendingRequest.TryGetBool("dryRun", out bool dryRun) && dryRun)
            {
                applyMode = ApplyMode.Clear;
                CompletePending(BridgeResponse.Json(new { dryRun = true, allowed, applied = false, bindingVerified,
                    child = new { index = m_PendingTarget.Index, version = m_PendingTarget.Version },
                    owner = new { index = m_UpgradeMoveOwner.Index, version = m_UpgradeMoveOwner.Version },
                    prefab = m_PendingPrefab.name, position = PreviewPoint(m_PendingPosition),
                    nativePreviewCost = m_UpgradeMoveCost, preview, errors = CollectPlacementErrors(), validationErrors = CollectPlacementErrorLocations(),
                    note = "完整原生独立扩建Relocate预览已清除，未施工/收费；原child与母体绑定另核，实际到达仍须应用后验收" }));
            }
            else if (allowed) { m_UpgradeMoveApplied = true; applyMode = ApplyMode.Apply; }
            else
            {
                applyMode = ApplyMode.Clear;
                CompletePending(BridgeResponse.Json(new { error = "native upgrade relocation preview rejected; no application",
                    bindingVerified, preview, validationErrors = CollectPlacementErrorLocations(), mayHaveApplied = false, safeToRetry = false },409));
            }
        }

        private bool CompleteBuildingUpgradeMove()
        {
            if (m_PendingRequest == null || !m_UpgradeMoveApplied) return true;
            bool retained = EntityManager.Exists(m_PendingTarget) && EntityManager.HasComponent<Owner>(m_PendingTarget)
                && EntityManager.GetComponentData<Owner>(m_PendingTarget).m_Owner == m_UpgradeMoveOwner
                && EntityManager.HasComponent<PrefabRef>(m_PendingTarget)
                && EntityManager.GetComponentData<PrefabRef>(m_PendingTarget).m_Prefab == m_PendingPrefabEntity;
            bool contract = retained && EntityManager.Exists(m_UpgradeMoveOwner) && EntityManager.HasBuffer<Game.Buildings.InstalledUpgrade>(m_UpgradeMoveOwner);
            if (contract)
            {
                var installed = EntityManager.GetBuffer<Game.Buildings.InstalledUpgrade>(m_UpgradeMoveOwner,true);
                contract = installed.Length == m_UpgradeMoveBefore.Length;
                var refs = new Dictionary<Entity,int>();
                foreach (var item in installed) { refs.TryGetValue(item.m_Upgrade,out int count); refs[item.m_Upgrade] = count + 1; }
                foreach (var entity in m_UpgradeMoveBefore) {
                    if (!refs.TryGetValue(entity,out int count) || count == 0) contract = false;
                    else refs[entity] = count - 1;
                }
            }
            bool geometry = retained && EntityManager.HasComponent<Transform>(m_PendingTarget)
                && math.distance(EntityManager.GetComponentData<Transform>(m_PendingTarget).m_Position, m_PendingPosition) < .1f
                && math.abs(math.dot(EntityManager.GetComponentData<Transform>(m_PendingTarget).m_Rotation.value,m_PendingRotation.value)) > .9999f;
            // 原生应用/升级buffer更新允许少量后续工具帧，不重复提交应用。
            if ((!contract || !geometry) && ++m_UpgradeMoveReadbacks < 8) return false;
            CompletePending(BridgeResponse.Json(new { moved = contract && geometry, applied = true,
                child = new { index = m_PendingTarget.Index, version = m_PendingTarget.Version },
                owner = new { index = m_UpgradeMoveOwner.Index, version = m_UpgradeMoveOwner.Version },
                originalChildRetained = retained, installedContractUnchanged = contract, geometryVerified = geometry,
                position = PreviewPoint(m_PendingPosition), nativePreviewCost = m_UpgradeMoveCost,
                mayHaveApplied = true, safeToRetry = false,
                note = contract && geometry ? "正常Relocate已回读原附属、Owner、完整安装引用及几何；车行/步行/告警和真实运营须另验"
                    : "已应用但附属/安装/几何回读未全通过，先查现场，不重放" }, contract && geometry ? 200 : 409));
            return true;
        }
    }
}
