using Game;
using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// Global override for the PostFX edge oil filter.
    /// Settings asset is the default; play-mode menu / prefs can force on or off.
    /// Editor preset capture boosts to full strength, full radius, and 16-tap HQ
    /// via <see cref="BeginEditorCaptureBoost"/>, then restores the previous override.
    /// </summary>
    public static class OilFilter
    {
        public const string PrefsKey = "CustomSRP.OilFilter";

        static bool _initialized;
        static bool _hasOverride;
        static bool _overrideEnabled;
        static bool _forceHighQuality;
        static float _strengthOverride = -1f;
        static float _radiusScaleOverride = -1f;
        static bool _captureMax;

        struct CaptureBoostState
        {
            public bool Active;
            public bool PrevHasOverride;
            public bool PrevEnabled;
            public bool PrevHq;
            public float PrevStrength;
            public float PrevRadius;
            public bool PrevCaptureMax;
        }

        static CaptureBoostState _boost;

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

        /// <summary>Editor capture: use 16-tap Kuwahara wet-oil instead of the 4-tap runtime path.</summary>
        public static bool ForceHighQuality
        {
            get
            {
                EnsureInit();
                return _forceHighQuality;
            }
        }

        public static bool IsEnabled(PostFXSettings settings)
        {
            if (!IslandGraphicsTier.AllowsOilFilter)
                return false;

            // B6: walking on mobile skips the full-screen oil edge pass.
            if (Application.isMobilePlatform && IslandLocomotionState.IsWalking)
                return false;

            EnsureInit();
            if (_hasOverride)
                return _overrideEnabled;
            return settings != null && settings.OilFilter.enabled;
        }

        /// <summary>Editor capture: drop the 0.50–0.78 edge damper so the dab blends at full strength.</summary>
        public static bool CaptureMax
        {
            get
            {
                EnsureInit();
                return _captureMax;
            }
        }

        /// <summary>Authored strength, or capture boost override (1 = full dab).</summary>
        public static float ResolveStrength(float authored)
        {
            EnsureInit();
            if (_strengthOverride > 0f)
                return Mathf.Clamp(_strengthOverride, 0.05f, 1f);
            return Mathf.Clamp(authored, 0.05f, 1f);
        }

        /// <summary>
        /// Resolution radius, or 1 during capture so 4K does not shrink the dab below the 1080 reference.
        /// </summary>
        public static float ResolveRadiusScale(float computed)
        {
            EnsureInit();
            if (_radiusScaleOverride >= 0f)
                return Mathf.Clamp(_radiusScaleOverride, 0.35f, 1f);
            return computed;
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

        /// <summary>
        /// Force oil on at maximum (strength 1, radius scale 1, full edge blend, 16-tap HQ)
        /// for editor preset screenshots.
        /// Nesting is ignored; call <see cref="EndEditorCaptureBoost"/> once per begin.
        /// </summary>
        public static void BeginEditorCaptureBoost()
        {
            EnsureInit();
            if (_boost.Active)
                return;

            _boost = new CaptureBoostState
            {
                Active = true,
                PrevHasOverride = _hasOverride,
                PrevEnabled = _overrideEnabled,
                PrevHq = _forceHighQuality,
                PrevStrength = _strengthOverride,
                PrevRadius = _radiusScaleOverride,
                PrevCaptureMax = _captureMax,
            };
            _hasOverride = true;
            _overrideEnabled = true;
            _forceHighQuality = true;
            _strengthOverride = 1f;
            _radiusScaleOverride = 1f;
            _captureMax = true;
        }

        public static void EndEditorCaptureBoost()
        {
            EnsureInit();
            if (!_boost.Active)
                return;

            _hasOverride = _boost.PrevHasOverride;
            _overrideEnabled = _boost.PrevEnabled;
            _forceHighQuality = _boost.PrevHq;
            _strengthOverride = _boost.PrevStrength;
            _radiusScaleOverride = _boost.PrevRadius;
            _captureMax = _boost.PrevCaptureMax;
            _boost = default;
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
