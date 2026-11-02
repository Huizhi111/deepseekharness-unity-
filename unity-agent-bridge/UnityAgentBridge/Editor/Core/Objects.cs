using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UnityAgentBridge
{
    /// <summary>Object lookup, type lookup and JSON description helpers.</summary>
    public static class Obj
    {
        // ------------------------------------------------------------ lookup

        public static List<GameObject> AllGameObjects()
        {
            var result = new List<GameObject>();
#if UNITY_2022_2_OR_NEWER
            var found = Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var found = Object.FindObjectsOfType<GameObject>(true);
#endif
            for (int i = 0; i < found.Length; i++)
            {
                var go = found[i];
                if (go == null) continue;
                if (!go.scene.IsValid()) continue; // skip prefab assets and other non-scene objects
                result.Add(go);
            }
            return result;
        }

        public static GameObject SelectedOrNull()
        {
            return Selection.activeGameObject;
        }

        public static GameObject GameObject(string target)
        {
            if (string.IsNullOrEmpty(target) || target == "selected" || target == "selection" || target == "sel")
            {
                var selected = Selection.activeGameObject;
                if (selected == null)
                    throw new CommandException("当前没有选中对象，请传入 target（#实例ID、完整路径或名称）", "no_selection");
                return selected;
            }

            if (target.StartsWith("#", StringComparison.Ordinal))
            {
                int objectId;
                if (int.TryParse(target.Substring(1), out objectId))
                {
                    Object found = EditorUtility.InstanceIDToObject(objectId);
                    var go = found as GameObject;
                    if (go == null)
                    {
                        var component = found as Component;
                        if (component != null) go = component.gameObject;
                    }
                    if (go != null) return go;
                }
                throw new CommandException("找不到实例 ID 为 " + target + " 的对象", "not_found");
            }

            var byPath = FindByPath(target);
            if (byPath != null) return byPath;

            GameObject inactiveMatch = null;
            var all = AllGameObjects();
            for (int i = 0; i < all.Count; i++)
            {
                var go = all[i];
                if (go.name != target) continue;
                if (go.activeInHierarchy) return go;
                if (inactiveMatch == null) inactiveMatch = go;
            }
            if (inactiveMatch != null) return inactiveMatch;

            throw new CommandException("找不到对象 " + target + "（支持 #实例ID、完整路径 Root/Child、名称）", "not_found");
        }

        public static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string trimmed = path.TrimStart('/');
            if (trimmed.IndexOf('/') < 0) return null;

            var found = UnityEngine.GameObject.Find(trimmed);
            if (found != null) return found;

            var all = AllGameObjects();
            for (int i = 0; i < all.Count; i++)
            {
                if (PathOf(all[i].transform) == trimmed) return all[i];
            }
            return null;
        }

        public static string PathOf(Transform transform)
        {
            if (transform == null) return "";
            var builder = new System.Text.StringBuilder(transform.name);
            var current = transform.parent;
            while (current != null)
            {
                builder.Insert(0, '/');
                builder.Insert(0, current.name);
                current = current.parent;
            }
            return builder.ToString();
        }

        public static Type FindType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            var unityAssemblies = new List<Assembly>();
            var otherAssemblies = new List<Assembly>();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                var asm = assemblies[i];
                string asmName = asm.GetName().Name;
                if (asmName.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                    asmName.StartsWith("UnityEditor", StringComparison.Ordinal))
                    unityAssemblies.Add(asm);
                else
                    otherAssemblies.Add(asm);
            }

            var ordered = new List<Assembly>(unityAssemblies);
            ordered.AddRange(otherAssemblies);

            // 1) exact full name / assembly qualified name
            for (int i = 0; i < ordered.Count; i++)
            {
                var direct = ordered[i].GetType(name, false);
                if (direct != null) return direct;
            }

            // 2) simple name, prefer Unity assemblies
            string simple = name;
            int dot = name.LastIndexOf('.');
            if (dot >= 0) simple = name.Substring(dot + 1);

            for (int i = 0; i < ordered.Count; i++)
            {
                Type[] types;
                try
                {
                    types = ordered[i].GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types;
                }
                catch (Exception)
                {
                    continue;
                }

                if (types == null) continue;
                for (int t = 0; t < types.Length; t++)
                {
                    var type = types[t];
                    if (type == null) continue;
                    if (type.Name == simple) return type;
                }
            }
            return null;
        }

        // ------------------------------------------------------------ describe

        public static Dictionary<string, object> Describe(GameObject go, bool withChildren, int depth)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["id"] = IdOf(go);
            d["name"] = go.name;
            d["path"] = PathOf(go.transform);
            d["activeSelf"] = go.activeSelf;
            d["activeInHierarchy"] = go.activeInHierarchy;
            d["tag"] = SafeTag(go);
            d["layer"] = LayerInfo(go.layer);
            d["isStatic"] = go.isStatic;
            d["transform"] = DescribeTransform(go.transform);
            d["childCount"] = go.transform.childCount;
            d["components"] = ComponentTypeNames(go);

            try
            {
                string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                if (!string.IsNullOrEmpty(prefabPath)) d["prefab"] = prefabPath;
                if (PrefabUtility.IsAnyPrefabInstanceRoot(go)) d["prefabInstance"] = true;
            }
            catch (Exception)
            {
                // PrefabUtility is unavailable for some object kinds; ignore.
            }

            if (withChildren && depth > 0)
            {
                var children = new List<object>();
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    var child = go.transform.GetChild(i).gameObject;
                    children.Add(Describe(child, depth > 1, depth - 1));
                }
                d["children"] = children;
            }
            return d;
        }

        public static Dictionary<string, object> DescribeTransform(Transform t)
        {
            return new Dictionary<string, object>
            {
                { "position", Vec(t.position) },
                { "rotation", Vec(t.eulerAngles) },
                { "scale", Vec(t.lossyScale) },
                { "localPosition", Vec(t.localPosition) },
                { "localRotation", Vec(t.localEulerAngles) },
                { "localScale", Vec(t.localScale) },
                { "parent", t.parent == null ? null : (object)t.parent.name },
                { "parentPath", t.parent == null ? null : PathOf(t.parent) }
            };
        }

        public static Dictionary<string, object> DescribeComponent(Component component, bool deep)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["id"] = IdOf(component);
            d["type"] = component.GetType().Name;
            d["fullType"] = component.GetType().FullName;
            d["gameObject"] = component.gameObject.name;
            d["gameObjectPath"] = PathOf(component.transform);

            var behaviour = component as Behaviour;
            if (behaviour != null) d["enabled"] = behaviour.enabled;
            var renderer = component as Renderer;
            if (renderer != null)
            {
                d["enabled"] = renderer.enabled;
                d["material"] = renderer.sharedMaterial != null ? renderer.sharedMaterial.name : null;
            }
            var collider = component as Collider;
            if (collider != null) d["enabled"] = collider.enabled;

            if (deep) d["properties"] = Properties.Dump(component);
            return d;
        }

        public static Dictionary<string, object> DescribeObject(Object obj)
        {
            if (obj == null) return null;
            var go = obj as GameObject;
            if (go != null) return Describe(go, false, 0);
            var component = obj as Component;
            if (component != null) return DescribeComponent(component, false);

            return new Dictionary<string, object>
            {
                { "name", obj.name },
                { "type", obj.GetType().Name },
                { "path", AssetDatabase.GetAssetPath(obj) }
            };
        }

        public static List<string> ComponentTypeNames(GameObject go)
        {
            var list = new List<string>();
            var components = go.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] == null) { list.Add("<MissingScript>"); continue; }
                list.Add(components[i].GetType().Name);
            }
            return list;
        }

        public static int IdOf(Object obj)
        {
            return obj.GetInstanceID();
        }

        public static Dictionary<string, object> LayerInfo(int layer)
        {
            string name = LayerMask.LayerToName(layer);
            return new Dictionary<string, object>
            {
                { "index", layer },
                { "name", string.IsNullOrEmpty(name) ? null : name }
            };
        }

        public static List<double> Vec(Vector3 v)
        {
            return new List<double> { Round(v.x), Round(v.y), Round(v.z) };
        }

        public static List<double> Vec(Vector2 v)
        {
            return new List<double> { Round(v.x), Round(v.y) };
        }

        public static List<double> ColorVec(Color c)
        {
            return new List<double> { Round(c.r), Round(c.g), Round(c.b), Round(c.a) };
        }

        static double Round(float value)
        {
            return Math.Round((double)value, 4);
        }

        static string SafeTag(GameObject go)
        {
            try { return go.tag; }
            catch (Exception) { return null; }
        }
    }
}
