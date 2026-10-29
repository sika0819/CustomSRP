using System;
using System.Threading;
using UnityGameFramework.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Native
{
    /// <summary>强类型 U→N Notify Helper（AOT 接口层；传输不变）。</summary>
    public static class NativeMessages
    {
        static int _islandReadySent;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetIslandReady()
        {
            _islandReadySent = 0;
        }

        public static void ResetIslandReadyFlag() => _islandReadySent = 0;

        public static void HudIcon(int iconId)
        {
            NativeBridgeTransport.Notify(MsgType.UiHudIcon, iconId);
        }

        public static void HudProfile() => HudIcon(MsgType.HudIconProfile);
        public static void HudTimer() => HudIcon(MsgType.HudIconTimer);
        public static void HudHealth() => HudIcon(MsgType.HudIconHealth);

        /// <summary>岛面可看就关原生启动页。HUD 未就绪也可以发；重复调用会被丢掉。</summary>
        public static void IslandReady()
        {
            if (Interlocked.Exchange(ref _islandReadySent, 1) != 0)
                return;
            IslandEntryLog.Ok("islandReady", "notify");
            NativeBridgeTransport.Notify(MsgType.EngineIslandReady);
        }

        /// <summary>
        /// 流程已在 GuestLogin。冷启动也会进这里。
        /// <paramref name="armed"/>：0 门口在等 106，1 已经接到本轮 106。
        /// </summary>
        public static void ReturnedToGuestLogin(int armed)
        {
            if (Game.ProcedureIsland.EnterCount > 0)
                IslandEntryLog.Ok(
                    "guestLogin",
                    "returned",
                    "islandEnter=" + Game.ProcedureIsland.EnterCount + " armed=" + armed);
            NativeBridgeTransport.Notify(MsgType.ReturnedToGuestLogin, armed);
        }

        /// <summary>同步告诉宿主锁屏 / 负一屏当前曲目。Notify 没有字符串，所以走 Query。inBlob=`song\talbum[\tposMs\tdurMs]`。</summary>
        public static void NowPlaying(
            int playing,
            int album,
            int track,
            string title,
            string albumName,
            int positionMs = 0,
            int durationMs = 0)
        {
            string blob = Sanitize(title) + "\t" + Sanitize(albumName) + "\t" +
                          Math.Max(0, positionMs) + "\t" + Math.Max(0, durationMs);
            NativeBridgeTransport.Query(MsgType.NowPlaying, playing != 0 ? 1 : 0, album, track, blob, out _);
        }

        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        }
    }

    /// <summary>原生 overlay 触摸门控（AOT；热更可读）。</summary>
    public static class NativeOverlayGate
    {
        public static bool OverlayPresent { get; private set; }
        public static bool TouchEnabled { get; private set; } = true;
        public static bool BottomIconsHidden { get; private set; }

        public static bool BlocksLocomotion => OverlayPresent && !TouchEnabled;

        /// <summary>原生页盖住岛：挡行走且不让触摸穿透时，把 GPU 让给原生。</summary>
        public static bool YieldsGpu => OverlayPresent && !TouchEnabled;

        /// <summary>
        /// 雨帘可见时抬高 overlay yield 下限（由 CustomSRP 雨驱动写入；Game 不引用 OilRainSettings）。
        /// </summary>
        public static bool RainActiveForYield { get; private set; }

        public static event System.Action Changed;

        static int _fpsBeforeYield = int.MinValue;

        /// <summary>雨强度变化时调用；若正 YieldsGpu 则重算 fps/interval。</summary>
        public static void SetRainActiveForYield(bool active)
        {
            if (RainActiveForYield == active)
                return;
            RainActiveForYield = active;
            if (YieldsGpu)
                ApplyGpuYield();
        }

        public static void Apply(int present, int touchEnabled, int bottomHidden)
        {
            OverlayPresent = present != 0;
            TouchEnabled = touchEnabled != 0;
            BottomIconsHidden = bottomHidden != 0;
            ApplyGpuYield();
            Changed?.Invoke();
        }

        static void ApplyGpuYield()
        {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            if (YieldsGpu)
            {
                if (_fpsBeforeYield == int.MinValue)
                    _fpsBeforeYield = Application.targetFrameRate;

                // 不 UnityPause：pause 清 GL，关页会闪相机蓝。隔帧提交，Surface 留最后一帧。
                // 雨天：30fps / interval 2，雨帘可滚且仍让出 GPU（配合 renderScale 0.25）。
                // 晴天：Android 禁止 1fps / interval=60（Push 会被拖死）→ 15/4；iOS 1/60 近冻景。
                int overlayFps;
                int overlayInterval;
                if (RainActiveForYield)
                {
                    overlayFps = 30;
                    overlayInterval = 2;
                }
                else
                {
#if UNITY_ANDROID
                    overlayFps = 15;
                    overlayInterval = 4;
#else
                    overlayFps = 1;
                    overlayInterval = 60;
#endif
                }
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = overlayFps;
                OnDemandRendering.renderFrameInterval = overlayInterval;
                Log.Info($"[IMP] overlay gpu-yield on fps={overlayFps} interval={overlayInterval} rain={RainActiveForYield}");
                return;
            }
#endif
            if (_fpsBeforeYield == int.MinValue)
            {
                if (OnDemandRendering.renderFrameInterval != 1)
                    OnDemandRendering.renderFrameInterval = 1;
                return;
            }

            OnDemandRendering.renderFrameInterval = 1;
            _fpsBeforeYield = int.MinValue;
            // 不盲目还原进 yield 前的值（可能是沉浸 25）；按当前岛面状态重算 60/25。
            Game.IslandCameraSrpSetup.ReapplyTargetFrameRateAfterOverlay();
            Log.Info("[IMP] overlay gpu-yield off");
        }

#if UNITY_EDITOR
        public static void ResetForEditor()
        {
            OverlayPresent = false;
            TouchEnabled = true;
            BottomIconsHidden = false;
            ApplyGpuYield();
            Changed?.Invoke();
        }
#endif
    }
}
