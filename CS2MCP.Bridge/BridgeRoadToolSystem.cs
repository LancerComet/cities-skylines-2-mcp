using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace CS2MCP
{
    /// <summary>
    /// Headless tool that changes existing road segments in place, separate from
    /// BridgeToolSystem so the existing construction code stays untouched.
    /// Replace mirrors NetToolSystem.CreateDefinitionsJob.CreateReplacement (the
    /// road tool's Replace mode): one CreationDefinition per segment with the
    /// segment as m_Original and the new prefab, flags Align | SubElevation (plus
    /// Invert to flip the drawing direction), and a NetCourse along the segment
    /// between its nodes. The game regenerates the segment, its lanes and zone
    /// blocks, validates the result and applies it, as for a player's replace.
    /// Build mirrors CreateDefinitionsJob.CreateStraightLine with snapped control
    /// points: each course end names the existing node it joins, or an existing
    /// segment plus the split position (GetCoursePos), so the new piece is
    /// connected like one drawn with the road tool's snapping.
    /// Runs for three tool frames: definitions, validate and apply, finish.
    /// </summary>
    public sealed partial class BridgeRoadToolSystem : ObjectToolBaseSystem
    {
        /// <summary>One end of a built segment: an existing node, a split point on an existing segment, or a free point.</summary>
        public struct BuildEnd
        {
            public Entity Entity;
            public float Split;
            public float3 Position;
            public float Elevation;
        }

        private enum Stage
        {
            Idle,
            CreateDefinitions,
            Apply,
            Finish,
        }

        private enum Mode
        {
            Replace,
            Build,
        }

        private Stage m_Stage = Stage.Idle;
        private Mode m_Mode;
        private BuildEnd m_BuildStart;
        private BuildEnd m_BuildEnd;
        private Bezier4x3 m_BuildCurve;
        private Entity[] m_PendingEdges;
        private float3[] m_PendingMidpoints;
        private bool m_PendingInvert;
        private Entity m_PendingPrefabEntity;
        private PrefabBase m_PendingPrefab;
        private BridgeRequest m_PendingRequest;
        private ToolBaseSystem m_PreviousTool;
        private bool m_Applied;

        private EntityQuery m_IconQuery;
        private EntityQuery m_EdgeQuery;

        public override string toolID => "CS2MCP.Road";

        public bool IsBusy => m_Stage != Stage.Idle;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_IconQuery = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<Game.Notifications.Icon>(),
                ComponentType.ReadOnly<Owner>(),
                ComponentType.ReadOnly<PrefabRef>());
            m_EdgeQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
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
        public bool TryQueueReplace(Entity[] edges, Entity prefabEntity, PrefabBase prefab, bool invert, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle)
            {
                return false;
            }
            m_PendingEdges = edges;
            m_PendingMidpoints = new float3[edges.Length];
            for (int i = 0; i < edges.Length; i++)
            {
                m_PendingMidpoints[i] = MathUtils.Position(EntityManager.GetComponentData<Curve>(edges[i]).m_Bezier, 0.5f);
            }
            m_PendingPrefabEntity = prefabEntity;
            m_PendingPrefab = prefab;
            m_PendingInvert = invert;
            m_PendingRequest = request;
            m_Applied = false;
            m_Mode = Mode.Replace;
            m_Stage = Stage.CreateDefinitions;
            m_PreviousTool = m_ToolSystem.activeTool;
            m_ToolSystem.activeTool = this;
            return true;
        }

        /// <summary>Must be called on the simulation thread. The curve must start at start.Position and end at end.Position.</summary>
        public bool TryQueueBuild(Entity prefabEntity, PrefabBase prefab, BuildEnd start, BuildEnd end, Bezier4x3 curve, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle)
            {
                return false;
            }
            m_BuildStart = start;
            m_BuildEnd = end;
            m_BuildCurve = curve;
            m_PendingEdges = null;
            m_PendingMidpoints = null;
            m_PendingPrefabEntity = prefabEntity;
            m_PendingPrefab = prefab;
            m_PendingInvert = false;
            m_PendingRequest = request;
            m_Applied = false;
            m_Mode = Mode.Build;
            m_Stage = Stage.CreateDefinitions;
            m_PreviousTool = m_ToolSystem.activeTool;
            m_ToolSystem.activeTool = this;
            return true;
        }

        private string OperationName => m_Mode == Mode.Build ? "building the connection" : "road replacement";

        [Preserve]
        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            try
            {
                switch (m_Stage)
                {
                    case Stage.CreateDefinitions:
                        applyMode = ApplyMode.Clear;
                        if (m_Mode == Mode.Build)
                        {
                            CreateBuildDefinitions();
                        }
                        else
                        {
                            CreateReplaceDefinitions();
                        }
                        m_Stage = Stage.Apply;
                        break;

                    case Stage.Apply:
                        if (GetAllowApply())
                        {
                            applyMode = ApplyMode.Apply;
                            m_Applied = true;
                        }
                        else
                        {
                            List<string> errors = CollectTempErrors();
                            applyMode = ApplyMode.Clear;
                            CompletePending(BridgeResponse.Error(409,
                                OperationName + " blocked by game validation (" +
                                (errors.Count > 0 ? string.Join(", ", errors) : "overlap, unsupported road type, protected segment...") +
                                "); nothing was changed"));
                        }
                        m_Stage = Stage.Finish;
                        break;

                    case Stage.Finish:
                        applyMode = ApplyMode.None;
                        if (m_Applied)
                        {
                            CompletePending(m_Mode == Mode.Build ? BuildConnectResponse() : BuildReplaceResponse());
                        }
                        Deactivate();
                        break;

                    default:
                        applyMode = ApplyMode.None;
                        // Idle but active (e.g. another bridge tool restored us as its
                        // "previous tool"): hand control back to the default tool.
                        if (m_ToolSystem.activeTool == this)
                        {
                            m_ToolSystem.activeTool = m_DefaultToolSystem;
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"BridgeRoadToolSystem error in stage {m_Stage}: {e}");
                string detail = $"{e.GetType().Name}: {e.Message}";
                CompletePending(BridgeResponse.Error(500, m_Applied
                    ? $"the game applied the change, but finishing {OperationName} failed ({detail}); check cs2_list_roads"
                    : $"{OperationName} failed: {detail}"));
                applyMode = ApplyMode.Clear;
                Deactivate();
            }
            return inputDeps;
        }

        [Preserve]
        protected override void OnStopRunning()
        {
            if (m_Stage != Stage.Idle && m_ToolSystem.activeTool != this)
            {
                // Another tool took over mid-operation; fail fast instead of
                // letting the HTTP call time out.
                CompletePending(BridgeResponse.Error(409,
                    OperationName + " interrupted because another tool became active; check cs2_list_roads before retrying"));
                m_Stage = Stage.Idle;
                m_PendingEdges = null;
                m_PendingPrefab = null;
                m_PendingPrefabEntity = Entity.Null;
                m_PreviousTool = null;
            }
            base.OnStopRunning();
        }

        /// <summary>Mirrors NetToolSystem.CreateDefinitionsJob.CreateReplacement for whole segments.</summary>
        private void CreateReplaceDefinitions()
        {
            EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            foreach (Entity edge in m_PendingEdges)
            {
                Edge nodes = EntityManager.GetComponentData<Edge>(edge);
                Curve curve = EntityManager.GetComponentData<Curve>(edge);
                Entity startNode = nodes.m_Start;
                Entity endNode = nodes.m_End;
                var definition = new CreationDefinition
                {
                    m_Original = edge,
                    m_Prefab = m_PendingPrefabEntity,
                    m_Flags = CreationFlags.Align | CreationFlags.SubElevation,
                };
                if (m_PendingInvert)
                {
                    curve.m_Bezier = MathUtils.Invert(curve.m_Bezier);
                    definition.m_Flags |= CreationFlags.Invert;
                    startNode = nodes.m_End;
                    endNode = nodes.m_Start;
                }

                NetCourse course = default;
                course.m_Curve = curve.m_Bezier;
                course.m_Length = curve.m_Length;
                course.m_FixedIndex = EntityManager.HasComponent<Fixed>(edge)
                    ? EntityManager.GetComponentData<Fixed>(edge).m_Index
                    : -1;
                course.m_StartPosition.m_Entity = startNode;
                course.m_StartPosition.m_Position = curve.m_Bezier.a;
                course.m_StartPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(curve.m_Bezier));
                course.m_StartPosition.m_CourseDelta = 0f;
                course.m_StartPosition.m_Flags = CoursePosFlags.IsFirst;
                course.m_EndPosition.m_Entity = endNode;
                course.m_EndPosition.m_Position = curve.m_Bezier.d;
                course.m_EndPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(curve.m_Bezier));
                course.m_EndPosition.m_CourseDelta = 1f;
                course.m_EndPosition.m_Flags = CoursePosFlags.IsLast;

                Entity entity = commandBuffer.CreateEntity();
                commandBuffer.AddComponent(entity, definition);
                commandBuffer.AddComponent(entity, default(Updated));
                commandBuffer.AddComponent(entity, course);
            }
        }

        /// <summary>
        /// Mirrors NetToolSystem.CreateDefinitionsJob.CreateStraightLine: one
        /// course whose ends reference the snapped node or segment (GetCoursePos:
        /// m_Entity = node, or segment + m_SplitPosition to split it there).
        /// </summary>
        private void CreateBuildDefinitions()
        {
            NetCourse course = default;
            course.m_Curve = m_BuildCurve;
            course.m_Length = MathUtils.Length(m_BuildCurve);
            course.m_FixedIndex = -1;
            course.m_Elevation = new float2(math.min(m_BuildStart.Elevation, m_BuildEnd.Elevation),
                math.max(m_BuildStart.Elevation, m_BuildEnd.Elevation));
            course.m_StartPosition = ToCoursePos(m_BuildStart, 0f);
            course.m_StartPosition.m_Flags = CoursePosFlags.IsFirst | CoursePosFlags.IsLeft | CoursePosFlags.IsRight;
            course.m_EndPosition = ToCoursePos(m_BuildEnd, 1f);
            course.m_EndPosition.m_Flags = CoursePosFlags.IsLast | CoursePosFlags.IsLeft | CoursePosFlags.IsRight;
            // Raised or lowered free ends keep their height (as cs2_build_road's elevated ends do).
            if (m_BuildStart.Entity == Entity.Null && m_BuildStart.Elevation != 0f)
            {
                course.m_StartPosition.m_Flags |= CoursePosFlags.FreeHeight;
            }
            if (m_BuildEnd.Entity == Entity.Null && m_BuildEnd.Elevation != 0f)
            {
                course.m_EndPosition.m_Flags |= CoursePosFlags.FreeHeight;
            }

            Unity.Mathematics.Random random = RandomSeed.Next().GetRandom(0);
            EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            Entity entity = commandBuffer.CreateEntity();
            commandBuffer.AddComponent(entity, new CreationDefinition
            {
                m_Prefab = m_PendingPrefabEntity,
                m_RandomSeed = random.NextInt(),
                m_Flags = CreationFlags.SubElevation,
            });
            commandBuffer.AddComponent(entity, default(Updated));
            commandBuffer.AddComponent(entity, course);
        }

        private CoursePos ToCoursePos(BuildEnd end, float courseDelta)
        {
            return new CoursePos
            {
                m_Entity = end.Entity,
                m_SplitPosition = end.Split,
                m_Position = end.Position,
                m_Elevation = end.Elevation,
                m_Rotation = NetUtils.GetNodeRotation(MathUtils.Tangent(m_BuildCurve, courseDelta)),
                m_CourseDelta = courseDelta,
                m_ParentMesh = -1,
            };
        }

        private BridgeResponse BuildConnectResponse()
        {
            // Segments of the new prefab lying along the course (the game may
            // split it where it crosses other networks).
            var segments = new List<object>();
            var nodes = new List<Entity>();
            Bezier4x2 course = m_BuildCurve.xz;
            using (NativeArray<Entity> edges = m_EdgeQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Curve> curves = m_EdgeQuery.ToComponentDataArray<Curve>(Allocator.Temp))
            using (NativeArray<PrefabRef> prefabs = m_EdgeQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            {
                for (int i = 0; i < edges.Length; i++)
                {
                    if (prefabs[i].m_Prefab != m_PendingPrefabEntity)
                    {
                        continue;
                    }
                    Bezier4x3 bezier = curves[i].m_Bezier;
                    if (MathUtils.Distance(course, bezier.a.xz, out _) > 2f || MathUtils.Distance(course, bezier.d.xz, out _) > 2f
                        || MathUtils.Distance(course, MathUtils.Position(bezier, 0.5f).xz, out _) > 2f)
                    {
                        continue;
                    }
                    Edge ends = EntityManager.GetComponentData<Edge>(edges[i]);
                    nodes.Add(ends.m_Start);
                    nodes.Add(ends.m_End);
                    segments.Add(new
                    {
                        road = new { index = edges[i].Index, version = edges[i].Version },
                        startNode = ends.m_Start.Index,
                        endNode = ends.m_End.Index,
                        start = new { x = bezier.a.x, y = bezier.a.y, z = bezier.a.z },
                        end = new { x = bezier.d.x, y = bezier.d.y, z = bezier.d.z },
                        length = curves[i].m_Length,
                    });
                }
            }
            return BridgeResponse.Json(new
            {
                built = true,
                prefab = m_PendingPrefab != null ? m_PendingPrefab.name : null,
                segments,
                start = DescribeBuiltEnd(m_BuildStart, nodes),
                end = DescribeBuiltEnd(m_BuildEnd, nodes),
                note = "built through the game's road tool pipeline with snapped ends (validated by the game); " +
                       "segmentsAtJunction > 1 means the end is joined to the existing network.",
            });
        }

        private object DescribeBuiltEnd(BuildEnd end, List<Entity> nodes)
        {
            // The node now at this end (existing, or created by the split).
            Entity node = Entity.Null;
            float best = 1f;
            foreach (Entity candidate in nodes)
            {
                if (!EntityManager.Exists(candidate) || !EntityManager.HasComponent<Node>(candidate))
                {
                    continue;
                }
                float distance = math.distance(EntityManager.GetComponentData<Node>(candidate).m_Position.xz, end.Position.xz);
                if (distance < best)
                {
                    best = distance;
                    node = candidate;
                }
            }
            int connected = node != Entity.Null && EntityManager.HasBuffer<ConnectedEdge>(node)
                ? EntityManager.GetBuffer<ConnectedEdge>(node, isReadOnly: true).Length
                : 0;
            string snappedTo = end.Entity == Entity.Null ? "free point"
                : EntityManager.HasComponent<Edge>(end.Entity) ? "segment (split)" : "junction";
            return new
            {
                snappedTo,
                position = new { x = end.Position.x, y = end.Position.y, z = end.Position.z },
                junction = node != Entity.Null ? new { index = node.Index, version = node.Version } : null,
                segmentsAtJunction = connected,
            };
        }

        private BridgeResponse BuildReplaceResponse()
        {
            // The game recreates replaced segments, so find the new ones by position.
            var replaced = new List<object>();
            using (NativeArray<Entity> edges = m_EdgeQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Curve> curves = m_EdgeQuery.ToComponentDataArray<Curve>(Allocator.Temp))
            using (NativeArray<PrefabRef> prefabs = m_EdgeQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            {
                foreach (float3 midpoint in m_PendingMidpoints)
                {
                    Entity found = Entity.Null;
                    float best = 3f;
                    for (int i = 0; i < edges.Length; i++)
                    {
                        if (prefabs[i].m_Prefab != m_PendingPrefabEntity)
                        {
                            continue;
                        }
                        Bezier4x3 bezier = curves[i].m_Bezier;
                        float2 min = math.min(math.min(bezier.a.xz, bezier.b.xz), math.min(bezier.c.xz, bezier.d.xz));
                        float2 max = math.max(math.max(bezier.a.xz, bezier.b.xz), math.max(bezier.c.xz, bezier.d.xz));
                        if (math.any(midpoint.xz < min - best) || math.any(midpoint.xz > max + best))
                        {
                            continue;
                        }
                        float distance = MathUtils.Distance(bezier.xz, midpoint.xz, out _);
                        if (distance < best)
                        {
                            best = distance;
                            found = edges[i];
                        }
                    }
                    replaced.Add(new
                    {
                        position = new { x = midpoint.x, z = midpoint.z },
                        road = found != Entity.Null ? new { index = found.Index, version = found.Version } : null,
                    });
                }
            }
            return BridgeResponse.Json(new
            {
                replaced = m_PendingEdges.Length,
                prefab = m_PendingPrefab != null ? m_PendingPrefab.name : null,
                inverted = m_PendingInvert,
                roads = replaced,
                note = "replaced through the game's road tool pipeline (Replace mode: segments, lanes and zone blocks are " +
                       "regenerated and validated by the game). The game updates segments in place, so they normally " +
                       "keep their ids; the replaced segments are listed by position.",
            });
        }

        /// <summary>
        /// Names of the validation errors the game raised on temp entities
        /// (it shows them as error icons whose prefab carries ToolErrorData).
        /// </summary>
        private List<string> CollectTempErrors()
        {
            var names = new List<string>();
            using (NativeArray<Entity> icons = m_IconQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < icons.Length; i++)
                {
                    Entity owner = EntityManager.GetComponentData<Owner>(icons[i]).m_Owner;
                    if (!EntityManager.Exists(owner) || !EntityManager.HasComponent<Temp>(owner))
                    {
                        continue;
                    }
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(icons[i]).m_Prefab;
                    if (!EntityManager.HasComponent<ToolErrorData>(prefab))
                    {
                        continue;
                    }
                    string name = EntityManager.GetComponentData<ToolErrorData>(prefab).m_Error.ToString();
                    if (!names.Contains(name))
                    {
                        names.Add(name);
                    }
                }
            }
            return names;
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
            m_PendingEdges = null;
            m_PendingPrefab = null;
            m_PendingPrefabEntity = Entity.Null;
            m_Applied = false;
            if (m_ToolSystem.activeTool == this)
            {
                m_ToolSystem.activeTool = m_PreviousTool != null && m_PreviousTool != this ? m_PreviousTool : m_DefaultToolSystem;
            }
            m_PreviousTool = null;
        }
    }
}
