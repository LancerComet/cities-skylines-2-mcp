using System;
using System.Collections.Generic;
using System.Linq;
using Game.Common;
using Game.Creatures;
using Game.Objects;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        /// <summary>读取实际行人的当前位置与当前导航目标，供天桥和路口运营验收；不写导航或路径。</summary>
        private BridgeResponse GetPedestrians(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            bool hasX = request.TryGetFloat("x", out float x);
            bool hasZ = request.TryGetFloat("z", out float z);
            if (hasX != hasZ) return BridgeResponse.Error(400, "provide both x and z for an area");
            float radius = request.TryGetFloat("radius", out float r) ? math.clamp(r, 1f, 2000f) : 300f;
            int limit = request.TryGetInt("limit", out int n) ? math.clamp(n, 1, 100) : 20;
            var samples = new List<object>();
            var currentTargets = new Dictionary<Entity, int>();
            int total, inArea = 0, withoutTarget = 0;
            using (var query = EntityManager.CreateEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Human>(), ComponentType.ReadOnly<HumanCurrentLane>(), ComponentType.ReadOnly<Transform>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() }
            }))
            using (var humans = query.ToEntityArray(Allocator.Temp))
            {
                total = humans.Length;
                foreach (Entity human in humans)
                {
                    float3 pos = EntityManager.GetComponentData<Transform>(human).m_Position;
                    if (hasX && math.distancesq(pos.xz, new float2(x, z)) > radius * radius) continue;
                    inArea++;
                    HumanCurrentLane lane = EntityManager.GetComponentData<HumanCurrentLane>(human);
                    Entity target = lane.m_Lane;
                    var owners = new List<object>();
                    var seen = new HashSet<Entity>();
                    for (int depth = 0; depth < 6 && target != Entity.Null && EntityManager.Exists(target) && seen.Add(target); depth++)
                    {
                        owners.Add(new { entity = Ref(target), prefab = PrefabNameOf(target) });
                        if (EntityManager.HasComponent<Game.Buildings.Building>(target) || !EntityManager.HasComponent<Owner>(target)) break;
                        target = EntityManager.GetComponentData<Owner>(target).m_Owner;
                    }
                    if (lane.m_Lane == Entity.Null) withoutTarget++;
                    else { currentTargets.TryGetValue(target, out int count); currentTargets[target] = count + 1; }
                    if (samples.Count < limit) samples.Add(new {
                        entity = Ref(human), position = new { x = pos.x, y = pos.y, z = pos.z },
                        currentTarget = Ref(lane.m_Lane), currentTargetPrefab = PrefabNameOf(lane.m_Lane),
                        laneFlags = lane.m_Flags.ToString(), queueEntity = Ref(lane.m_QueueEntity), owners
                    });
                }
            }
            return BridgeResponse.Json(new {
                capturedAtUtc = DateTime.UtcNow, humansWithCurrentLane = total, inArea, withoutTarget,
                area = hasX ? new { x, z, radius } : null,
                currentTargets = currentTargets.OrderByDescending(p => p.Value).Take(limit).Select(p => new { entity = Ref(p.Key), prefab = PrefabNameOf(p.Key), count = p.Value }).ToArray(),
                samples, samplesComplete = samples.Count == inArea,
                note = "read-only native HumanCurrentLane/Transform; current target can be a lane, surface or building; counts are current occupants, not capacity, completed trips or future path reachability"
            });
        }
    }
}
