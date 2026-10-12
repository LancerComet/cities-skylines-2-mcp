using System;
using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        /// <summary>窄迁站入口：同一路段同侧，复用原生Relocate保旧站及现线；默认只做完整预览。</summary>
        private BridgeResponse RelocateTransitStop(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error) || !ReadExistingEntity(request, out Entity stop, out error)) return error;
            string guid = World.GetExistingSystemManaged<Game.Serialization.LoadGameSystem>()?.context.instigatorGuid.ToString();
            if (!request.Query.TryGetValue("expectedLoadedSaveGuid", out string expectedGuid) || !string.Equals(guid, expectedGuid, StringComparison.OrdinalIgnoreCase))
                return BridgeResponse.Error(409, "loaded save identity does not match expectedLoadedSaveGuid; refresh the city before relocation");
            if (!EntityManager.HasComponent<Game.Routes.TransportStop>(stop) || !EntityManager.HasComponent<Attached>(stop)
                || !EntityManager.HasComponent<Transform>(stop) || !EntityManager.HasComponent<PrefabRef>(stop)
                || EntityManager.HasComponent<Game.Buildings.Building>(stop) || EntityManager.HasComponent<Owner>(stop))
                return BridgeResponse.Error(400, "only standalone attached roadside passenger stops can be relocated; station platforms are excluded");
            Entity prefabEntity = EntityManager.GetComponentData<PrefabRef>(stop).m_Prefab;
            if (!EntityManager.HasComponent<NetObjectData>(prefabEntity) || !EntityManager.HasComponent<TransportStopData>(prefabEntity)
                || !EntityManager.HasComponent<PlaceableObjectData>(prefabEntity) || !IsContentAvailable(prefabEntity) || IsLocked(prefabEntity))
                return BridgeResponse.Error(409, "the existing stop lacks normal available/unlocked roadside placement data");
            var stopData = EntityManager.GetComponentData<TransportStopData>(prefabEntity);
            var prefab = World.GetOrCreateSystemManaged<PrefabSystem>().GetPrefab<PrefabBase>(prefabEntity);
            if (!stopData.m_PassengerTransport || (stopData.m_TransportType != TransportType.Bus && stopData.m_TransportType != TransportType.Tram)
                || prefab == null || prefab.name.IndexOf("Invisible", StringComparison.OrdinalIgnoreCase) >= 0)
                return BridgeResponse.Error(400, "only normal visible roadside bus/tram passenger stops are supported");
            if (!request.TryGetFloat("x", out float x) || !request.TryGetFloat("z", out float z))
                return BridgeResponse.Error(400, "provide finite x and z");
            bool dryRun = true;
            if (request.Query.ContainsKey("dryRun") && !request.TryGetBool("dryRun", out dryRun))
                return BridgeResponse.Error(400, "dryRun must be a boolean; omitted defaults to true");
            request.Query["dryRun"] = dryRun ? "true" : "false";
            if (!request.Query.TryGetValue("road", out string rawRoad) || !TryParseEntityRef(rawRoad, out Entity road)
                || !EntityManager.Exists(road) || EntityManager.HasComponent<Deleted>(road) || EntityManager.HasComponent<Temp>(road)
                || EntityManager.HasComponent<Owner>(road) || !EntityManager.HasComponent<Game.Net.Edge>(road))
                return BridgeResponse.Error(400, "provide an existing independent road index:version");
            var original = EntityManager.GetComponentData<Transform>(stop);
            var attached = EntityManager.GetComponentData<Attached>(stop);
            if (attached.m_Parent != road)
                return BridgeResponse.Error(409, "this narrow relocation supports the current attached road only; changing corridors requires a separate design");
            if (TransitToolsBusy(out error)) return error;
            if (!TryComputeStopPlacement(prefabEntity, new float3(x, 0f, z), road, 40f, out ControlPoint point, out string reason))
                return BridgeResponse.Error(409, reason);
            if (math.distance(point.m_Position.xz, new float2(x, z)) > 40f)
                return BridgeResponse.Error(409, "native roadside snap is over 40m from the requested point");
            if (math.abs(math.dot(original.m_Rotation.value, point.m_Rotation.value)) < .9f)
                return BridgeResponse.Error(409, "relocation would change the existing stop's road side/direction; preserve its current passenger role");
            string before = CaptureStopLineContract(stop);
            Func<string> readContract = () => CaptureStopLineContract(stop);
            if (!World.GetOrCreateSystemManaged<BridgeToolSystem>().TryQueueTransitStopMove(stop, prefabEntity, prefab, point, before, readContract, request))
                return BridgeResponse.Error(409, "another construction operation is in progress");
            return null;
        }

        /// <summary>只比较线路身份、站序和设置；乘客、路长、动态出车与寻路结果在运营验收中另读。</summary>
        private string CaptureStopLineContract(Entity stop)
        {
            var rows = new SortedDictionary<int, object>();
            var names = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            var prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            using (var lines = TransitLineQuery.ToEntityArray(Allocator.Temp))
                foreach (var line in lines)
                {
                    var waypoints = EntityManager.GetBuffer<Game.Routes.RouteWaypoint>(line, true);
                    var stops = new List<object>();
                    bool serves = false;
                    foreach (var item in waypoints)
                    {
                        Entity connected = EntityManager.HasComponent<Game.Routes.Connected>(item.m_Waypoint)
                            ? EntityManager.GetComponentData<Game.Routes.Connected>(item.m_Waypoint).m_Connected : Entity.Null;
                        serves |= connected == stop;
                        stops.Add(new { waypoint = Ref(item.m_Waypoint), stop = Ref(connected) });
                    }
                    if (!serves) continue;
                    var data = Game.UI.InGame.TransportUIUtils.BuildTransportLine(line, EntityManager, prefabs);
                    var policies = new List<object>();
                    if (EntityManager.HasBuffer<Game.Policies.Policy>(line))
                        foreach (var policy in EntityManager.GetBuffer<Game.Policies.Policy>(line, true))
                            policies.Add(new { policy = Ref(policy.m_Policy), flags = policy.m_Flags.ToString(), adjustment = policy.m_Adjustment });
                    var models = new List<object>();
                    if (EntityManager.HasBuffer<Game.Routes.VehicleModel>(line))
                        foreach (var model in EntityManager.GetBuffer<Game.Routes.VehicleModel>(line, true))
                            models.Add(new { primary = Ref(model.m_PrimaryPrefab), secondary = Ref(model.m_SecondaryPrefab) });
                    rows.Add(line.Index, new { line = Ref(line), name = LabelOf(names, line), number = EntityManager.HasComponent<Game.Routes.RouteNumber>(line)
                        ? EntityManager.GetComponentData<Game.Routes.RouteNumber>(line).m_Number : 0,
                        prefab = PrefabNameOf(line), data.active, data.visible, data.schedule,
                        color = BridgeTransitToolSystem.ToHex(data.color), policies, models, stops });
                }
            return JsonConvert.SerializeObject(rows.Values);
        }
    }
}
