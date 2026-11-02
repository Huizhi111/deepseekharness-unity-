using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge
{
    /// <summary>
    /// Loopback-only HTTP server that exposes the Unity Editor to local tools.
    /// One request per connection, JSON in / JSON out.
    /// </summary>
    [InitializeOnLoad]
    public static class BridgeServer
    {
        public const string ProductName = "unity-agent-bridge";
        public const string Version = "1.0.0";

        static TcpListener _listener;
        static Thread _acceptThread;
        static volatile bool _running;
        static int _port = 17899;
        static string _token = "";
        static DateTime _startedAt = DateTime.MinValue;
        static long _requestCount;
        static string _lastError = "";

        public static event Action<string> Activity;

        public static bool IsRunning { get { return _running; } }
        public static int Port { get { return _port; } }
        public static string Token { get { return _token; } }
        public static string LastError { get { return _lastError; } }
        public static long RequestCount { get { return Interlocked.Read(ref _requestCount); } }
        public static DateTime StartedAt { get { return _startedAt; } }

        static BridgeServer()
        {
            EditorApplication.quitting += Stop;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            BridgeLog.EnsureInstalled();
        }

        public static bool Start(int port, string token)
        {
            if (_running) Stop();

            _lastError = "";
            Exception last = null;

            // 重启编辑器后旧连接可能还处在 TIME_WAIT，因此允许地址复用；
            // 但绑定前先探测端口是否已经有程序在应答（例如另一个 Unity 实例），
            // 有就顺延到下一个端口，避免两个服务共享端口导致请求被吞。
            for (int candidate = port; candidate <= Math.Min(65535, port + 9); candidate++)
            {
                if (IsPortAnswering(candidate))
                {
                    last = new Exception("端口 " + candidate + " 已被占用");
                    continue;
                }

                try
                {
                    var listener = new TcpListener(IPAddress.Loopback, candidate);
                    listener.ExclusiveAddressUse = false;
                    listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    listener.Start(128);

                    _listener = listener;
                    _port = candidate;
                    _token = token ?? "";
                    _startedAt = DateTime.Now;
                    _running = true;

                    _acceptThread = new Thread(AcceptLoop)
                    {
                        IsBackground = true,
                        Name = "UnityAgentBridge-Accept"
                    };
                    _acceptThread.Start();

                    if (candidate != port) PersistPort(candidate);
                    Log("监听 http://127.0.0.1:" + candidate);
                    return true;
                }
                catch (Exception e)
                {
                    last = e;
                    Thread.Sleep(100);
                }
            }

            _running = false;
            _listener = null;
            _lastError = last != null ? last.Message : "未知错误";
            Log("启动失败: " + _lastError);
            return false;
        }

        /// <summary>端口上是否已经有程序在接连接（用于避免两个服务抢同一个端口）。</summary>
        static bool IsPortAnswering(int port)
        {
            try
            {
                using (var probe = new TcpClient())
                {
                    var async = probe.BeginConnect(IPAddress.Loopback, port, null, null);
                    bool connected = async.AsyncWaitHandle.WaitOne(300);
                    if (!connected) return false;
                    probe.EndConnect(async);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        static void PersistPort(int port)
        {
            try
            {
                BridgeSettings settings = BridgeSettingsStore.Load();
                settings.port = port;
                BridgeSettingsStore.Save(settings);
            }
            catch (Exception)
            {
                // ignored
            }
        }

        public static void Stop()
        {
            bool wasRunning = _running;
            _running = false;

            try
            {
                if (_listener != null) _listener.Stop();
            }
            catch (Exception)
            {
                // ignored
            }
            _listener = null;

            var thread = _acceptThread;
            _acceptThread = null;
            if (thread != null && thread.IsAlive && Thread.CurrentThread != thread)
            {
                try { thread.Join(1500); }
                catch (Exception) { /* ignored */ }
            }

            if (wasRunning) Log("服务已停止");
            _startedAt = DateTime.MinValue;
        }

        public static void Restart(int port, string token)
        {
            Stop();
            Start(port, token);
        }

        static void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (!_running) break;
                    Thread.Sleep(25);
                    continue;
                }
                catch (Exception e)
                {
                    if (!_running) break;
                    Log("accept 失败: " + e);
                    Thread.Sleep(50);
                    continue;
                }

                // One short-lived worker per connection: a silent client must never block
                // the accept loop (that would stall every later request).
                TcpClient captured = client;
                var worker = new Thread(delegate()
                {
                    try
                    {
                        HandleClient(captured);
                    }
                    catch (Exception e)
                    {
                        Log("请求处理异常: " + e);
                    }
                    finally
                    {
                        try { captured.Close(); }
                        catch (Exception) { /* ignored */ }
                    }
                })
                {
                    IsBackground = true,
                    Name = "UnityAgentBridge-Conn"
                };
                worker.Start();
            }
        }

        static void HandleClient(TcpClient client)
        {
            var started = DateTime.UtcNow;
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 4000;
                client.SendTimeout = 300000;

                using (NetworkStream stream = client.GetStream())
                {
                    HttpRequest request = HttpRequest.Read(stream);
                    if (request == null)
                    {
                        Log("连接未发送任何数据即关闭");
                        return;
                    }

                    HttpResponse response = Router.Handle(request);
                    Write(stream, response);

                    long total = Interlocked.Increment(ref _requestCount);
                    if (total % 25 == 0 || BridgeSettingsProbe.Verbose)
                    {
                        Log(request.Method + " " + request.Path + " → " + response.Status + " (" +
                            (int)(DateTime.UtcNow - started).TotalMilliseconds + "ms)");
                    }
                }
            }
            catch (Exception e)
            {
                Log("请求处理异常: " + e);
            }
            finally
            {
                try { client.Close(); }
                catch (Exception) { /* ignored */ }
            }
        }

        static void Write(Stream stream, HttpResponse response)
        {
            byte[] body = Encoding.UTF8.GetBytes(response.Body ?? "");
            var head = new StringBuilder(256);
            head.Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(response.StatusText).Append("\r\n");
            head.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
            head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            head.Append("Connection: close\r\n");
            head.Append("Cache-Control: no-store\r\n");
            head.Append("Access-Control-Allow-Origin: *\r\n");
            head.Append("Access-Control-Allow-Headers: X-Bridge-Token, Content-Type, Authorization\r\n");
            head.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            head.Append("\r\n");

            byte[] header = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(header, 0, header.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        static readonly object LogGate = new object();

        public static void Log(string message)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
            var handler = Activity;
            if (handler != null)
            {
                try { handler(line); }
                catch (Exception) { /* ignored */ }
            }

            // Debug.Log is only safe to call from the main thread in some editor states,
            // so the socket thread writes to the log file instead.
            try
            {
                if (Dispatcher.IsMainThread) Debug.Log("[AgentBridge] " + message);
            }
            catch (Exception)
            {
                // ignored
            }

            try
            {
                lock (LogGate)
                {
                    BridgePaths.EnsureDirectories();
                    var info = new FileInfo(BridgePaths.ServerLogFile);
                    if (!info.Exists || info.Length < 4 * 1024 * 1024)
                        File.AppendAllText(BridgePaths.ServerLogFile, line + Environment.NewLine);
                }
            }
            catch (Exception)
            {
                // ignored
            }
        }
    }

    /// <summary>Lightweight accessor to the current settings without re-reading the file on every request.</summary>
    public static class BridgeSettingsProbe
    {
        public static bool Verbose;
    }

    /// <summary>Starts the bridge automatically after every domain reload (unless disabled).</summary>
    [InitializeOnLoad]
    public static class BridgeBootstrap
    {
        static BridgeBootstrap()
        {
            EditorApplication.delayCall += AutoStart;
        }

        static void AutoStart()
        {
            if (BridgeServer.IsRunning) return;

            BridgeSettings settings = BridgeSettingsStore.Load();
            BridgeSettingsProbe.Verbose = settings.verbose;

            // 播放模式同样要开服务：只有开着才看得到运行日志、截图、状态。
            // 批处理模式通常是 CI，需要显式开启。
            bool forced = Environment.GetEnvironmentVariable("UNITY_BRIDGE_AUTOSTART") == "1";
            if (Application.isBatchMode && !forced) return;
            if (!settings.autoStart && !forced) return;

            if (!BridgeServer.Start(settings.port, settings.token))
            {
                // A previous domain may still hold the port for a moment.
                EditorApplication.delayCall += RetryStart;
            }
        }

        static void RetryStart()
        {
            if (BridgeServer.IsRunning) return;
            BridgeSettings settings = BridgeSettingsStore.Load();
            if (!settings.autoStart) return;
            BridgeServer.Start(settings.port, settings.token);
        }
    }
}
