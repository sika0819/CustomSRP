using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 主存档内存模型：进岛加载、变更即 Persist。
    /// 落盘门控见 <see cref="BoluoGamePersistence.AllowsAccountProgressDisk"/>（saveLocal 且有 userId 才按桶读写）。
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
                $"[BoluoGameSave] loaded guest={GuestSession.IsGuest} weatherLinkage={_data.weatherLinkage} " +
                $"healthTip={_data.healthTip} recommend={_data.recommend} scheme={_data.curSchemeID} " +
                $"bgm={_data.bgmVolume} voice={_data.voiceVolume} " +
                $"atm={_data.hasIslandAtmosphere} kind={_data.weatherKind} lv={_data.weatherLevel} " +
                $"hours={_data.hours:F1} period={_data.period} " +
                $"music={_data.hasMusicTrack} bank={_data.musicBankName}");
        }

        /// <summary>无主存档时，从既有 Setting 音量键迁移一次。</summary>
        static void MigrateVolumesFromSettingIfNeeded(string raw)
        {
            if (!BoluoGamePersistence.AllowsAccountProgressDisk || !string.IsNullOrEmpty(raw))
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
            if (on)
                _data.curSchemeID = 0;
            Persist();
        }

        public static bool Recommend
        {
            get
            {
                EnsureLoaded();
                return _data.recommend;
            }
        }

        /// <summary>当前账号手动预设 id；智能推荐或未选时为 0。</summary>
        public static int PresetSchemeId
        {
            get
            {
                EnsureLoaded();
                return _data.recommend ? 0 : _data.curSchemeID;
            }
        }

        /// <summary>
        /// 手动套用预设：关掉智能推荐并记下 scheme id（对齐旧 ChangeState(false)+SelectScheme）。
        /// 无 userId / 未开 saveLocal 时只改内存，退登 ResetSession 会丢掉。
        /// </summary>
        public static void CapturePresetScheme(int schemeId)
        {
            if (schemeId <= 0)
                return;
            EnsureLoaded();
            _data.recommend = false;
            _data.curSchemeID = schemeId;
            Persist();
        }

        /// <summary>
        /// 退登 / 热切前：把岛上正在播的预设写入当前账号桶。服务已清掉时不写。
        /// </summary>
        public static void FlushPlayingPreset()
        {
            PresetService presets = PresetService.Instance ?? GameEntry.Presets;
            if (presets == null || presets.CurrentId <= 0)
                return;
            CapturePresetScheme(presets.CurrentId);
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

        public static bool HasIslandAtmosphere
        {
            get
            {
                EnsureLoaded();
                return _data.hasIslandAtmosphere;
            }
        }

        /// <summary>写入当前岛上天气/时段快照（无 userId 时 Persist 为空操作）。</summary>
        public static void CaptureIslandAtmosphere(
            IslandWeatherKind kind,
            int level,
            float hours,
            IslandTimePeriod period,
            IslandLookKind lookKind,
            float fogDensity)
        {
            EnsureLoaded();
            _data.hasIslandAtmosphere = true;
            _data.weatherKind = (int)kind;
            _data.weatherLevel = level;
            _data.hours = hours;
            _data.period = (int)period;
            _data.lookKind = (int)lookKind;
            _data.fogDensity = fogDensity;
            Persist();
        }

        /// <summary>
        /// 若有大气快照则灌进 <paramref name="weather"/>；成功返回 true。
        /// 调用方负责 <c>fireEvent:false</c> 路径下的 UI 同步。
        /// </summary>
        public static bool TryApplyIslandAtmosphere(WeatherService weather)
        {
            if (weather == null)
                return false;
            EnsureLoaded();
            if (!_data.hasIslandAtmosphere)
                return false;

            var kind = (IslandWeatherKind)_data.weatherKind;
            int level = _data.weatherLevel;
            float hours = _data.hours;
            var look = (IslandLookKind)_data.lookKind;
            float fog = _data.fogDensity;

            weather.ApplyAtmosphereSnapshot(kind, level, hours, look, fog, fireEvent: false);
            Log.Info(
                $"[BoluoGameSave] restored atmosphere kind={kind} lv={level} hours={hours:F1} look={look}");
            return true;
        }

        /// <summary>写入当前 BGM bank/event（无 userId 时 Persist 为空操作）。</summary>
        public static void CaptureMusicTrack(string bankName, string eventName)
        {
            if (string.IsNullOrEmpty(bankName))
                return;
            EnsureLoaded();
            _data.hasMusicTrack = true;
            _data.musicBankName = bankName ?? "";
            _data.musicEventName = eventName ?? "";
            Persist();
        }

        /// <summary>从 playlist 捕获当前曲（无播放则跳过）。</summary>
        public static void CaptureMusicFromPlaylist(MusicPlaylist music)
        {
            if (music == null || !music.HasRestorableTrack)
                return;
            var track = MusicCatalog.GetTrack(music.CurrentAlbumIndex, music.CurrentTrackIndex);
            if (!track.HasValue || string.IsNullOrEmpty(track.Value.BankName))
                return;
            CaptureMusicTrack(track.Value.BankName, track.Value.EventName);
        }

        /// <summary>若有曲目快照则输出 bank/event。</summary>
        public static bool TryGetSavedMusicTrack(out string bankName, out string eventName)
        {
            bankName = "";
            eventName = "";
            EnsureLoaded();
            if (!_data.hasMusicTrack || string.IsNullOrEmpty(_data.musicBankName))
                return false;
            bankName = _data.musicBankName;
            eventName = _data.musicEventName ?? "";
            return true;
        }
    }
}
