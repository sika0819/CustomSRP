#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Threading;

namespace Cysharp.Threading.Tasks
{
    // UniTask has no scheduler like TaskScheduler.
    // Only handle unobserved exception.

    public static class UniTaskScheduler
    {
        public static event Action<Exception> UnobservedTaskException;

        /// <summary>
        /// Propagate OperationCanceledException to UnobservedTaskException when true. Default is false.
        /// </summary>
        private static readonly bool PropagateOperationCanceledException = false;

#if UNITY_2018_3_OR_NEWER

        /// <summary>
        /// Write log type when catch unobserved exception and not registered UnobservedTaskException. Default is Exception.
        /// </summary>
        private static readonly UnityEngine.LogType UnobservedExceptionWriteLogType = UnityEngine.LogType.Exception;

        /// <summary>
        /// Dispatch exception event to Unity MainThread. Default is true.
        /// </summary>
        private static readonly bool DispatchUnityMainThread = true;
        
        // cache delegate.
        static readonly SendOrPostCallback HandleExceptionInvoke = InvokeUnobservedTaskException;

        static void InvokeUnobservedTaskException(object state)
        {
            if (UnobservedTaskException != null) UnobservedTaskException((Exception)state);
        }
#endif

        internal static void PublishUnobservedTaskException(Exception ex)
        {
            if (ex != null)
            {
                if (!PropagateOperationCanceledException && ex is OperationCanceledException)
                {
                    return;
                }

                if (UnobservedTaskException != null)
                {
#if UNITY_2018_3_OR_NEWER
                    if (!DispatchUnityMainThread || Thread.CurrentThread.ManagedThreadId == PlayerLoopHelper.MainThreadId)
                    {
                        // allows inlining call.
                        UnobservedTaskException.Invoke(ex);
                    }
                    else
                    {
                        // Post to MainThread.
                        PlayerLoopHelper.UnitySynchronizationContext.Post(HandleExceptionInvoke, ex);
                    }
#else
                    UnobservedTaskException.Invoke(ex);
#endif
                }
                else
                {
#if UNITY_2018_3_OR_NEWER
                    string msg = null;
                    if (UnobservedExceptionWriteLogType != UnityEngine.LogType.Exception)
                    {
                        msg = "UnobservedTaskException: " + ex;
                    }
                    switch (UnobservedExceptionWriteLogType)
                    {
                        case UnityEngine.LogType.Error:
                            UnityEngine.Debug.LogError(msg);
                            break;
                        case UnityEngine.LogType.Assert:
                            UnityEngine.Debug.LogAssertion(msg);
                            break;
                        case UnityEngine.LogType.Warning:
                            UnityEngine.Debug.LogWarning(msg);
                            break;
                        case UnityEngine.LogType.Log:
                            UnityEngine.Debug.Log(msg);
                            break;
                        case UnityEngine.LogType.Exception:
                            UnityEngine.Debug.LogException(ex);
                            break;
                    }
#else
                    Console.WriteLine("UnobservedTaskException: " + ex.ToString());
#endif
                }
            }
        }
    }
}

