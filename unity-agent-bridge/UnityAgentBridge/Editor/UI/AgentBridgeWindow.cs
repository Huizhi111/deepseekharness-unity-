using System;
using System.Collections.Concurrent;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge
{
    /// <summary>Control panel for the local bridge: start/stop, token, settings and live activity log.</summary>
    public class AgentBridgeWindow : EditorWindow
    {
        const int MaxLogLines = 200;

        static readonly ConcurrentQueue<string> PendingLog = new ConcurrentQueue<string>();

        BridgeSettings _settings;
        string _logText = "";
        Vector2 _logScroll;
        Vector2 _mainScroll;
        bool _showHelp;
        double _nextRepaint;
        string _portText;

        [MenuItem("Tools/Unity Agent Bridge", false, 1)]
        public static void Open()
        {
            var window = GetWindow<AgentBridgeWindow>(false, "Agent Bridge", true);
            window.titleContent = new GUIContent("Agent Bridge");
            window.minSize = new Vector2(460, 560);
            window.Show();
        }

        void OnEnable()
        {
            _settings = BridgeSettingsStore.Load();
            _portText = _settings.port.ToString();
            BridgeServer.Activity += OnActivity;
            EditorApplication.update += Tick;
        }

        void OnDisable()
        {
            BridgeServer.Activity -= OnActivity;
            EditorApplication.update -= Tick;
        }

        static void OnActivity(string line)
        {
            PendingLog.Enqueue(line);
        }

        void Tick()
        {
            bool drain = false;
            string line;
            while (PendingLog.TryDequeue(out line))
            {
                drain = true;
                _logText += line + "\n";
            }

            if (drain)
            {
                string[] lines = _logText.Split('\n');
                if (lines.Length > MaxLogLines)
                {
                    var builder = new StringBuilder();
                    for (int i = lines.Length - MaxLogLines; i < lines.Length; i++)
                    {
                        if (i < 0) continue;
                        builder.Append(lines[i]).Append('\n');
                    }
                    _logText = builder.ToString();
                }
            }

            if (EditorApplication.timeSinceStartup < _nextRepaint) return;
            _nextRepaint = EditorApplication.timeSinceStartup + 1.0;
            if (drain || BridgeServer.IsRunning) Repaint();
        }

        void OnGUI()
        {
            if (_settings == null) _settings = BridgeSettingsStore.Load();

            _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);
            DrawHeader();
            DrawControls();
            DrawSettings();
            DrawHelp();
            DrawLog();
            EditorGUILayout.EndScrollView();
        }

        void DrawHeader()
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Unity Agent Bridge", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                GUILayout.Label("v" + BridgeServer.Version, EditorStyles.miniLabel);
            }

            if (BridgeServer.IsRunning)
            {
                EditorGUILayout.HelpBox(
                    "正在监听 http://127.0.0.1:" + BridgeServer.Port + "\n" +
                    "请求数 " + BridgeServer.RequestCount +
                    "   启动于 " + BridgeServer.StartedAt.ToString("HH:mm:ss"),
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    string.IsNullOrEmpty(BridgeServer.LastError)
                        ? "服务未启动"
                        : "服务未启动：" + BridgeServer.LastError,
                    MessageType.Warning);
            }
        }

        void DrawControls()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("端口", GUILayout.Width(40));
                _portText = EditorGUILayout.TextField(_portText, GUILayout.Width(70));

                if (GUILayout.Button(BridgeServer.IsRunning ? "停止" : "启动", GUILayout.Width(70)))
                {
                    int port;
                    if (!int.TryParse(_portText, out port) || port <= 0 || port > 65535)
                    {
                        EditorUtility.DisplayDialog("端口无效", "请输入 1-65535 之间的端口号", "好");
                    }
                    else
                    {
                        _settings.port = port;
                        BridgeSettingsStore.Save(_settings);

                        if (BridgeServer.IsRunning) BridgeServer.Stop();
                        else BridgeServer.Start(port, _settings.token);
                    }
                }

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextField("127.0.0.1", GUILayout.Width(90));
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("令牌", GUILayout.Width(40));
                var previous = GUI.enabled;
                GUI.enabled = false;
                EditorGUILayout.TextField(_settings.token);
                GUI.enabled = previous;

                if (GUILayout.Button("复制", GUILayout.Width(50)))
                    EditorGUIUtility.systemCopyBuffer = _settings.token;

                if (GUILayout.Button("重置", GUILayout.Width(50)))
                {
                    _settings.token = BridgeSettingsStore.NewToken();
                    BridgeSettingsStore.Save(_settings);
                    if (BridgeServer.IsRunning) BridgeServer.Restart(_settings.port, _settings.token);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("运行自检"))
                    BridgeSelfTest.RunInEditor();

                if (GUILayout.Button("复制连接信息"))
                {
                    EditorGUIUtility.systemCopyBuffer =
                        "Unity Agent Bridge  http://127.0.0.1:" + BridgeServer.Port + "  token=" + _settings.token;
                }

                if (GUILayout.Button("打开日志目录"))
                {
                    BridgePaths.EnsureDirectories();
                    EditorUtility.RevealInFinder(BridgePaths.LibraryDir);
                }
            }
        }

        void DrawSettings()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("设置", EditorStyles.boldLabel);

            bool autoStart = EditorGUILayout.ToggleLeft("编辑器启动时自动开启服务", _settings.autoStart);
            if (autoStart != _settings.autoStart) { _settings.autoStart = autoStart; BridgeSettingsStore.Save(_settings); }

            bool allowCode = EditorGUILayout.ToggleLeft("允许远程执行 C# 代码（cs.execute，高风险）", _settings.allowRemoteCode);
            if (allowCode != _settings.allowRemoteCode) { _settings.allowRemoteCode = allowCode; BridgeSettingsStore.Save(_settings); }

            bool verbose = EditorGUILayout.ToggleLeft("记录详细日志到 Library/UnityAgentBridge/bridge.log", _settings.verbose);
            if (verbose != _settings.verbose)
            {
                _settings.verbose = verbose;
                BridgeSettingsProbe.Verbose = verbose;
                BridgeSettingsStore.Save(_settings);
            }

            EditorGUILayout.HelpBox(
                "服务只绑定 127.0.0.1，且除 /ping 外所有命令都需要 X-Bridge-Token 请求头。\n" +
                "令牌文件：" + BridgePaths.TokenFile,
                MessageType.None);
        }

        void DrawHelp()
        {
            EditorGUILayout.Space(6);
            _showHelp = EditorGUILayout.Foldout(_showHelp, "命令行示例", true);
            if (!_showHelp) return;

            string example =
                "curl -s http://127.0.0.1:" + _settings.port + "/ping\n\n" +
                "curl -s -X POST http://127.0.0.1:" + _settings.port + "/cmd ^\n" +
                "  -H \"X-Bridge-Token: " + _settings.token + "\" ^\n" +
                "  -H \"Content-Type: application/json\" ^\n" +
                "  -d \"{\\\"cmd\\\":\\\"hierarchy\\\",\\\"args\\\":{\\\"depth\\\":2}}\"\n\n" +
                "PowerShell: .\\unityctl.ps1 hierarchy -Depth 2\n" +
                "Python:     python unityctl.py hierarchy --depth 2";

            EditorGUILayout.TextArea(example, GUILayout.MinHeight(120));
        }

        void DrawLog()
        {
            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("活动日志", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("清空", EditorStyles.miniButton, GUILayout.Width(60)))
                    _logText = "";
            }

            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.MinHeight(140), GUILayout.MaxHeight(220));
            EditorGUILayout.TextArea(_logText, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }
    }
}
