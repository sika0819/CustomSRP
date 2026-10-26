using UnityGameFramework.Runtime;

namespace Game.Native
{
    /// <summary>
    /// IMP 接口层启动：桥接宿主 + 注册默认 N→U Handlers（AOT）。
    /// 业务侧（热更）可继续 Register 覆盖/追加 Handler。
    /// </summary>
    public static class NativeBridgeBootstrap
    {
        static bool _done;

        public static void ResetSession()
        {
            _done = false;
        }

        public static void Init()
        {
            if (_done)
                return;
            _done = true;

            BoluoNativeBridge.EnsureExists();
            NativePushReceiver.EnsureRegistered();

            MessageRouter.Register(MsgType.UiOverlay, (present, touch, bottom) =>
            {
                NativeOverlayGate.Apply(present, touch, bottom);
                Log.Info(
                    $"[IMP] UiOverlay present={present} touch={touch} bottomHidden={bottom}");
            });

            MessageRouter.Register(MsgType.HealthPressureLevel, (level, _, __) =>
            {
                Log.Info($"[IMP] HealthPressureLevel level={level}");
                if (Game.GameEntry.Event != null)
                    Game.GameEntry.Event.Fire(null, NativeHealthPressureEventArgs.Create(level));
            });

            MessageRouter.Register(MsgType.UiProfileBadge, (visible, _, __) =>
            {
                Log.Info($"[IMP] UiProfileBadge visible={visible}");
                if (Game.GameEntry.Event != null)
                    Game.GameEntry.Event.Fire(null, NativeProfileBadgeEventArgs.Create(visible != 0));
            });

            MessageRouter.Register(MsgType.HostArmIslandEntry, (_, __, ___) =>
            {
                HostIslandEntryGate.Arm();
            });
            MessageRouter.Register(MsgType.HostLeaveIsland, (a, __, ___) =>
            {
                HostIslandEntryGate.Leave(a);
            });

            Log.Info("[IMP] NativeBridgeBootstrap handlers registered");
        }
    }
}
