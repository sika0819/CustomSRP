using Game;
using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// Global override for the PostFX edge oil filter.
    /// Settings asset is the default; play-mode menu / prefs can force on or off.
    /// </summary>
    public static class OilFilter
    {
        public const string PrefsKey = "CustomSRP.OilFilter";

        static bool _initialized;
        static bool _hasOverride;
        static bool _overrideEnabled;

        public static bool HasOverride
        {
            get
            {
                EnsureInit();
                return _hasOverride;
            }
        }

        public static bool OverrideEnabled
        {
            get
            {
                EnsureInit();
                return _overrideEnabled;
            }
        }

        public static bool IsEnabled(PostFXSettings settings)
        {
            // B6: walking on mobile skips the full-screen oil edge pass.
            if (Application.isMobilePlatform && IslandLocomotionState.IsWalking)
                return false;

            EnsureInit();
            if (_hasOverride)
                return _overrideEnabled;
            return settings != null && settings.OilFilter.enabled;
        }

        public static void SetEnabled(bool enabled, bool persist = false)
        {
            EnsureInit();
            _hasOverride = true;
            _overrideEnabled = enabled;
            if (persist)
            {
                PlayerPrefs.SetInt(PrefsKey, enabled ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        public static void ClearOverride(bool clearPrefs = false)
        {
            EnsureInit();
            _hasOverride = false;
            if (clearPrefs && PlayerPrefs.HasKey(PrefsKey))
            {
                PlayerPrefs.DeleteKey(PrefsKey);
                PlayerPrefs.Save();
            }
        }

        public static void Toggle(bool persist = false, PostFXSettings settings = null)
        {
            SetEnabled(!IsEnabled(settings), persist);
        }

        static void EnsureInit()
        {
            if (_initialized)
                return;
            _initialized = true;
            if (PlayerPrefs.HasKey(PrefsKey))
            {
                _hasOverride = true;
                _overrideEnabled = PlayerPrefs.GetInt(PrefsKey, 0) != 0;
            }
        }
    }
}
