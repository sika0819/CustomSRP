using Game.Native;
using GameFramework.UI;

namespace Game
{
    /// <summary>Panel/Dialog open → block joystick and idle immersive.</summary>
    public static class HudOverlayGate
    {
        public static bool HasPopup()
        {
            return GroupHasForms("Panel") || GroupHasForms("Dialog");
        }

        public static bool BlocksLocomotion() =>
            HasPopup() || NativeOverlayGate.BlocksLocomotion;

        static bool GroupHasForms(string groupName)
        {
            if (GameEntry.UI == null)
                return false;
            IUIGroup group = GameEntry.UI.GetUIGroup(groupName);
            if (group == null)
                return false;
            IUIForm[] forms = group.GetAllUIForms();
            return forms != null && forms.Length > 0;
        }
    }
}
