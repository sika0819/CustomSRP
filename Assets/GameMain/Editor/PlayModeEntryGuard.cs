using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

using UnityGameFramework.Runtime;
namespace Game.Editor
{
    /// <summary>
    /// Keeps Editor Play on Launch with Domain Reload. Toggling Enter Play Mode
    /// Options otherwise breaks the second Play (statics / stuck boot).
    /// </summary>
    [InitializeOnLoad]
    static class PlayModeEntryGuard
    {
        const string LaunchPath = SceneAssets.Launch;

        static PlayModeEntryGuard()
        {
            EditorApplication.delayCall += Apply;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
                Apply();
        }

        static void Apply()
        {
            bool dirty = false;

            if (EditorSettings.enterPlayModeOptionsEnabled
                || EditorSettings.enterPlayModeOptions != EnterPlayModeOptions.None)
            {
                EditorSettings.enterPlayModeOptionsEnabled = false;
                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.None;
                dirty = true;
            }

            SceneAsset launch = AssetDatabase.LoadAssetAtPath<SceneAsset>(LaunchPath);
            if (launch != null && EditorSceneManager.playModeStartScene != launch)
            {
                EditorSceneManager.playModeStartScene = launch;
                dirty = true;
            }

            if (dirty)
                Log.Info("[PlayModeEntryGuard] Play entry locked to Launch + Domain Reload.");
        }
    }
}
