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

        /// <summary>
        /// 挡走路：Panel / 硬 Dialog / 原生半层。
        /// 区域申请窗与下载进度窗只作提示（固墙已拦住进区），不冻平移。
        /// </summary>
        public static bool BlocksLocomotion()
        {
            if (NativeOverlayGate.BlocksLocomotion)
                return true;
            if (GroupHasForms("Panel"))
                return true;
            return DialogsBlockLocomotion(DialogFormCount(), SoftDownloadDialogCount());
        }

        /// <summary>
        /// Dialog 组是否冻走路。软下载窗（申请 / 进度）可走开；其它 Dialog 仍冻。
        /// </summary>
        public static bool DialogsBlockLocomotion(int dialogCount, int softDownloadDialogCount) =>
            dialogCount > softDownloadDialogCount;

        static int DialogFormCount()
        {
            if (GameEntry.UI == null)
                return 0;
            IUIGroup group = GameEntry.UI.GetUIGroup("Dialog");
            return group != null ? group.UIFormCount : 0;
        }

        static int SoftDownloadDialogCount()
        {
            if (GameEntry.UI == null)
                return 0;
            int n = 0;
            if (GameEntry.UI.HasUIForm(UiAssets.RequestDownloadForm))
                n++;
            if (GameEntry.UI.HasUIForm(UiAssets.DownloadProgressForm))
                n++;
            return n;
        }

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
