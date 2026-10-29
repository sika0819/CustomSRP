using System;
using System.Runtime.InteropServices;
using UnityGameFramework.Runtime;
using UnityEngine;

namespace Game.Native
{
    /// <summary>U→N 传输：Notify / Query。DllImport 与 JNI 集中于此。</summary>
    public static class NativeBridgeTransport
    {
        const int MaxBlobBytes = 16 * 1024;

#if UNITY_ANDROID && !UNITY_EDITOR
        const string AndroidPluginClass = "com.wanos.boluo.unity.BoluoUnityPlugin";
        static AndroidJavaObject _plugin;
#endif

#if UNITY_IOS && !UNITY_EDITOR
        const string Lib = "__Internal";

        // BoluoImp_*：iOS 在 UnityFramework 的 BoluoImp.swift。
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern void BoluoImp_Notify(int type, int a, int b, int c);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr BoluoImp_Query(int type, int a, int b, int c, string inBlob);
#endif

        public static bool IsHybridLikely
        {
            get
            {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        public static void Notify(int type, int a = 0, int b = 0, int c = 0)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                var plugin = GetAndroidPlugin();
                if (plugin == null)
                {
                    Log.Warning($"[IMP] Notify type={type} plugin null");
                    return;
                }

                plugin.Call("onUnityNotify", type, a, b, c);
            }
            catch (Exception e)
            {
                Log.Warning($"[IMP] Notify type={type} failed: {e.Message}");
            }
#elif UNITY_IOS && !UNITY_EDITOR
            try
            {
                BoluoImp_Notify(type, a, b, c);
            }
            catch (Exception e)
            {
                Log.Warning($"[IMP] Notify type={type} failed: {e.Message}");
            }
#elif UNITY_EDITOR
            EditorNativeHost.OnNotify(type, a, b, c);
#else
            Log.Debug($"[IMP] Notify (stub) type={type} a={a} b={b} c={c}");
#endif
        }

        /// <summary>
        /// 同步查询。返回 <see cref="BridgeCode"/>；成功时 outBlob 为载荷（可含 '|'）。
        /// 原生返回格式：`{code}|{outBlob}`。
        /// </summary>
        public static int Query(int type, int a, int b, int c, string inBlob, out string outBlob)
        {
            outBlob = "";
            if (inBlob != null && inBlob.Length > MaxBlobBytes)
                return BridgeCode.ErrBufferTooSmall;

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                var plugin = GetAndroidPlugin();
                if (plugin == null)
                    return BridgeCode.ErrNotReady;

                string raw = plugin.Call<string>("onUnityQuery", type, a, b, c, inBlob ?? "");
                return ParseQueryResult(raw, out outBlob);
            }
            catch (Exception e)
            {
                Log.Warning($"[IMP] Query type={type} failed: {e.Message}");
                return BridgeCode.ErrNotReady;
            }
#elif UNITY_IOS && !UNITY_EDITOR
            try
            {
                IntPtr ptr = BoluoImp_Query(type, a, b, c, inBlob);
                if (ptr == IntPtr.Zero)
                    return BridgeCode.ErrNotReady;
                string raw = Marshal.PtrToStringUTF8(ptr) ?? "";
                return ParseQueryResult(raw, out outBlob);
            }
            catch (Exception e)
            {
                Log.Warning($"[IMP] Query type={type} failed: {e.Message}");
                return BridgeCode.ErrNotReady;
            }
#elif UNITY_EDITOR
            return EditorNativeHost.Query(type, a, b, c, inBlob, out outBlob);
#else
            Log.Debug($"[IMP] Query (stub) type={type} a={a} b={b} c={c}");
            outBlob = "";
            return BridgeCode.ErrNotReady;
#endif
        }

        /// <summary>握手：Query(0) 成功且 version≥1 视为 hybrid 可用。</summary>
        public static bool IsHybridAvailable()
        {
            int code = Query(MsgType.ProtocolHandshake, 0, 0, 0, null, out string ver);
            if (code != BridgeCode.Ok)
                return false;
            return int.TryParse(ver, out int v) && v >= 1;
        }

        static int ParseQueryResult(string raw, out string outBlob)
        {
            outBlob = "";
            if (string.IsNullOrEmpty(raw))
                return BridgeCode.ErrGeneric;

            int sep = raw.IndexOf('|');
            if (sep < 0)
                return BridgeCode.ErrGeneric;

            if (!int.TryParse(raw.Substring(0, sep), out int code))
                return BridgeCode.ErrGeneric;

            outBlob = sep + 1 < raw.Length ? raw.Substring(sep + 1) : "";
            if (outBlob.Length > MaxBlobBytes)
            {
                outBlob = "";
                return BridgeCode.ErrBufferTooSmall;
            }

            return code;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static AndroidJavaObject GetAndroidPlugin()
        {
            if (_plugin != null)
                return _plugin;
            try
            {
                _plugin = new AndroidJavaClass(AndroidPluginClass)
                    .CallStatic<AndroidJavaObject>("getInstance");
            }
            catch (Exception e)
            {
                Log.Warning($"[IMP] Android plugin unavailable: {e.Message}");
            }

            return _plugin;
        }
#endif

        /// <summary>宿主身份：Android / iOS 都走 Query(21)。非 hybrid 或失败返回空。</summary>
        public static string GetHostChannel() => HostIdentity.Channel;

        public static string GetHostOriginEnd() => HostIdentity.OriginEnd;

        public static string GetHostApiEnvironmentType() => HostIdentity.Env;

        public static int GetHostAppId() => HostIdentity.AppId;

        public static string GetHostDeviceId() => HostIdentity.DeviceId;

        static class HostIdentity
        {
            static bool _loaded;
            static string _channel = "";
            static string _originEnd = "";
            static string _env = "";
            static string _deviceId = "";
            static int _appId;

            public static string Channel { get { Ensure(); return _channel; } }
            public static string OriginEnd { get { Ensure(); return _originEnd; } }
            public static string Env { get { Ensure(); return _env; } }
            public static int AppId { get { Ensure(); return _appId; } }
            public static string DeviceId { get { Ensure(); return _deviceId; } }

            static void Ensure()
            {
                if (_loaded)
                    return;
                _loaded = true;
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
                int code = Query(MsgType.HostGetIdentity, 0, 0, 0, null, out string blob);
                if (code != BridgeCode.Ok || string.IsNullOrEmpty(blob))
                    return;
                string[] parts = blob.Split('\t');
                if (parts.Length >= 1) _channel = parts[0] ?? "";
                if (parts.Length >= 2) _originEnd = parts[1] ?? "";
                if (parts.Length >= 3) int.TryParse(parts[2], out _appId);
                if (parts.Length >= 4) _env = parts[3] ?? "";
                if (parts.Length >= 5) _deviceId = parts[4] ?? "";
                Log.Info(
                    $"[IMP] host identity channel={_channel} originEnd={_originEnd} appId={_appId} " +
                    $"env={_env} deviceIdLen={_deviceId.Length}");
#endif
            }
        }
    }
}
