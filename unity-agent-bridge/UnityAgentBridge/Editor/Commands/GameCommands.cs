using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UnityAgentBridge
{
    /// <summary>Game level helpers: bootstrap a playable scene, build a player.</summary>
    public static class GameCommands
    {
        public static void Register()
        {
            CommandRegistry.Register("game.bootstrap", "一键搭出可玩场景骨架：主相机 + 平行光 + 地面（可选）+ 玩家胶囊体", "ground?=true, light?=true, camera?=true, player?=true",
                delegate(Dictionary<string, object> args)
                {
                    SceneCommands.RequireEditMode("搭建场景");
                    var created = new List<object>();

                    if (J.Bool(args, "camera", true) && Camera.main == null)
                    {
                        var cameraObject = new GameObject("Main Camera");
                        var camera = cameraObject.AddComponent<Camera>();
                        camera.tag = "MainCamera";
                        cameraObject.transform.position = new Vector3(0, 1.5f, -6);
                        cameraObject.transform.rotation = Quaternion.Euler(15, 0, 0);
                        if (UnityEngine.Object.FindObjectOfType<AudioListener>() == null) cameraObject.AddComponent<AudioListener>();
                        Undo.RegisterCreatedObjectUndo(cameraObject, "Agent Bridge Camera");
                        created.Add(Obj.Describe(cameraObject, false, 0));
                    }

                    if (J.Bool(args, "light", true) && UnityEngine.Object.FindObjectOfType<Light>() == null)
                    {
                        var lightObject = new GameObject("Directional Light");
                        var light = lightObject.AddComponent<Light>();
                        light.type = LightType.Directional;
                        light.intensity = 1.1f;
                        light.shadows = LightShadows.Soft;
                        lightObject.transform.rotation = Quaternion.Euler(50, -30, 0);
                        Undo.RegisterCreatedObjectUndo(lightObject, "Agent Bridge Light");
                        created.Add(Obj.Describe(lightObject, false, 0));
                    }

                    if (J.Bool(args, "ground", true))
                    {
                        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                        ground.name = "Ground";
                        ground.transform.localScale = new Vector3(4, 1, 4);
                        ground.transform.position = Vector3.zero;
                        Undo.RegisterCreatedObjectUndo(ground, "Agent Bridge Ground");
                        ObjectCommands.MarkDirty(ground);
                        created.Add(Obj.Describe(ground, false, 0));
                    }

                    if (J.Bool(args, "player", true))
                    {
                        var player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                        player.name = "Player";
                        player.transform.position = new Vector3(0, 1, 0);
                        var body = player.AddComponent<Rigidbody>();
                        body.mass = 1f;
                        body.constraints = RigidbodyConstraints.FreezeRotation;
                        Undo.RegisterCreatedObjectUndo(player, "Agent Bridge Player");
                        ObjectCommands.MarkDirty(player);
                        created.Add(Obj.Describe(player, false, 0));
                    }

                    AssetDatabase.SaveAssets();
                    return new Dictionary<string, object>
                    {
                        { "created", created },
                        { "count", created.Count },
                        { "scene", SceneCommands.ActiveSceneName() }
                    };
                });

            CommandRegistry.Register("game.build", "构建播放器（win64/webgl/android/ios/macos/linux）", "target?=win64, outputPath?, scenes?, development?=false, options?",
                delegate(Dictionary<string, object> args)
                {
                    if (EditorApplication.isPlaying) throw new CommandException("请先退出播放模式再构建", "playing");

                    string targetName = J.Str(args, "target", "win64");
                    BuildTarget target = ParseTarget(targetName);

                    string[] scenes = J.Strings(args, "scenes");
                    if (scenes == null || scenes.Length == 0)
                    {
                        var buildScenes = EditorBuildSettings.scenes;
                        var enabled = new List<string>();
                        for (int i = 0; i < buildScenes.Length; i++)
                            if (buildScenes[i].enabled) enabled.Add(buildScenes[i].path);
                        scenes = enabled.ToArray();
                    }
                    if (scenes.Length == 0)
                        throw new CommandException("没有可构建的场景，请先用 scene.buildSettings 设置", "no_scenes");

                    string output = J.Str(args, "outputPath", DefaultOutput(target));
                    BuildOptions options = J.Bool(args, "development", false) ? BuildOptions.Development : BuildOptions.None;
                    if (J.Bool(args, "allowDebugging", false)) options |= BuildOptions.AllowDebugging;

                    var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                    {
                        scenes = scenes,
                        locationPathName = output,
                        target = target,
                        options = options
                    });

                    BuildSummary summary = report.summary;
                    return new Dictionary<string, object>
                    {
                        { "result", summary.result.ToString() },
                        { "outputPath", summary.outputPath },
                        { "totalBytes", (double)summary.totalSize },
                        { "totalSeconds", Math.Round(summary.totalTime.TotalSeconds, 1) },
                        { "errors", summary.totalErrors },
                        { "warnings", summary.totalWarnings },
                        { "scenes", scenes.Length },
                        { "target", target.ToString() }
                    };
                });

            CommandRegistry.Register("game.buildTarget", "切换当前构建目标（会触发平台重新导入，可能较慢）", "target", 
                delegate(Dictionary<string, object> args)
                {
                    string targetName = CommandRegistry.Require(args, "target", "win64/webgl/android/ios/macos/linux");
                    BuildTarget target = ParseTarget(targetName);
                    BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
                    bool ok = EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);
                    return new Dictionary<string, object>
                    {
                        { "switched", ok },
                        { "buildTarget", EditorUserBuildSettings.activeBuildTarget.ToString() }
                    };
                });

            CommandRegistry.Register("game.playOnce", "快捷操作：保存场景 -> 进入播放模式（客户端随后轮询 status）", "",
                delegate(Dictionary<string, object> args)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    if (scene.isDirty)
                    {
                        if (string.IsNullOrEmpty(scene.path))
                            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, "Assets/Scenes/" + SceneCommands.Sanitize(scene.name) + ".unity");
                        else
                            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
                    }
                    EditorApplication.delayCall += delegate { EditorApplication.isPlaying = true; };
                    return new Dictionary<string, object> { { "scheduled", true }, { "target", "playing" } };
                });
        }

        static BuildTarget ParseTarget(string name)
        {
            switch ((name ?? "win64").Trim().ToLowerInvariant())
            {
                case "win":
                case "win64":
                case "windows":
                case "standalonewindows64":
                    return BuildTarget.StandaloneWindows64;
                case "win32":
                    return BuildTarget.StandaloneWindows;
                case "mac":
                case "macos":
                case "osx":
                    return BuildTarget.StandaloneOSX;
                case "linux":
                    return BuildTarget.StandaloneLinux64;
                case "webgl":
                    return BuildTarget.WebGL;
                case "android":
                    return BuildTarget.Android;
                case "ios":
                    return BuildTarget.iOS;
                default:
                    throw new CommandException("不支持的构建目标 " + name + "（win64/webgl/android/ios/macos/linux）", "bad_request");
            }
        }

        static string DefaultOutput(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.StandaloneWindows64:
                case BuildTarget.StandaloneWindows:
                    return "Builds/Windows/Game.exe";
                case BuildTarget.StandaloneOSX:
                    return "Builds/macOS/Game.app";
                case BuildTarget.StandaloneLinux64:
                    return "Builds/Linux/Game.x86_64";
                case BuildTarget.WebGL:
                    return "Builds/WebGL";
                case BuildTarget.Android:
                    return "Builds/Android/Game.apk";
                case BuildTarget.iOS:
                    return "Builds/iOS";
                default:
                    return "Builds/Output";
            }
        }
    }
}
