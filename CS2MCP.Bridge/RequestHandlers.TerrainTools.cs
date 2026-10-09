using System;
using Colossal.Mathematics;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        /// <summary>只接入游戏内高度笔刷，不接入编辑器资源、海平面或水模拟作弊开关。</summary>
        private BridgeResponse UseTerrainTool(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            if (!request.Query.TryGetValue("mode", out string mode) || (mode != "raise" && mode != "lower" && mode != "level" && mode != "soften" && mode != "slope"))
                return BridgeResponse.Error(400, "mode must be raise/lower/level/soften/slope");
            foreach (string key in new[] { "x", "z", "x2", "z2", "size", "strength", "angle", "targetHeight" })
                if (request.Query.ContainsKey(key) && (!request.TryGetFloat(key,out float value) || !math.isfinite(value))) return BridgeResponse.Error(400,$"{key} must be finite");
            if (!request.TryGetFloat("x",out float x) || !request.TryGetFloat("z",out float z)) return BridgeResponse.Error(400,"x/z required");
            if (request.Query.ContainsKey("targetHeight") && mode != "level" && mode != "slope") return BridgeResponse.Error(400,"targetHeight applies only to level/slope");
            float x2 = x, z2 = z;
            bool hasEnd = request.Query.ContainsKey("x2") || request.Query.ContainsKey("z2");
            if (hasEnd && (!request.TryGetFloat("x2",out x2) || !request.TryGetFloat("z2",out z2))) return BridgeResponse.Error(400,"x2/z2 required together");
            if (mode == "slope" && (!hasEnd || math.distance(new float2(x,z),new float2(x2,z2)) < 8)) return BridgeResponse.Error(400,"slope requires a distinct end at least 8m from start");
            float size = request.TryGetFloat("size",out float s) ? s : 64;
            float strength = request.TryGetFloat("strength",out float st) ? st : .3f;
            float angle = request.TryGetFloat("angle",out float a) ? a : 0;
            int passes = request.TryGetInt("passes",out int p) ? p : 1;
            if (size < 8 || size > TerrainToolSystem.kMaxBrushSizeInGame || strength <= 0 || strength > 1 || passes < 1 || passes > 20)
                return BridgeResponse.Error(400,"size must be 8..1000m, strength >0..1, passes 1..20");
            var startBounds = ToolUtils.GetBounds(new Brush { m_Position = new float3(x,0,z), m_Size = size, m_Angle = math.radians(angle) });
            var endBounds = ToolUtils.GetBounds(new Brush { m_Position = new float3(x2,0,z2), m_Size = size, m_Angle = math.radians(angle) });
            float2 min = math.min(startBounds.min,endBounds.min), max = math.max(startBounds.max,endBounds.max);
            if (math.any(min < -7168) || math.any(max > 7168) || math.distance(new float2(x,z),new float2(x2,z2)) > 1000)
                return BridgeResponse.Error(400,"brush footprint must remain in playable map; stroke length <=1000m");
            using (var tiles = MapTileAreaQuery.ToEntityArray(Allocator.Temp))
                foreach (Entity tile in tiles)
                {
                    var bounds = EntityManager.GetComponentData<Geometry>(tile).m_Bounds;
                    if (EntityManager.HasComponent<Native>(tile) && math.all(bounds.min.xz < max) && math.all(bounds.max.xz > min))
                        return BridgeResponse.Error(409,"brush bounding rectangle reaches unowned land; reduce size/stroke or buy the required tiles normally");
                }
            TerraformingType type = mode == "level" ? TerraformingType.Level : mode == "soften" ? TerraformingType.Soften : mode == "slope" ? TerraformingType.Slope : TerraformingType.Shift;
            Entity toolEntity = Entity.Null; PrefabBase toolPrefab = null;
            using (var query = EntityManager.CreateEntityQuery(typeof(PrefabData),typeof(TerraformingData)))
            using (var tools = query.ToEntityArray(Allocator.Temp))
                foreach (Entity entity in tools)
                {
                    var data = EntityManager.GetComponentData<TerraformingData>(entity);
                    if (data.m_Target != TerraformingTarget.Height || data.m_Type != type || IsLocked(entity)) continue;
                    if (World.GetOrCreateSystemManaged<PrefabSystem>().TryGetPrefab(entity,out toolPrefab)) { toolEntity = entity; break; }
                }
            if (toolEntity == Entity.Null) return BridgeResponse.Error(409,"native height tool is not available or unlocked");
            var terrainTool = World.GetOrCreateSystemManaged<TerrainToolSystem>();
            BrushPrefab brushPrefab = terrainTool.brushType;
            if (request.Query.TryGetValue("brush",out string brushName))
            {
                brushPrefab = null;
                using (var query = EntityManager.CreateEntityQuery(typeof(PrefabData),typeof(BrushData)))
                using (var brushes = query.ToEntityArray(Allocator.Temp))
                    foreach (Entity entity in brushes)
                        if (NativeName(entity).Equals(brushName,StringComparison.OrdinalIgnoreCase) && !IsLocked(entity))
                            World.GetOrCreateSystemManaged<PrefabSystem>().TryGetPrefab(entity,out brushPrefab);
            }
            if (brushPrefab == null || brushPrefab.m_Texture == null) return BridgeResponse.Error(409,"provide an available native brush from tools/capabilities; no ready brush texture");
            Entity brushEntity = World.GetOrCreateSystemManaged<PrefabSystem>().GetEntity(brushPrefab);
            if (IsLocked(brushEntity)) return BridgeResponse.Error(409,"brush content unavailable");
            TerrainHeightData terrain = World.GetOrCreateSystemManaged<TerrainSystem>().GetHeightData();
            float3 start = new float3(x,0,z), end = new float3(x2,0,z2);
            start.y = TerrainUtils.SampleHeight(ref terrain,start); end.y = TerrainUtils.SampleHeight(ref terrain,end);
            float target = request.TryGetFloat("targetHeight",out float h) ? h : (mode == "slope" ? end.y : start.y);
            if (target < -1024 || target > 4096) return BridgeResponse.Error(400,"targetHeight must be -1024..4096m world height");
            if (mode == "raise" || mode == "lower" || mode == "soften")
                strength *= math.lerp(.4f,1f,EaseUtils.OutSine(math.clamp(size/5000f,0,1)));
            var definition = new BrushDefinition { m_Tool = toolEntity, m_Line = new Line3.Segment(start,end), m_Size = size,
                m_Strength = mode == "lower" ? -strength : strength, m_Angle = math.radians(angle), m_Target = new float3(end.x,target,end.z), m_Start = start };
            if (request.TryGetBool("dryRun",out bool dry) && dry)
                return BridgeResponse.Json(new { dryRun = true, mode, tool = toolPrefab.name, brush = brushPrefab.name, size, strength = definition.m_Strength,
                    passes, start = RoadPointInfo(start), end = RoadPointInfo(end), targetHeight = target,
                    note = "owned footprint and native asset/parameters checked only; not a rendered terrain preview; level defaults to start height, slope joins start height to target end height; brush passes are native strokes, not exact height changes" });
            if (TransitToolsBusy(out error)) return error;
            if (!World.GetOrCreateSystemManaged<BridgeToolSystem>().TryQueueTerrain(toolEntity,toolPrefab,brushEntity,definition,passes,request))
                return BridgeResponse.Error(409,"another construction operation is in progress");
            return null;
        }
    }
}
