using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge
{
    /// <summary>
    /// Self checks. Two modes:
    ///  * RunInEditor  - 菜单里点一下，直接调用命令层做冒烟测试（不走网络）。
    ///  * RunBatchSelfTest - 批处理入口：起服务后等待外部客户端跑完（写 done 文件），
    ///    用于 CI / 无人值守验证，不依赖 Unity 内部的线程池。
    /// </summary>
    public static class BridgeSelfTest
    {
        public const string ReadyFileName = "selftest-ready.txt";
        public const string DoneFileName = "selftest-done.txt";

        static readonly StringBuilder Report = new StringBuilder();

        static void Say(string line)
        {
            Debug.Log("[SELFTEST] " + line);
            lock (Report) Report.AppendLine(line);
        }

        static void FlushReport()
        {
            try
            {
                BridgePaths.EnsureDirectories();
                File.WriteAllText(Path.Combine(BridgePaths.LibraryDir, "selftest.txt"), Report.ToString());
            }
            catch (Exception)
            {
                // ignored
            }
        }

        [MenuItem("Tools/Unity Agent Bridge/运行自检", false, 20)]
        public static void RunInEditor()
        {
            Report.Length = 0;
            int passed = 0;
            int failed = 0;

            Say("服务: " + (BridgeServer.IsRunning ? "运行中 (" + BridgeServer.Port + ")" : "未启动"));
            Say("Unity: " + Application.unityVersion + "  平台: " + Application.platform);

            Check(ref passed, ref failed, "ping", delegate { return CommandRegistry.Execute("ping", new Dictionary<string, object>()); });
            Check(ref passed, ref failed, "status", delegate { return CommandRegistry.Execute("status", new Dictionary<string, object>()); });
            Check(ref passed, ref failed, "commands.list", delegate { return CommandRegistry.Execute("commands.list", new Dictionary<string, object>()); });
            Check(ref passed, ref failed, "scene.info", delegate { return CommandRegistry.Execute("scene.info", new Dictionary<string, object>()); });
            Check(ref passed, ref failed, "hierarchy", delegate { return CommandRegistry.Execute("hierarchy", new Dictionary<string, object> { { "depth", 1 } }); });

            GameObject created = null;
            try
            {
                Check(ref passed, ref failed, "go.create", delegate
                {
                    var result = CommandRegistry.Execute("go.create", new Dictionary<string, object> { { "type", "cube" }, { "name", "__bridge_selftest" } });
                    if (J.Bool(result, "ok", false) && J.HasValue(result, "result"))
                    {
                        var described = J.AsDict(result["result"]);
                        if (described != null && described.ContainsKey("id"))
                            created = EditorUtility.InstanceIDToObject(Convert.ToInt32(described["id"])) as GameObject;
                    }
                    return result;
                });

                if (created != null)
                {
                    Check(ref passed, ref failed, "go.set", delegate
                    {
                        return CommandRegistry.Execute("go.set", new Dictionary<string, object>
                        {
                            { "target", "#" + created.GetInstanceID() },
                            { "position", new List<object> { 1.0, 2.0, 3.0 } }
                        });
                    });

                    Check(ref passed, ref failed, "comp.add", delegate
                    {
                        return CommandRegistry.Execute("comp.add", new Dictionary<string, object>
                        {
                            { "target", "#" + created.GetInstanceID() },
                            { "type", "Rigidbody" }
                        });
                    });

                    Check(ref passed, ref failed, "comp.set", delegate
                    {
                        return CommandRegistry.Execute("comp.set", new Dictionary<string, object>
                        {
                            { "target", "#" + created.GetInstanceID() },
                            { "type", "Rigidbody" },
                            { "field", "m_Mass" },
                            { "value", 2.5 }
                        });
                    });
                }
            }
            finally
            {
                if (created != null) UnityEngine.Object.DestroyImmediate(created);
            }

            Check(ref passed, ref failed, "console.get", delegate
            {
                return CommandRegistry.Execute("console.get", new Dictionary<string, object> { { "count", 5 } });
            });

            Say("通过 " + passed + " / " + (passed + failed));
            FlushReport();
            EditorUtility.DisplayDialog("Unity Agent Bridge 自检", Report.ToString(), "好");
        }

        static void Check(ref int passed, ref int failed, string name, Func<Dictionary<string, object>> action)
        {
            try
            {
                var result = action();
                bool ok = J.Bool(result, "ok", false);
                if (ok)
                {
                    passed++;
                    Say("[OK]   " + name);
                }
                else
                {
                    failed++;
                    Say("[FAIL] " + name + " - " + J.Str(result, "error", "未知错误"));
                }
            }
            catch (Exception e)
            {
                failed++;
                Say("[ERR]  " + name + " - " + e.Message);
            }
        }

        // ------------------------------------------------------------ batch mode

        /// <summary>
        /// 用法：
        /// Unity.exe -batchmode -nographics -quit -projectPath &lt;proj&gt;
        ///           -executeMethod UnityAgentBridge.BridgeSelfTest.RunBatchSelfTest
        ///
        /// 启动服务后写 selftest-ready.txt，然后等外部客户端写 selftest-done.txt，最多等 300 秒。
        /// 这样验证的是真实的外部 HTTP 客户端路径，不依赖 Unity 内部的线程池行为。
        /// </summary>
        public static void RunBatchSelfTest()
        {
            Report.Length = 0;
            int port = 17899;
            string envPort = Environment.GetEnvironmentVariable("UNITY_BRIDGE_SELFTEST_PORT");
            int parsedPort;
            if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out parsedPort)) port = parsedPort;

            string token = "selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string readyPath = Path.Combine(BridgePaths.LibraryDir, ReadyFileName);
            string donePath = Path.Combine(BridgePaths.LibraryDir, DoneFileName);

            BridgePaths.EnsureDirectories();
            DeleteIfExists(readyPath);
            DeleteIfExists(donePath);

            Say("Unity " + Application.unityVersion + " 批次模式=" + Application.isBatchMode);
            Say("图形设备 " + SystemInfo.graphicsDeviceType);

            if (!BridgeServer.Start(port, token))
            {
                Say("[FAIL] 服务启动失败: " + BridgeServer.LastError);
                FlushReport();
                WriteFile(donePath, "start-failed");
                EditorApplication.Exit(2);
                return;
            }

            Say("[OK]   服务已启动 127.0.0.1:" + port);
            WriteFile(readyPath, "port=" + port + "\ntoken=" + token + "\nproject=" + BridgePaths.ProjectRoot + "\n");
            Say("等待外部客户端：写 " + donePath + " 表示结束");

            DateTime deadline = DateTime.UtcNow.AddSeconds(300);
            bool finished = false;
            while (DateTime.UtcNow < deadline)
            {
                Dispatcher.Pump();
                Thread.Sleep(10);
                if (File.Exists(donePath)) { finished = true; break; }
            }

            Say(finished ? "外部客户端已完成" : "[FAIL] 等待外部客户端超时（300 秒）");
            Say("请求总数 " + BridgeServer.RequestCount);
            BridgeServer.Stop();
            FlushReport();

            Console.WriteLine("SELFTEST SERVED " + (finished ? "OK" : "TIMEOUT"));
            EditorApplication.Exit(finished ? 0 : 3);
        }

        static void WriteFile(string path, string content)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, content);
            }
            catch (Exception e)
            {
                Debug.LogError("[AgentBridge] 写文件失败 " + path + ": " + e.Message);
            }
        }

        static void DeleteIfExists(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { /* ignored */ }
        }
    }
}
