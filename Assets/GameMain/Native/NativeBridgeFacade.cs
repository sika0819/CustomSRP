using System;
using Game.BoluoApi;
using UnityEngine;
using UnityGameFramework.Runtime;
// MusicPlaylist / GameEntry / IslandWorldReadyEventArgs live in parent namespace Game.

namespace Game.Native
{
    /// <summary>
    /// Native 门面。业务只走这里。对齐 Boluo NativeBridgeHotfix。
    /// </summary>
    public static class NativeBridgeFacade
    {
        static bool _done;

        public static bool OverlayPresent { get; private set; }
        public static bool BottomIconsHidden { get; private set; }
        public static bool BlocksLocomotion { get; private set; }

        public static void Init()
        {
            if (_done)
                return;
            _done = true;

            NativeBridgeBootstrap.Init();
            NowPlayingService.Init();
            NativeTimerService.AttachTo(BoluoNativeBridge.Instance != null
                ? BoluoNativeBridge.Instance.gameObject
                : null);
            NativeOverlayGate.Changed -= PublishOverlay;
            NativeOverlayGate.Changed += PublishOverlay;
            CaptureOverlay();

            MessageRouter.Register(MsgType.TimerProgress, (remaining, total, _) =>
            {
                Log.Info($"[IMP] TimerProgress remaining={remaining} total={total}");
                NativeTimerService.ApplyProgress(remaining, total);
            });

            MessageRouter.Register(MsgType.MediaCommand, (cmd, _, __) =>
            {
                Log.Info($"[IMP] MediaCommand cmd={cmd}");
                NowPlayingService.HandleMediaCommand(cmd);
            });

            if (Game.GameEntry.Event != null)
            {
                Game.GameEntry.Event.Subscribe(Game.IslandWorldReadyEventArgs.EventId, OnWorldReady);
            }

            Log.Info("[IMP] NativeBridgeFacade Init");
        }

        public static void ResetSession()
        {
            _done = false;
            OverlayPresent = false;
            BottomIconsHidden = false;
            BlocksLocomotion = false;
            MessageRouter.Unregister(MsgType.TimerProgress);
            MessageRouter.Unregister(MsgType.MediaCommand);
            NowPlayingService.ResetSession();
            NativeBridgeBootstrap.ResetSession();
            if (Game.GameEntry.Event != null)
                Game.GameEntry.Event.Unsubscribe(Game.IslandWorldReadyEventArgs.EventId, OnWorldReady);
            NativeMessages.ResetIslandReadyFlag();
            HostIslandEntryGate.Reset();
            NativeTimerService.Reset();
#if UNITY_EDITOR
            NativeOverlayGate.ResetForEditor();
#endif
        }

        public static void HudProfile() => NativeMessages.HudProfile();
        public static void HudTimer() => NativeMessages.HudTimer();
        public static void HudHealth() => NativeMessages.HudHealth();
        public static void NotifyIslandReady() => NativeMessages.IslandReady();

        /// <summary>
        /// 揭幕后手动 Apply overlay 后同步 Facade 镜像字段并通知 HUD（防 BlocksLocomotion 脏读）。
        /// </summary>
        public static void SyncOverlayFromGate() => PublishOverlay();

        public static void PublishNowPlaying(
            bool playing, int album, int track, string title, string albumName,
            int positionMs = 0, int durationMs = 0)
        {
            NativeMessages.NowPlaying(
                playing ? 1 : 0, album, track, title, albumName, positionMs, durationMs);
        }

        public static void SetStatusBarHidden(bool hidden)
        {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            ApplicationChrome.StatusBarState = hidden
                ? ApplicationChrome.States.Hidden
                : ApplicationChrome.States.VisibleOverContent;
#endif
        }

        /// <summary>
        /// Prefill 收尾补发：主路径已在 <c>ProcedureIsland</c> FadeHide 后 Notify；
        /// 若揭罩被打断未发，此处仍保证宿主能抬 hudReady。
        /// </summary>
        static void OnWorldReady(object sender, GameFramework.Event.GameEventArgs e)
        {
            if (!Game.ProcedureIsland.AcceptsIslandReadyNotify)
                return;
            NotifyIslandReady();
        }

        static void PublishOverlay()
        {
            CaptureOverlay();
            Game.HudBottomIcons.SyncNativeHidden(BottomIconsHidden);
            if (Game.GameEntry.Event == null)
                return;
            Game.GameEntry.Event.Fire(null, NativeOverlayEventArgs.Create(
                BottomIconsHidden, BlocksLocomotion, OverlayPresent));
        }

        static void CaptureOverlay()
        {
            OverlayPresent = NativeOverlayGate.OverlayPresent;
            BottomIconsHidden = NativeOverlayGate.BottomIconsHidden;
            BlocksLocomotion = NativeOverlayGate.BlocksLocomotion;
        }
    }
}
