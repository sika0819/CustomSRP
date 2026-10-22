using UnityEngine;

namespace Game
{
    public sealed class PresetService : MonoBehaviour
    {
        public const string PresetFolder = "Assets/GameMain/Environment/IslandPresets";
        public const int UiPresetCount = 10;

        IslandPresetAsset[] _presets = System.Array.Empty<IslandPresetAsset>();
        public int CurrentId { get; private set; }

        /// <summary>0 idle / 1 switching (veil + weather) / 2 playing — Boluo curSchemeState.</summary>
        public int SchemeState { get; private set; }

        public bool IsTransitioning => SchemeState == 1;

        public IslandPresetAsset[] Presets => _presets;

        public static PresetService Ensure()
        {
            PresetService existing = FindAnyObjectByType<PresetService>();
            if (existing != null)
                return existing;

            var go = new GameObject("PresetService");
            DontDestroyOnLoad(go);
            return go.AddComponent<PresetService>();
        }

        void Awake()
        {
            ReloadFromResources();
        }

        public void SetPresets(IslandPresetAsset[] presets)
        {
            _presets = presets ?? System.Array.Empty<IslandPresetAsset>();
        }

        public void ReloadFromResources()
        {
            // Editor Resource mode: load via Resources folder copy or assigned list.
            IslandPresetAsset[] loaded = Resources.LoadAll<IslandPresetAsset>("IslandPresets");
            if (loaded != null && loaded.Length > 0)
            {
                System.Array.Sort(loaded, (a, b) => a.Id.CompareTo(b.Id));
                _presets = loaded;
            }

            EnsureCovers();
        }

        /// <summary>Boluo YuShe list shows schemeInfos[0..9] only.</summary>
        public int GetUiCount()
        {
            int n = _presets.Length;
            return n > UiPresetCount ? UiPresetCount : n;
        }

        public IslandPresetAsset GetById(int id)
        {
            for (int i = 0; i < _presets.Length; i++)
            {
                if (_presets[i] != null && _presets[i].Id == id)
                    return _presets[i];
            }
            return null;
        }

        public IslandPresetAsset GetByIndex(int index)
        {
            if (index < 0 || index >= _presets.Length)
                return null;
            return _presets[index];
        }

        public void RequestTransition(int id)
        {
            if (GetById(id) == null)
                return;

            // 无 iris 订阅方时勿只抬 SchemeState=1：否则 IsTransitioning 永真，行走/开预设全死。
            bool hasIrisHandler = GameEntry.Event != null
                && GameEntry.Event.Count(PresetTransitionRequestEventArgs.EventId) > 0;
            if (hasIrisHandler)
            {
                SchemeState = 1;
                GameEntry.Event.Fire(this, PresetTransitionRequestEventArgs.Create(id));
                return;
            }

            Apply(id, teleportPlayer: true);
            MarkPlaying();
        }

        /// <summary>Called when the HUD veil finishes and the preset is live.</summary>
        public void MarkPlaying()
        {
            if (CurrentId > 0)
                SchemeState = 2;
        }

        public void Apply(int id, bool teleportPlayer = true)
        {
            IslandPresetAsset preset = GetById(id);
            if (preset == null)
                return;

            // Doc: BGM 不停播，落点强制切曲 —— 播本预设 Music* bank（先 Stop_Music 再 Play，不叠声）。
            GameEntry.Music?.ForceRetargetAtLanding(ResolvePresetMusicBank(preset));
            // Drop every content 3D voice before teleport so old emitters never follow.
            // Iris path already cleared + held; direct Apply clears here then retriggers below.
            AudioInteractionBootstrap.ClearContent3D();

            CurrentId = id;
            GameEntry.Weather?.SetLookKind(preset.LookKind);
            GameEntry.Weather?.SetTimeOfDay(preset.TimeHours);
            GameEntry.Weather?.ClearPanelTransitionLock();
            GameEntry.Weather?.SetWeather(preset.WeatherKind, preset.WeatherLevel);
            GameEntry.Weather?.SetFogDensity(preset.FogDensity);
            // Per-preset light color override (alpha=0 means "use the time-of-day table").
            Color? lightOverride = preset.LightColorOverride.a > 0.001f
                ? (Color?)preset.LightColorOverride
                : null;
            GameEntry.Weather?.SetLightColorOverride(lightOverride);

            if (teleportPlayer)
            {
                // Iris path: far framing then enter-dolly. Direct Apply (no transition): final pose.
                if (SchemeState == 1)
                    IslandJoystickCamera.Current?.ApplyPresetForReveal(preset);
                else
                    IslandJoystickCamera.Current?.ApplyPreset(preset);
            }

            // Iris transition holds precip + zone ambience until reveal; direct Apply retriggers now.
            if (SchemeState != 1)
                AudioInteractionBootstrap.RetriggerContentAtListener();

            // Veil path keeps SchemeState=1 until MarkPlaying; direct Apply is already live.
            if (SchemeState != 1)
                SchemeState = 2;

            if (GameEntry.Event != null)
                GameEntry.Event.Fire(this, PresetChangedEventArgs.Create(preset));
        }

        /// <summary>Interrupt (leave island / form close) — drop switching latch without a stuck busy state.</summary>
        public void AbortTransition()
        {
            SchemeState = CurrentId > 0 ? 2 : 0;
        }

        /// <summary>First <c>Music*</c> entry in <see cref="IslandPresetAsset.BankNames"/>, or null.</summary>
        static string ResolvePresetMusicBank(IslandPresetAsset preset)
        {
            string[] banks = preset != null ? preset.BankNames : null;
            if (banks == null)
                return null;
            for (int i = 0; i < banks.Length; i++)
            {
                string bn = banks[i];
                if (AmbienceEventCatalog.IsMusicBank(bn))
                    return bn;
            }
            return null;
        }

        public void ApplyIndex(int index, bool teleportPlayer = true)
        {
            IslandPresetAsset preset = GetByIndex(index);
            if (preset == null)
                return;
            Apply(preset.Id, teleportPlayer);
        }

        void EnsureCovers()
        {
            for (int i = 0; i < _presets.Length; i++)
            {
                IslandPresetAsset preset = _presets[i];
                if (preset == null || preset.Cover != null)
                    continue;
                int coverId = preset.Id;
                if (coverId < 1 || coverId > UiPresetCount)
                    coverId = ((preset.Id - 1) % UiPresetCount) + 1;
                preset.Cover = Resources.Load<Texture2D>($"PresetCovers/zhanweitu{coverId}");
            }
        }
    }
}
