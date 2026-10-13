using GameFramework.UI;

namespace Game
{
    public static class UiAssets
    {
        public const string LoadingForm = "Assets/GameMain/UI/Forms/LoadingForm.prefab";
        public const string IslandMainForm = "Assets/GameMain/UI/Forms/IslandMainForm.prefab";
        public const string WeatherForm = "Assets/GameMain/UI/Forms/WeatherForm.prefab";
        public const string MusicForm = "Assets/GameMain/UI/Forms/MusicForm.prefab";
        public const string VolumeForm = "Assets/GameMain/UI/Forms/VolumeForm.prefab";
        public const string PresetForm = "Assets/GameMain/UI/Forms/PresetForm.prefab";
        public const string TimerPauseForm = "Assets/GameMain/UI/Forms/TimerPauseForm.prefab";
        public const string MobileDataForm = "Assets/GameMain/UI/Forms/MobileDataForm.prefab";
        public const string DownloadAskForm = "Assets/GameMain/UI/Forms/DownloadAskForm.prefab";
        public const string NetworkRequiredForm = "Assets/GameMain/UI/Forms/NetworkRequiredForm.prefab";
        public const string DownloadProgressForm = "Assets/GameMain/UI/Forms/DownloadProgressForm.prefab";
        public const string StorageFullForm = "Assets/GameMain/UI/Forms/StorageFullForm.prefab";

        public static void OpenPanel(string assetPath)
        {
            CloseGroup("Panel");
            GameEntry.UI.OpenUIForm(assetPath, "Panel");
        }

        public static void OpenDialog(string assetPath)
        {
            GameEntry.UI.OpenUIForm(assetPath, "Dialog");
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
