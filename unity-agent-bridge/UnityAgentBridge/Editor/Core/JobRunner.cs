using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityAgentBridge
{
    /// <summary>
    /// Executes a dynamically compiled snippet from inside the generated editor script
    /// and persists the outcome next to the project's Library folder.
    /// </summary>
    public static class JobRunner
    {
        public static string ResultPath(string jobId)
        {
            return Path.Combine(BridgePaths.JobsDir, jobId + ".result.json");
        }

        public static string GeneratedAssetPath(string jobId)
        {
            return BridgePaths.GeneratedFolderAssetPath + "/DynamicJob_" + jobId + ".cs";
        }

        public static void Execute(string jobId, Func<object> body)
        {
            string resultPath = ResultPath(jobId);
            if (File.Exists(resultPath))
            {
                // Already executed in a previous domain; just make sure the generated file goes away.
                ScheduleCleanup(jobId);
                return;
            }

            var started = DateTime.UtcNow;
            long logSeq = BridgeLog.LatestSeq;
            object result = null;
            string error = null;
            string stack = null;

            try
            {
                result = body();
            }
            catch (Exception e)
            {
                error = e.Message;
                stack = e.ToString();
            }

            Dictionary<string, object> payload;
            try
            {
                payload = new Dictionary<string, object>
                {
                    { "jobId", jobId },
                    { "ok", error == null },
                    { "result", Encode(result, 0) },
                    { "error", error },
                    { "stack", stack },
                    { "durationMs", (int)(DateTime.UtcNow - started).TotalMilliseconds },
                    { "finishedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                    { "logs", CollectLogs(logSeq) }
                };
            }
            catch (Exception e)
            {
                payload = new Dictionary<string, object>
                {
                    { "jobId", jobId },
                    { "ok", false },
                    { "error", "结果序列化失败: " + e.Message },
                    { "stack", e.ToString() }
                };
            }

            try
            {
                Directory.CreateDirectory(BridgePaths.JobsDir);
                File.WriteAllText(resultPath, Json.SerializePretty(payload));
            }
            catch (Exception e)
            {
                Debug.LogError("[AgentBridge] 写入任务结果失败: " + e.Message);
            }

            ScheduleCleanup(jobId);
        }

        static void ScheduleCleanup(string jobId)
        {
            EditorApplication.delayCall += delegate { Cleanup(jobId); };
        }

        public static void Cleanup(string jobId)
        {
            try
            {
                string assetPath = GeneratedAssetPath(jobId);
                if (AssetDatabase.LoadAssetAtPath<Object>(assetPath) != null || File.Exists(FullPath(assetPath)))
                {
                    AssetDatabase.DeleteAsset(assetPath);
                    AssetDatabase.Refresh();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AgentBridge] 清理动态脚本失败: " + e.Message);
            }
        }

        public static string FullPath(string assetPath)
        {
            return Path.Combine(BridgePaths.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        static List<object> CollectLogs(long sinceSeq)
        {
            var entries = BridgeLog.Query(sinceSeq, null, null, 200);
            var list = new List<object>();
            for (int i = 0; i < entries.Count; i++) list.Add(BridgeLog.ToDict(entries[i]));
            return list;
        }

        // ------------------------------------------------------------- encoding

        /// <summary>Turns arbitrary runtime values into JSON friendly structures.</summary>
        public static object Encode(object value, int depth)
        {
            if (value == null) return null;
            if (depth > 6) return SafeToString(value);

            var text = value as string;
            if (text != null) return text;
            if (value is bool) return value;
            if (value is char) return value.ToString();

            if (value is double)
            {
                double d = (double)value;
                return double.IsNaN(d) || double.IsInfinity(d) ? (object)null : d;
            }
            if (value is float)
            {
                float f = (float)value;
                return float.IsNaN(f) || float.IsInfinity(f) ? (object)null : (double)f;
            }
            if (value is int || value is long || value is short || value is byte ||
                value is uint || value is ulong || value is sbyte || value is ushort)
                return value;
            if (value is decimal) return (double)(decimal)value;
            if (value is Enum) return value.ToString();

            var unityObject = value as Object;
            if (unityObject != null) return Obj.DescribeObject(unityObject);

            if (value is Vector2) return Obj.Vec((Vector2)value);
            if (value is Vector3) return Obj.Vec((Vector3)value);
            if (value is Vector4)
            {
                var v = (Vector4)value;
                return new List<double> { v.x, v.y, v.z, v.w };
            }
            if (value is Quaternion) return Obj.Vec(((Quaternion)value).eulerAngles);
            if (value is Color) return Obj.ColorVec((Color)value);
            if (value is Rect)
            {
                var r = (Rect)value;
                return new Dictionary<string, object>
                {
                    { "x", r.x }, { "y", r.y }, { "width", r.width }, { "height", r.height }
                };
            }
            if (value is Bounds)
            {
                var b = (Bounds)value;
                return new Dictionary<string, object> { { "center", Obj.Vec(b.center) }, { "size", Obj.Vec(b.size) } };
            }

            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                var result = new Dictionary<string, object>();
                foreach (DictionaryEntry entry in dictionary)
                    result[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)] = Encode(entry.Value, depth + 1);
                return result;
            }

            var enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                var list = new List<object>();
                int count = 0;
                foreach (object item in enumerable)
                {
                    if (count++ >= 500) { list.Add("<truncated>"); break; }
                    list.Add(Encode(item, depth + 1));
                }
                return list;
            }

            return SafeToString(value);
        }

        static string SafeToString(object value)
        {
            try { return Convert.ToString(value, CultureInfo.InvariantCulture); }
            catch (Exception) { return value.GetType().Name; }
        }
    }
}
