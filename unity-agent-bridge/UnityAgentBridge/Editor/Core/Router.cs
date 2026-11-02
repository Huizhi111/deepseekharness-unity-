using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge
{
    /// <summary>
    /// HTTP surface: GET /, /ping, /help, /cmd (POST or GET).
    /// The socket thread never touches Unity APIs directly: everything that needs the editor
    /// is marshalled through <see cref="Dispatcher"/>.
    /// </summary>
    public static class Router
    {
        const int MainThreadWaitMs = 30000;

        public static HttpResponse Handle(HttpRequest request)
        {
            try
            {
                string path = request.Path;
                if (string.IsNullOrEmpty(path)) path = "/";
                if (path.Length > 1) path = path.TrimEnd('/');
                if (path.Length == 0) path = "/";

                if (request.Method == "OPTIONS")
                    return HttpResponse.Json("{}", 204);

                if (path == "/") return FromMainThread(Info);
                if (path == "/ping") return FromMainThread(Ping);
                if (path == "/help" || path == "/commands") return FromMainThread(Help);
                if (path == "/cmd") return HandleCommand(request);

                return HttpResponse.Json(Json.Serialize(ErrorBody("未知路径 " + path + "（可用 /、/ping、/help、/cmd）", "not_found")), 404);
            }
            catch (Exception e)
            {
                BridgeServer.Log("路由异常: " + e);
                return HttpResponse.Json(Json.Serialize(ErrorBody(e.Message, "router_error")), 500);
            }
        }

        /// <summary>Runs a producer on the Unity main thread and turns it into a JSON response.</summary>
        static HttpResponse FromMainThread(Func<Dictionary<string, object>> producer)
        {
            InvokeResult outcome = Dispatcher.Invoke(delegate { return producer(); }, MainThreadWaitMs);
            if (outcome.TimedOut)
                return HttpResponse.Json(Json.Serialize(ErrorBody(outcome.TimeoutMessage, "timeout")), 200);

            if (!outcome.Success)
            {
                var error = ErrorBody(outcome.Error != null ? outcome.Error.Message : "未知错误", "exception");
                if (outcome.Error != null) error["stack"] = outcome.Error.StackTrace;
                return HttpResponse.Json(Json.Serialize(error), 200);
            }

            return HttpResponse.Json(Json.Serialize(outcome.Value));
        }

        static HttpResponse HandleCommand(HttpRequest request)
        {
            if (!Authorized(request))
            {
                return HttpResponse.Json(
                    Json.Serialize(ErrorBody("未授权：请求头需要 X-Bridge-Token（或 ?token=），令牌见 Unity 菜单 Tools/Unity Agent Bridge", "unauthorized")),
                    401);
            }

            string cmd = request.QueryValue("cmd");
            string id = request.QueryValue("id");
            int timeoutMs = Math.Max(1000, (int)J.ToDouble(request.QueryValue("timeoutMs"), 120000));
            var args = new Dictionary<string, object>(StringComparer.Ordinal);
            string body = request.BodyText;

            if (!string.IsNullOrEmpty(body) && body.Trim().Length > 0)
            {
                Dictionary<string, object> wrapper;
                try
                {
                    wrapper = J.AsDict(Json.Parse(body));
                }
                catch (Exception e)
                {
                    return HttpResponse.Json(Json.Serialize(ErrorBody("请求体不是合法 JSON: " + e.Message, "bad_json")), 400);
                }

                if (wrapper == null)
                    return HttpResponse.Json(Json.Serialize(ErrorBody("请求体必须是 JSON 对象，例如 {\"cmd\":\"ping\"}", "bad_json")), 400);

                if (wrapper.ContainsKey("cmd"))
                {
                    cmd = J.Str(wrapper, "cmd");
                    args = J.Dict(wrapper, "args") ?? J.Dict(wrapper, "arguments") ?? args;
                    id = J.Str(wrapper, "id", id);
                    timeoutMs = Math.Max(1000, J.Int(wrapper, "timeoutMs", timeoutMs));
                }
                else
                {
                    args = wrapper;
                }
            }
            else if (!string.IsNullOrEmpty(cmd))
            {
                string rawArgs = request.QueryValue("args");
                if (!string.IsNullOrEmpty(rawArgs))
                {
                    try
                    {
                        var parsed = J.AsDict(Json.Parse(rawArgs));
                        if (parsed != null) args = parsed;
                    }
                    catch (Exception e)
                    {
                        return HttpResponse.Json(Json.Serialize(ErrorBody("args 查询参数不是合法 JSON: " + e.Message, "bad_json")), 400);
                    }
                }
            }

            if (string.IsNullOrEmpty(cmd))
                return HttpResponse.Json(Json.Serialize(ErrorBody("缺少 cmd 参数", "bad_request")), 400);

            cmd = cmd.Trim();

            var sw = Stopwatch.StartNew();
            InvokeResult outcome = Dispatcher.Invoke(delegate { return ExecuteOnMainThread(cmd, args); }, timeoutMs);
            sw.Stop();

            if (BridgeSettingsProbe.Verbose || sw.ElapsedMilliseconds > 500)
                BridgeServer.Log(cmd + " " + sw.ElapsedMilliseconds + "ms ok=" + outcome.Success + " timeout=" + outcome.TimedOut);

            Dictionary<string, object> payload;
            if (outcome.TimedOut)
            {
                payload = ErrorBody(outcome.TimeoutMessage, "timeout");
            }
            else if (!outcome.Success)
            {
                payload = ErrorBody(outcome.Error != null ? outcome.Error.Message : "未知错误", "exception");
                if (outcome.Error != null) payload["stack"] = outcome.Error.StackTrace;
            }
            else
            {
                payload = J.AsDict(outcome.Value) ?? new Dictionary<string, object> { { "ok", true }, { "result", outcome.Value } };
            }

            payload["cmd"] = cmd;
            payload["ms"] = sw.ElapsedMilliseconds;
            if (!string.IsNullOrEmpty(id)) payload["id"] = id;

            return HttpResponse.Json(Json.Serialize(payload));
        }

        /// <summary>Runs on the Unity main thread only.</summary>
        static object ExecuteOnMainThread(string cmd, Dictionary<string, object> args)
        {
            bool busy = EditorApplication.isCompiling &&
                        cmd != "status" && cmd != "ping" && cmd != "commands.list" && cmd != "cs.status";

            if (busy)
            {
                return new Dictionary<string, object>
                {
                    { "ok", false },
                    { "error", "Unity 正在编译脚本，请稍后重试" },
                    { "code", "compiling" },
                    { "cmd", cmd }
                };
            }

            return CommandRegistry.Execute(cmd, args);
        }

        static bool Authorized(HttpRequest request)
        {
            string token = BridgeServer.Token;
            if (string.IsNullOrEmpty(token)) return true;

            string provided = request.Header("X-Bridge-Token");
            if (string.IsNullOrEmpty(provided)) provided = request.QueryValue("token");
            if (string.IsNullOrEmpty(provided)) provided = request.Header("Authorization");
            if (!string.IsNullOrEmpty(provided) && provided.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                provided = provided.Substring(7);

            return string.Equals(provided, token, StringComparison.Ordinal);
        }

        static Dictionary<string, object> ErrorBody(string message, string code)
        {
            return new Dictionary<string, object>
            {
                { "ok", false },
                { "error", message },
                { "code", code ?? "error" }
            };
        }

        static Dictionary<string, object> Ping()
        {
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "pong", true },
                { "name", BridgeServer.ProductName },
                { "version", BridgeServer.Version },
                { "unity", Application.unityVersion },
                { "port", BridgeServer.Port },
                { "isPlaying", EditorApplication.isPlaying },
                { "isCompiling", EditorApplication.isCompiling },
                { "time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }
            };
        }

        static Dictionary<string, object> Help()
        {
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "protocol", "POST /cmd {\"cmd\":\"<name>\",\"args\":{...},\"timeoutMs\":120000}" },
                { "auth", "X-Bridge-Token 请求头，或 ?token= 查询参数" },
                { "count", CommandRegistry.Commands.Count },
                { "commands", CommandRegistry.Describe() }
            };
        }

        static Dictionary<string, object> Info()
        {
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "name", BridgeServer.ProductName },
                { "version", BridgeServer.Version },
                { "unity", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "project", System.IO.Path.GetFileName(BridgePaths.ProjectRoot) },
                { "projectPath", BridgePaths.ProjectRoot },
                { "port", BridgeServer.Port },
                { "running", BridgeServer.IsRunning },
                { "requests", BridgeServer.RequestCount },
                { "activeScene", SceneCommands.ActiveSceneName() },
                { "isPlaying", EditorApplication.isPlaying },
                { "isCompiling", EditorApplication.isCompiling },
                { "commands", CommandRegistry.Commands.Count }
            };
        }
    }
}
