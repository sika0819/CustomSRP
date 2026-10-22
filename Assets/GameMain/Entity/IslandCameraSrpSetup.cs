using System;
using System.Reflection;
using Game.BoluoApi;
using Game.Native;
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
        /// 站立 renderScale。手机 0.5；闺蜜机大屏默认 1（锐度优先）。
        /// </summary>
        public static float DefaultRenderScale => BoluoDeviceInfo.IsSkyworthGmj() ? 1f : 0.5f;

        /// <summary>
        /// 走路略压一档省带宽。手机 0.375；闺蜜机 0.5。起停会重建中间 RT，可接受。
        /// </summary>
        public static float WalkingRenderScale => BoluoDeviceInfo.IsSkyworthGmj() ? 0.5f : 0.375f;

        /// <summary>Unity Panel/Dialog 或原生半层（OverlayPresent）打开时，与沉浸同档。</summary>
        public static float PopupRenderScale => ImmersiveRenderScale;

        /// <summary>Idle 沉浸（HUD 隐去）：手机 0.25；闺蜜机 0.5。</summary>
        public static float ImmersiveRenderScale => BoluoDeviceInfo.IsSkyworthGmj() ? 0.5f : 0.25f;

        /// <summary>默认目标帧率（与 GameEntry / Base.FrameRate 一致）。</summary>
        public const int DefaultFrameRate = 60;

        /// <summary>闲置沉浸降载：够看就行，省电。</summary>
        public const int ImmersiveFrameRate = 25;

        static Type _crpType;
        static PropertyInfo _settingsProp;
        static FieldInfo _renderScaleField;
        static bool _reflectionReady;
        // B7: cache CRP + Settings for the active island camera (avoid per-frame GetComponent).
        static EntityId _cachedEntityId;
        static Component _cachedCrp;
        static object _cachedSettings;
        static int _appliedFrameRate = int.MinValue;

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
            // Island horizon. Fog still softens mid-distance.
            camera.farClipPlane = 750f;

            EnsureCustomRenderPipelineCamera(camera.gameObject);
        }

        public static Component EnsureCustomRenderPipelineCamera(GameObject go)
        {
            if (go == null)
                return null;

            Type crpType = ResolveCrpType();
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

        /// <summary>
        /// Override camera render scale（idle / walking）。No-op if reflection fails.
        /// </summary>
        public static void SetRenderScale(GameObject go, float renderScale)
        {
            if (go == null || !EnsureReflection())
                return;

            EntityId eid = go.GetEntityId();
            if (_cachedCrp == null || _cachedEntityId != eid || _cachedSettings == null)
            {
                Component crp = go.GetComponent(_crpType);
                if (crp == null)
                    return;
                object settings = _settingsProp.GetValue(crp);
                if (settings == null)
                    return;
                _cachedEntityId = eid;
                _cachedCrp = crp;
                _cachedSettings = settings;
            }

            // CameraSettings is a class; mutate in place (Settings has no setter).
            _renderScaleField.SetValue(_cachedSettings, renderScale);
        }

        /// <summary>
        /// 按岛面状态设目标帧率：默认 60 / 沉浸 25。
        /// 原生半层 YieldsGpu 时不抢（overlay 自管 15/1）。
        /// </summary>
        public static void ApplyTargetFrameRate()
        {
            if (NativeOverlayGate.YieldsGpu)
                return;

            int fps = IslandIdleImmersive.IsActive ? ImmersiveFrameRate : DefaultFrameRate;
            if (fps == _appliedFrameRate
                && Application.targetFrameRate == fps)
                return;

            _appliedFrameRate = fps;
            if (GameEntry.Base != null)
                GameEntry.Base.FrameRate = fps;
            else
                Application.targetFrameRate = fps;
        }

        /// <summary>overlay 让帧结束后强制按当前岛面状态重算（清缓存）。</summary>
        public static void ReapplyTargetFrameRateAfterOverlay()
        {
            _appliedFrameRate = int.MinValue;
            ApplyTargetFrameRate();
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
            // 河岸 Depth 拍岸白边需要 _CameraDepthTexture。倒影用烘焙立方体贴图，不拷颜色。
            SetField(settingsType, settings, "copyColor", false);
            SetField(settingsType, settings, "copyDepth", true);

            // 保留场景已挂的 PostFXSettings（进岛时绝不能清成 null）。
            // 有资产 → override 开；没有 → 用管线默认。
            object existingPostFx = GetField(settingsType, settings, "postFXSettings");
            SetField(settingsType, settings, "overridePostFX", existingPostFx != null);

            // CameraSettings.RenderScaleMode.Override == 2
            SetField(settingsType, settings, "renderScaleMode", 2);
            SetField(settingsType, settings, "renderScale", DefaultRenderScale);
            SetField(settingsType, settings, "allowFXAA", false);
            SetField(settingsType, settings, "keepAlpha", false);
            SetField(settingsType, settings, "maskLights", false);
        }

        static object GetField(Type type, object target, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            return field != null ? field.GetValue(target) : null;
        }

        static Type ResolveCrpType()
        {
            if (_crpType != null)
                return _crpType;
            _crpType = Type.GetType(CrpTypeName);
            return _crpType;
        }

        static bool EnsureReflection()
        {
            if (_reflectionReady)
                return true;

            Type crpType = ResolveCrpType();
            if (crpType == null)
                return false;

            _settingsProp = crpType.GetProperty(
                "Settings", BindingFlags.Instance | BindingFlags.Public);
            if (_settingsProp == null)
                return false;

            _renderScaleField = _settingsProp.PropertyType.GetField(
                "renderScale", BindingFlags.Instance | BindingFlags.Public);
            if (_renderScaleField == null)
                return false;

            _reflectionReady = true;
            return true;
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
