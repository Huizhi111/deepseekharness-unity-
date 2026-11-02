using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityAgentBridge
{
    /// <summary>Keeps a bounded ring buffer of Unity console messages so an agent can read them.</summary>
    public static class BridgeLog
    {
        public sealed class Entry
        {
            public long seq;
            public string type;
            public string message;
            public string stack;
            public double time;
        }

        const int Capacity = 3000;
        static readonly object Gate = new object();
        static readonly List<Entry> Buffer = new List<Entry>(Capacity);
        static long _seq;
        static bool _installed;

        [UnityEditor.InitializeOnLoadMethod]
        static void InstallOnEditorLoad()
        {
            EnsureInstalled();
        }

        public static int Count
        {
            get { lock (Gate) return Buffer.Count; }
        }

        public static long LatestSeq
        {
            get { return System.Threading.Interlocked.Read(ref _seq); }
        }

        public static void EnsureInstalled()
        {
            if (_installed) return;
            _installed = true;
            Application.logMessageReceivedThreaded += OnMessage;
        }

        static void OnMessage(string condition, string stackTrace, LogType type)
        {
            var entry = new Entry
            {
                seq = System.Threading.Interlocked.Increment(ref _seq),
                type = TypeName(type),
                message = condition,
                stack = stackTrace,
                time = Time.realtimeSinceStartup
            };

            lock (Gate)
            {
                Buffer.Add(entry);
                if (Buffer.Count > Capacity) Buffer.RemoveRange(0, Buffer.Count - Capacity);
            }
        }

        public static void Clear()
        {
            lock (Gate) Buffer.Clear();
        }

        public static List<Entry> Query(long sinceSeq, string[] types, string filter, int count)
        {
            var result = new List<Entry>();
            lock (Gate)
            {
                for (int i = 0; i < Buffer.Count; i++)
                {
                    var e = Buffer[i];
                    if (e.seq <= sinceSeq) continue;
                    if (types != null && types.Length > 0 && !MatchesType(e.type, types)) continue;
                    if (!string.IsNullOrEmpty(filter) &&
                        (e.message == null || e.message.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                    result.Add(e);
                }
            }

            if (count > 0 && result.Count > count)
                result = result.GetRange(result.Count - count, count);
            return result;
        }

        /// <summary>Recent compiler errors (heuristic, based on message shape).</summary>
        public static List<Entry> CompileErrors(int max)
        {
            var all = Query(0, new[] { "error", "exception", "assert" }, null, 0);
            var result = new List<Entry>();
            for (int i = all.Count - 1; i >= 0 && result.Count < max; i--)
            {
                var e = all[i];
                string m = e.message ?? "";
                if (m.Contains("error CS") || m.Contains("Compilation failed") || m.Contains("compiler errors") ||
                    (e.stack != null && e.stack.Contains("CSharpCompiler")))
                {
                    result.Add(e);
                }
            }
            result.Reverse();
            return result;
        }

        static bool MatchesType(string entryType, string[] types)
        {
            for (int i = 0; i < types.Length; i++)
            {
                string t = types[i];
                if (string.IsNullOrEmpty(t)) continue;
                if (string.Equals(t, entryType, StringComparison.OrdinalIgnoreCase)) return true;
                if (t == "log" && entryType == "log") return true;
                if (t == "error" && (entryType == "error" || entryType == "exception" || entryType == "assert")) return true;
            }
            return false;
        }

        static string TypeName(LogType type)
        {
            switch (type)
            {
                case LogType.Error: return "error";
                case LogType.Assert: return "assert";
                case LogType.Warning: return "warning";
                case LogType.Exception: return "exception";
                default: return "log";
            }
        }

        public static Dictionary<string, object> ToDict(Entry e)
        {
            return new Dictionary<string, object>
            {
                { "seq", e.seq },
                { "type", e.type },
                { "message", e.message },
                { "stack", string.IsNullOrEmpty(e.stack) ? null : TrimStack(e.stack) },
                { "time", Math.Round(e.time, 3) }
            };
        }

        static string TrimStack(string stack)
        {
            const int limit = 2000;
            return stack.Length <= limit ? stack : stack.Substring(0, limit) + "...";
        }
    }
}
