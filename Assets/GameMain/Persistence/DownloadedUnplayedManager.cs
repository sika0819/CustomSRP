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

        /// <summary>
        /// Editor 游客 <c>saveLocal=false</c> 时账号进度盘隔离；已听若不另存，
        /// 退登 <see cref="ResetSession"/> 后重进岛红点必回潮。
        /// </summary>
        const string EditorHeardPrefsKey = "Boluo.Editor.DownloadedUnplayed";

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
            string json = LoadRaw();
            _loaded = true;
            if (string.IsNullOrEmpty(json))
                return;
            try
            {
                var wrapper = JsonUtility.FromJson<SerializableData>(json);
                if (wrapper?.played != null)
                {
                    for (int i = 0; i < wrapper.played.Length; i++)
                    {
                        string k = wrapper.played[i];
                        if (!string.IsNullOrEmpty(k))
                            Played.Add(k);
                    }
                }

                if (wrapper?.firstShownAt != null)
                {
                    for (int i = 0; i < wrapper.firstShownAt.Length; i++)
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
            var entries = new List<SerializableEntry>(FirstShownAt.Count);
            foreach (var kv in FirstShownAt)
                entries.Add(new SerializableEntry { key = kv.Key, timestampUtc = kv.Value });

            var wrapper = new SerializableData
            {
                // JsonUtility 对 List 偶发丢字段；数组 round-trip 稳定。
                played = new List<string>(Played).ToArray(),
                firstShownAt = entries.ToArray(),
            };
            PersistRaw(JsonUtility.ToJson(wrapper));
        }

        static string LoadRaw()
        {
#if UNITY_EDITOR
            // Editor：优先读独立已听键（含游客），再回落账号桶。
            string editorJson = PlayerPrefs.GetString(EditorHeardPrefsKey, "") ?? "";
            if (!string.IsNullOrEmpty(editorJson))
                return editorJson;
#endif
            return BoluoGamePersistence.LoadDownloadedUnplayedRaw();
        }

        static void PersistRaw(string json)
        {
#if UNITY_EDITOR
            // Editor 游客也落已听，避免退出重进红点回潮；真机游客隔离仍走下方门控。
            PlayerPrefs.SetString(EditorHeardPrefsKey, json ?? "");
            PlayerPrefs.Save();
#endif
            BoluoGamePersistence.PersistDownloadedUnplayedRaw(json);
            BoluoGamePersistence.PlayerPrefsSave();
        }

        public static void MarkPlayed(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;
            EnsureLoaded();
            // 内存始终更新（对齐 Boluo）；落盘：正式号走账号桶，Editor 另写独立键。
            Played.Add(key);
            FirstShownAt.Remove(key);
            Save();
        }

        public static bool ShouldShowRedDot(string key)
        {
            if (string.IsNullOrEmpty(key))
                return false;
            EnsureLoaded();
            // 不按 AllowsAccountProgressDisk 禁红点：游客/无 saveLocal 仍会话内显示；
            // 仅 Save/Load 走 persistence 门控（对齐 Boluo DownloadedUnplayedManager）。
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

#if UNITY_EDITOR
        /// <summary>EditMode 测试：清 Editor 已听键。</summary>
        public static void EditorClearHeardPrefsForTests()
        {
            PlayerPrefs.DeleteKey(EditorHeardPrefsKey);
            PlayerPrefs.Save();
            ResetSession();
        }
#endif

        [Serializable]
        class SerializableEntry
        {
            public string key;
            public long timestampUtc;
        }

        [Serializable]
        class SerializableData
        {
            public string[] played;
            public SerializableEntry[] firstShownAt;
        }
    }
}
