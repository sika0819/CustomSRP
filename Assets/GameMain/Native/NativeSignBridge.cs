using System;
using System.Runtime.InteropServices;
using UnityGameFramework.Runtime;
using UnityEngine;
using UnityEngine.Scripting;

namespace Game.Native
{
    /// <summary>
    /// AOT 侧 SignKit：热更里 DllImport(string) 会被 HybridCLR 错误封送。
    /// Android/Editor 直链 libSignKit；iOS hybrid 走 BoluoImp_GenerateSign（Swift @_cdecl）。
    /// </summary>
    [Preserve]
    public static class NativeSignBridge
    {
#if UNITY_IOS && !UNITY_EDITOR
        const string Lib = "__Internal";

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr BoluoImp_GenerateSign(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string jsonBody);
#else
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        const string SignKitLib = "SignKit";
#else
        const string SignKitLib = "libSignKit";
#endif

        [DllImport(SignKitLib, CallingConvention = CallingConvention.Cdecl)]
        static extern void SetEnv(int env);

        [DllImport(SignKitLib, CallingConvention = CallingConvention.Cdecl)]
        static extern int GetEnv();

        [DllImport(SignKitLib, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr GenerateSign(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string jsonStr,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string fixedNonceStr);
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void PreserveAot()
        {
            _ = (Action<int>)SetEnvironment;
            _ = (Func<int>)GetEnvironment;
            _ = (Func<string, string>)Generate;
        }

        public static void SetEnvironment(int env)
        {
#if UNITY_IOS && !UNITY_EDITOR
            return;
#else
            SetEnv(env);
#endif
        }

        public static int GetEnvironment()
        {
#if UNITY_IOS && !UNITY_EDITOR
            return -1;
#else
            return GetEnv();
#endif
        }

        public static string Generate(string jsonBody)
        {
            return Generate(jsonBody, null);
        }

        public static string Generate(string jsonBody, string fixedNonceStr)
        {
            try
            {
#if UNITY_IOS && !UNITY_EDITOR
                IntPtr ptr = BoluoImp_GenerateSign(jsonBody ?? "{}");
#else
                IntPtr ptr = GenerateSign(jsonBody ?? "{}", fixedNonceStr);
#endif
                if (ptr == IntPtr.Zero)
                    return null;
                return Marshal.PtrToStringUTF8(ptr);
            }
            catch (Exception e)
            {
                Log.Warning($"[IMP] GenerateSign failed: {e.Message}");
                return null;
            }
        }
    }
}
