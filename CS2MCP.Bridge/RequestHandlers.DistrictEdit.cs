using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Areas;
using Game.Prefabs;
using Game.Simulation;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// District management: reshape (redraw the polygon through BridgeAreaToolSystem's
    /// edit pipeline, as the vanilla area tool does for an existing area), rename
    /// (NameSystem.SetCustomName, run by BridgeTransitRenameSystem in the UI phase),
    /// delete (the same bulldoze pipeline as /build/demolish) and a detail readback
    /// with the polygon nodes.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private bool TryResolveDistrictRef(BridgeRequest request, out Entity district, out BridgeResponse error)
        {
            district = Entity.Null;
            error = null;
            if (!request.Query.TryGetValue("district", out string raw) || string.IsNullOrWhiteSpace(raw))
            {
                // Fall back to the index=&version= form used by the policy endpoints.
                return TryResolveDistrict(request, out district, out error);
            }
            if (!TryParseEntityRef(raw, out Entity entity))
            {
                error = BridgeResponse.Error(400, $"district '{raw}' is not 'index:version' (see /districts)");
                return false;
            }
            if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<District>(entity)
                || EntityManager.HasComponent<Game.Common.Deleted>(entity))
            {
                error = BridgeResponse.Error(404, $"entity {raw} is not an existing district (see /districts)");
                return false;
            }
            district = entity;
            return true;
        }

        private BridgeResponse DistrictDetail(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!TryResolveDistrictRef(request, out Entity district, out error))
            {
                return error;
            }

            var nodes = new List<object>();
            double signedArea = 0;
            if (EntityManager.HasBuffer<Node>(district))
            {
                DynamicBuffer<Node> buffer = EntityManager.GetBuffer<Node>(district, isReadOnly: true);
                for (int i = 0; i < buffer.Length; i++)
                {
                    float3 p = buffer[i].m_Position;
                    float3 q = buffer[(i + 1) % buffer.Length].m_Position;
                    signedArea += (double)p.x * q.z - (double)q.x * p.z;
                    nodes.Add(new { x = p.x, y = p.y, z = p.z });
                }
            }

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            string prefabName = null;
            if (EntityManager.HasComponent<PrefabRef>(district))
            {
                prefabName = prefabSystem.GetPrefab<PrefabBase>(EntityManager.GetComponentData<PrefabRef>(district).m_Prefab)?.name;
            }
            var activePolicies = new List<object>();
            if (EntityManager.HasBuffer<Game.Policies.Policy>(district))
            {
                DynamicBuffer<Game.Policies.Policy> policies = EntityManager.GetBuffer<Game.Policies.Policy>(district, isReadOnly: true);
                for (int i = 0; i < policies.Length; i++)
                {
                    if ((policies[i].m_Flags & Game.Policies.PolicyFlags.Active) == 0)
                    {
                        continue;
                    }
                    PolicyPrefab policy = prefabSystem.GetPrefab<PolicyPrefab>(policies[i].m_Policy);
                    activePolicies.Add(new { name = policy != null ? policy.name : null, adjustment = policies[i].m_Adjustment });
                }
            }
            Geometry geometry = EntityManager.GetComponentData<Geometry>(district);
            return BridgeResponse.Json(new
            {
                district = new { index = district.Index, version = district.Version },
                name = LabelOf(World.GetOrCreateSystemManaged<Game.UI.NameSystem>(), district),
                prefab = prefabName,
                center = new { x = geometry.m_CenterPosition.x, z = geometry.m_CenterPosition.z },
                polygonArea = Math.Round(Math.Abs(signedArea) * 0.5, 1),
                surfaceArea = geometry.m_SurfaceArea,
                nodeCount = nodes.Count,
                nodes,
                activePolicies,
                note = "reshape with /build/district/reshape, rename with /build/district/rename, delete with /build/district/delete",
            });
        }

        private BridgeResponse RenameDistrict(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!TryResolveDistrictRef(request, out Entity district, out error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("name", out string name))
            {
                return BridgeResponse.Error(400, "provide ?name=<new name> (empty clears the custom name)");
            }
            name = name.Trim();
            if (name.Length > 64)
            {
                return BridgeResponse.Error(400, "name is too long (max 64 characters)");
            }
            World.GetOrCreateSystemManaged<BridgeTransitRenameSystem>().Enqueue(district, name, request, warning =>
            {
                if (warning != null)
                {
                    return BridgeResponse.Error(500, warning.Replace("the line was created but naming", "naming").Replace("rename it in the transportation panel", "rename it in the district panel"));
                }
                return BridgeResponse.Json(new
                {
                    district = new { index = district.Index, version = district.Version },
                    name = LabelOf(World.GetOrCreateSystemManaged<Game.UI.NameSystem>(), district),
                    renamed = true,
                });
            });
            return null;
        }

        private BridgeResponse DeleteDistrict(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!TryResolveDistrictRef(request, out Entity district, out error))
            {
                return error;
            }
            // The bulldoze pipeline behind /build/demolish (it accepts districts).
            request.Query["index"] = district.Index.ToString(CultureInfo.InvariantCulture);
            request.Query["version"] = district.Version.ToString(CultureInfo.InvariantCulture);
            return Demolish(request);
        }

        private BridgeResponse ReshapeDistrict(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!TryResolveDistrictRef(request, out Entity district, out error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("points", out string raw) || string.IsNullOrWhiteSpace(raw))
            {
                return BridgeResponse.Error(400, "provide ?points=x,z;x,z;x,z;... (3+ corners in world meters)");
            }
            string[] pairs = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (pairs.Length < 3)
            {
                return BridgeResponse.Error(400, "InvalidShape: the polygon needs at least 3 corners (points=x,z;x,z;x,z)");
            }
            if (pairs.Length > 64)
            {
                return BridgeResponse.Error(400, "polygon too complex (max 64 corners)");
            }
            var corners = new float2[pairs.Length];
            for (int i = 0; i < pairs.Length; i++)
            {
                string[] parts = pairs[i].Split(',');
                if (parts.Length != 2
                    || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    return BridgeResponse.Error(400, $"cannot parse corner '{pairs[i]}'; expected x,z");
                }
                corners[i] = new float2(x, z);
            }
            for (int i = 0; i < corners.Length; i++)
            {
                if (math.distance(corners[i], corners[(i + 1) % corners.Length]) < 1f)
                {
                    return BridgeResponse.Error(400, $"InvalidShape: corners {i} and {(i + 1) % corners.Length} are less than 1 m apart");
                }
            }
            for (int i = 0; i < corners.Length; i++)
            {
                for (int j = i + 1; j < corners.Length; j++)
                {
                    if (j == i + 1 || (i == 0 && j == corners.Length - 1))
                    {
                        continue;
                    }
                    if (SegmentsIntersect(corners[i], corners[(i + 1) % corners.Length], corners[j], corners[(j + 1) % corners.Length]))
                    {
                        return BridgeResponse.Error(400,
                            $"InvalidShape: the polygon intersects itself (edge {i}-{(i + 1) % corners.Length} crosses edge {j}-{(j + 1) % corners.Length})");
                    }
                }
            }
            double signedArea = 0;
            for (int i = 0; i < corners.Length; i++)
            {
                float2 p = corners[i];
                float2 q = corners[(i + 1) % corners.Length];
                signedArea += (double)p.x * q.y - (double)q.x * p.y;
            }
            if (Math.Abs(signedArea) * 0.5 < 1.0)
            {
                return BridgeResponse.Error(400, "SmallArea: the polygon has (almost) no area");
            }

            TerrainHeightData heightData = World.GetOrCreateSystemManaged<TerrainSystem>().GetHeightData();
            var nodes = new float3[corners.Length];
            for (int i = 0; i < corners.Length; i++)
            {
                var position = new float3(corners[i].x, 0f, corners[i].y);
                position.y = TerrainUtils.SampleHeight(ref heightData, position);
                nodes[i] = position;
            }

            if (TransitToolsBusy(out error) || World.GetOrCreateSystemManaged<BridgeAreaToolSystem>().IsBusy)
            {
                return error ?? BridgeResponse.Error(409, "another area operation is in progress, retry shortly");
            }
            Entity areaPrefab = EntityManager.GetComponentData<PrefabRef>(district).m_Prefab;
            PrefabBase prefab = World.GetOrCreateSystemManaged<PrefabSystem>().GetPrefab<PrefabBase>(areaPrefab);

            // The area tool completes this proxy with extractor wording; re-word it for districts.
            var proxy = new BridgeRequest { Method = "GET", Path = "/build/district/reshape" };
            BridgeAreaToolSystem tool = World.GetOrCreateSystemManaged<BridgeAreaToolSystem>();
            if (!tool.TryQueueReshape(district, Entity.Null, areaPrefab, prefab, nodes, proxy))
            {
                return BridgeResponse.Error(409, "another area operation is in progress, retry shortly");
            }
            int count = nodes.Length;
            double areaSquareMeters = Math.Round(Math.Abs(signedArea) * 0.5, 1);
            World.GetOrCreateSystemManaged<BridgeGridBuilderSystem>().Defer(proxy, request, response =>
            {
                if (response.Status != 200)
                {
                    return response;
                }
                return BridgeResponse.Json(new
                {
                    reshaped = true,
                    district = new { index = district.Index, version = district.Version },
                    nodes = count,
                    polygonArea = areaSquareMeters,
                    note = "applied through the area tool pipeline (the game validated the shape); check /districts/detail for the new polygon",
                });
            });
            return null;
        }
    }
}
