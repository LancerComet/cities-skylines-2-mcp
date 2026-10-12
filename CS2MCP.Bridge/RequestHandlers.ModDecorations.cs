using System;
using System.Linq;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        /// <summary>复用Better Bulldozer的拥有者/防重生记录契约；限定非经营、非网络、非交通功能的静态子对象。</summary>
        private BridgeResponse ActDecoration(string action, JObject data)
        {
            OptionalModApi.Keys(data, "entity", "owner", "permanent", "dryRun");
            var assembly = OptionalModApi.Assembly("BetterBulldozer");
            var recordType = assembly.GetType("Better_Bulldozer.Components.OwnerRecord", true);
            var bufferType = assembly.GetType("Better_Bulldozer.Components.PermanentlyRemovedSubElementPrefab", true);
            Entity owner, target = Entity.Null;
            if (action == "restoreDecorations") owner = ModEntity(data["owner"]);
            else
            {
                target = ModEntity(data["entity"]);
                if (!EntityManager.HasComponent<Owner>(target)) throw new ArgumentException("target has no owner; use native demolition for standalone objects");
                owner = EntityManager.GetComponentData<Owner>(target).m_Owner;
                if (owner != ModEntity(data["owner"])) throw new ArgumentException("owner identity mismatch");
                CheckDecoration(target);
            }
            bool dryRun = (bool?)data["dryRun"] != false, permanent = (bool?)data["permanent"] == true;
            if (action == "restoreDecorations")
            {
                var before = ModEntityComponents(owner, "Better_Bulldozer.Components.");
                if (dryRun) return BridgeResponse.Json(new { applied = false, owner = OptionalModApi.JsonValue(owner), before });
                EntityManager.RemoveComponent(owner, ComponentType.ReadWrite(bufferType));
                EntityManager.AddComponent<Updated>(owner);
                return BridgeResponse.Json(new { requested = true, owner = OptionalModApi.JsonValue(owner), before, note = "取消防重生记录并请求母体原生再生成；等待实际模型回读，不销毁母体或清除全城记录" });
            }
            if (!EntityManager.HasBuffer<Game.Objects.SubObject>(owner)) throw new ArgumentException("owner does not contain a SubObject buffer");
            var prefab = EntityManager.GetComponentData<PrefabRef>(target).m_Prefab;
            var targets = permanent ? EntityManager.GetBuffer<Game.Objects.SubObject>(owner, true).Select(x => x.m_SubObject).Where(e => EntityManager.Exists(e)
                && EntityManager.HasComponent<PrefabRef>(e) && EntityManager.GetComponentData<PrefabRef>(e).m_Prefab == prefab).ToArray() : new[] { target };
            if (targets.Length == 0 || targets.Length > 128 || !targets.Contains(target)) throw new ArgumentException("decoration targets outside expected owner scope");
            foreach (var e in targets) CheckDecoration(e);
            var identities = targets.Select(e => OptionalModApi.JsonValue(e)).ToArray();
            if (dryRun) return BridgeResponse.Json(new { applied = false, owner = OptionalModApi.JsonValue(owner), targets = identities, permanent,
                note = "永久防重生按母体内Prefab匹配，会影响列出的全部同类；单个临时删除可能在母体更新时重生" });
            if (permanent)
            {
                if (!EntityManager.HasComponent(owner, ComponentType.ReadOnly(bufferType))) typeof(EntityManager).GetMethods().First(m => m.Name == "AddBuffer" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
                    .MakeGenericMethod(bufferType).Invoke(EntityManager, new object[] { owner });
                var buffer = OptionalModApi.Buffer(EntityManager, owner, bufferType, false);
                Entity record = EntityManager.CreateEntity();
                EntityManager.AddComponent(record, ComponentType.ReadWrite(recordType));
                var value = Activator.CreateInstance(recordType, new object[] { owner });
                typeof(EntityManager).GetMethods().First(m => m.Name == "SetComponentData" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2)
                    .MakeGenericMethod(recordType).Invoke(EntityManager, new object[] { record, value });
                EntityManager.AddComponentData(record, new PrefabRef { m_Prefab = prefab });
                OptionalModApi.Call(buffer, "Add", Activator.CreateInstance(bufferType, new object[] { record }));
            }
            foreach (var e in targets) EntityManager.AddComponent<Deleted>(e);
            return BridgeResponse.Json(new { requested = true, owner = OptionalModApi.JsonValue(owner), targets = identities, permanent,
                note = "按Better Bulldozer契约标记删除，需等原生删除系统与模组更新后核实际子对象；没有删除车道/设施升级/母体" });
        }
        private void CheckDecoration(Entity entity)
        {
            if (!EntityManager.HasComponent<PrefabRef>(entity) || !EntityManager.HasComponent<Game.Objects.Transform>(entity)) throw new ArgumentException("static decoration identity required");
            using (var components = EntityManager.GetComponentTypes(entity, Unity.Collections.Allocator.Temp))
                foreach (var c in components)
                {
                    string name = c.GetManagedType()?.FullName ?? "";
                    if (name.StartsWith("Game.Net.", StringComparison.Ordinal) || name.StartsWith("Game.Buildings.", StringComparison.Ordinal)
                        || name.StartsWith("Game.Vehicles.", StringComparison.Ordinal) || name.StartsWith("Game.Creatures.", StringComparison.Ordinal)
                        || new[] { "Game.Objects.Extension", "Game.Objects.NetObject", "Game.Objects.ActivityLocation", "Game.Objects.StreetLight", "Game.Objects.TrafficLight", "Game.Objects.Quantity" }.Contains(name))
                        throw new ArgumentException("functional, network or owned service sub-element is protected: " + name);
                }
            var prefabEntity = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            var prefabs = World.GetExistingSystemManaged<PrefabSystem>();
            if (!prefabs.TryGetPrefab<PrefabBase>(prefabEntity, out var prefab) || !(prefab is StaticObjectPrefab) || prefab is BuildingPrefab
                || prefab.name.IndexOf("Invisible", StringComparison.OrdinalIgnoreCase) >= 0) throw new ArgumentException("only visible static decorative subobjects supported");
        }
    }
}
