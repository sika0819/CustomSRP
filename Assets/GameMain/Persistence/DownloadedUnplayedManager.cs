using System;
using System.Collections.Generic;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 下载未播红点：已播集合 + 首次展示 UTC + 24h 保护期。
    /// 音乐 key: <c>m:{listName}|{musicName}</c>，预设 key: <c>p:{presetName}</c>。
    /// </summary>
    public static class DownloadedUnplayedManager
    {
        const double ProtectionHours = 24.0;

        static readonly HashSet<string> Played = new HashSet<string>(StringComparer.Ordinal);
        static readonly Dictionary<string, long> FirstShownAt = new Dictionary<string, long>(StringComparer.Ordinal);
        static bool _loaded;

        public static void EnsureLoaded()
        {
            if (_loaded)
                return;
            Load();
        }

        public static void ResetSession()
        {
            _loaded = false;
            Played.Clear();
            FirstShownAt.Clear();
        }

        static void Load()
        {
            Played.Clear();
            FirstShownAt.Clear();
            string json = BoluoGamePersistence.LoadDownloadedUnplayedRaw();
            _loaded = true;
            if (string.IsNullOrEmpty(json))
                return;
            try
            {
                var wrapper = JsonUtility.FromJson<SerializableData>(json);
                if (wrapper?.played != null)
                {
                    for (int i = 0; i < wrapper.played.Count; i++)
                    {
                        string k = wrapper.played[i];
                        if (!string.IsNullOrEmpty(k))
                            Played.Add(k);
                    }
                }

                if (wrapper?.firstShownAt != null)
                {
                    for (int i = 0; i < wrapper.firstShownAt.Count; i++)
                    {
                        var e = wrapper.firstShownAt[i];
                        if (e != null && !string.IsNullOrEmpty(e.key))
                            FirstShownAt[e.key] = e.timestampUtc;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[DownloadedUnplayed] Load failed: {ex.Message}");
            }
        }

        static void Save()
        {
            var wrapper = new SerializableData
            {
                played = new List<string>(Played),
                firstShownAt = new List<SerializableEntry>(),
            };
            foreach (var kv in FirstShownAt)
                wrapper.firstShownAt.Add(new SerializableEntry { key = kv.Key, timestampUtc = kv.Value });
            BoluoGamePersistence.PersistDownloadedUnplayedRaw(JsonUtility.ToJson(wrapper));
        }

        public static void MarkPlayed(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;
            EnsureLoaded();
            if (!BoluoGamePersistence.AllowsAccountProgressDisk)
                return;
            Played.Add(key);
            FirstShownAt.Remove(key);
            Save();
        }

        public static bool ShouldShowRedDot(string key)
        {
            if (string.IsNullOrEmpty(key))
                return false;
            EnsureLoaded();
            if (!BoluoGamePersistence.AllowsAccountProgressDisk)
                return false;
            if (Played.Contains(key))
                return false;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (FirstShownAt.TryGetValue(key, out long ts))
            {
                if (now - ts > (long)(ProtectionHours * 3600))
                {
                    FirstShownAt.Remove(key);
                    Save();
                    return false;
                }

                return true;
            }

            FirstShownAt[key] = now;
            Save();
            return true;
        }

        public static string MusicKey(string albumName, string musicName) =>
            "m:" + (albumName ?? "") + "|" + (musicName ?? "");

        public static string PresetKey(string displayName) =>
            "p:" + (displayName ?? "");

        [Serializable]
        class SerializableEntry
        {
            public string key;
            public long timestampUtc;
        }

        [Serializable]
        class SerializableData
        {
            public List<string> played;
            public List<SerializableEntry> firstShownAt;
        }
    }
}
