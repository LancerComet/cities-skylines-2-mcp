using Game;
using Game.City;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using static Game.Tools.ObjectToolBaseSystem;
using AgeMask = Game.Tools.AgeMask;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    /// <summary>
    /// Specialized-industry placement, mirroring ObjectToolSystem in game mode
    /// for a placeholder building (PlaceholderBuildingData): UpdateDefinitions
    /// runs FindAttachmentBuildingJob to pick a level-1 spawnable building of
    /// the placeholder's zone (the extractor building companies move into) and
    /// hands it to CreateDefinitionsJob as m_AttachmentPrefab, which creates it
    /// attached to the placeholder (offset towards the lot's front). The
    /// existing BridgeToolSystem placement passes no attachment, which leaves
    /// an invisible placeholder with an extractor area but no building, so no
    /// company ever moves in.
    /// </summary>
    public sealed partial class BridgeAreaToolSystem
    {
        private bool m_PlaceMode;
        private Entity m_PlacePrefab;
        private float3 m_PlacePosition;
        private quaternion m_PlaceRotation;
        private Entity m_PlaceAttachment;
        private float3 m_PlaceAttachmentOffset;

        /// <summary>Must be called on the simulation thread.</summary>
        public bool TryQueuePlacement(Entity prefabEntity, PrefabBase prefab, float3 position, quaternion rotation,
            Entity attachmentPrefab, float3 attachmentOffset, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle)
            {
                return false;
            }
            m_PlaceMode = true;
            m_PlacePrefab = prefabEntity;
            m_PendingPrefab = prefab;
            m_PlacePosition = position;
            m_PlaceRotation = rotation;
            m_PlaceAttachment = attachmentPrefab;
            m_PlaceAttachmentOffset = attachmentOffset;
            m_PendingNodes = null;
            m_PendingRequest = request;
            m_Applied = false;
            m_Stage = Stage.CreateDefinitions;
            m_PreviousTool = m_ToolSystem.activeTool;
            m_ToolSystem.activeTool = this;
            return true;
        }

        private BridgeResponse BuildPlacementResponse()
        {
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            PrefabBase attachment = m_PlaceAttachment != Entity.Null ? prefabSystem.GetPrefab<PrefabBase>(m_PlaceAttachment) : null;
            return BridgeResponse.Json(new
            {
                placed = true,
                prefab = m_PendingPrefab != null ? m_PendingPrefab.name : null,
                attachedBuilding = attachment != null ? attachment.name : null,
                position = new { x = m_PlacePosition.x, y = m_PlacePosition.y, z = m_PlacePosition.z },
                note = "placed like the game's specialized industry tool (placeholder + attached extractor building + extractor area); " +
                       "an extractor company moves in while the simulation runs",
            });
        }

        /// <summary>Same job setup as BridgeToolSystem.CreatePlacementDefinitions, plus the attachment.</summary>
        private void CreatePlacementDefinitions()
        {
            CityConfigurationSystem cityConfiguration = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            var attachment = new NativeReference<AttachmentData>(Allocator.TempJob);
            try
            {
                attachment.Value = new AttachmentData { m_Entity = m_PlaceAttachment, m_Offset = m_PlaceAttachmentOffset };

                CreateDefinitions definitions = default;
                definitions.m_RandomizationEnabled = false;
                definitions.m_FixedRandomSeed = 0;
                definitions.m_EditorMode = m_ToolSystem.actionMode.IsEditor();
                definitions.m_LefthandTraffic = cityConfiguration.leftHandTraffic;
                definitions.m_ObjectPrefab = m_PlacePrefab;
                definitions.m_Theme = cityConfiguration.defaultTheme;
                definitions.m_RandomSeed = RandomSeed.Next();
                definitions.m_AgeMask = AgeMask.Mature;
                definitions.m_ControlPoint = new ControlPoint
                {
                    m_Position = m_PlacePosition,
                    m_Rotation = m_PlaceRotation,
                };
                definitions.m_AttachmentPrefab = attachment;
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
                definitions.m_WaterSurfaceData = World.GetOrCreateSystemManaged<WaterSystem>().GetSurfaceData(out Unity.Jobs.JobHandle waterDeps);
                waterDeps.Complete();
                definitions.m_TerrainHeightData = World.GetOrCreateSystemManaged<TerrainSystem>().GetHeightData();
                definitions.m_CommandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
                definitions.Execute();
            }
            finally
            {
                attachment.Dispose();
            }
        }
    }
}
