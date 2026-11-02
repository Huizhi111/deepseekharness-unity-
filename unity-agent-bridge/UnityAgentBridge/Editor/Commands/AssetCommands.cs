using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityAgentBridge
{
    /// <summary>Asset database commands: find, create material / prefab / script, refresh.</summary>
    public static class AssetCommands
    {
        public static void Register()
        {
            CommandRegistry.Register("asset.find", "搜索资源（AssetDatabase.FindAssets）", "filter?, type?, folder?=Assets, limit?=50",
                delegate(Dictionary<string, object> args)
                {
                    string filter = J.Str(args, "filter", "");
                    string type = J.Str(args, "type");
                    string folder = J.Str(args, "folder", "Assets");
                    int limit = Mathf.Clamp(J.Int(args, "limit", 50), 1, 2000);

                    string query = filter ?? "";
                    if (!string.IsNullOrEmpty(type)) query = (query + " t:" + type).Trim();

                    string[] guids = AssetDatabase.FindAssets(query, new[] { folder });
                    var list = new List<object>();
                    for (int i = 0; i < guids.Length && list.Count < limit; i++)
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                        list.Add(new Dictionary<string, object>
                        {
                            { "path", path },
                            { "name", Path.GetFileNameWithoutExtension(path) },
                            { "extension", Path.GetExtension(path) },
                            { "guid", guids[i] }
                        });
                    }
                    return new Dictionary<string, object> { { "count", list.Count }, { "assets", list } };
                });

            CommandRegistry.Register("asset.info", "读取单个资源信息", "path",
                delegate(Dictionary<string, object> args)
                {
                    string path = CommandRegistry.Require(args, "path", "Assets/...");
                    var asset = AssetDatabase.LoadAssetAtPath<Object>(path);
                    if (asset == null) throw new CommandException("资源不存在: " + path, "not_found");

                    return new Dictionary<string, object>
                    {
                        { "path", path },
                        { "name", asset.name },
                        { "type", asset.GetType().Name },
                        { "guid", AssetDatabase.AssetPathToGUID(path) },
                        { "dependencies", AssetDatabase.GetDependencies(path, false).Length }
                    };
                });

            CommandRegistry.Register("asset.createMaterial", "创建材质并设置颜色/金属度/光滑度", "path, color?=#FFFFFF, shader?, metallic?=0, smoothness?=0.5",
                delegate(Dictionary<string, object> args)
                {
                    string path = CommandRegistry.Require(args, "path", "Assets/Materials/Foo.mat");
                    if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) path += ".mat";
                    SceneCommands.EnsureAssetFolder(Path.GetDirectoryName(path).Replace('\\', '/'));

                    Shader shader = ResolveShader(J.Str(args, "shader"));
                    var material = new Material(shader);
                    var color = J.Col(args, "color", Color.white);
                    if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                    if (material.HasProperty("_Color")) material.SetColor("_Color", color);
                    if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", J.Float(args, "metallic", 0f));
                    if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", J.Float(args, "smoothness", 0.5f));
                    if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", J.Float(args, "smoothness", 0.5f));

                    AssetDatabase.CreateAsset(material, path);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();

                    return new Dictionary<string, object>
                    {
                        { "path", path },
                        { "shader", shader.name },
                        { "color", Obj.ColorVec(color) }
                    };
                });

            CommandRegistry.Register("asset.setMaterialProperty", "设置材质属性（_Color / _Metallic / _MainTex ...）", "path, property, value",
                delegate(Dictionary<string, object> args)
                {
                    string path = CommandRegistry.Require(args, "path", "Assets/Materials/Foo.mat");
                    var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (material == null) throw new CommandException("材质不存在: " + path, "not_found");

                    string property = CommandRegistry.Require(args, "property", "例如 _Color、_Metallic");
                    if (!material.HasProperty(property)) throw new CommandException("材质上没有属性 " + property, "not_found");

                    object value = args.ContainsKey("value") ? args["value"] : null;
                    Undo.RecordObject(material, "Agent Bridge Material");
                    ApplyMaterialValue(material, property, value);
                    EditorUtility.SetDirty(material);
                    AssetDatabase.SaveAssets();

                    return new Dictionary<string, object> { { "path", path }, { "property", property } };
                });

            CommandRegistry.Register("asset.createPrefab", "把场景对象保存为预制体", "target, path",
                delegate(Dictionary<string, object> args)
                {
                    var go = Obj.GameObject(CommandRegistry.Require(args, "target"));
                    string path = CommandRegistry.Require(args, "path", "Assets/Prefabs/Foo.prefab");
                    if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) path += ".prefab";
                    SceneCommands.EnsureAssetFolder(Path.GetDirectoryName(path).Replace('\\', '/'));

                    var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
                    if (prefab == null) throw new CommandException("保存预制体失败: " + path, "prefab_failed");
                    AssetDatabase.SaveAssets();

                    return new Dictionary<string, object> { { "path", path }, { "name", prefab.name } };
                });

            CommandRegistry.Register("asset.instantiatePrefab", "把预制体实例化到场景（保留预制体连接）", "path, parent?, position?",
                delegate(Dictionary<string, object> args)
                {
                    string path = CommandRegistry.Require(args, "path", "Assets/Prefabs/Foo.prefab");
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab == null) throw new CommandException("预制体不存在: " + path, "not_found");

                    var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                    if (instance == null) throw new CommandException("实例化预制体失败", "prefab_failed");

                    Undo.RegisterCreatedObjectUndo(instance, "Agent Bridge Instantiate Prefab");

                    string parentName = J.Str(args, "parent");
                    if (!string.IsNullOrEmpty(parentName) && parentName != "none")
                        instance.transform.SetParent(Obj.GameObject(parentName).transform, false);

                    if (J.HasValue(args, "position"))
                        instance.transform.position = J.Vec3(args, "position", instance.transform.position);

                    ObjectCommands.MarkDirty(instance);
                    return Obj.Describe(instance, false, 0);
                });

            CommandRegistry.Register("asset.createScript", "写入一个 C# 脚本资源并刷新资源库", "path, content",
                delegate(Dictionary<string, object> args)
                {
                    string path = CommandRegistry.Require(args, "path", "Assets/Scripts/Foo.cs");
                    if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) path += ".cs";
                    string content = CommandRegistry.Require(args, "content", "完整的 C# 源码");

                    SceneCommands.EnsureAssetFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
                    string full = Path.Combine(BridgePaths.ProjectRoot, path.Replace('/', Path.DirectorySeparatorChar));
                    File.WriteAllText(full, content);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    AssetDatabase.Refresh();

                    return new Dictionary<string, object> { { "path", path }, { "bytes", content.Length } };
                });

            CommandRegistry.Register("asset.refresh", "刷新资源库（重新导入已修改文件）", "force?=false",
                delegate(Dictionary<string, object> args)
                {
                    AssetDatabase.Refresh(J.Bool(args, "force", false) ? ImportAssetOptions.ForceUpdate : ImportAssetOptions.Default);
                    return new Dictionary<string, object> { { "refreshed", true } };
                });

            CommandRegistry.Register("asset.delete", "删除资源", "path",
                delegate(Dictionary<string, object> args)
                {
                    string path = CommandRegistry.Require(args, "path");
                    bool ok = AssetDatabase.DeleteAsset(path);
                    if (!ok) throw new CommandException("删除失败: " + path, "delete_failed");
                    return new Dictionary<string, object> { { "deleted", path } };
                });

            CommandRegistry.Register("asset.move", "移动/重命名资源", "from, to",
                delegate(Dictionary<string, object> args)
                {
                    string from = CommandRegistry.Require(args, "from");
                    string to = CommandRegistry.Require(args, "to");
                    string error = AssetDatabase.MoveAsset(from, to);
                    if (!string.IsNullOrEmpty(error)) throw new CommandException(error, "move_failed");
                    return new Dictionary<string, object> { { "from", from }, { "to", to } };
                });

            CommandRegistry.Register("asset.folders", "列出指定目录下的子文件夹与文件数量", "path?=Assets",
                delegate(Dictionary<string, object> args)
                {
                    string path = J.Str(args, "path", "Assets");
                    if (!AssetDatabase.IsValidFolder(path)) throw new CommandException("不是有效目录: " + path, "not_found");

                    var folders = new List<object>();
                    foreach (string guid in AssetDatabase.FindAssets("t:Folder", new[] { path }))
                    {
                        string folder = AssetDatabase.GUIDToAssetPath(guid);
                        if (folder == path) continue;
                        if (folder.LastIndexOf('/') != path.Length) continue; // direct children only
                        folders.Add(folder);
                    }
                    var files = AssetDatabase.FindAssets("", new[] { path });
                    return new Dictionary<string, object>
                    {
                        { "path", path },
                        { "folders", folders },
                        { "fileCount", files.Length }
                    };
                });
        }

        static Shader ResolveShader(string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                var explicitShader = Shader.Find(name);
                if (explicitShader == null) throw new CommandException("找不到着色器 " + name, "not_found");
                return explicitShader;
            }

            string[] candidates = { "Universal Render Pipeline/Lit", "Standard", "HDRP/Lit", "Sprites/Default" };
            for (int i = 0; i < candidates.Length; i++)
            {
                var shader = Shader.Find(candidates[i]);
                if (shader != null) return shader;
            }
            throw new CommandException("找不到可用的默认着色器", "not_found");
        }

        static void ApplyMaterialValue(Material material, string property, object value)
        {
            var dict = J.AsDict(value);
            if (dict != null)
            {
                if (dict.ContainsKey("x") && dict.ContainsKey("y") && dict.ContainsKey("z"))
                {
                    material.SetVector(property, new Vector4(
                        (float)J.Num(dict, "x", 0),
                        (float)J.Num(dict, "y", 0),
                        (float)J.Num(dict, "z", 0),
                        (float)J.Num(dict, "w", 0)));
                    return;
                }
                if (dict.ContainsKey("r") || dict.ContainsKey("g"))
                {
                    material.SetColor(property, J.ToColor(value, Color.white));
                    return;
                }
            }

            var list = J.AsList(value);
            if (list != null && list.Count >= 3)
            {
                material.SetColor(property, J.ToColor(value, Color.white));
                return;
            }

            string text = value as string;
            if (text != null)
            {
                Color color;
                if (ColorUtility.TryParseHtmlString(text, out color)) { material.SetColor(property, color); return; }
                if (text.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    var texture = AssetDatabase.LoadAssetAtPath<Texture>(text);
                    if (texture == null) throw new CommandException("贴图不存在: " + text, "not_found");
                    material.SetTexture(property, texture);
                    return;
                }
                float number;
                if (float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number))
                {
                    material.SetFloat(property, number);
                    return;
                }
                material.SetFloat(property, text == "true" ? 1f : 0f);
                return;
            }

            var shader = material.shader;
            int index = shader != null ? shader.FindPropertyIndex(property) : -1;
            if (index >= 0 && shader.GetPropertyType(index) == UnityEngine.Rendering.ShaderPropertyType.Color)
                material.SetColor(property, J.ToColor(value, Color.white));
            else if (index >= 0 && shader.GetPropertyType(index) == UnityEngine.Rendering.ShaderPropertyType.Vector)
                material.SetVector(property, J.ToVector3(value, Vector3.zero));
            else
                material.SetFloat(property, (float)J.ToDouble(value, 0));
        }
    }
}
