using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityAgentBridge
{
    /// <summary>Component level commands.</summary>
    public static class ComponentCommands
    {
        public static void Register()
        {
            CommandRegistry.Register("comp.list", "列出对象上的所有组件", "target",
                delegate(Dictionary<string, object> args)
                {
                    var go = Obj.GameObject(CommandRegistry.Require(args, "target"));
                    var components = go.GetComponents<Component>();
                    var list = new List<object>();
                    for (int i = 0; i < components.Length; i++)
                    {
                        if (components[i] == null) { list.Add(new Dictionary<string, object> { { "type", "<MissingScript>" } }); continue; }
                        list.Add(Obj.DescribeComponent(components[i], false));
                    }
                    return new Dictionary<string, object> { { "gameObject", go.name }, { "count", list.Count }, { "components", list } };
                });

            CommandRegistry.Register("comp.add", "添加组件（已存在时默认直接返回现有组件）", "target, type, allowDuplicates?=false",
                delegate(Dictionary<string, object> args)
                {
                    var go = Obj.GameObject(CommandRegistry.Require(args, "target"));
                    string typeName = CommandRegistry.Require(args, "type", "例如 Rigidbody、BoxCollider、AudioSource");
                    Type type = RequireComponentType(typeName);

                    var existing = go.GetComponent(type);
                    if (existing != null && !J.Bool(args, "allowDuplicates", false))
                    {
                        var d0 = Obj.DescribeComponent(existing, false);
                        d0["alreadyExists"] = true;
                        return d0;
                    }

                    Component added;
                    try
                    {
                        added = Undo.AddComponent(go, type);
                    }
                    catch (Exception e)
                    {
                        throw new CommandException("添加组件失败: " + e.Message, "add_failed");
                    }

                    ObjectCommands.MarkDirty(go);
                    return Obj.DescribeComponent(added, false);
                });

            CommandRegistry.Register("comp.get", "读取组件属性（序列化字段），depth 控制嵌套深度", "target, type, depth?=2",
                delegate(Dictionary<string, object> args)
                {
                    var component = Resolve(args);
                    int depth = Mathf.Clamp(J.Int(args, "depth", 2), 0, 5);
                    var d = Obj.DescribeComponent(component, false);
                    d["properties"] = Properties.Dump(component, depth);
                    return d;
                });

            CommandRegistry.Register("comp.set", "写入组件序列化属性（字段路径来自 comp.get 的 path）", "target, type, field, value",
                delegate(Dictionary<string, object> args)
                {
                    var component = Resolve(args);
                    string field = CommandRegistry.Require(args, "field", "例如 mass、m_IsKinematic、m_Color.r");

                    if (field == "enabled")
                    {
                        var behaviour = component as Behaviour;
                        if (behaviour == null) throw new CommandException(component.GetType().Name + " 不支持 enabled", "unsupported");
                        Undo.RecordObject(behaviour, "Agent Bridge Enabled");
                        behaviour.enabled = J.Bool(args, "value", true);
                        ObjectCommands.MarkDirty(component.gameObject);
                        return new Dictionary<string, object> { { "field", field }, { "value", behaviour.enabled } };
                    }

                    Properties.Set(component, field, args.ContainsKey("value") ? args["value"] : null);
                    ObjectCommands.MarkDirty(component.gameObject);

                    var serialized = new SerializedObject(component);
                    var property = serialized.FindProperty(field);
                    return new Dictionary<string, object>
                    {
                        { "field", field },
                        { "value", property != null ? Properties.Read(property) : null }
                    };
                });

            CommandRegistry.Register("comp.remove", "移除组件", "target, type",
                delegate(Dictionary<string, object> args)
                {
                    var component = Resolve(args);
                    string typeName = component.GetType().Name;
                    Undo.DestroyObjectImmediate(component);
                    return new Dictionary<string, object> { { "removed", typeName } };
                });

            CommandRegistry.Register("comp.enabled", "启用/禁用组件", "target, type, enabled",
                delegate(Dictionary<string, object> args)
                {
                    var component = Resolve(args);
                    var behaviour = component as Behaviour;
                    if (behaviour != null)
                    {
                        Undo.RecordObject(behaviour, "Agent Bridge Enabled");
                        behaviour.enabled = J.Bool(args, "enabled", true);
                    }
                    else
                    {
                        var renderer = component as Renderer;
                        if (renderer == null) throw new CommandException(component.GetType().Name + " 不支持启用/禁用", "unsupported");
                        Undo.RecordObject(renderer, "Agent Bridge Enabled");
                        renderer.enabled = J.Bool(args, "enabled", true);
                    }
                    ObjectCommands.MarkDirty(component.gameObject);
                    return Obj.DescribeComponent(component, false);
                });

            CommandRegistry.Register("comp.call", "在组件上调用一个公开实例方法", "target, type, method, args?:[...]",
                delegate(Dictionary<string, object> args)
                {
                    var component = Resolve(args);
                    string methodName = CommandRegistry.Require(args, "method");
                    var callArgs = J.List(args, "args") ?? new List<object>();

                    var method = FindMethod(component.GetType(), methodName, callArgs.Count);
                    if (method == null)
                        throw new CommandException("在 " + component.GetType().Name + " 上找不到匹配 " + callArgs.Count + " 个参数的方法 " + methodName, "not_found");

                    var parameters = method.GetParameters();
                    var values = new object[parameters.Length];
                    for (int i = 0; i < parameters.Length; i++)
                        values[i] = Coerce(i < callArgs.Count ? callArgs[i] : null, parameters[i].ParameterType);

                    object result;
                    try
                    {
                        result = method.Invoke(component, values);
                    }
                    catch (TargetInvocationException e)
                    {
                        throw new CommandException(e.InnerException != null ? e.InnerException.Message : e.Message, "call_failed");
                    }

                    ObjectCommands.MarkDirty(component.gameObject);
                    return new Dictionary<string, object>
                    {
                        { "method", method.Name },
                        { "returns", method.ReturnType.Name },
                        { "result", JobRunner.Encode(result, 0) }
                    };
                });
        }

        internal static Component Resolve(Dictionary<string, object> args)
        {
            var go = Obj.GameObject(CommandRegistry.Require(args, "target"));
            string typeName = CommandRegistry.Require(args, "type", "例如 Rigidbody、BoxCollider");
            Type type = RequireComponentType(typeName);
            var component = go.GetComponent(type);
            if (component == null)
                throw new CommandException(go.name + " 上没有 " + type.Name + " 组件（可用 comp.list 查看）", "not_found");
            return component;
        }

        internal static Type RequireComponentType(string name)
        {
            Type type = Obj.FindType(name);
            if (type == null) throw new CommandException("找不到类型 " + name, "not_found");
            if (!typeof(Component).IsAssignableFrom(type))
                throw new CommandException(name + " 不是 Component 类型", "bad_type");
            return type;
        }

        static MethodInfo FindMethod(Type type, string name, int argumentCount)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            MethodInfo fallback = null;
            for (int i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                if (method.Name != name) continue;
                if (method.GetParameters().Length == argumentCount) return method;
                if (fallback == null) fallback = method;
            }
            return fallback;
        }

        internal static object Coerce(object value, Type target)
        {
            if (target == typeof(void)) return null;
            if (target == typeof(string)) return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

            if (value == null)
                return target.IsValueType ? Activator.CreateInstance(target) : null;

            if (target == typeof(bool)) return J.ToDouble(value, 0) != 0;
            if (target == typeof(int)) return (int)Math.Round(J.ToDouble(value, 0));
            if (target == typeof(float)) return (float)J.ToDouble(value, 0);
            if (target == typeof(double)) return J.ToDouble(value, 0);
            if (target == typeof(long)) return (long)Math.Round(J.ToDouble(value, 0));
            if (target == typeof(short)) return (short)Math.Round(J.ToDouble(value, 0));
            if (target == typeof(byte)) return (byte)Math.Max(0, Math.Round(J.ToDouble(value, 0)));

            if (target == typeof(Vector2)) { Vector3 v2 = J.ToVector3(value, Vector2.zero); return new Vector2(v2.x, v2.y); }
            if (target == typeof(Vector3)) return J.ToVector3(value, Vector3.zero);
            if (target == typeof(Vector4)) { Vector3 v = J.ToVector3(value, Vector3.zero); return new Vector4(v.x, v.y, v.z, 0); }
            if (target == typeof(Quaternion)) return J.Quat(new Dictionary<string, object> { { "v", value } }, "v", Quaternion.identity);
            if (target == typeof(Color)) return J.ToColor(value, Color.white);
            if (target == typeof(Rect)) { var list = J.AsList(value); return list != null && list.Count >= 4 ? new Rect((float)J.ToDouble(list[0], 0), (float)J.ToDouble(list[1], 0), (float)J.ToDouble(list[2], 0), (float)J.ToDouble(list[3], 0)) : new Rect(); }

            if (target.IsEnum)
            {
                string text = value as string;
                return text != null ? Enum.Parse(target, text, true) : Enum.ToObject(target, (int)Math.Round(J.ToDouble(value, 0)));
            }

            if (typeof(Object).IsAssignableFrom(target)) return Properties.ResolveObject(value);

            if (target.IsAssignableFrom(value.GetType())) return value;

            try
            {
                return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                throw new CommandException("无法把参数 " + value + " 转换成 " + target.Name, "bad_arg");
            }
        }
    }
}
