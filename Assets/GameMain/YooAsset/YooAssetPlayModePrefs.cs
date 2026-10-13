using UnityEngine;
using YooAsset;

using UnityGameFramework.Runtime;
namespace Game
{
    /// <summary>
    /// 编辑器资源运行模式。对齐 BoluoUnity EditorPrefs key "EditorPlayMode"。
    /// </summary>
    public static class YooAssetPlayModePrefs
    {
        public const string PrefsKey = "EditorPlayMode";

        public static EPlayMode GetEditorPlayMode()
        {
#if UNITY_EDITOR
            return (EPlayMode)UnityEditor.EditorPrefs.GetInt(PrefsKey, (int)EPlayMode.EditorSimulateMode);
#else
            return EPlayMode.OfflinePlayMode;
#endif
        }

        public static void SetEditorPlayMode(EPlayMode playMode)
        {
#if UNITY_EDITOR
            UnityEditor.EditorPrefs.SetInt(PrefsKey, (int)playMode);
            Log.Info($"[YooAsset] EditorPlayMode → {playMode}");
#endif
        }
    }
}
