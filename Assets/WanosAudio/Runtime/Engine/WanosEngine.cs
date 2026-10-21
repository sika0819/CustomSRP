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

        /// <summary>最后一次写入引擎的音量（0–1 UI 量纲），用于回前台重推。</summary>
        static float _lastMusicVolume01 = 1f;
        static float _lastFxVolume01 = 1f;

        /// <summary>
        /// 音频被打断标记：系统 / 其它 App 抢焦点、原生 muted 回调置位。
        /// 回前台或玩家点场景时用它决定是否要跑一次恢复。
        /// </summary>
        static bool _interrupted;

        public static bool IsLoaded => WanosNative.Instance.IsCreated;
        public static bool IsPlaybackEnabled => _playbackEnabled;
        public static bool IsEventPostAllowed => _eventPostAllowed;

        /// <summary>音频是否处于「被打断」状态（mute / 焦点丢失 / 系统静音回调）。</summary>
        public static bool IsInterrupted => _interrupted;

        /// <summary>标记音频被打断（原生 muted / 焦点失败回调）。</summary>
        public static void MarkInterrupted(string reason)
        {
            if (_interrupted)
                return;
            _interrupted = true;
            Debug.Log($"[{LogTag}] interrupted ({reason})");
        }

        /// <summary>清除打断标记（恢复流程走完）。</summary>
        public static void ClearInterrupted(string reason)
        {
            if (!_interrupted)
                return;
            _interrupted = false;
            Debug.Log($"[{LogTag}] interrupted cleared ({reason})");
        }

        public static void ResetSession()
        {
            _playbackEnabled = false;
            _eventPostAllowed = false;
            _interrupted = false;
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

                // Call EnablePlayback (device) then StartEngine (DSP) — macOS wrapper needs device open.
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
                // Stop events while DSP can still accept them, then halt the thread
                // before DestoryEngine. GameEntry.OnDestroy / Editor exit used to skip
                // StopEngine and leave CoreAudio / DSP alive across Play restarts.
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

                StopEngine(reason + "/pre-destroy");
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

        /// <summary>
        /// 回前台的引擎层恢复：解 mute + 重推音量 + 清打断标记。
        /// 不做业务判断（曲目 / 内容声由 GameMain 决定）。
        /// </summary>
        public static bool RestoreForeground(string reason)
        {
            if (!WanosNative.Instance.IsCreated)
                return false;

            Unmute(reason);
            ReapplyVolumes();
            ClearInterrupted(reason);
            return true;
        }

        /// <summary>
        /// 重推上次写入的音量 RTPC。引擎 mute/unmute、设备重建后 RTPC 可能回到默认值，
        /// 回前台必须补一次，否则「有播放但音量不对 / 听不到」。
        /// </summary>
        public static void ReapplyVolumes()
        {
            if (!WanosNative.Instance.IsCreated)
                return;
            SetVolumeParameter(MusicVolumeParam, _lastMusicVolume01);
            SetVolumeParameter(FxVolumeParam, _lastFxVolume01);
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

        /// <summary>
        /// 音量 RTPC 名白名单。引擎侧 RTPC 量程是 0–100（bank 定义：
        /// <c>Music_Volume</c> / <c>Fx_Volume</c> 的 Range max = 100，Default = 80）。
        /// 写错名字会被引擎静默丢弃 → 音量恒为默认/无效，且没有任何报错。
        /// </summary>
        public const string MusicVolumeParam = "Music_Volume";
        public const string FxVolumeParam = "Fx_Volume";

        /// <summary>
        /// Volume RTPCs (<c>Music_Volume</c> / <c>Fx_Volume</c>) are authored 0–100
        /// (legacy GameData.bgmVolume default 80). UI stores 0–1; multiply here.
        /// <para>
        /// 量纲是硬约定：引擎 RTPC 量程 0–100，把 0–1 的 UI 值原样透传过去 = 0.8% 音量 = 听不到。
        /// 所有音量必须走本方法，不要直接调 <see cref="WanosNative.SetParameter"/>。
        /// </para>
        /// </summary>
        public static void SetVolumeParameter(string paramName, float normalized01)
        {
            if (paramName != MusicVolumeParam && paramName != FxVolumeParam)
            {
                Debug.LogError($"[{LogTag}] SetVolumeParameter 未知音量 RTPC 名 '{paramName}'" +
                               $"（合法：{MusicVolumeParam} / {FxVolumeParam}）。写错名字引擎会静默丢弃，音量不会变。");
                return;
            }

            float engineValue = Mathf.Clamp01(normalized01) * 100f;
            // 缓存 UI 量纲：回前台恢复时按缓存重推（引擎侧 RTPC 可能被重置回默认值）。
            if (paramName == MusicVolumeParam)
                _lastMusicVolume01 = Mathf.Clamp01(normalized01);
            else if (paramName == FxVolumeParam)
                _lastFxVolume01 = Mathf.Clamp01(normalized01);
            // 走 SetParameterEngineValue：这里已经是 0–100 引擎量纲，不该再被 0–1 量纲护栏判定。
            WanosNative.Instance.SetParameterEngineValue(paramName, engineValue);
            Debug.Log($"[{LogTag}] SetVolumeParameter {paramName}={engineValue:F1} (ui={normalized01:F3})");
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
