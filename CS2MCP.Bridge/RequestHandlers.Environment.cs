using System;
using System.Collections.Generic;
using Game.Simulation;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        /// <summary>一次按片区读取污染传输所用的原生风场和水流，避免逐点远程往返。</summary>
        private BridgeResponse GetEnvironmentField(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error)) return error;
            float x = request.TryGetFloat("x", out float requestedX) ? requestedX : 0f;
            float z = request.TryGetFloat("z", out float requestedZ) ? requestedZ : 0f;
            float radius = request.TryGetFloat("radius", out float requestedRadius) ? requestedRadius : kWorldHalfSize;
            int resolution = request.TryGetInt("resolution", out int requestedResolution) ? requestedResolution : 16;
            foreach (string key in new[] { "x", "z", "radius" })
                if (request.Query.ContainsKey(key) && (!request.TryGetFloat(key, out float number) || !math.isfinite(number)))
                    return BridgeResponse.Error(400, key + " must be finite numeric data");
            if (request.Query.ContainsKey("resolution") && !request.TryGetInt("resolution", out _))
                return BridgeResponse.Error(400, "resolution must be an integer");
            if (math.abs(x) > kWorldHalfSize || math.abs(z) > kWorldHalfSize || radius < 50 || radius > kWorldHalfSize || resolution < 4 || resolution > 64)
                return BridgeResponse.Error(400, "center must be inside the native map, radius 50..7168, resolution 4..64");

            var windSystem = World.GetOrCreateSystemManaged<WindSystem>();
            var wind = windSystem.GetMap(readOnly: true, out var windDependencies);
            var waterSystem = World.GetOrCreateSystemManaged<WaterSystem>();
            var surface = waterSystem.GetSurfaceData(out var surfaceDependencies);
            var velocity = waterSystem.GetVelocitiesSurfaceData(out var velocityDependencies);
            var airSystem = World.GetOrCreateSystemManaged<AirPollutionSystem>();
            var air = airSystem.GetMap(readOnly: true, out var airDependencies);
            windDependencies.Complete();
            surfaceDependencies.Complete();
            velocityDependencies.Complete();
            airDependencies.Complete();
            if (!wind.IsCreated || wind.Length < WindSystem.kTextureSize * WindSystem.kTextureSize || !surface.isCreated || !velocity.isCreated || !air.IsCreated)
                return BridgeResponse.Error(503, "native environmental CPU maps are not ready; unavailable flow must not be interpreted as still water");

            float xMin = math.max(-kWorldHalfSize, x - radius), xMax = math.min(kWorldHalfSize, x + radius);
            float zMin = math.max(-kWorldHalfSize, z - radius), zMax = math.min(kWorldHalfSize, z + radius);
            float stepX = (xMax - xMin) / resolution, stepZ = (zMax - zMin) / resolution;
            var samples = new List<object>(resolution * resolution);
            for (int row = 0; row < resolution; row++)
                for (int col = 0; col < resolution; col++) {
                    var position = new float3(xMin + (col + .5f) * stepX, 0f, zMin + (row + .5f) * stepZ);
                    var windVector = WindSystem.GetWind(position, wind).m_Wind;
                    float depth = WaterUtils.SampleDepth(ref surface, position);
                    bool wet = depth > .05f;
                    float2? flow = wet ? WaterUtils.SampleVelocity(ref velocity, position) : (float2?)null;
                    samples.Add(new {
                        x = position.x, z = position.z,
                        wind = new { x = windVector.x, z = windVector.y },
                        airPollution = AirPollutionSystem.GetPollution(position, air).m_Pollution,
                        waterDepthMeters = depth,
                        waterFlow = flow.HasValue ? new { x = flow.Value.x, z = flow.Value.y } : null,
                        waterPollution = wet ? WaterUtils.SamplePolluted(ref surface, position) : (float?)null
                    });
                }
            return BridgeResponse.Json(new {
                capturedAtUtc = DateTime.UtcNow.ToString("o"), simulationFrame = World.GetOrCreateSystemManaged<SimulationSystem>().frameIndex,
                simulationHasTickedSinceLoad = m_System.SimulationHasTickedSinceLoad,
                warning = m_System.SimulationHasTickedSinceLoad ? null : "simulation has not ticked since reload; restored CPU maps and climate may still need initialization",
                resolution, bounds = new { xMin, xMax, zMin, zMax }, cellSize = new { x = stepX, z = stepZ },
                units = new { wind = "native wind vector used for air-pollution advection", waterFlow = "native world-space water velocity; SI speed not independently verified", pollution = "native values, not a fabricated percentage" },
                note = "row-major; vectors point toward world +x/+z transport, not meteorological wind-from bearing. Dry cells have null water flow. A snapshot is not a permanent prevailing wind or downstream safety guarantee.",
                samples
            });
        }
    }
}
