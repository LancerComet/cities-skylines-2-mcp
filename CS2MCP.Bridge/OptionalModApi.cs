using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.UI;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>可选模组的版本边界。只访问已加载程序集；调用方固定类型/成员，不接受远程任意反射。</summary>
    internal static class OptionalModApi
    {
        internal const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static Assembly Assembly(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(name + " is not loaded; subscription is not runtime availability");
        internal static object System(World world, string assembly, string type) => world.GetExistingSystemManaged(Assembly(assembly).GetType(type, true))
            ?? throw new InvalidOperationException(type + " existing system unavailable");
        internal static FieldInfo Field(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, Members | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            throw new MissingFieldException(type.FullName, name);
        }
        internal static object Read(object target, string name)
        {
            var p = target.GetType().GetProperty(name, Members);
            return p != null ? p.GetValue(target) : Field(target.GetType(), name).GetValue(target);
        }
        internal static object Static(string assembly, string typeName, string name)
        {
            var type = Assembly(assembly).GetType(typeName, true);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            return type.GetProperty(name, flags)?.GetValue(null) ?? type.GetField(name, flags)?.GetValue(null);
        }
        internal static void Write(object target, string name, object value)
        {
            var p = target.GetType().GetProperty(name, Members);
            if (p != null) p.SetValue(target, value); else Field(target.GetType(), name).SetValue(target, value);
        }
        internal static object Call(object target, string name, params object[] args)
        {
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var methods = type.GetMethods(Members | BindingFlags.DeclaredOnly).Where(m => m.Name == name && m.GetParameters().Length == args.Length).ToArray();
                foreach (var method in methods)
                {
                    if (method.GetParameters().Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(x => x))
                        return method.Invoke(target, args);
                }
            }
            throw new MissingMethodException(target.GetType().FullName, name);
        }
        internal static object Binding(object system, string group, string name, object input)
        {
            var bindings = (IEnumerable)typeof(UISystemBase).GetField("m_Bindings", Members).GetValue(system);
            foreach (var b in bindings)
                if ((string)Read(b, "group") == group && (string)Read(b, "name") == name)
                    return ((Delegate)Field(b.GetType(), "m_Callback").GetValue(b)).DynamicInvoke(input);
            throw new InvalidOperationException("mod binding unavailable: " + group + "." + name);
        }
        internal static object JsonValue(object value, int depth = 0)
        {
            if (value == null) return null;
            if (value is Entity e) return new { index = e.Index, version = e.Version };
            if (value is Game.Prefabs.PrefabBase prefab) return new { name = prefab.name, type = prefab.GetType().Name };
            if (value is float3 p) return new { x = p.x, y = p.y, z = p.z };
            if (value is float2 p2) return new { x = p2.x, y = p2.y };
            var type = value.GetType();
            if (type.IsEnum) return value.ToString();
            if (type.IsPrimitive || value is string || value is decimal) return value;
            if (depth >= 6) return value.ToString();
            if (value is IDictionary dictionary)
            {
                var result = new Dictionary<string, object>();
                foreach (DictionaryEntry item in dictionary) { if (result.Count >= 128) break; result[item.Key.ToString()] = JsonValue(item.Value, depth + 1); }
                return result;
            }
            if (value is IEnumerable sequence) return sequence.Cast<object>().Take(128).Select(x => JsonValue(x, depth + 1)).ToArray();
            if (type.Namespace?.StartsWith("NodeController.Domain.Common", StringComparison.Ordinal) == true)
            {
                // NodeController的轨迹属性会访问尚未生成的数组。状态读取仅访问标量，明确报告省略和不可用字段。
                var state = new Dictionary<string, object>();
                var omitted = new List<string>();
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p3 => p3.CanRead && p3.GetIndexParameters().Length == 0).Take(64))
                {
                    var scalar = prop.PropertyType;
                    if (!(scalar.IsPrimitive || scalar.IsEnum || scalar == typeof(string) || scalar == typeof(decimal) || scalar == typeof(Entity))) { omitted.Add(prop.Name); continue; }
                    try { state[prop.Name] = JsonValue(prop.GetValue(value), depth + 1); }
                    catch (TargetInvocationException ex) { state[prop.Name] = new { available = false, error = ex.InnerException?.GetType().Name ?? ex.GetType().Name }; }
                }
                return new { type = type.FullName, scalarState = state, omittedGeometryOrCollections = omitted };
            }
            var fields = new Dictionary<string, object>();
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance).Take(64)) fields[field.Name] = JsonValue(field.GetValue(value), depth + 1);
            if (fields.Count > 0) return fields;
            if (type.Namespace?.StartsWith("RoadBuilder.Domain.Configurations", StringComparison.Ordinal) == true || type.FullName == "Anarchy.Domain.ErrorCheck")
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p3 => p3.CanRead && p3.GetIndexParameters().Length == 0))
                    fields[prop.Name] = JsonValue(prop.GetValue(value), depth + 1);
            return fields.Count > 0 ? (object)fields : value.ToString();
        }
        internal static JObject Payload(BridgeRequest request)
        {
            var raw = request.Query.TryGetValue("payload", out var query) ? query : request.Body;
            if (string.IsNullOrWhiteSpace(raw)) return new JObject();
            if (raw.Length > 16000) throw new ArgumentException("mod payload exceeds 16000 characters");
            return JObject.Parse(raw);
        }
        internal static void Keys(JObject data, params string[] keys)
        {
            var extra = data.Properties().FirstOrDefault(p => !keys.Contains(p.Name));
            if (extra != null) throw new ArgumentException("unsupported parameter: " + extra.Name);
        }
        internal static float Number(JToken token, float min, float max, string key)
        {
            if (token == null || token.Type != JTokenType.Integer && token.Type != JTokenType.Float) throw new ArgumentException(key + " must be a number");
            float value = token.Value<float>();
            if (float.IsNaN(value) || float.IsInfinity(value) || value < min || value > max) throw new ArgumentException(key + " outside " + min + ".." + max);
            return value;
        }
        internal static object Buffer(EntityManager manager, Entity entity, Type type, bool readOnly = true)
        {
            return typeof(EntityManager).GetMethods().First(m => m.Name == "GetBuffer" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(bool))
                .MakeGenericMethod(type).Invoke(manager, new object[] { entity, readOnly });
        }
        internal static object Component(EntityManager manager, Entity entity, Type type) => typeof(EntityManager).GetMethods()
            .First(m => m.Name == "GetComponentData" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Entity))
            .MakeGenericMethod(type).Invoke(manager, new object[] { entity });
    }
}
