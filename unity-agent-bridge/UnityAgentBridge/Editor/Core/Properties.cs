using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityAgentBridge
{
    /// <summary>Reads and writes serialized properties of any UnityEngine.Object.</summary>
    public static class Properties
    {
        const int MaxProperties = 500;

        public static List<object> Dump(Object target, int maxDepth = 2)
        {
            var list = new List<object>();
            if (target == null) return list;

            var serialized = new SerializedObject(target);
            SerializedProperty iterator = serialized.GetIterator();
            bool enterChildren = true;
            int guard = 0;

            while (iterator.NextVisible(enterChildren) && guard++ < MaxProperties)
            {
                enterChildren = iterator.propertyType == SerializedPropertyType.Generic && iterator.depth < maxDepth;

                var d = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "path", iterator.propertyPath },
                    { "name", iterator.displayName },
                    { "type", iterator.propertyType.ToString() }
                };

                if (iterator.isArray)
                {
                    d["arraySize"] = iterator.arraySize;
                }
                else
                {
                    object value = Read(iterator);
                    if (value != null) d["value"] = value;
                }

                if (iterator.depth > maxDepth) continue;
                list.Add(d);
            }
            return list;
        }

        public static object Read(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return property.longValue >= int.MinValue && property.longValue <= int.MaxValue
                        ? (object)(int)property.longValue
                        : (object)property.longValue;
                case SerializedPropertyType.LayerMask:
                    return Obj.LayerInfo(property.intValue);
                case SerializedPropertyType.Enum:
                    {
                        var names = property.enumNames;
                        var display = property.enumDisplayNames;
                        int index = property.enumValueIndex;
                        string name = names != null && index >= 0 && index < names.Length ? names[index] : null;
                        string label = display != null && index >= 0 && index < display.Length ? display[index] : null;
                        return new Dictionary<string, object>
                        {
                            { "name", name },
                            { "label", label },
                            { "index", index }
                        };
                    }
                case SerializedPropertyType.Boolean:
                    return property.boolValue;
                case SerializedPropertyType.Float:
                    return (double)property.floatValue;
                case SerializedPropertyType.String:
                    return property.stringValue;
                case SerializedPropertyType.Color:
                    return Obj.ColorVec(property.colorValue);
                case SerializedPropertyType.ObjectReference:
                    {
                        Object reference = property.objectReferenceValue;
                        if (reference == null) return null;
                        return new Dictionary<string, object>
                        {
                            { "name", reference.name },
                            { "type", reference.GetType().Name },
                            { "assetPath", AssetDatabase.GetAssetPath(reference) },
                            { "id", Obj.IdOf(reference) }
                        };
                    }
                case SerializedPropertyType.Vector2:
                    return Obj.Vec(property.vector2Value);
                case SerializedPropertyType.Vector3:
                    return Obj.Vec(property.vector3Value);
                case SerializedPropertyType.Vector4:
                    {
                        Vector4 v = property.vector4Value;
                        return new List<double> { v.x, v.y, v.z, v.w };
                    }
                case SerializedPropertyType.Rect:
                    {
                        Rect r = property.rectValue;
                        return new Dictionary<string, object>
                        {
                            { "x", r.x }, { "y", r.y }, { "width", r.width }, { "height", r.height }
                        };
                    }
                case SerializedPropertyType.Bounds:
                    {
                        Bounds b = property.boundsValue;
                        return new Dictionary<string, object>
                        {
                            { "center", Obj.Vec(b.center) },
                            { "size", Obj.Vec(b.size) }
                        };
                    }
                case SerializedPropertyType.Quaternion:
                    return Obj.Vec(property.quaternionValue.eulerAngles);
                case SerializedPropertyType.Character:
                    return ((char)property.intValue).ToString();
                case SerializedPropertyType.Generic:
                    return null;
                default:
                    return null;
            }
        }

        public static bool Set(Object target, string path, object value)
        {
            if (target == null) throw new CommandException("目标对象为空", "bad_request");

            var serialized = new SerializedObject(target);
            serialized.Update();
            SerializedProperty property = serialized.FindProperty(path);
            if (property == null)
                throw new CommandException("找不到属性 " + path + "（用 comp.get 查看可写属性路径）", "not_found");

            Undo.RecordObject(target, "Agent Bridge: " + path);
            Apply(property, value);
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
            return true;
        }

        public static void Apply(SerializedProperty property, object value)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    property.longValue = (long)Math.Round(J.ToDouble(value, property.longValue));
                    break;
                case SerializedPropertyType.LayerMask:
                    property.intValue = ToLayer(value, property.intValue);
                    break;
                case SerializedPropertyType.Enum:
                    property.enumValueIndex = ToEnumIndex(property, value);
                    break;
                case SerializedPropertyType.Boolean:
                    property.boolValue = J.ToDouble(value, property.boolValue ? 1 : 0) != 0;
                    break;
                case SerializedPropertyType.Float:
                    property.floatValue = (float)J.ToDouble(value, property.floatValue);
                    break;
                case SerializedPropertyType.String:
                    property.stringValue = J.Str(new Dictionary<string, object> { { "v", value } }, "v", property.stringValue);
                    break;
                case SerializedPropertyType.Color:
                    property.colorValue = J.ToColor(value, property.colorValue);
                    break;
                case SerializedPropertyType.Vector2:
                    property.vector2Value = J.Vec2(new Dictionary<string, object> { { "v", value } }, "v", property.vector2Value);
                    break;
                case SerializedPropertyType.Vector3:
                    property.vector3Value = J.Vec3(new Dictionary<string, object> { { "v", value } }, "v", property.vector3Value);
                    break;
                case SerializedPropertyType.Vector4:
                    {
                        Vector3 v = J.ToVector3(value, property.vector4Value);
                        property.vector4Value = new Vector4(v.x, v.y, v.z, property.vector4Value.w);
                        break;
                    }
                case SerializedPropertyType.Quaternion:
                    property.quaternionValue = J.Quat(new Dictionary<string, object> { { "v", value } }, "v", property.quaternionValue);
                    break;
                case SerializedPropertyType.Rect:
                    {
                        var values = J.AsDict(value);
                        if (values == null && J.AsList(value) != null)
                        {
                            var list = J.AsList(value);
                            property.rectValue = new Rect(
                                (float)J.ToDouble(list[0], 0), (float)J.ToDouble(list[1], 0),
                                (float)J.ToDouble(list[2], 0), (float)J.ToDouble(list[3], 0));
                        }
                        else if (values != null)
                        {
                            property.rectValue = new Rect(
                                (float)J.Num(values, "x", 0), (float)J.Num(values, "y", 0),
                                (float)J.Num(values, "width", 0), (float)J.Num(values, "height", 0));
                        }
                        break;
                    }
                case SerializedPropertyType.Bounds:
                    {
                        var bounds = J.AsDict(value);
                        if (bounds != null)
                        {
                            var current = property.boundsValue;
                            property.boundsValue = new Bounds(
                                J.Vec3(bounds, "center", current.center),
                                J.Vec3(bounds, "size", current.size));
                        }
                        break;
                    }
                case SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = ResolveObject(value);
                    break;
                case SerializedPropertyType.ArraySize:
                    property.arraySize = (int)Math.Round(J.ToDouble(value, property.arraySize));
                    break;
                default:
                    throw new CommandException("不支持写入的属性类型 " + property.propertyType, "unsupported");
            }
        }

        /// <summary>Resolves "#id", "Assets/...", a scene object path, or a bare object name.</summary>
        public static Object ResolveObject(object value)
        {
            if (value == null) return null;

            var dict = J.AsDict(value);
            if (dict != null)
            {
                string dictPath = J.Str(dict, "assetPath");
                if (!string.IsNullOrEmpty(dictPath)) return AssetDatabase.LoadAssetAtPath<Object>(dictPath);
                value = J.Str(dict, "name", J.Str(dict, "path"));
            }

            string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(text) || text == "null" || text == "none") return null;

            if (text.StartsWith("Assets/", StringComparison.Ordinal) || text.Contains("/Resources/") ||
                text.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var asset = AssetDatabase.LoadAssetAtPath<Object>(text);
                if (asset != null) return asset;
                throw new CommandException("找不到资源 " + text, "not_found");
            }

            if (text.StartsWith("#", StringComparison.Ordinal))
            {
                int id;
                if (int.TryParse(text.Substring(1), out id)) return EditorUtility.InstanceIDToObject(id);
            }

            try
            {
                return Obj.GameObject(text);
            }
            catch (CommandException)
            {
                var asset = AssetDatabase.LoadAssetAtPath<Object>(text);
                if (asset != null) return asset;
                throw new CommandException("无法把 " + text + " 解析为对象引用（需要 Assets/ 路径、#实例ID 或场景对象名）", "not_found");
            }
        }

        static int ToLayer(object value, int fallback)
        {
            string name = value as string;
            if (!string.IsNullOrEmpty(name))
            {
                int layer = LayerMask.NameToLayer(name);
                if (layer >= 0) return layer;
            }
            return (int)Math.Round(J.ToDouble(value, fallback));
        }

        static int ToEnumIndex(SerializedProperty property, object value)
        {
            var names = property.enumNames;
            string text = value as string;
            if (names != null && !string.IsNullOrEmpty(text))
            {
                for (int i = 0; i < names.Length; i++)
                {
                    if (string.Equals(names[i], text, StringComparison.OrdinalIgnoreCase)) return i;
                }
                var display = property.enumDisplayNames;
                if (display != null)
                {
                    for (int i = 0; i < display.Length; i++)
                    {
                        if (string.Equals(display[i], text, StringComparison.OrdinalIgnoreCase)) return i;
                    }
                }
                throw new CommandException("枚举值 " + text + " 不存在，可用值: " + string.Join(", ", names), "bad_value");
            }

            int index = (int)Math.Round(J.ToDouble(value, property.enumValueIndex));
            if (names != null && (index < 0 || index >= names.Length))
                throw new CommandException("枚举索引越界: " + index, "bad_value");
            return index;
        }
    }
}
