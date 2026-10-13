using System;
using UnityEngine;

namespace YooAsset
{
    /// <summary>
    /// Android 包内 assets 同步读取。首包 <c>jar:file://...apk!/assets/...</c> 走 AssetManager，
    /// 避免 UnityWebRequest 单帧卡几百毫秒。
    /// </summary>
    internal static class AndroidStreamingAsset
    {
        const string AssetsMarker = "!/assets/";

        public static bool TryReadBytes(string filePath, out byte[] bytes)
        {
            bytes = null;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!TryGetRelativePath(filePath, out string relative))
                return false;

            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    if (activity == null)
                        return false;
                    using (var assets = activity.Call<AndroidJavaObject>("getAssets"))
                    using (var input = assets.Call<AndroidJavaObject>("open", relative))
                    {
                        bytes = Drain(input);
                        return bytes != null && bytes.Length > 0;
                    }
                }
            }
            catch (Exception e)
            {
                YooLogger.Warning($"AndroidStreamingAsset '{relative}' failed: {e.Message}");
                bytes = null;
                return false;
            }
#else
            return false;
#endif
        }

        static bool TryGetRelativePath(string filePath, out string relative)
        {
            relative = null;
            if (string.IsNullOrEmpty(filePath))
                return false;

            int marker = filePath.IndexOf(AssetsMarker, StringComparison.Ordinal);
            if (marker >= 0)
            {
                relative = filePath.Substring(marker + AssetsMarker.Length);
                return relative.Length > 0;
            }

            string streaming = Application.streamingAssetsPath;
            if (!string.IsNullOrEmpty(streaming) &&
                filePath.StartsWith(streaming, StringComparison.Ordinal))
            {
                relative = filePath.Substring(streaming.Length).TrimStart('/', '\\');
                return relative.Length > 0;
            }

            return false;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static byte[] Drain(AndroidJavaObject input)
        {
            // API 33+：一次拿完整 byte[]。
            try
            {
                byte[] direct = input.Call<byte[]>("readAllBytes");
                if (direct != null && direct.Length > 0)
                    return direct;
            }
            catch (Exception)
            {
            }

            try
            {
                sbyte[] signed = input.Call<sbyte[]>("readAllBytes");
                if (signed != null && signed.Length > 0)
                    return ToUnsigned(signed);
            }
            catch (Exception)
            {
            }

            // API < 33：绝不能 InputStream.read(C# byte[])。
            // Unity JNI 不会把 Java 写回 C# 数组，会得到正确长度的脏/零字节，Yoo catalog 报 Invalid。
            return DrainViaJniBuffer(input);
        }

        static byte[] DrainViaJniBuffer(AndroidJavaObject input)
        {
            using (var baos = new AndroidJavaObject("java.io.ByteArrayOutputStream"))
            {
                IntPtr jbuf = AndroidJNI.NewByteArray(8192);
                try
                {
                    IntPtr inputRaw = input.GetRawObject();
                    IntPtr inputClass = AndroidJNI.GetObjectClass(inputRaw);
                    IntPtr readMid = AndroidJNIHelper.GetMethodID(inputClass, "read", "([B)I");
                    IntPtr baosRaw = baos.GetRawObject();
                    IntPtr baosClass = AndroidJNI.GetObjectClass(baosRaw);
                    IntPtr writeMid = AndroidJNIHelper.GetMethodID(baosClass, "write", "([BII)V");
                    try
                    {
                        var readArgs = new jvalue[1];
                        var writeArgs = new jvalue[3];
                        while (true)
                        {
                            readArgs[0].l = jbuf;
                            int n = AndroidJNI.CallIntMethod(inputRaw, readMid, readArgs);
                            if (n <= 0)
                                break;
                            writeArgs[0].l = jbuf;
                            writeArgs[1].i = 0;
                            writeArgs[2].i = n;
                            AndroidJNI.CallVoidMethod(baosRaw, writeMid, writeArgs);
                        }
                    }
                    finally
                    {
                        AndroidJNI.DeleteLocalRef(inputClass);
                        AndroidJNI.DeleteLocalRef(baosClass);
                    }
                }
                finally
                {
                    AndroidJNI.DeleteLocalRef(jbuf);
                }

                try
                {
                    byte[] unsigned = baos.Call<byte[]>("toByteArray");
                    if (unsigned != null && unsigned.Length > 0)
                        return unsigned;
                }
                catch (Exception)
                {
                }

                sbyte[] raw = baos.Call<sbyte[]>("toByteArray");
                return raw != null ? ToUnsigned(raw) : Array.Empty<byte>();
            }
        }

        static byte[] ToUnsigned(sbyte[] raw)
        {
            var bytes = new byte[raw.Length];
            Buffer.BlockCopy(raw, 0, bytes, 0, raw.Length);
            return bytes;
        }
#endif
    }
}
