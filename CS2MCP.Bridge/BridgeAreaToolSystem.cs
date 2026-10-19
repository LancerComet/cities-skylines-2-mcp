using System;
using System.Collections.Generic;
using Game.Common;
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
    /// Headless tool that redraws an existing owned area (a specialized-industry
    /// extractor area), separate from BridgeToolSystem so the existing
    /// construction code stays untouched. Mirrors AreaToolSystem
    /// .CreateDefinitionsJob.Edit in game mode for an area that has an owner
    /// building: CreationDefinition with m_Prefab = the area prefab,
    /// m_Original = the area, m_Owner = the owner building and the Relocate
    /// flag, plus the full node polygon (elevation float.MinValue so nodes
    /// follow the terrain, as outside the editor). The game regenerates the
    /// area, validates it (ValidationHelpers.ValidateArea: shape, overlap,
    /// LotData.m_MaxRadius from the owner, city limits) and applies it.
    /// Runs for three tool frames: definitions, validate and apply, finish.
    /// </summary>
    public sealed partial class BridgeAreaToolSystem : ToolBaseSystem
    {
        private enum Stage
        {
            Idle,
            CreateDefinitions,
            Apply,
            Finish,
        }

        private Stage m_Stage = Stage.Idle;
        private Entity m_PendingArea;
        private Entity m_PendingOwner;
        private Entity m_PendingAreaPrefab;
        private PrefabBase m_PendingPrefab;
        private float3[] m_PendingNodes;
        private BridgeRequest m_PendingRequest;
        private ToolBaseSystem m_PreviousTool;
        private ToolOutputBarrier m_ToolOutputBarrier;
        private bool m_Applied;

        private EntityQuery m_IconQuery;

        public override string toolID => "CS2MCP.Area";

        public bool IsBusy => m_Stage != Stage.Idle;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolOutputBarrier = World.GetOrCreateSystemManaged<ToolOutputBarrier>();
            m_IconQuery = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<Game.Notifications.Icon>(),
                ComponentType.ReadOnly<Owner>(),
                ComponentType.ReadOnly<PrefabRef>());
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
        public bool TryQueueReshape(Entity area, Entity owner, Entity areaPrefab, PrefabBase prefab, float3[] nodes, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle)
            {
                return false;
            }
            m_PendingArea = area;
            m_PendingOwner = owner;
            m_PendingAreaPrefab = areaPrefab;
            m_PendingPrefab = prefab;
            m_PendingNodes = nodes;
            m_PendingRequest = request;
            m_Applied = false;
            m_PlaceMode = false;
            m_Stage = Stage.CreateDefinitions;
            m_PreviousTool = m_ToolSystem.activeTool;
            m_ToolSystem.activeTool = this;
            return true;
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
                        if (m_PlaceMode)
                        {
                            CreatePlacementDefinitions();
                        }
                        else
                        {
                            CreateReshapeDefinition();
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
                                (m_PlaceMode ? "placement" : "area reshape") + " blocked by game validation (" +
                                (errors.Count > 0 ? string.Join(", ", errors) : "invalid shape, overlap, too far from the building...") +
                                "); nothing was changed"));
                        }
                        m_Stage = Stage.Finish;
                        break;

                    case Stage.Finish:
                        applyMode = ApplyMode.None;
                        if (m_Applied && m_PlaceMode)
                        {
                            CompletePending(BuildPlacementResponse());
                        }
                        else if (m_Applied)
                        {
                            CompletePending(BridgeResponse.Json(new
                            {
                                reshaped = true,
                                area = new { index = m_PendingArea.Index, version = m_PendingArea.Version },
                                owner = new { index = m_PendingOwner.Index, version = m_PendingOwner.Version },
                                prefab = m_PendingPrefab != null ? m_PendingPrefab.name : null,
                                nodes = m_PendingNodes.Length,
                                note = "applied through the area tool pipeline; check /build/specialized-area/list for the new surface area",
                            }));
                        }
                        Deactivate();
                        break;

                    default:
                        applyMode = ApplyMode.None;
                        if (m_ToolSystem.activeTool == this)
                        {
                            m_ToolSystem.activeTool = m_DefaultToolSystem;
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"BridgeAreaToolSystem error in stage {m_Stage}: {e}");
                string detail = $"{e.GetType().Name}: {e.Message}";
                CompletePending(BridgeResponse.Error(500, m_Applied
                    ? $"the game applied the reshape, but finishing failed ({detail}); check /build/specialized-area/list"
                    : $"area reshape failed: {detail}"));
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
                CompletePending(BridgeResponse.Error(409,
                    "area reshape interrupted because another tool became active; check /build/specialized-area/list before retrying"));
                m_Stage = Stage.Idle;
                m_PendingPrefab = null;
                m_PreviousTool = null;
            }
            base.OnStopRunning();
        }

        /// <summary>AreaToolSystem.CreateDefinitionsJob.Edit, game mode, existing owned area.</summary>
        private void CreateReshapeDefinition()
        {
            EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            Entity entity = commandBuffer.CreateEntity();
            commandBuffer.AddComponent(entity, new CreationDefinition
            {
                m_Prefab = m_PendingAreaPrefab,
                m_Original = m_PendingArea,
                m_Owner = m_PendingOwner,
                m_Flags = CreationFlags.Relocate,
            });
            commandBuffer.AddComponent(entity, default(Updated));
            DynamicBuffer<Game.Areas.Node> nodes = commandBuffer.AddBuffer<Game.Areas.Node>(entity);
            foreach (float3 position in m_PendingNodes)
            {
                nodes.Add(new Game.Areas.Node(position, float.MinValue));
            }
        }

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
            m_PlaceMode = false;
            m_PendingRequest = null;
            m_PendingPrefab = null;
            m_PendingNodes = null;
            m_Applied = false;
            if (m_ToolSystem.activeTool == this)
            {
                m_ToolSystem.activeTool = m_PreviousTool != null && m_PreviousTool != this ? m_PreviousTool : m_DefaultToolSystem;
            }
            m_PreviousTool = null;
        }
    }
}
