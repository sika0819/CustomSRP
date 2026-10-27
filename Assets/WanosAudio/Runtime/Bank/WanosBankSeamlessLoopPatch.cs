using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace WanosAudio
{
    /// <summary>
    /// Rain.wb 的 Rain_Small 序列容器 loop_times=1，播完约两分钟就停；
    /// 中雨 / 大雨是 0（无限循环）。进引擎前把小雨改成 0，并打开流式预加载，避免循环缝空一截。
    /// 原文件不动（下载 MD5 仍对），补丁写到 persistent/WanosBankPatched。
    /// </summary>
    public static class WanosBankSeamlessLoopPatch
    {
        public const string SidecarDirectoryName = "WanosBankPatched";

        /// <summary>改此值会换目录，强制重生补丁副本。</summary>
        public const int SidecarPatchRevision = 2;

        /// <summary>流式预载长度（采样帧）。2s@48k，覆盖循环缝 decode。</summary>
        public const ulong DefaultPrepareLoadLenSamples = 96000;

        const byte FieldBool = 1;
        const byte FieldU32 = 6;
        const byte FieldU64 = 8;
        const int HeaderSizeOffset = 6;
        const int HeaderCrcOffset = 14;
        const int HeaderCrcPayloadOffset = 18;

        static readonly object Gate = new object();
        static readonly Dictionary<string, long> SidecarSourceWriteTicks =
            new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>交给 LoadBankFromPath 的路径。非 Rain.wb、或补丁失败时返回原路径。</summary>
        public static string ResolveEngineLoadPath(string originalPath)
        {
            if (string.IsNullOrEmpty(originalPath) || !File.Exists(originalPath))
                return originalPath;

            string fileName = Path.GetFileName(originalPath);
            if (string.IsNullOrEmpty(fileName) ||
                !fileName.StartsWith("Rain.wb", StringComparison.OrdinalIgnoreCase))
                return originalPath;

            long writeTicks = 0;
            try
            {
                writeTicks = File.GetLastWriteTimeUtc(originalPath).Ticks;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WanosBankSeamlessLoop] mtime failed " + fileName + ": " + e.Message);
            }

            lock (Gate)
            {
                if (SidecarSourceWriteTicks.TryGetValue(originalPath, out long cached) &&
                    cached == writeTicks)
                {
                    string cachedSidecar = GetSidecarPath(fileName);
                    if (File.Exists(cachedSidecar))
                        return cachedSidecar;
                }
            }

            byte[] source;
            try
            {
                source = File.ReadAllBytes(originalPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WanosBankSeamlessLoop] read failed " + fileName + ": " + e.Message);
                return originalPath;
            }

            if (!TryBuildPatchedBytes(source, fileName, out byte[] patched))
                return originalPath;

            string sidecar = GetSidecarPath(fileName);
            try
            {
                string dir = Path.GetDirectoryName(sidecar);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllBytes(sidecar, patched);
                lock (Gate)
                    SidecarSourceWriteTicks[originalPath] = writeTicks;
                Debug.Log("[WanosBankSeamlessLoop] sidecar " + fileName + " rev=" + SidecarPatchRevision);
                return sidecar;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WanosBankSeamlessLoop] write sidecar failed " + fileName + ": " + e.Message);
                return originalPath;
            }
        }

        public static string GetSidecarPath(string fileName)
        {
            return Path.Combine(
                Application.persistentDataPath,
                SidecarDirectoryName,
                "r" + SidecarPatchRevision,
                fileName);
        }

        /// <summary>
        /// 在副本上打开流式预加载，并把 Rain_Small 的 loop_times 改成 0。
        /// 无需改动时返回 false。
        /// </summary>
        public static bool TryBuildPatchedBytes(byte[] source, string fileName, out byte[] patched)
        {
            patched = null;
            if (source == null || source.Length < HeaderCrcPayloadOffset + 8)
                return false;

            var data = (byte[])source.Clone();
            uint headerSize = ReadU32(data, HeaderSizeOffset);
            if (headerSize == 0 || HeaderCrcPayloadOffset + headerSize > data.Length)
                return false;

            int headerEnd = (int)Math.Min(data.Length, HeaderCrcPayloadOffset + headerSize);
            if (!TryReadU64Field(data, 0, headerEnd, "bankSampleBasicInfoBlockInfo_offset", out ulong sampleOff) ||
                !TryReadU64Field(data, 0, headerEnd, "bankSampleBasicInfoBlockInfo_size", out ulong sampleSize))
                return false;

            int sampleStart = (int)sampleOff;
            int sampleEnd = sampleStart + (int)sampleSize;
            if (sampleStart < 0 || sampleEnd > data.Length || sampleSize == 0)
                return false;

            PatchStreamPrepareFields(data, sampleStart, sampleEnd, out int flipped, out int lenPatched);
            bool rainSmall = false;
            if (!string.IsNullOrEmpty(fileName) &&
                fileName.StartsWith("Rain.wb", StringComparison.OrdinalIgnoreCase))
            {
                rainSmall = TryPatchRainSmallLoopTimes(data, HeaderCrcPayloadOffset + (int)headerSize);
            }

            if (flipped == 0 && lenPatched == 0 && !rainSmall)
                return false;

            if (!TryWriteU32Field(data, 0, headerEnd,
                    "bankSampleBasicInfoBlockInfo_crc32", Crc32(data, sampleStart, (int)sampleSize)))
                return false;

            if (rainSmall &&
                TryReadU64Field(data, 0, headerEnd, "bankAudioComponentBlockInfo_offset", out ulong compOff) &&
                TryReadU64Field(data, 0, headerEnd, "bankAudioComponentBlockInfo_size", out ulong compSize) &&
                (int)compOff + (int)compSize <= data.Length)
            {
                TryWriteU32Field(data, 0, headerEnd,
                    "bankAudioComponentBlockInfo_crc32", Crc32(data, (int)compOff, (int)compSize));
            }

            WriteU32(data, HeaderCrcOffset, Crc32(data, HeaderCrcPayloadOffset, (int)headerSize));
            patched = data;
            Debug.Log(
                "[WanosBankSeamlessLoop] patch " + fileName +
                " flipped=" + flipped + " prepareLenPatched=" + lenPatched +
                " rainSmall=" + (rainSmall ? "1" : "0") +
                " rev=" + SidecarPatchRevision);
            return true;
        }

        /// <summary>is_stream_load=1 时打开 is_prepare_load，并把 prepare_load_len 从 0 写成有效采样数。</summary>
        static void PatchStreamPrepareFields(byte[] data, int start, int end, out int flipped, out int lenPatched)
        {
            flipped = 0;
            lenPatched = 0;
            byte[] streamName = Encoding.ASCII.GetBytes("is_stream_load");
            byte[] prepareName = Encoding.ASCII.GetBytes("is_prepare_load");
            byte[] prepareLenName = Encoding.ASCII.GetBytes("prepare_load_len");
            byte[] sampleCountName = Encoding.ASCII.GetBytes("sample_count");
            int i = start;
            while (i + 3 + streamName.Length < end)
            {
                if (data[i] == FieldBool && ReadU16(data, i + 1) == streamName.Length &&
                    BytesEqual(data, i + 3, streamName))
                {
                    int streamVal = i + 3 + streamName.Length;
                    if (streamVal < end && data[streamVal] == 1)
                    {
                        int lookEnd = Math.Min(end, streamVal + 120);
                        int prepareVal = -1;
                        int prepareLenVal = -1;
                        int sampleCountVal = -1;
                        int j = streamVal + 1;
                        while (j + 3 < lookEnd)
                        {
                            byte t = data[j];
                            ushort nameLen = ReadU16(data, j + 1);
                            int nameAt = j + 3;
                            int valueAt = nameAt + nameLen;
                            if (nameLen == 0 || valueAt > lookEnd)
                            {
                                j++;
                                continue;
                            }

                            if (t == FieldBool && nameLen == prepareName.Length &&
                                BytesEqual(data, nameAt, prepareName))
                            {
                                prepareVal = valueAt;
                                j = valueAt + 1;
                                continue;
                            }

                            if (t == FieldU64 && nameLen == prepareLenName.Length &&
                                BytesEqual(data, nameAt, prepareLenName) &&
                                valueAt + 8 <= end)
                            {
                                prepareLenVal = valueAt;
                                j = valueAt + 8;
                                continue;
                            }

                            if (t == FieldU64 && nameLen == sampleCountName.Length &&
                                BytesEqual(data, nameAt, sampleCountName) &&
                                valueAt + 8 <= end)
                            {
                                sampleCountVal = valueAt;
                                j = valueAt + 8;
                                continue;
                            }

                            j++;
                        }

                        if (prepareVal >= 0 && prepareVal < end && data[prepareVal] == 0)
                        {
                            data[prepareVal] = 1;
                            flipped++;
                        }

                        if (prepareLenVal >= 0 && prepareLenVal + 8 <= end)
                        {
                            ulong cur = ReadU64(data, prepareLenVal);
                            if (cur == 0)
                            {
                                ulong sampleCount = sampleCountVal >= 0
                                    ? ReadU64(data, sampleCountVal)
                                    : 0;
                                ulong len = DefaultPrepareLoadLenSamples;
                                if (sampleCount > 0 && sampleCount < len)
                                    len = sampleCount;
                                WriteU64(data, prepareLenVal, len);
                                lenPatched++;
                            }
                        }
                    }

                    i = streamVal + 1;
                    continue;
                }

                i++;
            }
        }

        /// <summary>Rain_Small 序列容器 loop_times=1 播完就停；改成 0 与中雨/大雨一致。</summary>
        static bool TryPatchRainSmallLoopTimes(byte[] data, int searchFrom)
        {
            byte[] marker = Encoding.ASCII.GetBytes("Rain_Small");
            int markerAt = IndexOf(data, marker, searchFrom);
            if (markerAt < 0)
                return false;

            byte[] loopName = Encoding.ASCII.GetBytes("loop_times");
            int i = markerAt;
            int limit = Math.Min(data.Length - 8, markerAt + 1600);
            while (i + 3 + loopName.Length + 8 <= limit)
            {
                if (data[i] == FieldU64 && ReadU16(data, i + 1) == loopName.Length &&
                    BytesEqual(data, i + 3, loopName))
                {
                    int valuePos = i + 3 + loopName.Length;
                    ulong old = ReadU64(data, valuePos);
                    if (old == 1)
                    {
                        WriteU64(data, valuePos, 0);
                        return true;
                    }
                }

                i++;
            }

            return false;
        }

        static bool TryReadU64Field(byte[] data, int start, int end, string fieldName, out ulong value)
        {
            value = 0;
            if (!TryFindField(data, start, end, fieldName, FieldU64, out int valuePos))
                return false;
            if (valuePos + 8 > data.Length)
                return false;
            value = ReadU64(data, valuePos);
            return true;
        }

        static bool TryWriteU32Field(byte[] data, int start, int end, string fieldName, uint value)
        {
            if (!TryFindField(data, start, end, fieldName, FieldU32, out int valuePos))
                return false;
            if (valuePos + 4 > data.Length)
                return false;
            WriteU32(data, valuePos, value);
            return true;
        }

        static bool TryFindField(byte[] data, int start, int end, string fieldName, byte type, out int valuePos)
        {
            valuePos = -1;
            byte[] name = Encoding.ASCII.GetBytes(fieldName);
            int i = start;
            int last = Math.Min(end, data.Length) - 3 - name.Length;
            while (i <= last)
            {
                if (data[i] == type && ReadU16(data, i + 1) == name.Length && BytesEqual(data, i + 3, name))
                {
                    valuePos = i + 3 + name.Length;
                    return true;
                }

                i++;
            }

            return false;
        }

        static bool BytesEqual(byte[] data, int offset, byte[] name)
        {
            for (int i = 0; i < name.Length; i++)
            {
                if (data[offset + i] != name[i])
                    return false;
            }

            return true;
        }

        static int IndexOf(byte[] data, byte[] needle, int start)
        {
            int last = data.Length - needle.Length;
            for (int i = start; i <= last; i++)
            {
                if (BytesEqual(data, i, needle))
                    return i;
            }

            return -1;
        }

        static ushort ReadU16(byte[] data, int offset)
        {
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        static uint ReadU32(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) |
                          (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }

        static ulong ReadU64(byte[] data, int offset)
        {
            uint lo = ReadU32(data, offset);
            uint hi = ReadU32(data, offset + 4);
            return lo | ((ulong)hi << 32);
        }

        static void WriteU32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }

        static void WriteU64(byte[] data, int offset, ulong value)
        {
            WriteU32(data, offset, (uint)value);
            WriteU32(data, offset + 4, (uint)(value >> 32));
        }

        /// <summary>与 zlib.crc32 / Wanos bank 头校验相同的 IEEE CRC-32。</summary>
        static uint Crc32(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFFu;
            int end = offset + length;
            for (int i = offset; i < end; i++)
            {
                crc ^= data[i];
                for (int b = 0; b < 8; b++)
                {
                    uint mask = (crc & 1u) != 0 ? 0xEDB88320u : 0u;
                    crc = (crc >> 1) ^ mask;
                }
            }

            return crc ^ 0xFFFFFFFFu;
        }
    }
}
