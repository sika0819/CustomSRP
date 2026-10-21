using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 主存档内存模型：进岛加载、变更即 Persist。游客会话使用默认值且不落盘。
    /// </summary>
    public static class BoluoGameSave
    {
        static BoluoSaveData _data = new BoluoSaveData();
        static bool _loaded;

        public static BoluoSaveData Data
        {
            get
            {
                EnsureLoaded();
                return _data;
            }
        }

        public static bool IsLoaded => _loaded;

        public static void EnsureLoaded()
        {
            if (_loaded)
                return;
            Load();
        }

        public static void Load()
        {
            _data = new BoluoSaveData();
            string raw = BoluoGamePersistence.LoadBoluoSaveDataRaw();
            if (!string.IsNullOrEmpty(raw))
            {
                try
                {
                    var parsed = JsonUtility.FromJson<BoluoSaveData>(raw);
                    if (parsed != null)
                        _data = parsed;
                }
                catch (System.Exception ex)
                {
                    Log.Warning($"[BoluoGameSave] Load parse failed: {ex.Message}");
                }

#if UNITY_IOS
                // 旧存档缺字段时反序列化会得到 false，迁移为默认开启
                if (!raw.Contains("healthTip"))
                    _data.healthTip = true;
                if (!raw.Contains("recommend"))
                    _data.recommend = true;
#endif
            }

            MigrateVolumesFromSettingIfNeeded(raw);
            _loaded = true;
            Log.Info(
                $"[BoluoGameSave] loaded guest={GuestSession.IsGuest} weather={_data.weatherLinkage} " +
                $"healthTip={_data.healthTip} recommend={_data.recommend} " +
                $"bgm={_data.bgmVolume} voice={_data.voiceVolume}");
        }

        /// <summary>无主存档时，从既有 Setting 音量键迁移一次（正式账号）。</summary>
        static void MigrateVolumesFromSettingIfNeeded(string raw)
        {
            if (GuestSession.IsGuest || !string.IsNullOrEmpty(raw))
                return;
            if (GameEntry.Setting == null)
                return;

            bool migrated = false;
            if (GameEntry.Setting.HasSetting(VolumeForm.MusicVolumeKey))
            {
                _data.bgmVolume = Mathf.RoundToInt(
                    Mathf.Clamp01(GameEntry.Setting.GetFloat(VolumeForm.MusicVolumeKey, 0.8f)) * 100f);
                migrated = true;
            }

            if (GameEntry.Setting.HasSetting(VolumeForm.EffectVolumeKey))
            {
                _data.voiceVolume = Mathf.RoundToInt(
                    Mathf.Clamp01(GameEntry.Setting.GetFloat(VolumeForm.EffectVolumeKey, 0.8f)) * 100f);
                migrated = true;
            }

            if (migrated)
                Persist();
        }

        public static void Persist()
        {
            EnsureLoaded();
            BoluoGamePersistence.PersistBoluoSaveDataRaw(JsonUtility.ToJson(_data));
        }

        public static void ResetSession()
        {
            _loaded = false;
            _data = new BoluoSaveData();
        }

        public static float MusicVolume01
        {
            get
            {
                EnsureLoaded();
                return Mathf.Clamp01(_data.bgmVolume / 100f);
            }
        }

        public static float EffectVolume01
        {
            get
            {
                EnsureLoaded();
                return Mathf.Clamp01(_data.voiceVolume / 100f);
            }
        }

        public static void SetMusicVolume01(float value)
        {
            EnsureLoaded();
            _data.bgmVolume = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(value) * 100f), 0, 100);
            Persist();
        }

        public static void SetEffectVolume01(float value)
        {
            EnsureLoaded();
            _data.voiceVolume = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(value) * 100f), 0, 100);
            Persist();
        }

        public static void SetWeatherLinkage(bool on)
        {
            EnsureLoaded();
            _data.weatherLinkage = on;
            Persist();
        }

        public static void SetHealthTip(bool on)
        {
            EnsureLoaded();
            _data.healthTip = on;
            Persist();
        }

        public static void SetRecommend(bool on)
        {
            EnsureLoaded();
            _data.recommend = on;
            Persist();
        }

        public static void SetMaxHealthLevel(int level, uint dateUnix)
        {
            EnsureLoaded();
            _data.maxHealthLevel = level;
            _data.maxHealthLevelDate = dateUnix;
            Persist();
        }

        public static void SetNoEnquireHealthLevel(int level)
        {
            EnsureLoaded();
            _data.noEnquireHealthLevel = level;
            Persist();
        }

        public static void SetLastPressureLevel(int level, uint dateUnix)
        {
            EnsureLoaded();
            _data.lastPressureLevel = level;
            _data.lastPressureLevelDate = dateUnix;
            Persist();
        }
    }
}
