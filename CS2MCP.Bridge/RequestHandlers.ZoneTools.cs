using System;
using Colossal.Mathematics;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        /// <summary>原生填充、框选和沿线细刷；坐标先落到真实可见格，矩形按种子格的道路方向对齐。</summary>
        private BridgeResponse UseZoneTool(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            if (!request.Query.TryGetValue("zone", out string name) || string.IsNullOrWhiteSpace(name))
                return BridgeResponse.Error(400, "provide zone prefab name or None to dezone");
            string mode = request.Query.TryGetValue("mode", out string value) ? value : "fill";
            if (mode != "fill" && mode != "marquee" && mode != "paint") return BridgeResponse.Error(400, "mode must be fill, marquee or paint");
            if (!request.TryGetFloat("x", out float x) || !request.TryGetFloat("z", out float z) || !math.all(math.isfinite(new float2(x,z))))
                return BridgeResponse.Error(400, "finite x/z seed coordinates required");
            float x2 = x, z2 = z;
            bool hasEnd = request.Query.ContainsKey("x2") || request.Query.ContainsKey("z2");
            if (hasEnd && (!request.TryGetFloat("x2", out x2) || !request.TryGetFloat("z2", out z2) || !math.all(math.isfinite(new float2(x2,z2)))))
                return BridgeResponse.Error(400, "provide finite x2/z2 together");
            if (mode == "fill" && hasEnd) return BridgeResponse.Error(400, "fill accepts one seed; use separate operations for separate blocks");
            if (mode == "marquee" && !hasEnd) return BridgeResponse.Error(400, "marquee requires x2/z2");
            if (math.distance(new float2(x,z),new float2(x2,z2)) > 2000) return BridgeResponse.Error(400, "stroke length must be <=2000m");
            int maxCells = request.TryGetInt("maxCells", out int max) ? max : 4096;
            if (maxCells < 1 || maxCells > 20000) return BridgeResponse.Error(400, "maxCells must be 1..20000");
            bool clear = name.Equals("None", StringComparison.OrdinalIgnoreCase);
            Entity prefabEntity = Entity.Null; PrefabBase prefab = null;
            using (var prefabs = ZonePrefabQuery.ToEntityArray(Allocator.Temp))
                foreach (Entity entity in prefabs)
                    if (clear || NativeName(entity).Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        prefabEntity = entity;
                        World.GetOrCreateSystemManaged<PrefabSystem>().TryGetPrefab(entity, out prefab);
                        break;
                    }
            if (prefabEntity == Entity.Null || prefab == null) return BridgeResponse.Error(404, "unknown zoning prefab");
            if (!clear && IsLocked(prefabEntity)) return BridgeResponse.Error(409, "zoning prefab is locked or its content is unavailable");
            Entity seed = Entity.Null; float distance = 5.66f; float3 start = new float3(x,0,z); Block seedData = default;
            using (var blocks = ZoneBlockQuery.ToEntityArray(Allocator.Temp))
                foreach (Entity entity in blocks)
                {
                    Block block = EntityManager.GetComponentData<Block>(entity);
                    if (math.distance(block.m_Position.xz,new float2(x,z)) > math.length((float2)block.m_Size)*8+6) continue;
                    var cells = EntityManager.GetBuffer<Cell>(entity, true);
                    for (int iz = 0; iz < block.m_Size.y; iz++) for (int ix = 0; ix < block.m_Size.x; ix++)
                    {
                        int index = iz * block.m_Size.x + ix;
                        if (index >= cells.Length || (cells[index].m_State & CellFlags.Visible) == 0 || (cells[index].m_State & CellFlags.Blocked) != 0) continue;
                        float3 position = ZoneUtils.GetCellPosition(block, new int2(ix,iz));
                        float d = math.distance(position.xz, new float2(x,z));
                        if (d < distance) { distance = d; start = position; seed = entity; seedData = block; }
                    }
                }
            if (seed == Entity.Null) return BridgeResponse.Error(409, "seed is not within a visible native zoning cell; use zone_cells to find the actual grid");
            bool overwrite = request.TryGetBool("overwrite", out bool ow) && ow;
            bool previewOnly = request.TryGetBool("previewOnly", out bool preview) && preview;
            Zoning zoning = new Zoning { m_Flags = clear ? ZoningFlags.Dezone | ZoningFlags.Overwrite : ZoningFlags.Zone | (overwrite ? ZoningFlags.Overwrite : 0) };
            float3 end = hasEnd ? new float3(x2,start.y,z2) : start;
            if (mode == "fill") { zoning.m_Flags |= ZoningFlags.FloodFill; zoning.m_Position = new Quad3(start,start,start,start); }
            else if (mode == "paint") { zoning.m_Flags |= ZoningFlags.Paint; zoning.m_Position = new Quad3(start,start,end,end); }
            else
            {
                zoning.m_Flags |= ZoningFlags.Marquee;
                float3 u = new float3(seedData.m_Direction.x,0,seedData.m_Direction.y);
                float3 v = new float3(MathUtils.Right(seedData.m_Direction).x,0,MathUtils.Right(seedData.m_Direction).y);
                float du = math.dot(end-start,u), dv = math.dot(end-start,v);
                if (du < 0) { u = -u; du = -du; }
                if (dv < 0) { v = -v; dv = -dv; }
                zoning.m_Position = new Quad3(start-(u+v)*4, start-u*4+v*(dv+4), start+u*(du+4)+v*(dv+4), start+u*(du+4)-v*4);
            }
            TerrainHeightData terrain = World.GetOrCreateSystemManaged<TerrainSystem>().GetHeightData();
            zoning.m_Position.a.y = TerrainUtils.SampleHeight(ref terrain,zoning.m_Position.a);
            zoning.m_Position.b.y = TerrainUtils.SampleHeight(ref terrain,zoning.m_Position.b);
            zoning.m_Position.c.y = TerrainUtils.SampleHeight(ref terrain,zoning.m_Position.c);
            zoning.m_Position.d.y = TerrainUtils.SampleHeight(ref terrain,zoning.m_Position.d);
            if (TransitToolsBusy(out error)) return error;
            if (!World.GetOrCreateSystemManaged<BridgeToolSystem>().TryQueueZoning(prefabEntity,prefab,seed,zoning,previewOnly,maxCells,request))
                return BridgeResponse.Error(409, "another construction operation is in progress");
            return null;
        }
    }
}
