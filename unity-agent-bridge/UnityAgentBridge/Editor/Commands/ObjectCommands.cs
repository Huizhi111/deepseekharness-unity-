using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UnityAgentBridge
{
    /// <summary>GameObject level commands.</summary>
    public static class ObjectCommands
    {
        public static void Register()
        {
            CommandRegistry.Register("hierarchy", "当前场景层级树（可指定深度）", "depth?=1, includeInactive?=true",
                delegate(Dictionary<string, object> args)
                {
                    int depth = Mathf.Clamp(J.Int(args, "depth", 1), 0, 12);
                    bool includeInactive = J.Bool(args, "includeInactive", true);
                    var scene = SceneManager.GetActiveScene();
                    var roots = scene.GetRootGameObjects();
                    var list = new List<object>();
                    for (int i = 0; i < roots.Length; i++)
                    {
                        if (!includeInactive && !roots[i].activeInHierarchy) continue;
                        list.Add(Obj.Describe(roots[i], depth > 0, depth));
                    }
                    return new Dictionary<string, object>
                    {
                        { "scene", scene.name },
                        { "count", list.Count },
                        { "roots", list }
                    };
                });

            CommandRegistry.Register("go.create", "创建对象。type: empty|cube|sphere|capsule|cylinder|plane|quad|camera|light", "type, name?, parent?, position?, rotation?, scale?, tag?, layer?, lightType?, color?, intensity?",
                delegate(Dictionary<string, object> args)
                {
                    string type = (J.Str(args, "type", "empty") ?? "empty").ToLowerInvariant();
                    string name = J.Str(args, "name");

                    GameObject go = Create(type, name, args);

                    Undo.RegisterCreatedObjectUndo(go, "Agent Bridge Create " + type);

                    var parentName = J.Str(args, "parent");
                    if (!string.IsNullOrEmpty(parentName) && parentName != "none")
                    {
                        var parent = Obj.GameObject(parentName);
                        go.transform.SetParent(parent.transform, false);
                    }

                    if (J.HasValue(args, "position")) go.transform.position = J.Vec3(args, "position", go.transform.position);
                    if (J.HasValue(args, "localPosition")) go.transform.localPosition = J.Vec3(args, "localPosition", go.transform.localPosition);
                    if (J.HasValue(args, "rotation")) go.transform.eulerAngles = J.Vec3(args, "rotation", go.transform.eulerAngles);
                    if (J.HasValue(args, "scale")) go.transform.localScale = J.Vec3(args, "scale", go.transform.localScale);

                    string tag = J.Str(args, "tag");
                    if (!string.IsNullOrEmpty(tag)) SafeSetTag(go, tag);

                    if (J.HasValue(args, "layer")) go.layer = J.Int(args, "layer", go.layer);

                    MarkDirty(go);
                    return Obj.Describe(go, false, 0);
                });

            CommandRegistry.Register("go.get", "读取对象详情（变换、组件、预制体来源），includeChildren 可带子树", "target, includeChildren?=false, depth?=1",
                delegate(Dictionary<string, object> args)
                {
                    var go = Obj.GameObject(CommandRegistry.Require(args, "target", "#id / Root/Child / Name"));
                    bool children = J.Bool(args, "includeChildren", false);
                    int depth = Mathf.Clamp(J.Int(args, "depth", 1), 0, 12);
                    return Obj.Describe(go, children, depth);
                });

            CommandRegistry.Register("go.find", "按名称/标签/组件/路径查找对象", "name?, tag?, component?, path?, limit?=50",
                delegate(Dictionary<string, object> args)
                {
                    string name = J.Str(args, "name");
                    string tag = J.Str(args, "tag");
                    string component = J.Str(args, "component");
                    string path = J.Str(args, "path");
                    int limit = Mathf.Clamp(J.Int(args, "limit", 50), 1, 1000);

                    Type componentType = null;
                    if (!string.IsNullOrEmpty(component))
                    {
                        componentType = Obj.FindType(component);
                        if (componentType == null) throw new CommandException("找不到组件类型 " + component, "not_found");
                    }

                    var results = new List<object>();
                    var all = Obj.AllGameObjects();
                    for (int i = 0; i < all.Count && results.Count < limit; i++)
                    {
                        var go = all[i];
                        if (name != null && go.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (tag != null && SafeGetTag(go) != tag) continue;
                        if (path != null && Obj.PathOf(go.transform).IndexOf(path, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (componentType != null && go.GetComponent(componentType) == null) continue;
                        results.Add(Obj.Describe(go, false, 0));
                    }

                    return new Dictionary<string, object> { { "count", results.Count }, { "objects", results } };
                });

            CommandRegistry.Register("go.set", "修改对象：名称/激活/标签/层/父级/位置/旋转/缩放", "target, name?, active?, tag?, layer?, parent?, position?, localPosition?, rotation?, localRotation?, scale?, localScale?, static?",
                delegate(Dictionary<string, object> args)
                {
                    var go = Obj.GameObject(CommandRegistry.Require(args, "target", "#id / Root/Child / Name"));
                    var transform = go.transform;
                    var changed = new List<string>();

                    Undo.RecordObject(go, "Agent Bridge Set");
                    Undo.RecordObject(transform, "Agent Bridge Set");

                    string name = J.Str(args, "name");
                    if (name != null) { go.name = name; changed.Add("name"); }

                    if (J.HasValue(args, "active")) { go.SetActive(J.Bool(args, "active", true)); changed.Add("active"); }

                    string tag = J.Str(args, "tag");
                    if (!string.IsNullOrEmpty(tag)) { SafeSetTag(go, tag); changed.Add("tag"); }

                    if (J.HasValue(args, "layer")) { go.layer = J.Int(args, "layer", go.layer); changed.Add("layer"); }
                    if (J.HasValue(args, "static")) { go.isStatic = J.Bool(args, "static", false); changed.Add("static"); }

                    if (J.HasValue(args, "parent"))
                    {
                        string parentName = J.Str(args, "parent");
                        Transform parent = (string.IsNullOrEmpty(parentName) || parentName == "none" || parentName == "null")
                            ? null
                            : Obj.GameObject(parentName).transform;
                        Undo.SetTransformParent(transform, parent, "Agent Bridge Parent");
                        changed.Add("parent");
                    }

                    if (J.HasValue(args, "position")) { transform.position = J.Vec3(args, "position", transform.position); changed.Add("position"); }
                    if (J.HasValue(args, "localPosition")) { transform.localPosition = J.Vec3(args, "localPosition", transform.localPosition); changed.Add("localPosition"); }
                    if (J.HasValue(args, "rotation")) { transform.eulerAngles = J.Vec3(args, "rotation", transform.eulerAngles); changed.Add("rotation"); }
                    if (J.HasValue(args, "localRotation")) { transform.localEulerAngles = J.Vec3(args, "localRotation", transform.localEulerAngles); changed.Add("localRotation"); }
                    if (J.HasValue(args, "scale")) { transform.localScale = J.Vec3(args, "scale", transform.localScale); changed.Add("scale"); }
                    if (J.HasValue(args, "localScale")) { transform.localScale = J.Vec3(args, "localScale", transform.localScale); changed.Add("localScale"); }

                    MarkDirty(go);
                    return new Dictionary<string, object>
                    {
                        { "changed", changed },
                        { "object", Obj.Describe(go, false, 0) }
                    };
                });

            CommandRegistry.Register("go.delete", "删除对象（可撤销）", "target",
                delegate(Dictionary<string, object> args)
                {
                    var go = Obj.GameObject(CommandRegistry.Require(args, "target"));
                    string name = go.name;
                    string path = Obj.PathOf(go.transform);
                    Undo.DestroyObjectImmediate(go);
                    return new Dictionary<string, object> { { "deleted", name }, { "path", path } };
                });

            CommandRegistry.Register("go.duplicate", "复制对象（保留父级）", "target, name?, offset?",
                delegate(Dictionary<string, object> args)
                {
                    var go = Obj.GameObject(CommandRegistry.Require(args, "target"));
                    var copy = Object.Instantiate(go, go.transform.parent);
                    copy.name = J.Str(args, "name", go.name + " (1)");
                    Undo.RegisterCreatedObjectUndo(copy, "Agent Bridge Duplicate");

                    if (J.HasValue(args, "offset"))
                        copy.transform.position = copy.transform.position + J.Vec3(args, "offset", Vector3.zero);

                    MarkDirty(copy);
                    return Obj.Describe(copy, false, 0);
                });

            CommandRegistry.Register("go.select", "在编辑器中选中对象（可用 \"none\" 取消选中）", "target?, add?=false",
                delegate(Dictionary<string, object> args)
                {
                    string target = J.Str(args, "target");
                    if (string.IsNullOrEmpty(target) || target == "none" || target == "null")
                    {
                        Selection.activeGameObject = null;
                        return new Dictionary<string, object> { { "selected", new List<object>() } };
                    }

                    var go = Obj.GameObject(target);
                    if (J.Bool(args, "add", false))
                    {
                        var current = new List<Object>(Selection.objects);
                        if (!current.Contains(go)) current.Add(go);
                        Selection.objects = current.ToArray();
                    }
                    else
                    {
                        Selection.activeGameObject = go;
                    }
                    EditorGUIUtility.PingObject(go);

                    return new Dictionary<string, object> { { "selected", Obj.Describe(go, false, 0) } };
                });

            CommandRegistry.Register("transform.lookAt", "让对象朝向某个世界坐标点", "target, point",
                delegate(Dictionary<string, object> args)
                {
                    var go = Obj.GameObject(CommandRegistry.Require(args, "target"));
                    var point = J.Vec3(args, "point", go.transform.position + Vector3.forward);
                    Undo.RecordObject(go.transform, "Agent Bridge LookAt");
                    go.transform.LookAt(point);
                    MarkDirty(go);
                    return Obj.DescribeTransform(go.transform);
                });
        }

        static GameObject Create(string type, string name, Dictionary<string, object> args)
        {
            switch (type)
            {
                case "empty":
                case "gameobject":
                    return new GameObject(string.IsNullOrEmpty(name) ? "GameObject" : name);

                case "cube":
                    return Primitive(PrimitiveType.Cube, name, "Cube");
                case "sphere":
                    return Primitive(PrimitiveType.Sphere, name, "Sphere");
                case "capsule":
                    return Primitive(PrimitiveType.Capsule, name, "Capsule");
                case "cylinder":
                    return Primitive(PrimitiveType.Cylinder, name, "Cylinder");
                case "plane":
                    return Primitive(PrimitiveType.Plane, name, "Plane");
                case "quad":
                    return Primitive(PrimitiveType.Quad, name, "Quad");

                case "camera":
                    {
                        var go = new GameObject(string.IsNullOrEmpty(name) ? "Main Camera" : name);
                        var camera = go.AddComponent<Camera>();
                        camera.tag = "MainCamera";
                        go.transform.position = new Vector3(0, 1, -10);
                        if (Object.FindObjectOfType<AudioListener>() == null) go.AddComponent<AudioListener>();
                        return go;
                    }

                case "light":
                    {
                        var go = new GameObject(string.IsNullOrEmpty(name) ? "Directional Light" : name);
                        var light = go.AddComponent<Light>();
                        string lightType = J.Str(args, "lightType", "directional").ToLowerInvariant();
                        light.type = lightType == "point" ? LightType.Point
                            : lightType == "spot" ? LightType.Spot
                            : LightType.Directional;
                        light.color = J.Col(args, "color", Color.white);
                        light.intensity = J.Float(args, "intensity", 1f);
                        go.transform.rotation = Quaternion.Euler(50, -30, 0);
                        return go;
                    }

                default:
                    throw new CommandException("不支持的 type: " + type + "（empty|cube|sphere|capsule|cylinder|plane|quad|camera|light）", "bad_request");
            }
        }

        static GameObject Primitive(PrimitiveType primitive, string name, string fallbackName)
        {
            var go = GameObject.CreatePrimitive(primitive);
            go.name = string.IsNullOrEmpty(name) ? fallbackName : name;
            return go;
        }

        internal static void MarkDirty(GameObject go)
        {
            if (go == null) return;
            EditorUtility.SetDirty(go);
            try
            {
                if (go.scene.IsValid()) EditorSceneManager.MarkSceneDirty(go.scene);
            }
            catch (Exception)
            {
                // ignored
            }
        }

        internal static void SafeSetTag(GameObject go, string tag)
        {
            try
            {
                go.tag = tag;
            }
            catch (Exception)
            {
                throw new CommandException("标签 " + tag + " 未在 Tag Manager 中定义", "bad_tag");
            }
        }

        internal static string SafeGetTag(GameObject go)
        {
            try { return go.tag; }
            catch (Exception) { return null; }
        }
    }
}
