using System;
using System.Collections.Generic;

namespace UnityAgentBridge
{
    public sealed class CommandInfo
    {
        public string Name;
        public string Summary;
        public string Args;
        public Func<Dictionary<string, object>, object> Handler;
    }

    /// <summary>Command table shared by every command module.</summary>
    public static class CommandRegistry
    {
        static readonly Dictionary<string, CommandInfo> Table =
            new Dictionary<string, CommandInfo>(StringComparer.OrdinalIgnoreCase);

        public static Dictionary<string, CommandInfo> Commands
        {
            get { return Table; }
        }

        static CommandRegistry()
        {
            SystemCommands.Register();
            SceneCommands.Register();
            ObjectCommands.Register();
            ComponentCommands.Register();
            EditorCommands.Register();
            ConsoleCommands.Register();
            ScreenshotCommands.Register();
            AssetCommands.Register();
            GameCommands.Register();
            DynamicCommands.Register();
        }

        public static void Register(string name, string summary, string args, Func<Dictionary<string, object>, object> handler)
        {
            if (string.IsNullOrEmpty(name) || handler == null) return;
            Table[name] = new CommandInfo
            {
                Name = name,
                Summary = summary ?? "",
                Args = args ?? "",
                Handler = handler
            };
        }

        public static List<object> Describe()
        {
            var list = new List<object>();
            var names = new List<string>(Table.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Count; i++)
            {
                var info = Table[names[i]];
                list.Add(new Dictionary<string, object>
                {
                    { "name", info.Name },
                    { "summary", info.Summary },
                    { "args", info.Args }
                });
            }
            return list;
        }

        public static Dictionary<string, object> Execute(string name, Dictionary<string, object> args)
        {
            if (string.IsNullOrEmpty(name))
                return Fail("缺少命令名", "bad_request");

            CommandInfo info;
            if (!Table.TryGetValue(name, out info))
                return Fail("未知命令 " + name + "（用 commands.list 查看全部命令）", "unknown_command");

            if (args == null) args = new Dictionary<string, object>();

            try
            {
                object result = info.Handler(args);
                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "result", result }
                };
            }
            catch (CommandException e)
            {
                return Fail(e.Message, e.Code);
            }
            catch (Exception e)
            {
                return new Dictionary<string, object>
                {
                    { "ok", false },
                    { "error", e.Message },
                    { "code", "exception" },
                    { "stack", e.StackTrace }
                };
            }
        }

        public static Dictionary<string, object> Fail(string message, string code)
        {
            return new Dictionary<string, object>
            {
                { "ok", false },
                { "error", message },
                { "code", code ?? "error" }
            };
        }

        /// <summary>Convenience for handlers that need to require a string argument.</summary>
        public static string Require(Dictionary<string, object> args, string key, string hint = null)
        {
            string value = J.Str(args, key);
            if (string.IsNullOrEmpty(value))
                throw new CommandException("缺少必填参数 " + key + (hint != null ? "（" + hint + "）" : ""), "bad_request");
            return value;
        }
    }
}
