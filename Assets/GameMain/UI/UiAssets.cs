using GameFramework.UI;

namespace Game
{
    public static class UiAssets
    {
        // Loading
        public const string LoadingForm = "Assets/GameMain/UI/Forms/LoadingForm.prefab";

        // Main
        public const string IslandMainForm = "Assets/GameMain/UI/Forms/IslandMainForm.prefab";

        /// <summary>Above Dialog, below Loading. Not a popup — does not block locomotion.</summary>
        public const string TipGroup = "Tip";
        public const string TipToastForm = "Assets/GameMain/UI/Forms/TipToastForm.prefab";

        // Panel
        public const string WeatherForm = "Assets/GameMain/UI/Forms/WeatherForm.prefab";
        public const string MusicForm = "Assets/GameMain/UI/Forms/MusicForm.prefab";
        public const string VolumeForm = "Assets/GameMain/UI/Forms/VolumeForm.prefab";
        public const string PresetForm = "Assets/GameMain/UI/Forms/PresetForm.prefab";

        // Dialog
        public const string TimerPauseForm = "Assets/GameMain/UI/Forms/TimerPauseForm.prefab";
        public const string CommonDialogForm = "Assets/GameMain/UI/Forms/CommonDialogForm.prefab";

        // Download chain
        public const string RequestDownloadForm = "Assets/GameMain/UI/Forms/Download/RequestDownloadForm.prefab";
        public const string DownloadProgressForm = "Assets/GameMain/UI/Forms/Download/DownloadProgressForm.prefab";
        public const string MobileDataConfirmForm = "Assets/GameMain/UI/Forms/Download/MobileDataConfirmForm.prefab";
        public const string InsufficientStorageForm = "Assets/GameMain/UI/Forms/Download/InsufficientStorageForm.prefab";
        public const string NoNetworkForm = "Assets/GameMain/UI/Forms/Download/NoNetworkForm.prefab";

        // Pause / Timer
        public const string PauseTipForm = "Assets/GameMain/UI/Forms/PauseTipForm.prefab";
        public const string TimerEndForm = "Assets/GameMain/UI/Forms/TimerEndForm.prefab";

        // Login / Account
        public const string AgreementForm = "Assets/GameMain/UI/Forms/AgreementForm.prefab";
#if UNITY_EDITOR
        /// <summary>编辑器联调注销弹窗；资源在 Editor/UI，不进发包采集。</summary>
        public const string CancelAccountReminderForm =
            "Assets/GameMain/Editor/UI/CancelAccountReminder/CancelAccountReminderForm.prefab";
#endif

        // Settings
        public const string LogoutConfirmForm = "Assets/GameMain/UI/Forms/LogoutConfirmForm.prefab";
        public const string SaveConfirmForm = "Assets/GameMain/UI/Forms/SaveConfirmForm.prefab";

        public static void OpenPanel(string assetPath)
        {
            CloseGroup("Panel");
            GameEntry.UI.OpenUIForm(assetPath, "Panel");
        }

        public static void OpenDialog(string assetPath, object userData = null)
        {
            GameEntry.UI.OpenUIForm(assetPath, "Dialog", userData);
        }

        public static void CloseGroup(string groupName)
        {
            IUIGroup group = GameEntry.UI.GetUIGroup(groupName);
            if (group == null)
                return;

            IUIForm[] forms = group.GetAllUIForms();
            int[] serials = new int[forms.Length];
            for (int i = 0; i < forms.Length; i++)
                serials[i] = forms[i].SerialId;

            for (int i = 0; i < serials.Length; i++)
                GameEntry.UI.CloseUIForm(serials[i]);
        }
    }
}
