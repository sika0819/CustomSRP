using UnityEditor;
using UnityEngine;
using YooAsset;

namespace Game.Editor
{
    /// <summary>
    /// 编辑器资源运行模式菜单。对齐 BoluoUnity EditorPlayMode prefs。
    /// </summary>
    public static class YooAssetPlayModeMenu
    {
        const string Root = "Game/YooAsset/PlayMode/";

        [MenuItem(Root + "EditorSimulateMode", false, 100)]
        static void SetEditorSimulate() => Set(EPlayMode.EditorSimulateMode);

        [MenuItem(Root + "EditorSimulateMode", true)]
        static bool SetEditorSimulateValidate() => Validate(EPlayMode.EditorSimulateMode);

        [MenuItem(Root + "OfflinePlayMode", false, 101)]
        static void SetOffline() => Set(EPlayMode.OfflinePlayMode);

        [MenuItem(Root + "OfflinePlayMode", true)]
        static bool SetOfflineValidate() => Validate(EPlayMode.OfflinePlayMode);

        [MenuItem(Root + "HostPlayMode", false, 102)]
        static void SetHost() => Set(EPlayMode.HostPlayMode);

        [MenuItem(Root + "HostPlayMode", true)]
        static bool SetHostValidate() => Validate(EPlayMode.HostPlayMode);

        [MenuItem(Root + "WebPlayMode", false, 103)]
        static void SetWeb() => Set(EPlayMode.WebPlayMode);

        [MenuItem(Root + "WebPlayMode", true)]
        static bool SetWebValidate() => Validate(EPlayMode.WebPlayMode);

        static void Set(EPlayMode mode)
        {
            YooAssetPlayModePrefs.SetEditorPlayMode(mode);
            Menu.SetChecked(Root + "EditorSimulateMode", mode == EPlayMode.EditorSimulateMode);
            Menu.SetChecked(Root + "OfflinePlayMode", mode == EPlayMode.OfflinePlayMode);
            Menu.SetChecked(Root + "HostPlayMode", mode == EPlayMode.HostPlayMode);
            Menu.SetChecked(Root + "WebPlayMode", mode == EPlayMode.WebPlayMode);
        }

        static bool Validate(EPlayMode mode)
        {
            bool isMode = YooAssetPlayModePrefs.GetEditorPlayMode() == mode;
            Menu.SetChecked(Root + ModeMenuName(mode), isMode);
            return !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        static string ModeMenuName(EPlayMode mode)
        {
            return mode switch
            {
                EPlayMode.EditorSimulateMode => "EditorSimulateMode",
                EPlayMode.OfflinePlayMode => "OfflinePlayMode",
                EPlayMode.HostPlayMode => "HostPlayMode",
                EPlayMode.WebPlayMode => "WebPlayMode",
                _ => mode.ToString(),
            };
        }
    }
}
