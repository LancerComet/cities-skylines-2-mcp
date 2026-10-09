using System;
using System.Collections.Generic;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        /// <summary>读取原生能力及当前选择；不把界面偏好伪装成自定义施工接口已生效的参数。</summary>
        private BridgeResponse GetToolCapabilities()
        {
            if (!TryGetCity(out _,out BridgeResponse error)) return error;
            var net = World.GetOrCreateSystemManaged<NetToolSystem>();
            var zone = World.GetOrCreateSystemManaged<ZoneToolSystem>();
            var terrain = World.GetOrCreateSystemManaged<TerrainToolSystem>();
            var obj = World.GetOrCreateSystemManaged<ObjectToolSystem>();
            var area = World.GetOrCreateSystemManaged<AreaToolSystem>();
            var bulldoze = World.GetOrCreateSystemManaged<BulldozeToolSystem>();
            var brushes = new List<object>(); var heightTools = new List<object>();
            using (var query = EntityManager.CreateEntityQuery(typeof(PrefabData),typeof(BrushData)))
            using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities)
                {
                    World.GetOrCreateSystemManaged<PrefabSystem>().TryGetPrefab<BrushPrefab>(entity,out var prefab);
                    brushes.Add(new { name = NativeName(entity), available = !IsLocked(entity), textureReady = prefab != null && prefab.m_Texture != null });
                }
            using (var query = EntityManager.CreateEntityQuery(typeof(PrefabData),typeof(TerraformingData)))
            using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities)
                {
                    var data = EntityManager.GetComponentData<TerraformingData>(entity);
                    if (data.m_Target == TerraformingTarget.Height)
                        heightTools.Add(new { name = NativeName(entity), type = data.m_Type.ToString(), available = !IsLocked(entity) });
                }
            var snaps = new List<object>();
            foreach (Snap snap in Enum.GetValues(typeof(Snap))) snaps.Add(new { name = snap.ToString(), mask = (uint)snap });
            return BridgeResponse.Json(new { capturedAtUtc = DateTime.UtcNow.ToString("o"), bridgeVersion = Mod.Version,
                activeTool = World.GetOrCreateSystemManaged<ToolSystem>().activeTool?.toolID,
                mods = DescribeModIntegrations(),
                roads = new { native = ToolSnapshot(net), modes = Enum.GetNames(typeof(NetToolSystem.Mode)), currentMode = net.actualMode.ToString(),
                    net.elevation, net.elevationStep, net.parallelCount, net.parallelOffset, net.allowParallel, net.allowGrid, net.allowReplace, net.allowUnderground,
                    bridge = "connect_road: straight/quadratic/cubic, endpoint elevation and height-filtered compatible snapping, control elevations, geometry dryRun; replace_road/upgrade/junction: native apply; road_path: sequential connected segments; grid/parallel layout require explicit geometry, UI preferences do not drive bridge definitions" },
                zones = new { native = ToolSnapshot(zone), modes = Enum.GetNames(typeof(ZoneToolSystem.Mode)), currentMode = zone.mode.ToString(), zone.overwrite,
                    bridge = "zone_tool fill/marquee/paint: native preview/application; zone_polygon: explicit plan boundary; zone_cells: actual coverage; fill for homogeneous connected land, marquee/polygon for reserved or mixed blocks" },
                terrain = new { native = ToolSnapshot(terrain), heightTools, brushes, selectedBrush = terrain.brushType?.name,
                    terrain.brushSize, terrain.brushStrength, terrain.brushHeight,
                    bridge = "terrain_brush: raise/lower/level/soften/slope, size/strength/angle/brush/target height and bounded passes; owned footprint checked; asynchronous ground/water update requires resampling; material/resources/editor water settings excluded" },
                objects = new { native = ToolSnapshot(obj), modes = Enum.GetNames(typeof(ObjectToolSystem.Mode)), currentMode = obj.mode.ToString(),
                    obj.allowLine, obj.allowCurve, obj.allowBrush, obj.allowAge, obj.distance, ageMask = obj.ageMask.ToString(),
                    bridge = "place_building + service upgrade + tree placement + standalone building native Relocate supported; object brush/stamp/line/curve modes not connected; MoveIt loading is not proof of its write API" },
                areas = new { native = ToolSnapshot(area), modes = Enum.GetNames(typeof(AreaToolSystem.Mode)), area.allowGenerate,
                    bridge = "district creation; extractor-areas: bounded building-owned area detail; extractor-area/edit: existing native editable extractor Lot only, locked entrance/radius/owner/cache preserved through native preview/apply; no standalone specialized/invisible area creation or editor Generate" },
                bulldoze = new { native = ToolSnapshot(bulldoze), modes = Enum.GetNames(typeof(BulldozeToolSystem.Mode)), bulldoze.underground,
                    bridge = "precise entity bulldoze supported via native owner pipeline; sub-element/everything selection is not blindly applied to an entire area" },
                snapFlags = snaps,
                note = "native snapshots depend on each tool's currently selected prefab; masks are native UI capabilities, not flags silently honoured by every bridge operation; unavailable features are explicit gaps" });
        }

        private static object ToolSnapshot(ToolBaseSystem tool)
        {
            var modes = new List<ToolMode>(); var result = new List<object>();
            tool.GetUIModes(modes);
            foreach (var mode in modes) result.Add(new { id = mode.name, mode.index });
            tool.GetAvailableSnapMask(out Snap onMask,out Snap offMask);
            return new { tool = tool.toolID, prefab = tool.GetPrefab()?.name, availableModes = result, selectedSnap = tool.selectedSnap.ToString(),
                actualSnap = ToolBaseSystem.GetActualSnap(tool.selectedSnap,onMask,offMask).ToString(), onMask = (uint)onMask, offMask = (uint)offMask };
        }
    }
}
