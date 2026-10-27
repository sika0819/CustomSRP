using Game.Native;
using GameFramework.UI;

namespace Game
{
    /// <summary>Panel/Dialog open → block joystick and idle immersive.</summary>
    public static class HudOverlayGate
    {
        /// <summary>Unity Panel/Dialog only。相机 renderScale 另认 NativeOverlayGate.OverlayPresent（同档 0.375）。</summary>
        public static bool HasPopup()
        {
            return GroupHasForms("Panel") || GroupHasForms("Dialog");
        }

        /// <summary>
        /// 挡空闲沉浸：Unity 弹窗 + 原生半层（overlayPresent）+ 定时窗（bottomIconsHidden）。
        /// 对齐 iOS：定时只抬 bottomIconsHidden 挡沉浸、不挡帧；关半层后须重启沉浸倒计时。
        /// </summary>
        public static bool BlocksIdleImmersive() =>
            HasPopup()
            || NativeOverlayGate.BottomIconsHidden
            || NativeOverlayGate.OverlayPresent;

        public static bool BlocksLocomotion() =>
            HasPopup() || NativeOverlayGate.BlocksLocomotion;

        static bool GroupHasForms(string groupName)
        {
            if (GameEntry.UI == null)
                return false;
            IUIGroup group = GameEntry.UI.GetUIGroup(groupName);
            // UIFormCount — 勿 GetAllUIForms()：相机每帧 HasPopup 会分配 IUIForm[]。
            return group != null && group.UIFormCount > 0;
        }
    }
}
