using System;
using System.Reflection;
using UnityEngine;

using UnityGameFramework.Runtime;
namespace Game
{
    /// <summary>
    /// Wires Launch/main camera for Custom SRP island rendering.
    /// CustomSRP types live in Assembly-CSharp; Game talks to them via reflection.
    /// </summary>
    public static class IslandCameraSrpSetup
    {
        const string CrpTypeName = "CustomSRP.CustomRenderPipelineCamera, Assembly-CSharp";

        /// <summary>
        /// Skybox clear flags + CustomRenderPipelineCamera (copy color/depth, mobile scale).
        /// Matches island camera defaults from CreateIslandTerrainScene prefab bake.
        /// </summary>
        public static void ApplyForIsland(Camera camera)
        {
            if (camera == null)
                return;

            camera.clearFlags = CameraClearFlags.Skybox;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            if (camera.nearClipPlane < 0.3f)
                camera.nearClipPlane = 0.3f;
            // Fog end≈250 softens distance; 750 cuts far cull/draw without empty sky wall.
            if (camera.farClipPlane > 750f || camera.farClipPlane < 600f)
                camera.farClipPlane = 750f;

            EnsureCustomRenderPipelineCamera(camera.gameObject);
        }

        public static Component EnsureCustomRenderPipelineCamera(GameObject go)
        {
            if (go == null)
                return null;

            Type crpType = Type.GetType(CrpTypeName);
            if (crpType == null)
            {
                Log.Error(
                    "[IslandCamera] CustomRenderPipelineCamera type not found (" + CrpTypeName + ").");
                return null;
            }

            Component crp = go.GetComponent(crpType);
            if (crp == null)
                crp = go.AddComponent(crpType);

            ApplyDefaultSettings(crp);
            Camera cam = go.GetComponent<Camera>();
            Log.Info(
                "[IslandCamera] CRP ready RP="
                + (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null
                    ? UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.name
                    : "null")
                + " cam=" + go.name
                + " pixel=" + (cam != null ? cam.pixelWidth + "x" + cam.pixelHeight : "?")
                + " screen=" + Screen.width + "x" + Screen.height);
            return crp;
        }

        static void ApplyDefaultSettings(Component crp)
        {
            PropertyInfo settingsProp = crp.GetType().GetProperty(
                "Settings", BindingFlags.Instance | BindingFlags.Public);
            if (settingsProp == null)
                return;

            object settings = settingsProp.GetValue(crp);
            if (settings == null)
                return;

            Type settingsType = settings.GetType();
            // 河岸 Depth 拍岸白边需要 _CameraDepthTexture；Copy Color 仍关以省带宽。
            SetField(settingsType, settings, "copyColor", false);
            SetField(settingsType, settings, "copyDepth", true);
            SetField(settingsType, settings, "overridePostFX", true);
            SetField(settingsType, settings, "postFXSettings", null);
            // CameraSettings.RenderScaleMode.Override == 2
            SetField(settingsType, settings, "renderScaleMode", 2);
            // DPI×0.7 + scale 0.75 + bicubic; FPS from cloud/LOD/shadows, not lower scale.
            SetField(settingsType, settings, "renderScale", 0.75f);
            SetField(settingsType, settings, "allowFXAA", false);
            SetField(settingsType, settings, "keepAlpha", false);
            SetField(settingsType, settings, "maskLights", false);
        }

        static void SetField(Type type, object target, string name, object value)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            if (field == null)
                return;

            if (value != null && field.FieldType.IsEnum && value is int intVal)
                field.SetValue(target, Enum.ToObject(field.FieldType, intVal));
            else
                field.SetValue(target, value);
        }
    }
}
