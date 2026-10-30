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
        /// 站立 renderScale。手机 0.5；闺蜜机 0.75（大屏站立糊感太重）。
        /// </summary>
        public static float DefaultRenderScale =>
            BoluoDeviceInfo.IsSkyworthGmj() ? 0.75f : 0.5f;

        /// <summary>
        /// 走路略压一档。手机 0.375；闺蜜机 0.5（站立 0.75 的下一档）。
        /// 起停会重建中间 RT。
        /// </summary>
        public static float WalkingRenderScale =>
            BoluoDeviceInfo.IsSkyworthGmj() ? 0.5f : 0.375f;

        /// <summary>Unity Panel/Dialog 或原生半层（OverlayPresent）打开时，与沉浸同档。</summary>
        public static float PopupRenderScale => ImmersiveRenderScale;

        /// <summary>预设面板：手机、闺蜜机标准版、4K 一律 0.375。</summary>
        public const float PresetPanelRenderScale = 0.375f;

        /// <summary>Idle 沉浸（HUD 隐去）。手机 0.375；闺蜜机 0.5。</summary>
        public static float ImmersiveRenderScale =>
            BoluoDeviceInfo.IsSkyworthGmj() ? 0.5f : 0.375f;

        /// <summary>
        /// 默认目标帧率（判定稳定后缓存）。
        /// 闺蜜机标准版 45、4K 版 60（<see cref="BoluoDeviceInfo.IsSkyworthGmj4K"/>）。
        /// 其它手机：低 30 / 中 45 / 高 60。iOS 按机型代际（A12 及更早 30，A13/A14 45，A15 起 60）。
        /// iPad Pro 2018/2020（A12X/A12Z，iPad8）按 60；iPad13 里的 A14（Air 4、第十代）按 45，M1 按 60。
        /// Android 按 <see cref="IslandGraphicsTier"/>（弱 GPU 或约 4GB 为低，约 8GB 且 GPU 不弱为高）。
        /// 45 在不能整除的刷新率上收到 30。高刷屏默认仍不超过 60。热状态严重 30 / 危急 15，不把系统降频写回去。
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
        static int _loggedPacedFps = int.MinValue;

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
        /// 再取热状态上限，并把不能整除刷新率的 45 收到 30。
        /// 原生半层 YieldsGpu 时不抢（overlay 自管 15/1）。
        /// </summary>
        public static void ApplyTargetFrameRate()
        {
            if (NativeOverlayGate.YieldsGpu)
                return;

            int requested = IslandIdleImmersive.IsActive ? ImmersiveFrameRate : DefaultFrameRate;
            int fps = PaceToRefresh(ApplyThermalCap(requested));
            if (fps == _appliedFrameRate
                && Application.targetFrameRate == fps)
                return;

            if (fps != requested && fps != _loggedPacedFps)
            {
                _loggedPacedFps = fps;
                Log.Info(
                    "[IslandCamera] fps " + requested + " → " + fps
                    + " refresh=" + CurrentRefreshHz());
            }

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
            // 海面 / 河岸拍岸沫读 _CameraDepthTexture。平面倒影走反射 RT，不拷颜色。
            // 低档关后处理时深度拷贝仍开，否则岸沫没有场景深度。
            SetField(settingsType, settings, "copyColor", false);
            SetField(settingsType, settings, "copyDepth", true);

            // 低档无湿油时整机关后处理；有湿油则走管线 PostFX（Bloom/调色仍由档位门控）。
            if (!IslandGraphicsTier.AllowsPostFx && !IslandGraphicsTier.AllowsOilFilter)
            {
                SetField(settingsType, settings, "postFXSettings", null);
                SetField(settingsType, settings, "overridePostFX", true);
            }
            else if (!IslandGraphicsTier.AllowsPostFx)
            {
                SetField(settingsType, settings, "overridePostFX", false);
            }
            else
            {
                object existingPostFx = GetField(settingsType, settings, "postFXSettings");
                SetField(settingsType, settings, "overridePostFX", existingPostFx != null);
            }

            // CameraSettings.RenderScaleMode.Override == 2
            SetField(settingsType, settings, "renderScaleMode", 2);
            SetField(settingsType, settings, "renderScale", DefaultRenderScale);
            // 全机型关 FXAA。
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
                return IpadFrameRate(model, ipad);

            if (!string.IsNullOrEmpty(model)
                && model.IndexOf("iPod", StringComparison.Ordinal) >= 0)
                return LowFps;

            return 0;
        }

        /// <summary>
        /// iPad 主版本不能当芯片代。iPad8 是 A12X/A12Z Pro；iPad13 里 Air 4 与第十代是 A14，其余是 M1。
        /// </summary>
        static int IpadFrameRate(string model, int major)
        {
            if (major == 8)
                return HighFps;

            bool hasMinor = TryAppleMinor(model, "iPad", out int minor);
            if (major == 13)
            {
                if (hasMinor && ((minor >= 1 && minor <= 2) || (minor >= 18 && minor <= 19)))
                    return MidFps;
                if (!hasMinor)
                    return MidFps;
                return HighFps;
            }

            if (major >= 14)
                return HighFps;
            if (major >= 12)
                return MidFps;
            return LowFps;
        }

        /// <summary>
        /// Player 的 adjustIOSFPSUsingThermalState 会把目标帧降到 30 / 15。
        /// 岛内每帧写回目标帧时取同一上限，避免下一帧抬回去。
        /// </summary>
        static int ApplyThermalCap(int fps)
        {
#if UNITY_IOS && !UNITY_EDITOR
            switch (UnityEngine.iOS.Device.thermalState)
            {
                case UnityEngine.iOS.ThermalState.Serious:
                    return Mathf.Min(fps, 30);
                case UnityEngine.iOS.ThermalState.Critical:
                    return Mathf.Min(fps, 15);
            }
#endif
            return fps;
        }

        /// <summary>45 不能整除 60Hz / 120Hz，呈现会一长一短。能被 30 整除时收到 30。90Hz 上 45 保持。</summary>
        static int PaceToRefresh(int fps)
        {
            if (fps != MidFps)
                return fps;

            int refresh = CurrentRefreshHz();
            if (refresh < 48 || refresh % MidFps == 0)
                return fps;
            if (refresh % LowFps == 0)
                return LowFps;
            return fps;
        }

        static int CurrentRefreshHz()
        {
            double hz = Screen.currentResolution.refreshRateRatio.value;
            if (hz < 30.0 || hz > 240.0)
                return 0;
            return (int)Math.Round(hz);
        }

        static bool TryAppleMinor(string model, string family, out int minor)
        {
            minor = 0;
            if (!TryAppleMajor(model, family, out _))
                return false;

            int index = model.IndexOf(family, StringComparison.Ordinal);
            int i = index + family.Length;
            while (i < model.Length && char.IsDigit(model[i]))
                i++;
            if (i >= model.Length || model[i] != ',')
                return false;
            i++;

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
            minor = value;
            return true;
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
