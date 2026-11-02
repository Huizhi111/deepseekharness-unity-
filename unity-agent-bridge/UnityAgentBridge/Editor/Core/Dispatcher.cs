using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge
{
    /// <summary>
    /// Marshals work from socket threads onto the Unity main thread and pumps the queue
    /// from <see cref="EditorApplication.update"/>.
    /// </summary>
    [InitializeOnLoad]
    public static class Dispatcher
    {
        sealed class WorkItem
        {
            public Func<object> Action;
            public object Result;
            public Exception Error;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        static readonly ConcurrentQueue<WorkItem> Queue = new ConcurrentQueue<WorkItem>();
        static int _mainThreadId;

        static Dispatcher()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            EditorApplication.update += Pump;
        }

        public static bool IsMainThread
        {
            get { return Thread.CurrentThread.ManagedThreadId == _mainThreadId; }
        }

        /// <summary>Runs queued work items. Safe to call more than once per frame.</summary>
        public static void Pump()
        {
            WorkItem item;
            int guard = 0;
            while (Queue.TryDequeue(out item))
            {
                try
                {
                    item.Result = item.Action();
                }
                catch (Exception e)
                {
                    item.Error = e;
                }
                finally
                {
                    item.Done.Set();
                }

                guard++;
                if (guard > 64) break;
            }
        }

        /// <summary>
        /// Executes <paramref name="action"/> on the main thread and waits for it.
        /// Returns a small envelope so callers never have to unwrap exceptions.
        /// </summary>
        public static InvokeResult Invoke(Func<object> action, int timeoutMs)
        {
            if (IsMainThread)
            {
                try
                {
                    return InvokeResult.Ok(action());
                }
                catch (Exception e)
                {
                    return InvokeResult.Fail(e);
                }
            }

            var item = new WorkItem { Action = action };
            Queue.Enqueue(item);

            bool signalled = item.Done.Wait(timeoutMs <= 0 ? 120000 : timeoutMs);
            if (!signalled)
            {
                BridgeServer.Log("主线程未响应（队列长度 " + Queue.Count + "，主线程标记=" + _mainThreadId + "，当前线程=" + Thread.CurrentThread.ManagedThreadId + "）");
                return InvokeResult.Timeout("Unity 主线程在 " + timeoutMs + "ms 内没有响应（可能正在编译、导入资源或弹出模态对话框）");
            }

            if (item.Error != null) return InvokeResult.Fail(item.Error);
            return InvokeResult.Ok(item.Result);
        }
    }

    public sealed class InvokeResult
    {
        public bool Success;
        public bool TimedOut;
        public object Value;
        public Exception Error;
        public string TimeoutMessage;

        public static InvokeResult Ok(object value)
        {
            return new InvokeResult { Success = true, Value = value };
        }

        public static InvokeResult Fail(Exception error)
        {
            return new InvokeResult { Success = false, Error = error };
        }

        public static InvokeResult Timeout(string message)
        {
            return new InvokeResult { Success = false, TimedOut = true, TimeoutMessage = message };
        }
    }
}
