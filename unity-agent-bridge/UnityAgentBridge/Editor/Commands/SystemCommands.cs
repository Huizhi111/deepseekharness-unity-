using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge
{
    /// <summary>Server / project level commands.</summary>
    public static class SystemCommands
    {
        public static void Register()
        {
            CommandRegistry.Register("ping", "轻量健康检查，不需要 Unity 主线程", "",
                delegate(Dictionary<string, object> args) { return new Dictionary<string, object> { { "pong", true }, { "time", DateTime.Now.ToString("HH:mm:ss") } }; });

            CommandRegistry.Register("status", "服务与编辑器状态（端口、场景、播放模式、编译状态、请求数）", "",
                delegate(Dictionary<string, object> args)
                {
                    return new Dictionary<string, object>
                    {
                        { "running", BridgeServer.IsRunning },
                        { "port", BridgeServer.Port },
                        { "requests", BridgeServer.RequestCount },
                        { "startedAt", BridgeServer.StartedAt == DateTime.MinValue ? null : BridgeServer.StartedAt.ToString("yyyy-MM-dd HH:mm:ss") },
                        { "unity", Application.unityVersion },
                        { "platform", Application.platform.ToString() },
                        { "batchMode", Application.isBatchMode },
                        { "isPlaying", EditorApplication.isPlaying },
                        { "isPaused", EditorApplication.isPaused },
                        { "isCompiling", EditorApplication.isCompiling },
                        { "scriptCompilationFailed", EditorUtility.scriptCompilationFailed },
                        { "activeScene", SceneCommands.ActiveSceneName() },
                        { "project", BridgePaths.ProjectRoot },
                        { "buildTarget", EditorUserBuildSettings.activeBuildTarget.ToString() },
                        { "dirtyScenes", SceneCommands.DirtySceneCount() },
                        { "logs", BridgeLog.Count },
                        { "commands", CommandRegistry.Commands.Count }
                    };
                });

            CommandRegistry.Register("commands.list", "列出全部命令及其参数说明", "",
                delegate(Dictionary<string, object> args) { return CommandRegistry.Describe(); });

            CommandRegistry.Register("project.info", "工程信息：路径、产品名、公司、版本、构建目标、场景列表", "",
                delegate(Dictionary<string, object> args)
                {
                    var scenes = new List<object>();
                    var buildScenes = EditorBuildSettings.scenes;
                    for (int i = 0; i < buildScenes.Length; i++)
                        scenes.Add(new Dictionary<string, object> { { "path", buildScenes[i].path }, { "enabled", buildScenes[i].enabled } });

                    return new Dictionary<string, object>
                    {
                        { "path", BridgePaths.ProjectRoot },
                        { "name", Application.productName },
                        { "company", Application.companyName },
                        { "version", Application.version },
                        { "unity", Application.unityVersion },
                        { "buildTarget", EditorUserBuildSettings.activeBuildTarget.ToString() },
                        { "scenes", scenes }
                    };
                });

            CommandRegistry.Register("project.set", "修改 PlayerSettings：companyName / productName / version / defaultScreenWidth / defaultScreenHeight / fullScreenMode", "companyName?, productName?, version?, defaultScreenWidth?, defaultScreenHeight?, fullScreenMode?",
                delegate(Dictionary<string, object> args)
                {
                    var changed = new List<string>();
                    string company = J.Str(args, "companyName");
                    if (company != null) { PlayerSettings.companyName = company; changed.Add("companyName"); }
                    string product = J.Str(args, "productName");
                    if (product != null) { PlayerSettings.productName = product; changed.Add("productName"); }
                    string version = J.Str(args, "version");
                    if (version != null) { PlayerSettings.bundleVersion = version; changed.Add("version"); }
                    if (J.HasValue(args, "defaultScreenWidth"))
                    {
                        PlayerSettings.defaultScreenWidth = J.Int(args, "defaultScreenWidth", 1920);
                        changed.Add("defaultScreenWidth");
                    }
                    if (J.HasValue(args, "defaultScreenHeight"))
                    {
                        PlayerSettings.defaultScreenHeight = J.Int(args, "defaultScreenHeight", 1080);
                        changed.Add("defaultScreenHeight");
                    }
                    string fullScreen = J.Str(args, "fullScreenMode");
                    if (!string.IsNullOrEmpty(fullScreen))
                    {
                        PlayerSettings.fullScreenMode = (FullScreenMode)Enum.Parse(typeof(FullScreenMode), fullScreen, true);
                        changed.Add("fullScreenMode");
                    }
                    AssetDatabase.SaveAssets();
                    return new Dictionary<string, object> { { "changed", changed }, { "productName", Application.productName }, { "companyName", Application.companyName } };
                });

            CommandRegistry.Register("batch", "一次请求串联执行多条命令：{calls:[{cmd,args}...], stopOnError?}", "calls:[{cmd,args}]",
                delegate(Dictionary<string, object> args)
                {
                    var calls = J.List(args, "calls");
                    if (calls == null) throw new CommandException("缺少 calls 数组", "bad_request");
                    bool stopOnError = J.Bool(args, "stopOnError", true);

                    var results = new List<object>();
                    for (int i = 0; i < calls.Count; i++)
                    {
                        var call = J.AsDict(calls[i]);
                        if (call == null) { results.Add(CommandRegistry.Fail("calls[" + i + "] 不是对象", "bad_request")); continue; }
                        string name = J.Str(call, "cmd");
                        if (string.Equals(name, "batch", StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(CommandRegistry.Fail("不允许嵌套 batch", "bad_request"));
                            if (stopOnError) break;
                            continue;
                        }
                        var callArgs = J.Dict(call, "args") ?? new Dictionary<string, object>();
                        var result = CommandRegistry.Execute(name, callArgs);
                        result["cmd"] = name;
                        result["index"] = i;
                        results.Add(result);
                        if (stopOnError && !J.Bool(result, "ok", false)) break;
                    }

                    return new Dictionary<string, object>
                    {
                        { "count", results.Count },
                        { "results", results }
                    };
                });

            CommandRegistry.Register("log.mark", "在 Unity Console 打一条日志（用于标记阶段）", "message, type?",
                delegate(Dictionary<string, object> args)
                {
                    string message = CommandRegistry.Require(args, "message");
                    string type = J.Str(args, "type", "log");
                    if (type == "warning") Debug.LogWarning("[Agent] " + message);
                    else if (type == "error") Debug.LogError("[Agent] " + message);
                    else Debug.Log("[Agent] " + message);
                    return new Dictionary<string, object> { { "logged", message }, { "type", type } };
                });
        }
    }
}
