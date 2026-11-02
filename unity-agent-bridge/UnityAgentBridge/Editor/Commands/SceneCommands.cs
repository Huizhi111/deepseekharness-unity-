using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityAgentBridge
{
    /// <summary>Scene level commands.</summary>
    public static class SceneCommands
    {
        public static void Register()
        {
            CommandRegistry.Register("scene.info", "当前场景名、路径、是否脏、根对象数量", "",
                delegate(Dictionary<string, object> args) { return Info(); });

            CommandRegistry.Register("scene.new", "新建场景（默认带相机+灯光），可选保存到 Assets/Scenes/<name>.unity", "name?, save?, empty?",
                delegate(Dictionary<string, object> args)
                {
                    RequireEditMode("新建场景");
                    bool empty = J.Bool(args, "empty", false);
                    var scene = EditorSceneManager.NewScene(
                        empty ? NewSceneSetup.EmptyScene : NewSceneSetup.DefaultGameObjects,
                        NewSceneMode.Single);

                    string name = J.Str(args, "name");
                    string savedPath = null;
                    if (!string.IsNullOrEmpty(name))
                    {
                        scene.name = name;
                        if (J.Bool(args, "save", true))
                        {
                            savedPath = "Assets/Scenes/" + Sanitize(name) + ".unity";
                            EnsureAssetFolder("Assets/Scenes");
                            EditorSceneManager.SaveScene(scene, savedPath);
                            AssetDatabase.Refresh();
                        }
                    }

                    return new Dictionary<string, object>
                    {
                        { "scene", scene.name },
                        { "path", string.IsNullOrEmpty(savedPath) ? scene.path : savedPath },
                        { "rootCount", scene.rootCount }
                    };
                });

            CommandRegistry.Register("scene.open", "打开场景（默认先自动保存当前已修改场景）", "path, saveFirst?",
                delegate(Dictionary<string, object> args)
                {
                    RequireEditMode("打开场景");
                    string path = CommandRegistry.Require(args, "path", "Assets/Scenes/Foo.unity");
                    if (!File.Exists(Path.Combine(BridgePaths.ProjectRoot, path.Replace('/', Path.DirectorySeparatorChar))))
                    {
                        var asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
                        if (asset == null) throw new CommandException("场景不存在: " + path, "not_found");
                    }
                    if (J.Bool(args, "saveFirst", true)) EditorSceneManager.SaveOpenScenes();

                    var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                    Selection.activeGameObject = null;
                    return new Dictionary<string, object> { { "scene", scene.name }, { "path", scene.path }, { "rootCount", scene.rootCount } };
                });

            CommandRegistry.Register("scene.save", "保存当前所有已打开场景", "",
                delegate(Dictionary<string, object> args)
                {
                    var active = SceneManager.GetActiveScene();
                    bool ok = string.IsNullOrEmpty(active.path)
                        ? EditorSceneManager.SaveScene(active, "Assets/Scenes/" + Sanitize(active.name) + ".unity")
                        : EditorSceneManager.SaveScene(active);
                    AssetDatabase.SaveAssets();
                    return new Dictionary<string, object> { { "saved", ok }, { "path", SceneManager.GetActiveScene().path } };
                });

            CommandRegistry.Register("scene.saveAs", "把当前场景另存为指定路径", "path",
                delegate(Dictionary<string, object> args)
                {
                    string path = CommandRegistry.Require(args, "path", "Assets/Scenes/Foo.unity");
                    EnsureAssetFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
                    bool ok = EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), path);
                    AssetDatabase.Refresh();
                    return new Dictionary<string, object> { { "saved", ok }, { "path", path } };
                });

            CommandRegistry.Register("scene.list", "列出工程内所有场景及是否在 Build Settings 中", "",
                delegate(Dictionary<string, object> args)
                {
                    var buildPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var buildScenes = EditorBuildSettings.scenes;
                    for (int i = 0; i < buildScenes.Length; i++) buildPaths.Add(buildScenes[i].path);

                    var list = new List<object>();
                    string[] guids = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" });
                    for (int i = 0; i < guids.Length; i++)
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                        list.Add(new Dictionary<string, object>
                        {
                            { "path", path },
                            { "name", Path.GetFileNameWithoutExtension(path) },
                            { "inBuild", buildPaths.Contains(path) }
                        });
                    }
                    return list;
                });

            CommandRegistry.Register("scene.buildSettings", "读取或设置 Build Settings 场景列表", "scenes?:[path], enabled?",
                delegate(Dictionary<string, object> args)
                {
                    var scenes = J.Strings(args, "scenes");
                    if (scenes != null)
                    {
                        var entries = new List<EditorBuildSettingsScene>();
                        for (int i = 0; i < scenes.Length; i++)
                            entries.Add(new EditorBuildSettingsScene(scenes[i], true));
                        EditorBuildSettings.scenes = entries.ToArray();
                    }

                    var result = new List<object>();
                    var current = EditorBuildSettings.scenes;
                    for (int i = 0; i < current.Length; i++)
                        result.Add(new Dictionary<string, object> { { "path", current[i].path }, { "enabled", current[i].enabled } });
                    return result;
                });

            CommandRegistry.Register("scene.dirty", "列出已修改未保存的场景", "",
                delegate(Dictionary<string, object> args)
                {
                    var list = new List<object>();
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                    {
                        var scene = SceneManager.GetSceneAt(i);
                        if (!scene.isDirty) continue;
                        list.Add(new Dictionary<string, object> { { "name", scene.name }, { "path", scene.path } });
                    }
                    return list;
                });
        }

        public static Dictionary<string, object> Info()
        {
            var scene = SceneManager.GetActiveScene();
            return new Dictionary<string, object>
            {
                { "name", scene.name },
                { "path", scene.path },
                { "isDirty", scene.isDirty },
                { "isLoaded", scene.isLoaded },
                { "rootCount", scene.rootCount },
                { "buildIndex", scene.buildIndex },
                { "loadedScenes", SceneManager.sceneCount },
                { "isPlaying", EditorApplication.isPlaying }
            };
        }

        public static string ActiveSceneName()
        {
            try
            {
                var scene = SceneManager.GetActiveScene();
                return scene.IsValid() ? scene.name : "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        public static int DirtySceneCount()
        {
            int count = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).isDirty) count++;
            }
            return count;
        }

        public static void RequireEditMode(string action)
        {
            if (EditorApplication.isPlaying)
                throw new CommandException("播放模式下无法" + action + "，请先 editor.stop", "playing");
        }

        public static void EnsureAssetFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            folder = folder.Replace('\\', '/');
            if (folder == "Assets" || AssetDatabase.IsValidFolder(folder)) return;

            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            string leaf = Path.GetFileName(folder);
            if (!string.IsNullOrEmpty(parent) && parent != "Assets" && !AssetDatabase.IsValidFolder(parent))
                EnsureAssetFolder(parent);

            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder(string.IsNullOrEmpty(parent) ? "Assets" : parent, leaf);
        }

        public static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Untitled";
            char[] invalid = Path.GetInvalidFileNameChars();
            var builder = new System.Text.StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool bad = false;
                for (int j = 0; j < invalid.Length; j++) if (invalid[j] == c) { bad = true; break; }
                builder.Append(bad ? '_' : c);
            }
            return builder.ToString();
        }
    }
}
