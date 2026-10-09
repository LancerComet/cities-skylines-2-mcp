using System;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class BridgeToolSystem
    {
        /// <summary>
        /// 同步复用ObjectToolSystem.FindAttachmentBuildingJob 的选择规则。
        /// 只为原生 PlaceholderBuildingData 选择同分区、一级且适合占地的所属建筑；
        /// 创建仍由完整 ObjectTool 定义、碰撞预览与原生应用执行，不直接创建企业或实时实体。
        /// </summary>
        private NativeReference<AttachmentData> CreateNativePlaceholderAttachment(RandomSeed seed)
        {
            if (m_ToolSystem.actionMode.IsEditor()
                || !EntityManager.HasComponent<PlaceholderBuildingData>(m_PendingPrefabEntity)) return default;

            var placeholder = EntityManager.GetComponentData<PlaceholderBuildingData>(m_PendingPrefabEntity);
            var zone = EntityManager.GetComponentData<ZoneData>(placeholder.m_ZonePrefab);
            var lot = EntityManager.GetComponentData<BuildingData>(m_PendingPrefabEntity);
            var random = seed.GetRandom(2000000);
            var access = new bool2((lot.m_Flags & BuildingFlags.LeftAccess) != 0,
                (lot.m_Flags & BuildingFlags.RightAccess) != 0);
            AttachmentData selected = default;
            BuildingData selectedBuilding = default;
            float best = 0f;

            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<BuildingData>(),
                ComponentType.ReadOnly<SpawnableBuildingData>(), ComponentType.ReadOnly<BuildingSpawnGroupData>(),
                ComponentType.ReadOnly<PrefabData>()))
            {
                query.SetSharedComponentFilter(new BuildingSpawnGroupData(zone.m_ZoneType));
                using (var entities = query.ToEntityArray(Allocator.Temp))
                {
                    for (int i = 0; i < entities.Length; i++)
                    {
                        var entity = entities[i];
                        if (EntityManager.GetComponentData<SpawnableBuildingData>(entity).m_Level != 1) continue;
                        var candidate = EntityManager.GetComponentData<BuildingData>(entity);
                        var size = candidate.m_LotSize;
                        if (!math.all(size <= lot.m_LotSize)) continue;
                        var candidateAccess = new bool2((candidate.m_Flags & BuildingFlags.LeftAccess) != 0,
                            (candidate.m_Flags & BuildingFlags.RightAccess) != 0);
                        var spare = math.select(lot.m_LotSize - size, 0, size == lot.m_LotSize - 1);
                        float score = (size.x * size.y) * random.NextFloat(1f, 1.05f);
                        score += (spare.x * size.y) * random.NextFloat(0.95f, 1f);
                        score += (lot.m_LotSize.x * spare.y) * random.NextFloat(0.55f, 0.6f);
                        score /= lot.m_LotSize.x * lot.m_LotSize.y;
                        score *= math.csum(math.select(0.01f, 0.5f, access == candidateAccess));
                        if (score <= best) continue;
                        best = score;
                        selected.m_Entity = entity;
                        selectedBuilding = candidate;
                    }
                }
            }

            if (selected.m_Entity == Entity.Null)
                throw new InvalidOperationException("原生专门产业没有适合的经营建筑，停止放置，避免生成无法运营的占位区域。");
            selected.m_Offset = new float3(0f, 0f, (lot.m_LotSize.y - selectedBuilding.m_LotSize.y) * 4f);
            var result = new NativeReference<AttachmentData>(Allocator.TempJob);
            result.Value = selected;
            return result;
        }
    }
}
