using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class BridgeToolSystem
    {
        private struct ZonePreviewCell
        {
            public Entity Block;
            public int Index;
            public Cell Before;
            public Cell Preview;
        }

        private Zoning m_PendingZoning;
        private bool m_ZonePreviewOnly;
        private bool m_ZoneApplied;
        private int m_ZoneMaxCells;
        private List<ZonePreviewCell> m_ZonePreview;

        /// <summary>填充、框选、细刷均交给原生 GenerateZones/ApplyZones；不自行推测跨路段格子连通。</summary>
        public bool TryQueueZoning(Entity prefabEntity, PrefabBase prefab, Entity seedBlock, Zoning zoning,
            bool previewOnly, int maxCells, BridgeRequest request)
        {
            if (IsBusy) return false;
            m_PendingKind = OperationKind.Zoning;
            m_PendingPrefabEntity = prefabEntity;
            m_PendingPrefab = prefab;
            m_PendingTarget = seedBlock;
            m_PendingZoning = zoning;
            m_ZonePreviewOnly = previewOnly;
            m_ZoneApplied = false;
            m_ZoneMaxCells = maxCells;
            m_ZonePreview = new List<ZonePreviewCell>();
            m_PendingRequest = request;
            Activate();
            return true;
        }

        private void CreateZoningDefinition()
        {
            var buffer = m_ToolOutputBarrier.CreateCommandBuffer();
            Entity definition = buffer.CreateEntity();
            buffer.AddComponent(definition, new CreationDefinition { m_Prefab = m_PendingPrefabEntity, m_Original = m_PendingTarget });
            buffer.AddComponent(definition, m_PendingZoning);
            buffer.AddComponent<Updated>(definition);
        }

        private void ApplyZoningPreview()
        {
            // 查询仅限本工具刚生成的原生临时格；切换工具的 Clear 阶段负责清理上一工具预览。
            using (var query = EntityManager.CreateEntityQuery(typeof(Block), typeof(Cell), typeof(Temp)))
            using (var blocks = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in blocks)
                {
                    var temp = EntityManager.GetComponentData<Temp>(entity);
                    if (temp.m_Original == Entity.Null || !EntityManager.HasBuffer<Cell>(temp.m_Original)) continue;
                    var before = EntityManager.GetBuffer<Cell>(temp.m_Original, true);
                    var preview = EntityManager.GetBuffer<Cell>(entity, true);
                    for (int i = 0; i < preview.Length && i < before.Length; i++)
                        if ((preview[i].m_State & CellFlags.Selected) != 0)
                            m_ZonePreview.Add(new ZonePreviewCell { Block = temp.m_Original, Index = i, Before = before[i], Preview = preview[i] });
                }
            }
            if (m_ZonePreview.Count > m_ZoneMaxCells)
            {
                applyMode = ApplyMode.Clear;
                CompletePending(BridgeResponse.Error(409, $"native zoning preview selects {m_ZonePreview.Count} cells, exceeding maxCells={m_ZoneMaxCells}; nothing applied; use marquee/polygon for mixed or reserved land"));
                return;
            }
            if (m_ZonePreviewOnly || m_ZonePreview.Count == 0)
            {
                applyMode = ApplyMode.Clear;
                CompletePending(ZoningResult(false));
                return;
            }
            if (!GetAllowApply())
            {
                applyMode = ApplyMode.Clear;
                CompletePending(BridgeResponse.Error(409, "native zoning validation rejected the preview; nothing applied"));
                return;
            }
            applyMode = ApplyMode.Apply;
            m_ZoneApplied = true;
            // 不在 Apply 阶段提前宣布完成；下一工具帧读取原生应用后的实际缓冲区。
        }

        private void CompleteZoning()
        {
            if (m_PendingRequest != null) CompletePending(ZoningResult(m_ZoneApplied));
        }

        private BridgeResponse ZoningResult(bool applied)
        {
            int plannedChanges = 0, changed = 0, mismatches = 0;
            var samples = new List<object>();
            foreach (var item in m_ZonePreview)
            {
                bool changesZone = (item.Before.m_State & CellFlags.Overridden) == 0 && !item.Before.m_Zone.Equals(item.Preview.m_Zone);
                if (changesZone) plannedChanges++;
                if (applied)
                {
                    if (!EntityManager.Exists(item.Block) || !EntityManager.HasBuffer<Cell>(item.Block)) { mismatches++; continue; }
                    var cells = EntityManager.GetBuffer<Cell>(item.Block, true);
                    if (item.Index >= cells.Length) { mismatches++; continue; }
                    if (!cells[item.Index].m_Zone.Equals(item.Before.m_Zone)) changed++;
                    if (!cells[item.Index].m_Zone.Equals(changesZone ? item.Preview.m_Zone : item.Before.m_Zone)) mismatches++;
                }
                if (samples.Count < 1000)
                    samples.Add(new { block = new { index = item.Block.Index, version = item.Block.Version }, cell = item.Index,
                        beforeZone = item.Before.m_Zone.m_Index, previewZone = item.Preview.m_Zone.m_Index });
            }
            return BridgeResponse.Json(new { applied, previewOnly = m_ZonePreviewOnly, mode = m_PendingZoning.m_Flags.ToString(),
                selectedCells = m_ZonePreview.Count, plannedChanges, changedCells = changed, verified = applied && mismatches == 0,
                mismatchedCells = mismatches, cells = samples, cellsComplete = samples.Count == m_ZonePreview.Count,
                note = "native GenerateZones/ApplyZones; flood fill follows native connected cells and can cross block entities; public reservations require marquee/polygon; changed cells do not prove buildings have grown; if verification fails inspect actual cells before retrying" });
        }
    }
}
