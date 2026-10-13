using UnityEngine;

namespace Game
{
    public sealed class PresetService : MonoBehaviour
    {
        public const string PresetFolder = "Assets/GameMain/Environment/IslandPresets";
        public const int UiPresetCount = 10;

        IslandPresetAsset[] _presets = System.Array.Empty<IslandPresetAsset>();
        public int CurrentId { get; private set; }

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

            if (GameEntry.Event != null)
            {
                GameEntry.Event.Fire(this, PresetTransitionRequestEventArgs.Create(id));
                return;
            }

            Apply(id, teleportPlayer: true);
        }

        public void Apply(int id, bool teleportPlayer = true)
        {
            IslandPresetAsset preset = GetById(id);
            if (preset == null)
                return;

            CurrentId = id;
            GameEntry.Weather?.SetTimeOfDay(preset.TimeHours);
            GameEntry.Weather?.SetWeather(preset.WeatherKind, preset.WeatherLevel);
            GameEntry.Weather?.SetFogDensity(preset.FogDensity);
            // Per-preset light color override (alpha=0 means "use the time-of-day table").
            Color? lightOverride = preset.LightColorOverride.a > 0.001f
                ? (Color?)preset.LightColorOverride
                : null;
            GameEntry.Weather?.SetLightColorOverride(lightOverride);

            if (teleportPlayer)
                IslandJoystickCamera.Current?.ApplyPreset(preset);

            if (GameEntry.Event != null)
                GameEntry.Event.Fire(this, PresetChangedEventArgs.Create(preset));
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
