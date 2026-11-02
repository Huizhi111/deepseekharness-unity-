using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityAgentBridge
{
    /// <summary>Editor control: play mode, selection, menus, undo, compilation.</summary>
    public static class EditorCommands
    {
        public static void Register()
        {
            CommandRegistry.Register("editor.play", "进入播放模式。立即返回 scheduled=true，随后客户端应轮询 status 直到 isPlaying", "",
                delegate(Dictionary<string, object> args)
                {
                    if (EditorApplication.isPlaying) return PlayState();

                    // Entering play mode reloads the domain and would kill this request,
                    // so schedule the transition and answer immediately.
                    EditorApplication.delayCall += delegate { EditorApplication.isPlaying = true; };
                    return new Dictionary<string, object>
                    {
                        { "scheduled", true },
                        { "target", "playing" },
                        { "hint", "轮询 status 直到 isPlaying=true；域重载期间服务会短暂重启" }
                    };
                });

            CommandRegistry.Register("editor.stop", "退出播放模式。立即返回 scheduled=true，随后客户端应轮询 status 直到 isPlaying=false", "",
                delegate(Dictionary<string, object> args)
                {
                    if (!EditorApplication.isPlaying) return PlayState();
                    EditorApplication.delayCall += delegate { EditorApplication.isPlaying = false; };
                    return new Dictionary<string, object>
                    {
                        { "scheduled", true },
                        { "target", "editmode" },
                        { "hint", "轮询 status 直到 isPlaying=false" }
                    };
                });

            CommandRegistry.Register("editor.pause", "暂停/继续播放模式", "paused?=true",
                delegate(Dictionary<string, object> args)
                {
                    EditorApplication.isPaused = J.Bool(args, "paused", true);
                    return PlayState();
                });

            CommandRegistry.Register("editor.step", "播放模式下单步一帧", "",
                delegate(Dictionary<string, object> args)
                {
                    if (!EditorApplication.isPlaying) throw new CommandException("只有播放模式下才能单步", "playing");
                    EditorApplication.Step();
                    return PlayState();
                });

            CommandRegistry.Register("editor.select", "选中场景对象（也可以在 Scene 面板里选中后用 target: \"sel\"）", "target?, add?",
                delegate(Dictionary<string, object> args)
                {
                    string target = J.Str(args, "target");
                    if (string.IsNullOrEmpty(target) || target == "none") { Selection.activeGameObject = null; return SelectionInfo(); }
                    var go = Obj.GameObject(target);
                    if (J.Bool(args, "add", false))
                    {
                        var current = new List<UnityEngine.Object>(Selection.objects);
                        if (!current.Contains(go)) current.Add(go);
                        Selection.objects = current.ToArray();
                    }
                    else Selection.activeGameObject = go;
                    EditorGUIUtility.PingObject(go);
                    return SelectionInfo();
                });

            CommandRegistry.Register("editor.selection", "读取当前选中对象", "",
                delegate(Dictionary<string, object> args) { return SelectionInfo(); });

            CommandRegistry.Register("editor.menu", "执行编辑器菜单命令，例如 \"GameObject/3D Object/Cube\"", "path",
                delegate(Dictionary<string, object> args)
                {
                    string path = CommandRegistry.Require(args, "path", "例如 GameObject/Light");
                    bool ok = EditorApplication.ExecuteMenuItem(path);
                    if (!ok) throw new CommandException("菜单命令执行失败或不存在: " + path, "menu_failed");
                    return new Dictionary<string, object> { { "executed", path } };
                });

            CommandRegistry.Register("editor.undo", "撤销上一步操作", "",
                delegate(Dictionary<string, object> args)
                {
                    Undo.PerformUndo();
                    return new Dictionary<string, object> { { "undone", true } };
                });

            CommandRegistry.Register("editor.redo", "重做被撤销的操作", "",
                delegate(Dictionary<string, object> args)
                {
                    Undo.PerformRedo();
                    return new Dictionary<string, object> { { "redone", true } };
                });

            CommandRegistry.Register("editor.save", "保存所有资源（AssetDatabase.SaveAssets）", "",
                delegate(Dictionary<string, object> args)
                {
                    AssetDatabase.SaveAssets();
                    return new Dictionary<string, object> { { "saved", true } };
                });

            CommandRegistry.Register("editor.compile", "请求重新编译脚本", "",
                delegate(Dictionary<string, object> args)
                {
                    CompilationPipeline.RequestScriptCompilation();
                    return new Dictionary<string, object> { { "requested", true } };
                });

            CommandRegistry.Register("editor.compileErrors", "读取最近的脚本编译错误", "count?=20",
                delegate(Dictionary<string, object> args)
                {
                    int count = Mathf.Clamp(J.Int(args, "count", 20), 1, 200);
                    var errors = BridgeLog.CompileErrors(count);
                    var list = new List<object>();
                    for (int i = 0; i < errors.Count; i++) list.Add(BridgeLog.ToDict(errors[i]));
                    return new Dictionary<string, object>
                    {
                        { "failed", EditorUtility.scriptCompilationFailed },
                        { "isCompiling", EditorApplication.isCompiling },
                        { "count", list.Count },
                        { "errors", list }
                    };
                });

            CommandRegistry.Register("editor.focus", "把编辑器窗口切到前台（Windows 上会调用 SetForegroundWindow）", "",
                delegate(Dictionary<string, object> args)
                {
                    FocusEditor();
                    return new Dictionary<string, object> { { "focused", true } };
                });
        }

        static Dictionary<string, object> PlayState()
        {
            return new Dictionary<string, object>
            {
                { "isPlaying", EditorApplication.isPlaying },
                { "isPlayingOrWillChangePlaymode", EditorApplication.isPlayingOrWillChangePlaymode },
                { "isPaused", EditorApplication.isPaused },
                { "isCompiling", EditorApplication.isCompiling }
            };
        }

        static Dictionary<string, object> SelectionInfo()
        {
            var list = new List<object>();
            var objects = Selection.objects;
            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i] == null) continue;
                list.Add(Obj.DescribeObject(objects[i]));
            }
            return new Dictionary<string, object>
            {
                { "count", list.Count },
                { "active", Selection.activeGameObject != null ? Selection.activeGameObject.name : null },
                { "selection", list }
            };
        }

        static void FocusEditor()
        {
            // Best effort; never throws.
            try
            {
#if UNITY_EDITOR_WIN
                var helper = new ForegroundHelper();
                helper.BringToFront();
#endif
            }
            catch (Exception)
            {
                // ignored
            }
        }

#if UNITY_EDITOR_WIN
        sealed class ForegroundHelper
        {
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            static extern bool SetForegroundWindow(IntPtr hWnd);

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            static extern IntPtr GetForegroundWindow();

            public void BringToFront()
            {
                var process = System.Diagnostics.Process.GetCurrentProcess();
                IntPtr handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero) return;
                ShowWindow(handle, 9);
                SetForegroundWindow(handle);
            }
        }
#endif
    }
}
