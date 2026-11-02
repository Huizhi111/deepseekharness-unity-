using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityAgentBridge
{
    /// <summary>Console log access.</summary>
    public static class ConsoleCommands
    {
        public static void Register()
        {
            CommandRegistry.Register("console.get", "读取 Console 日志（按 seq 增量拉取）", "count?=50, since?=0, types?:[error|warning|log], filter?",
                delegate(Dictionary<string, object> args)
                {
                    int count = Mathf.Clamp(J.Int(args, "count", 50), 1, 2000);
                    long since = (long)J.Num(args, "since", 0);
                    string filter = J.Str(args, "filter");
                    string[] types = J.Strings(args, "types");

                    var entries = BridgeLog.Query(since, types, filter, count);
                    var list = new List<object>();
                    for (int i = 0; i < entries.Count; i++) list.Add(BridgeLog.ToDict(entries[i]));

                    return new Dictionary<string, object>
                    {
                        { "count", list.Count },
                        { "total", BridgeLog.Count },
                        { "logs", list }
                    };
                });

            CommandRegistry.Register("console.clear", "清空桥接层缓存的日志（不会清空 Unity Console 窗口）", "",
                delegate(Dictionary<string, object> args)
                {
                    int before = BridgeLog.Count;
                    BridgeLog.Clear();
                    return new Dictionary<string, object> { { "cleared", before }, { "remaining", BridgeLog.Count } };
                });

            CommandRegistry.Register("console.tail", "读取最近 N 条日志的纯文本（方便直接看）", "count?=30",
                delegate(Dictionary<string, object> args)
                {
                    int count = Mathf.Clamp(J.Int(args, "count", 30), 1, 500);
                    var entries = BridgeLog.Query(0, null, null, count);
                    var text = new System.Text.StringBuilder();
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var e = entries[i];
                        text.Append('[').Append(e.type).Append("] ").Append(e.message).Append('\n');
                    }
                    return new Dictionary<string, object> { { "count", entries.Count }, { "text", text.ToString() } };
                });
        }
    }
}
