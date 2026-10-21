using UnityEditor;
using UnityEngine;

namespace WanosAudio.Editor
{
    /// <summary>
    /// Editor Play exit / quit must tear down native WIAEngine before Domain Reload
    /// or process exit. Relying only on GameEntry.OnDestroy can race plugin unload
    /// and leave the engine alive → second Play has no sound.
    /// </summary>
    [InitializeOnLoad]
    static class WanosAudioEditorLifecycle
    {
        static WanosAudioEditorLifecycle()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += OnEditorQuitting;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // Before GameObject teardown / Domain Reload.
            if (state == PlayModeStateChange.ExitingPlayMode)
                ForceUnload("ExitingPlayMode");
        }

        static void OnEditorQuitting()
        {
            ForceUnload("EditorApplication.quitting");
        }

        static void ForceUnload(string reason)
        {
            if (!WanosNative.Instance.IsCreated)
                return;

            Debug.Log($"[WanosAudioEditorLifecycle] ForceUnload ({reason})");
            WanosEngine.Unload(reason);
        }
    }
}
