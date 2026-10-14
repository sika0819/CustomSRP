#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP.Editor
{
    public static class OilFilterMenu
    {
        static PostFXSettings ActiveSettings()
        {
            var asset = GraphicsSettings.currentRenderPipeline as CustomRenderPipelineAsset;
            return asset != null ? asset.Settings.postFXSettings : null;
        }

        [MenuItem("CustomSRP/油画滤镜/开")]
        public static void Enable()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[OilFilter] 请在 Play Mode 开关");
                return;
            }

            OilFilter.SetEnabled(true, persist: true);
            Debug.Log("[OilFilter] on");
        }

        [MenuItem("CustomSRP/油画滤镜/关")]
        public static void Disable()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[OilFilter] 请在 Play Mode 开关");
                return;
            }

            OilFilter.SetEnabled(false, persist: true);
            Debug.Log("[OilFilter] off");
        }

        [MenuItem("CustomSRP/油画滤镜/切换")]
        public static void Toggle()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[OilFilter] 请在 Play Mode 开关");
                return;
            }

            OilFilter.Toggle(persist: true, ActiveSettings());
            Debug.Log(OilFilter.IsEnabled(ActiveSettings())
                ? "[OilFilter] on"
                : "[OilFilter] off");
        }

        [MenuItem("CustomSRP/油画滤镜/清除覆盖（回 Settings）")]
        public static void ClearOverride()
        {
            OilFilter.ClearOverride(clearPrefs: true);
            Debug.Log("[OilFilter] override cleared; using Post FX Settings");
        }

        [MenuItem("CustomSRP/油画滤镜/开", true)]
        [MenuItem("CustomSRP/油画滤镜/关", true)]
        [MenuItem("CustomSRP/油画滤镜/切换", true)]
        static bool PlayModeValidate() => Application.isPlaying;
    }
}
#endif
