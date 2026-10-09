using System;
using System.Collections.Generic;
using Game.Policies;
using Game.Prefabs;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private bool HasNativePaidParking(Entity building)
        {
            if (!EntityManager.HasComponent<Game.Buildings.Building>(building) || !EntityManager.HasBuffer<Policy>(building)
                || !EntityManager.HasComponent<PrefabRef>(building)) return false;
            var prefab = EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
            if (!EntityManager.HasComponent<BuildingData>(prefab)) return false;
            var flags = EntityManager.GetComponentData<BuildingData>(prefab).m_Flags;
            if ((flags & (Game.Prefabs.BuildingFlags.RestrictedPedestrian | Game.Prefabs.BuildingFlags.RestrictedCar)) != 0) return false;
            // 与原生 PoliciesUISystem 的停车政策筛选一致，只检查建筑、自有子物体及子网络，不扫描全图。
            var visited = new HashSet<Entity>();
            return HasParkingInOwnedObject(building, visited, 0);
        }

        private bool HasParkingInOwnedObject(Entity entity, HashSet<Entity> visited, int depth)
        {
            if (!EntityManager.Exists(entity) || depth > 2 || !visited.Add(entity)) return false;
            if (EntityManager.HasBuffer<Game.Net.SubLane>(entity)) foreach (var item in EntityManager.GetBuffer<Game.Net.SubLane>(entity, true)) {
                var lane = item.m_SubLane;
                if (EntityManager.HasComponent<Game.Net.ParkingLane>(lane)) {
                    if ((EntityManager.GetComponentData<Game.Net.ParkingLane>(lane).m_Flags & Game.Net.ParkingLaneFlags.VirtualLane) == 0) return true;
                } else if (EntityManager.HasComponent<Game.Net.ConnectionLane>(lane)
                    && (EntityManager.GetComponentData<Game.Net.ConnectionLane>(lane).m_Flags & Game.Net.ConnectionLaneFlags.Parking) != 0) return true;
            }
            if (depth == 0 && EntityManager.HasBuffer<Game.Net.SubNet>(entity)) foreach (var item in EntityManager.GetBuffer<Game.Net.SubNet>(entity, true))
                if (HasParkingInOwnedObject(item.m_SubNet, visited, 1)) return true;
            if (EntityManager.HasBuffer<Game.Objects.SubObject>(entity)) foreach (var item in EntityManager.GetBuffer<Game.Objects.SubObject>(entity, true))
                if (HasParkingInOwnedObject(item.m_SubObject, visited, depth + 1)) return true;
            return false;
        }

        private Entity PaidParkingPolicy()
        {
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PolicyData>(), ComponentType.ReadOnly<BuildingOptionData>()))
            using (var entities = query.ToEntityArray(Allocator.Temp)) foreach (var entity in entities) {
                if (Game.Buildings.BuildingUtils.HasOption(EntityManager.GetComponentData<BuildingOptionData>(entity), Game.Buildings.BuildingOption.PaidParking)) return entity;
            }
            return Entity.Null;
        }

        private object FacilityParkingFee(Entity building, Entity policy)
        {
            bool active = false;
            var slider = EntityManager.GetComponentData<PolicySliderData>(policy);
            float adjustment = slider.m_Default;
            foreach (var item in EntityManager.GetBuffer<Policy>(building, true)) if (item.m_Policy == policy) {
                active = (item.m_Flags & PolicyFlags.Active) != 0; adjustment = item.m_Adjustment; break;
            }
            return new { entity = Ref(building), name = LabelOf(World.GetOrCreateSystemManaged<Game.UI.NameSystem>(), building),
                prefab = NativeName(EntityManager.GetComponentData<PrefabRef>(building).m_Prefab), active, adjustment,
                effectiveFee = active ? adjustment : 0, slider = PolicySlider(policy), locked = IsLocked(policy) };
        }

        private BridgeResponse GetFacilityParkingFees()
        {
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            var policy = PaidParkingPolicy();
            if (policy == Entity.Null || !EntityManager.HasComponent<PolicySliderData>(policy)) return BridgeResponse.Error(409, "native paid parking slider is unavailable");
            var rows = new List<object>();
            using (var query = EntityManager.CreateEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Policy>() },
                Any = new[] { ComponentType.ReadOnly<Game.Buildings.ParkingFacility>(), ComponentType.ReadOnly<Game.City.CityServiceUpkeep>() },
                None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Game.Common.Owner>() }
            }))
            using (var entities = query.ToEntityArray(Allocator.Temp)) foreach (var entity in entities)
                if (HasNativePaidParking(entity)) rows.Add(FacilityParkingFee(entity, policy));
            return BridgeResponse.Json(new { policy = NativeName(policy), facilities = rows,
                note = "native building PaidParking policy; includes fee-capable service car parks; road-built parking uses district policy instead" });
        }

        private BridgeResponse SetFacilityParkingFee(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error) || !ReadExistingEntity(request, out Entity building, out error)) return error;
            if (!HasNativePaidParking(building)) return BridgeResponse.Error(409, "this building has no parking fee option in the native panel");
            var policy = PaidParkingPolicy();
            if (policy == Entity.Null || !EntityManager.HasComponent<PolicySliderData>(policy)) return BridgeResponse.Error(409, "native parking slider is unavailable");
            if (IsLocked(policy)) return BridgeResponse.Error(409, "native parking fee policy is locked");
            var data = EntityManager.GetComponentData<PolicySliderData>(policy);
            if (!request.TryGetFloat("fee", out float fee) || float.IsNaN(fee) || float.IsInfinity(fee) || fee < data.m_Range.min || fee > data.m_Range.max)
                return BridgeResponse.Error(400, $"fee must be within native range {data.m_Range.min}..{data.m_Range.max}");
            var before = FacilityParkingFee(building, policy);
            World.GetOrCreateSystemManaged<PoliciesUISystem>().SetPolicy(building, policy, true, fee);
            return BridgeResponse.Json(new { building = Ref(building), requestedFee = fee, before, note = "native policy queued; read facility-parking after end of frame to verify" });
        }
    }
}
