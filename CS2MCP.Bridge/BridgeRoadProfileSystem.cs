using System;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// CourseSplit完成后、GenerateNodes及正常Validation之前，仅恢复本次请求明示的纵断面。
    /// 与正常等坡工具相同修改NetCourse，不碰Owner、错误、资产或现场永久路网。
    /// 随机种子范围和完整平面子曲线双重定位；无法对应时整次拒绝，而不是猜测。
    /// </summary>
    public sealed class BridgeRoadProfileSystem : GameSystemBase
    {
        internal const int SeedStride = 4096;
        private EntityQuery m_Courses;
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Courses = GetEntityQuery(ComponentType.ReadWrite<NetCourse>(), ComponentType.ReadOnly<CreationDefinition>(),
                ComponentType.ReadOnly<Updated>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Overridden>());
        }
        protected override void OnUpdate()
        {
            var tool = World.GetExistingSystemManaged<BridgeToolSystem>();
            if (tool == null || !tool.RoadProfilePending) return;
            // ToEntityArray及SetComponentData同步关联原生jobs，不在运行中的job内改组件。
            using (var entities = m_Courses.ToEntityArray(Allocator.Temp)) tool.RestoreRoadProfiles(entities);
        }
    }

    public sealed partial class BridgeToolSystem
    {
        private int? m_RoadProfileSeedBase;
        private Bezier4x3[] m_RoadProfileWorld;
        private bool m_RoadProfileProcessed;
        private string m_RoadProfileError;
        private int m_RoadProfileRestored;
        internal bool RoadProfilePending => m_PendingKind == OperationKind.RoadLayout && m_Stage == Stage.Apply
            && m_RoadProfileSeedBase.HasValue && !m_RoadProfileProcessed;

        private void BeginRoadProfile(RoadLayoutPart[] parts, float3 origin, quaternion rotation, bool preserve)
        {
            ClearRoadProfile();
            if (!preserve) return;
            var random = Game.Common.RandomSeed.Next().GetRandom(0);
            m_RoadProfileSeedBase = random.NextInt(10000000, 1000000000);
            m_RoadProfileWorld = new Bezier4x3[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                m_RoadProfileWorld[i] = Game.Objects.ObjectUtils.LocalToWorld(origin, rotation, parts[i].Curve);
        }
        private void ClearRoadProfile()
        {
            m_RoadProfileSeedBase = null;
            m_RoadProfileWorld = null;
            m_RoadProfileProcessed = false;
            m_RoadProfileError = null;
            m_RoadProfileRestored = 0;
        }
        private bool RoadProfileAllowed => !m_RoadProfileSeedBase.HasValue || (m_RoadProfileProcessed && m_RoadProfileError == null && m_RoadProfileRestored > 0);
        private object RoadProfileAudit() => !m_RoadProfileSeedBase.HasValue ? null : new {
            requested = true, processed = m_RoadProfileProcessed, restoredCourses = m_RoadProfileRestored,
            allowed = RoadProfileAllowed, error = m_RoadProfileError,
            scope = "current request seed range + matching prefab + exact XZ subcurve; normal Validation/cost retained"
        };

        internal void RestoreRoadProfiles(NativeArray<Entity> entities)
        {
            m_RoadProfileProcessed = true;
            var terrain = m_TerrainSystem.GetHeightData();
            var seen = new bool[m_RoadLayout.Length];
            try {
                foreach (var entity in entities) {
                    var definition = EntityManager.GetComponentData<CreationDefinition>(entity);
                    long offset = (long)definition.m_RandomSeed - m_RoadProfileSeedBase.Value;
                    if (offset < 0 || offset >= (long)m_RoadLayout.Length * BridgeRoadProfileSystem.SeedStride) continue;
                    if ((definition.m_Flags & CreationFlags.Delete) != 0) continue;
                    int part = (int)(offset / BridgeRoadProfileSystem.SeedStride);
                    if (definition.m_Prefab != m_RoadLayout[part].Prefab || definition.m_Owner != Entity.Null
                        || EntityManager.HasComponent<OwnerDefinition>(entity))
                        throw new InvalidOperationException("纵断面范围出现不匹配预制体或Owner");
                    var course = EntityManager.GetComponentData<NetCourse>(entity);
                    var planned = m_RoadProfileWorld[part];
                    if (MathUtils.Distance(planned.xz, course.m_Curve.a.xz, out float t0) > 0.05f
                        || MathUtils.Distance(planned.xz, course.m_Curve.d.xz, out float t1) > 0.05f || t1 <= t0)
                        throw new InvalidOperationException($"第{part}段原生子曲线端点无法唯一对应纵断面");
                    var cut = MathUtils.Cut(planned, new float2(t0, t1));
                    for (int sample = 0; sample <= 8; sample++) {
                        float t = sample / 8f;
                        if (math.distance(MathUtils.Position(cut, t).xz, MathUtils.Position(course.m_Curve, t).xz) > 0.05f)
                            throw new InvalidOperationException($"第{part}段平面几何已改变，拒绝套用纵断面");
                    }
                    CheckProfileExistingNode(course.m_StartPosition, cut.a.y);
                    CheckProfileExistingNode(course.m_EndPosition, cut.d.y);
                    // 只恢复y；平面、拓扑、拆分结果、原生错误与安全标记保持原样。
                    course.m_Curve.y = cut.y;
                    var limits = EntityManager.GetComponentData<PlaceableNetData>(definition.m_Prefab).m_ElevationRange;
                    if (!math.isfinite(limits.min) || !math.isfinite(limits.max) || limits.min > limits.max)
                        throw new InvalidOperationException("纵断面缺少有效原生高程范围");
                    float polygonLength = math.distance(cut.a.xz, cut.b.xz) + math.distance(cut.b.xz, cut.c.xz) + math.distance(cut.c.xz, cut.d.xz);
                    int samples = math.max(1, (int)math.ceil(polygonLength * 3f / 4f));
                    for (int i = 0; i <= samples; i++) {
                        var point = MathUtils.Position(course.m_Curve, (float)i / samples);
                        float elevation = point.y - TerrainUtils.SampleHeight(ref terrain, point);
                        if (!math.all(math.isfinite(point)) || !math.isfinite(elevation) || elevation < limits.min || elevation > limits.max)
                            throw new InvalidOperationException($"第{part}段生成后纵断面超出原生高程范围");
                    }
                    course.m_StartPosition.m_Position.y = cut.a.y;
                    course.m_EndPosition.m_Position.y = cut.d.y;
                    course.m_StartPosition.m_Elevation = cut.a.y - TerrainUtils.SampleHeight(ref terrain, course.m_StartPosition.m_Position);
                    course.m_EndPosition.m_Elevation = cut.d.y - TerrainUtils.SampleHeight(ref terrain, course.m_EndPosition.m_Position);
                    course.m_Elevation = (course.m_StartPosition.m_Elevation + course.m_EndPosition.m_Elevation) * 0.5f;
                    course.m_Length = MathUtils.Length(course.m_Curve);
                    EntityManager.SetComponentData(entity, course);
                    m_RoadProfileRestored++;
                    seen[part] = true;
                }
                if (m_RoadProfileRestored == 0) throw new InvalidOperationException("未找到本次道路定义，拒绝空范围通过");
                for (int i = 0; i < seen.Length; i++) if (!seen[i])
                    throw new InvalidOperationException($"第{i}段未找到本次原生定义，拒绝部分恢复通过");
            } catch (Exception error) {
                m_RoadProfileError = error.Message;
                Mod.Log.Warn("Road profile preview rejected: " + error.Message);
            }
        }
        private void CheckProfileExistingNode(CoursePos position, float height)
        {
            if (position.m_Entity != Entity.Null && EntityManager.HasComponent<Node>(position.m_Entity)
                && math.abs(EntityManager.GetComponentData<Node>(position.m_Entity).m_Position.y - height) > 0.1f)
                throw new InvalidOperationException("纵断面与既有原生节点高度不符，须重新设计连接");
        }
    }
}
