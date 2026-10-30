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
        /// 站立 renderScale。手机 0.5；闺蜜机标准版 0.75（清晰度优先，4K 跟手机）。
        /// </summary>
        public static float DefaultRenderScale =>
            BoluoDeviceInfo.IsSkyworthGmjStandard() ? 0.75f : 0.5f;

        /// <summary>
        /// 走路略压一档。手机 / 闺蜜机标准版均为 0.375（树密朝向走转要过 20）。
        /// 起停会重建中间 RT。闺蜜机站立仍 0.75。
        /// </summary>
        public static float WalkingRenderScale => 0.375f;

        /// <summary>Unity Panel/Dialog 或原生半层（OverlayPresent）打开时，与沉浸同档。</summary>
        public static float PopupRenderScale => ImmersiveRenderScale;

        /// <summary>预设面板：手机、闺蜜机标准版、4K 一律 0.375。</summary>
        public const float PresetPanelRenderScale = 0.375f;

        /// <summary>Idle 沉浸（HUD 隐去）。手机与闺蜜机一律 0.375。</summary>
        public const float ImmersiveRenderScale = 0.375f;

        /// <summary>
        /// 默认目标帧率（判定稳定后缓存）。
        /// 闺蜜机标准版 45、4K 版 60（<see cref="BoluoDeviceInfo.IsSkyworthGmj4K"/>）。
        /// 其它手机：低 30 / 中 45 / 高 60。iOS 按机型代际（A12 及更早 30，A13/A14 45，A15 起 60）；
        /// Android 按 <see cref="IslandGraphicsTier"/>（弱 GPU 或约 4GB 为低，约 8GB 且 GPU 不弱为高）。
        /// 与原神、鸣潮的 30·45·60 默认同档；高刷屏默认仍是 60。
        /// </summary>
        public static int DefaultFrameRate
        {
            get
            {
                if (_cachedFrameRate > 0)
                    return _cachedFrameRate;

                int fps = ComputeDefaultFrameRate(out bool cache, out string reason);
                if (!cache)
                    return fps;

                _cachedFrameRate = fps;
                Log.Info(
                    "[IslandCamera] defaultFPS=" + fps
                    + " reason=" + reason
                    + " model=" + SystemInfo.deviceModel
                    + " gpu=" + SystemInfo.graphicsDeviceName
                    + " mem=" + SystemInfo.systemMemorySize);
                return fps;
            }
        }

        /// <summary>闲置沉浸降载：够看就行，省电。</summary>
        public const int ImmersiveFrameRate = 25;

        const int GmjStandardFps = 45;
        const int GmjUltraFps = 60;
        const int LowFps = 30;
        const int MidFps = 45;
        const int HighFps = 60;

        static int _cachedFrameRate;

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
        /// 按岛面状态设目标帧率：默认见 <see cref="DefaultFrameRate"/>，沉浸 25。
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
            // 闺蜜机不用后处理：关深度拷贝，省一次全屏 blit。
            bool gmj = BoluoDeviceInfo.IsSkyworthGmj();
            SetField(settingsType, settings, "copyColor", false);
            SetField(settingsType, settings, "copyDepth", !gmj);

            // 手机：保留场景已挂的 PostFXSettings。闺蜜机：强制关掉后处理（含 OilFilter）。
            if (gmj)
            {
                SetField(settingsType, settings, "postFXSettings", null);
                SetField(settingsType, settings, "overridePostFX", true);
            }
            else
            {
                object existingPostFx = GetField(settingsType, settings, "postFXSettings");
                SetField(settingsType, settings, "overridePostFX", existingPostFx != null);
            }

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

        static int ComputeDefaultFrameRate(out bool cache, out string reason)
        {
            cache = true;
            if (BoluoDeviceInfo.IsSkyworthGmj())
            {
                bool ultra = BoluoDeviceInfo.IsSkyworthGmj4K();
                cache = BoluoDeviceInfo.IsSkyworthGmj4KSettled;
                reason = ultra ? "gmj4k" : "gmj";
                return ultra ? GmjUltraFps : GmjStandardFps;
            }

#if UNITY_EDITOR
            reason = "editor";
            return HighFps;
#else
            if (!Application.isMobilePlatform)
            {
                reason = "desktop";
                return HighFps;
            }

            return MobileFrameRate(out reason);
#endif
        }

        static int MobileFrameRate(out string reason)
        {
#if UNITY_IOS
            int ios = IosFrameRate(SystemInfo.deviceModel);
            if (ios > 0)
            {
                reason = "ios";
                return ios;
            }
#endif
            switch (IslandGraphicsTier.GpuTier)
            {
                case IslandGraphicsTier.Tier.Low:
                    reason = "low";
                    return LowFps;
                case IslandGraphicsTier.Tier.Mid:
                    reason = "mid";
                    return MidFps;
                default:
                    reason = "high";
                    return HighFps;
            }
        }

        /// <summary>
        /// iPhone / iPad 的 <c>SystemInfo.deviceModel</c> 主版本（iPhone14,5 → 14）。
        /// 认不出返回 0，交给画质档。
        /// </summary>
        static int IosFrameRate(string model)
        {
            if (TryAppleMajor(model, "iPhone", out int iphone))
            {
                if (iphone >= 14)
                    return HighFps;
                if (iphone >= 12)
                    return MidFps;
                return LowFps;
            }

            if (TryAppleMajor(model, "iPad", out int ipad))
            {
                if (ipad >= 13)
                    return HighFps;
                if (ipad >= 12)
                    return MidFps;
                return LowFps;
            }

            if (!string.IsNullOrEmpty(model)
                && model.IndexOf("iPod", StringComparison.Ordinal) >= 0)
                return LowFps;

            return 0;
        }

        static bool TryAppleMajor(string model, string family, out int major)
        {
            major = 0;
            if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(family))
                return false;

            int index = model.IndexOf(family, StringComparison.Ordinal);
            if (index < 0)
                return false;

            int i = index + family.Length;
            int value = 0;
            bool any = false;
            while (i < model.Length && char.IsDigit(model[i]))
            {
                any = true;
                value = value * 10 + (model[i] - '0');
                i++;
            }

            if (!any)
                return false;

            major = value;
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
