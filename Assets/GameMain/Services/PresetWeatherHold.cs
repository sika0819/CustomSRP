using System;

namespace Game
{
    /// <summary>
    /// Suppresses rain visuals, precip audio, and zone ambience loads during preset iris
    /// close / black / half-open. Release only after iris is fully clear.
    /// </summary>
    public static class PresetWeatherHold
    {
        public static bool IsHeld { get; private set; }

        public static event Action HeldChanged;

        public static void Begin()
        {
            if (IsHeld)
                return;
            IsHeld = true;
            // Inhibit zone StartSource for the whole iris window (doc: 禁止走路新加载).
            // Do NOT ClearPlaying here — iris is still open; hard-stop/RTPC-zero causes
            // audible crackle. Peak Apply → ClearContent3D runs under black.
            AmbienceZoneService.SetPresetTransitionHeld(true);
            HeldChanged?.Invoke();
        }

        public static void Release()
        {
            if (!IsHeld)
                return;
            IsHeld = false;
            AmbienceZoneService.SetPresetTransitionHeld(false);
            HeldChanged?.Invoke();
            WeatherAudioBridge.Refresh();
            AudioInteractionBootstrap.RetriggerContentAtListener();
        }

        /// <summary>Interrupt / leave island — drop hold without retriggering ambience.</summary>
        public static void Abort()
        {
            if (!IsHeld)
                return;
            IsHeld = false;
            AmbienceZoneService.SetPresetTransitionHeld(false);
            HeldChanged?.Invoke();
        }
    }
}
