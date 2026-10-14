using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityGameFramework.Runtime;

namespace Game.Utility
{
    public static class Http
    {
            /// <summary>
            /// HTTP 重试策略（对齐 BoluoProject / UGF 下载侧：指数退避、等网、卡死检测）。
            /// </summary>
            public sealed class RetryPolicy
            {
                /// <summary>单请求最大尝试次数（含首次）。</summary>
                public int MaxAttempts = 3;

                /// <summary>网络失败后基础等待（秒）。</summary>
                public float RetryBaseSeconds = 5f;

                /// <summary>指数退避上限（秒）。</summary>
                public float RetryMaxSeconds = 30f;

                /// <summary>下载无进度视为卡住（秒）；≤0 关闭。</summary>
                public float StallTimeoutSeconds = 30f;

                /// <summary>无网时轮询间隔（秒）。</summary>
                public float NetworkCheckIntervalSeconds = 2f;

                /// <summary>单次请求 timeout（秒），写入 UnityWebRequest.timeout。</summary>
                public int RequestTimeoutSeconds = 30;

                /// <summary>为 true 时不预先空等网络，无网直接失败。</summary>
                public bool SkipWaitForNetwork;

                public static RetryPolicy DefaultApi => new RetryPolicy
                {
                    MaxAttempts = 3,
                    RetryBaseSeconds = 5f,
                    RetryMaxSeconds = 30f,
                    StallTimeoutSeconds = 0f,
                    RequestTimeoutSeconds = 30,
                };

                /// <summary>启动换票 / catalog：1 次、8s，禁止空等。</summary>
                public static RetryPolicy BootApi => new RetryPolicy
                {
                    MaxAttempts = 1,
                    RetryBaseSeconds = 0f,
                    RetryMaxSeconds = 0f,
                    StallTimeoutSeconds = 0f,
                    RequestTimeoutSeconds = 8,
                    SkipWaitForNetwork = true,
                };

                public static RetryPolicy DefaultDownload => new RetryPolicy
                {
                    MaxAttempts = 3,
                    RetryBaseSeconds = 5f,
                    RetryMaxSeconds = 30f,
                    StallTimeoutSeconds = 30f,
                    RequestTimeoutSeconds = 120,
                };
            }

            /// <summary>是否像传输层/网络错误（可重试）。</summary>
            public static bool IsRetryableNetworkError(UnityWebRequest req)
            {
                if (req == null)
                    return true;
                if (req.result == UnityWebRequest.Result.ConnectionError)
                    return true;
                if (req.result == UnityWebRequest.Result.DataProcessingError)
                    return true;
                // 5xx / 408 / 429
                long code = req.responseCode;
                if (code == 408 || code == 429 || code >= 500)
                    return true;
                return IsRetryableNetworkError(req.error);
            }

            public static bool IsRetryableNetworkError(string error)
            {
                if (string.IsNullOrEmpty(error))
                    return false;
                string e = error.ToLowerInvariant();
                return e.Contains("timeout")
                       || e.Contains("timed out")
                       || e.Contains("connection")
                       || e.Contains("network")
                       || e.Contains("unreachable")
                       || e.Contains("resolve")
                       || e.Contains("temporary")
                       || e.Contains("reset")
                       || e.Contains("broken pipe");
            }

            /// <summary>无网则等待恢复（可被 maxWaitSeconds 截断；≤0 一直等）。</summary>
            public static IEnumerator WaitForNetwork(RetryPolicy policy, float maxWaitSeconds = 0f)
            {
                float interval = policy?.NetworkCheckIntervalSeconds > 0
                    ? policy.NetworkCheckIntervalSeconds
                    : 2f;
                float waited = 0f;
                while (Application.internetReachability == NetworkReachability.NotReachable)
                {
                    if (maxWaitSeconds > 0f && waited >= maxWaitSeconds)
                        yield break;
                    yield return new WaitForSecondsRealtime(interval);
                    waited += interval;
                }
            }

            /// <summary>
            /// 按策略重试执行一次「可重建」的请求。
            /// <paramref name="sendOnce"/> 每次尝试须新建并 Send UnityWebRequest，完成后回调 req（由调用方 Dispose）。
            /// </summary>
            public static IEnumerator SendWithRetry(
                Func<UnityWebRequest> createRequest,
                RetryPolicy policy,
                Action<UnityWebRequest> onFinished,
                Func<UnityWebRequest, bool> isSuccess = null,
                Action<int, float, string> onRetry = null,
                Action<float> onProgress = null)
            {
                policy ??= RetryPolicy.DefaultApi;
                int max = Math.Max(1, policy.MaxAttempts);
                float delay = policy.RetryBaseSeconds;
                isSuccess ??= (r => r != null && r.result == UnityWebRequest.Result.Success);

                for (int attempt = 1; attempt <= max; attempt++)
                {
                    if (!policy.SkipWaitForNetwork)
                        yield return WaitForNetwork(policy);

                    if (Application.internetReachability == NetworkReachability.NotReachable)
                    {
                        onRetry?.Invoke(attempt, delay, "no network");
                        if (attempt >= max)
                        {
                            onFinished?.Invoke(null);
                            yield break;
                        }

                        yield return new WaitForSecondsRealtime(delay);
                        delay = Mathf.Min(delay * 2f, policy.RetryMaxSeconds);
                        continue;
                    }

                    UnityWebRequest req = null;
                    Exception createError = null;
                    try
                    {
                        req = createRequest();
                    }
                    catch (Exception e)
                    {
                        createError = e;
                    }

                    if (createError != null)
                    {
                        Log.Error($"[Http.Retry] createRequest failed: {createError.Message}");
                        if (attempt >= max)
                        {
                            onFinished?.Invoke(null);
                            yield break;
                        }

                        yield return new WaitForSecondsRealtime(delay);
                        delay = Mathf.Min(delay * 2f, policy.RetryMaxSeconds);
                        continue;
                    }

                    if (req.timeout <= 0 && policy.RequestTimeoutSeconds > 0)
                        req.timeout = policy.RequestTimeoutSeconds;

                    bool stalled = false;
                    yield return SendObservingStall(req, policy, () => stalled = true, onProgress);

                    bool ok = !stalled && isSuccess(req);
                    if (ok)
                    {
                        onFinished?.Invoke(req);
                        yield break;
                    }

                    string err = stalled ? "stall timeout" : (req.error ?? $"code={req.responseCode}");
                    bool retryable = stalled || IsRetryableNetworkError(req);
                    req.Dispose();

                    if (!retryable || attempt >= max)
                    {
                        Log.Warning($"[Http.Retry] give up attempt={attempt}/{max} err={err}");
                        onFinished?.Invoke(null);
                        yield break;
                    }

                    onRetry?.Invoke(attempt, delay, err);
                    Log.Warning($"[Http.Retry] attempt={attempt}/{max} retry in {delay:0.#}s err={err}");
                    yield return new WaitForSecondsRealtime(delay);
                    delay = Mathf.Min(delay * 2f, policy.RetryMaxSeconds);
                }
            }

            /// <summary>GET + 重试，成功回传响应文本；失败回传 null。</summary>
            public static IEnumerator GetWithRetry(
                string url,
                Action<string> onResult,
                RetryPolicy policy = null)
            {
                string body = null;
                yield return SendWithRetry(
                    () => UnityWebRequest.Get(url),
                    policy ?? RetryPolicy.DefaultApi,
                    req =>
                    {
                        if (req != null && req.result == UnityWebRequest.Result.Success)
                            body = req.downloadHandler?.text;
                        req?.Dispose();
                    });
                onResult?.Invoke(body);
            }

            /// <summary>原始 POST（application/json）+ 重试。</summary>
            public static IEnumerator PostJsonWithRetry(
                string url,
                byte[] bodyBytes,
                Action<string> onResult,
                RetryPolicy policy = null,
                Action<UnityWebRequest> configure = null)
            {
                string text = null;
                yield return SendWithRetry(
                    () =>
                    {
                        var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
                        req.uploadHandler = new UploadHandlerRaw(bodyBytes ?? Array.Empty<byte>());
                        req.downloadHandler = new DownloadHandlerBuffer();
                        req.SetRequestHeader("Content-Type", "application/json");
                        configure?.Invoke(req);
                        return req;
                    },
                    policy ?? RetryPolicy.DefaultApi,
                    req =>
                    {
                        if (req != null && req.result == UnityWebRequest.Result.Success)
                            text = req.downloadHandler?.text;
                        req?.Dispose();
                    });
                onResult?.Invoke(text);
            }

            /// <summary>文件下载 + 重试（含卡死检测）。落盘路径每次尝试覆盖。</summary>
            public static IEnumerator DownloadFileWithRetry(
                string url,
                string savePath,
                Action<bool, string> onResult,
                RetryPolicy policy = null,
                Action<float> onProgress = null)
            {
                policy ??= RetryPolicy.DefaultDownload;
                bool ok = false;
                string err = null;

                yield return SendWithRetry(
                    () =>
                    {
                        var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET);
                        req.downloadHandler = new DownloadHandlerFile(savePath) { removeFileOnAbort = true };
                        return req;
                    },
                    policy,
                    req =>
                    {
                        if (req != null && req.result == UnityWebRequest.Result.Success)
                            ok = true;
                        else
                            err = req?.error ?? "download failed";
                        req?.Dispose();
                    },
                    null,
                    null,
                    onProgress);

                onProgress?.Invoke(ok ? 1f : 0f);
                onResult?.Invoke(ok, err);
            }

            static IEnumerator SendObservingStall(
                UnityWebRequest req,
                RetryPolicy policy,
                Action onStall,
                Action<float> onProgress = null)
            {
                var op = req.SendWebRequest();
                if (policy == null || policy.StallTimeoutSeconds <= 0f)
                {
                    while (!op.isDone)
                    {
                        onProgress?.Invoke(req.downloadProgress);
                        yield return null;
                    }

                    yield break;
                }

                ulong lastBytes = 0;
                float lastProgressTime = Time.realtimeSinceStartup;
                float stall = policy.StallTimeoutSeconds;

                while (!op.isDone)
                {
                    onProgress?.Invoke(req.downloadProgress);
                    ulong bytes = req.downloadedBytes;
                    if (bytes > lastBytes)
                    {
                        lastBytes = bytes;
                        lastProgressTime = Time.realtimeSinceStartup;
                    }
                    else if (Time.realtimeSinceStartup - lastProgressTime > stall)
                    {
                        Log.Warning($"[Http.Retry] stall >{stall}s abort {req.url}");
                        req.Abort();
                        onStall?.Invoke();
                        yield break;
                    }

                    yield return null;
                }
            }
        }
}
