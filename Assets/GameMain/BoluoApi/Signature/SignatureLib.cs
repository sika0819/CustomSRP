using System;
using System.Threading;
using Game.Native;
using UnityGameFramework.Runtime;
using UnityEngine;

namespace Game.BoluoApi
{
    [Serializable]
    class SignResultInfo
    {
        public int code;
        public string message;
        public SignResultData data;
    }

    [Serializable]
    class SignResultData
    {
        public string sign;
        public string nonceStr;
    }

    /// <summary>
    /// 主线程串行调用 libSignKit GenerateSign（与 BoluoProject SignatureLib 对齐）。
    /// iOS hybrid：设置 <see cref="NativeSignProvider"/>，由原生 SignKit 回签 JSON。
    /// </summary>
    public static class SignatureLib
    {
        static readonly object SignLock = new object();
        static int _mainThreadId;
        static SignEnvType _env = SignEnvType.EnvDev;
        static bool _inited;

        /// <summary>
        /// 可选：覆盖 AOT SignKit。默认走 <see cref="NativeSession"/>。
        /// 入参为待签 body JSON，返回 SignKit 原始结果
        /// <c>{"code":0,"data":{"sign":"...","nonceStr":"..."}}</c>。
        /// </summary>
        public static Func<string, string> NativeSignProvider { get; set; }

        public static void Init()
        {
            if (_inited)
                return;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _env = MapEnv(BoluoApiEnvironment.GetEnvironmentType());
            NativeSession.SetSignEnvironment((int)_env);
            _inited = true;
            Log.Info($"[SignatureLib] Init env={_env} apiEnv={BoluoApiEnvironment.GetEnvironmentType()}");
        }

        public static void SetEnvironment(SignEnvType env)
        {
            _env = env;
            NativeSession.SetSignEnvironment((int)env);
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor 测试覆盖：切 API 环境时同步 SignKit（prerelease → PRODUCT，与真机一致）。
        /// </summary>
        public static void ApplyEditorEnvironmentOverride(string environmentType)
        {
            if (!_inited)
                Init();
            SetEnvironment(MapEnv(environmentType));
            Log.Info($"[SignatureLib] Editor 签名环境覆盖: {environmentType} -> {_env}");
        }

        /// <summary>清除覆盖后恢复编译期宏决定的签名环境。</summary>
        public static void ResetEditorEnvironmentToCompileDefault()
        {
#if BOLUO_PRODUCT || BOLUO_PRERELEASE
            SetEnvironment(SignEnvType.EnvProduct);
#elif BOLUO_TEST
            SetEnvironment(SignEnvType.EnvTest);
#else
            SetEnvironment(SignEnvType.EnvDev);
#endif
            Log.Info($"[SignatureLib] Editor 签名环境恢复编译默认: {_env}");
        }
#endif

        public static void SignatureGeneration(string jsonStr, out string sign, out string nonceStr)
        {
            sign = "";
            nonceStr = "";
            EnsureMainThread();
            if (!_inited)
                Init();

            lock (SignLock)
            {
                try
                {
                    string raw;
                    if (NativeSignProvider != null)
                        raw = NativeSignProvider(jsonStr);
                    else
                        raw = NativeSession.GenerateSign(jsonStr);
                    if (!TryParse(raw, out sign, out nonceStr))
                        Log.Error($"[SignatureLib] parse failed: {raw}");
                }
                catch (Exception e)
                {
                    Log.Error($"[SignatureLib] GenerateSign failed: {e.Message}");
                }
            }
        }

        static bool TryParse(string raw, out string sign, out string nonceStr)
        {
            sign = "";
            nonceStr = "";
            if (string.IsNullOrEmpty(raw))
                return false;
            var res = JsonUtility.FromJson<SignResultInfo>(raw);
            if (res == null || res.code != 0 || res.data == null)
                return false;
            sign = res.data.sign ?? "";
            nonceStr = res.data.nonceStr ?? "";
            return !string.IsNullOrEmpty(sign);
        }

        static void EnsureMainThread()
        {
            if (_mainThreadId == 0)
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                throw new InvalidOperationException(
                    "GenerateSign must run on the Unity main thread.");
            }
        }

        static SignEnvType MapEnv(string environmentType)
        {
            return environmentType switch
            {
                "product" => SignEnvType.EnvProduct,
                "prerelease" => SignEnvType.EnvProduct,
                "test" => SignEnvType.EnvTest,
                _ => SignEnvType.EnvDev,
            };
        }
    }
}
