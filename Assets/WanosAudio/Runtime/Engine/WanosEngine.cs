using System;
using UnityEngine;

namespace WanosAudio
{
    /// <summary>
    /// Engine lifecycle: Load (silent) → EnablePlayback → Unload.
    /// Platform Mute-before-open is inlined (no Boluo policy classes).
    /// </summary>
    public static class WanosEngine
    {
        const string LogTag = nameof(WanosEngine);

        static bool _playbackEnabled;
        static bool _eventPostAllowed;

        public static bool IsLoaded => WanosNative.Instance.IsCreated;
        public static bool IsPlaybackEnabled => _playbackEnabled;
        public static bool IsEventPostAllowed => _eventPostAllowed;

        public static void ResetSession()
        {
            _playbackEnabled = false;
            _eventPostAllowed = false;
            WanosInteraction.ResetSession();
            WanosBankStore.ResetSession();
        }

        /// <summary>Create engine; banks may load; business PostEvent blocked.</summary>
        public static bool Load(string reason)
        {
            if (WanosNative.Instance.IsCreated)
            {
                KeepSilent(reason + "/already-loaded");
                return true;
            }

            try
            {
                WanosResult r = WanosNative.Instance.Create();
                WanosBankStore.ClearAfterEngineUnload();
                _playbackEnabled = false;
                _eventPostAllowed = false;
                KeepSilent(reason);
                Debug.Log($"[{LogTag}] Load ({reason}) result={r} events=off");
                return r == WanosResult.Success || WanosNative.Instance.IsCreated;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] Load({reason}): {e.Message}");
                return false;
            }
        }

        /// <summary>Start the DSP audio thread. Safe on all platforms (optional export).</summary>
        public static void StartEngine(string reason = null)
        {
            if (!WanosNative.Instance.IsCreated)
                return;
            bool ok = WanosNative.Instance.StartEngine();
            Debug.Log($"[{LogTag}] StartEngine ({reason ?? "<no-reason>"}) ok={ok}");
        }

        /// <summary>Stop the DSP audio thread. Safe on all platforms (optional export).</summary>
        public static void StopEngine(string reason = null)
        {
            if (!WanosNative.Instance.IsCreated)
                return;
            WanosNative.Instance.StopAllVoices(500);
            WanosNative.Instance.TrySetMute(true);
            WanosNative.Instance.StopEngine();
            Debug.Log($"[{LogTag}] StopEngine ({reason ?? "<no-reason>"})");
        }

        /// <summary>Attach playback device, start DSP, allow business PostEvent.</summary>
        public static void EnablePlayback(string reason)
        {
            if (!WanosNative.Instance.IsCreated)
                Load(reason + "/auto-load");

            if (!WanosNative.Instance.IsCreated)
            {
                Debug.LogError($"[{LogTag}] EnablePlayback ({reason}) aborted — engine not created");
                return;
            }

            try
            {
                if (!WanosNative.Instance.OpenDefaultPlaybackDevice())
                {
                    Debug.LogError($"[{LogTag}] EnablePlayback ({reason}) aborted — playback device failed");
                    _playbackEnabled = false;
                    _eventPostAllowed = false;
                    return;
                }

                // Engine start is the business's responsibility (call StartEngine() before EnablePlayback).
                // This method only attaches the playback device + unblocks PostEvent.

                _playbackEnabled = true;
                _eventPostAllowed = true;

                // On mobile platforms the engine starts muted; on others it's already unmuted.
                // We don't touch mute here — callers use Unmute()/KeepSilent() explicitly.
                var state = WanosNative.Instance.PlaybackState;
                Debug.Log($"[{LogTag}] EnablePlayback ({reason}) events=on playbackState={state}");
            }
            catch (Exception e)
            {
                _playbackEnabled = false;
                _eventPostAllowed = false;
                Debug.LogWarning($"[{LogTag}] EnablePlayback({reason}): {e.Message}");
            }
        }

        public static void Unload(string reason)
        {
            _eventPostAllowed = false;
            _playbackEnabled = false;

            if (!WanosNative.Instance.IsCreated)
                return;

            try
            {
                _eventPostAllowed = true;
                try
                {
                    WanosNative.Instance.PostEvent(0, "Stop_All");
                    WanosNative.Instance.PostEvent(0, "Pause_ALL");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[{LogTag}] stop on Unload: {e.Message}");
                }
                finally
                {
                    _eventPostAllowed = false;
                }

                WanosInteraction.ShutdownObjects();
                WanosNative.Instance.Destroy(reason);
                WanosBankStore.ClearAfterEngineUnload();
                WanosInteraction.ResetSession();
                Debug.Log($"[{LogTag}] Unload OK ({reason})");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] Unload({reason}): {e.Message}");
            }
        }

        public static void KeepSilent(string reason)
        {
            try
            {
                bool prev = _eventPostAllowed;
                _eventPostAllowed = true;
                try { WanosNative.Instance.PostEvent(0, "Pause_ALL"); }
                finally { _eventPostAllowed = prev; }

                if (WanosNative.Instance.IsCreated)
                    WanosNative.Instance.TrySetMute(true);
                Debug.Log($"[{LogTag}] silent ({reason})");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] KeepSilent({reason}): {e.Message}");
            }
        }

        public static void Unmute(string reason)
        {
            if (WanosNative.Instance.IsCreated)
                WanosNative.Instance.TrySetMute(false);
            Debug.Log($"[{LogTag}] unmute ({reason})");
        }

        public static bool ShouldAllowPostEvent(string eventName)
        {
            if (!WanosNative.Instance.IsCreated)
                return false;
            if (_eventPostAllowed)
                return true;
            return IsControlEvent(eventName);
        }

        public static bool IsControlEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName))
                return false;
            return eventName == "Pause_ALL"
                   || eventName == "Stop_All"
                   || eventName == "Resume_ALL"
                   || eventName == "Pause_Music"
                   || eventName == "Resume_Music"
                   || eventName == "Stop_Music"
                   || eventName == "Break_Music";
        }

        public static void PauseAll() => WanosNative.Instance.PostEvent(0, "Pause_ALL");
        public static void ResumeAll() => WanosNative.Instance.PostEvent(0, "Resume_ALL");
        public static void StopAll() => WanosNative.Instance.PostEvent(0, "Stop_All");

        public static void SetVolumeParameter(string paramName, float normalized01)
        {
            WanosNative.Instance.SetParameter(paramName, Mathf.Clamp01(normalized01));
        }

        /// <summary>
        /// Global RTPC (weather etc.). Not clamped — Boluo weather uses 0 / 30 / 60 / 90.
        /// Volume knobs still go through <see cref="SetVolumeParameter"/>.
        /// </summary>
        public static void SetGlobalParameter(string paramName, float value)
        {
            WanosNative.Instance.SetParameter(paramName, value);
        }
    }
}
