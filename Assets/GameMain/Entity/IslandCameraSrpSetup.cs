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

        /// <summary>闺蜜机 4K 中间 RT 长边。对齐旧工程，不改 Screen / 触摸 / HUD。</summary>
        public const int Gmj4KColorLongEdge = 1920;

        /// <summary>4K 走路相对 1920 缓冲的倍率（长边约 1440）。</summary>
        public const float Gmj4KWalkFactor = 0.75f;

        /// <summary>4K 弹窗 / 原生半层相对 1920 缓冲的倍率（长边约 960）。</summary>
        public const float Gmj4KOverlayFactor = 0.5f;

        /// <summary>闺蜜机标准版站立：画满当前缓冲，不缩中间图。</summary>
        public const float GmjStandardStandScale = 1f;

        /// <summary>闺蜜机标准版走路绝对档。1080p 上面长边约 1440。</summary>
        public const float GmjStandardWalkScale = 0.75f;

        /// <summary>闺蜜机标准版打开界面时的 renderScale。1080p 上面长边约 960。</summary>
        public const float GmjStandardUiScale = 0.5f;

        /// <summary>闺蜜机标准版闲置沉浸的中间 RT 长边。</summary>
        public const int GmjStandardImmersiveLongEdge = 1280;

        /// <summary>
        /// 站立 renderScale。4K 把中间 RT 长边收到 <see cref="Gmj4KColorLongEdge"/>；
        /// 闺蜜机标准版画满当前缓冲；平板 / 高性能安卓 0.75；其余手机 0.5。
        /// 平板判定：<see cref="ScreenLayout.IsPad"/>。
        /// 高性能安卓见 <see cref="IslandGraphicsTier.IsHighEnd"/>（约 8GB+ 且 GPU 够强）。
        /// </summary>
        public static float DefaultRenderScale =>
            BoluoDeviceInfo.IsSkyworthGmj4K()
                ? Gmj4KBufferScale
                : BoluoDeviceInfo.IsSkyworthGmjStandard()
                    ? GmjStandardStandScale
                    : ScreenLayout.IsPad || IsHighPerfAndroidIdleScale()
                        ? 0.75f
                        : 0.5f;

        /// <summary>
        /// 走路略压一档。4K 为 1920 缓冲的 0.75；闺蜜机标准版 0.75；高性能安卓 0.5；其余手机 0.375。
        /// 起停会重建中间 RT。
        /// </summary>
        public static float WalkingRenderScale =>
            BoluoDeviceInfo.IsSkyworthGmj4K()
                ? Gmj4KBufferScale * Gmj4KWalkFactor
                : BoluoDeviceInfo.IsSkyworthGmjStandard()
                    ? GmjStandardWalkScale
                    : IsHighPerfAndroidIdleScale()
                        ? 0.5f
                        : 0.375f;

        /// <summary>
        /// 让中间 RT 长边等于 <paramref name="longEdgeClamp"/>。
        /// 按当前 Screen 像素算，背缓冲已经缩过时不会再对物理面板乘一次。
        /// </summary>
        public static float ScaleToLongEdge(int width, int height, int longEdgeClamp)
        {
            int longEdge = Mathf.Max(width, height);
            if (longEdgeClamp <= 0 || longEdge <= longEdgeClamp)
                return 1f;
            return longEdgeClamp / (float)longEdge;
        }

        /// <summary>高性能安卓站立清晰度：仅真机 Android 高档。</summary>
        static bool IsHighPerfAndroidIdleScale()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return IslandGraphicsTier.IsHighEnd;
#else
            return false;
#endif
        }

        /// <summary>
        /// Unity Panel/Dialog 或原生半层（OverlayPresent）。
        /// 4K 为 1920 缓冲的一半；标准版固定 0.5；其余与沉浸同档。
        /// </summary>
        public static float PopupRenderScale =>
            BoluoDeviceInfo.IsSkyworthGmj4K()
                ? Gmj4KBufferScale * Gmj4KOverlayFactor
                : BoluoDeviceInfo.IsSkyworthGmjStandard()
                    ? GmjStandardUiScale
                    : ImmersiveRenderScale;

        /// <summary>预设面板：手机、闺蜜机标准版、4K 一律 0.375。</summary>
        public const float PresetPanelRenderScale = 0.375f;

        /// <summary>
        /// Idle 沉浸（HUD 隐去）。4K 保持 1920 缓冲；标准版长边收到 1280；手机 0.375。
        /// </summary>
        public static float ImmersiveRenderScale =>
            BoluoDeviceInfo.IsSkyworthGmj4K()
                ? Gmj4KBufferScale
                : BoluoDeviceInfo.IsSkyworthGmjStandard()
                    ? GmjStandardImmersiveScale
                    : 0.375f;

        static bool _logged4KScale;
        static bool _loggedStandardScale;

        /// <summary>4K 站立 / 沉浸：当前 Screen 长边收到 1920 的系数。</summary>
        static float Gmj4KBufferScale
        {
            get
            {
                float scale = ScaleToLongEdge(Screen.width, Screen.height, Gmj4KColorLongEdge);
                if (!_logged4KScale && Screen.width > 8 && Screen.height > 8)
                {
                    _logged4KScale = true;
                    Log.Info(
                        "[IslandCamera] gmj4k bufferScale=" + scale.ToString("F3")
                        + " longEdge=" + Gmj4KColorLongEdge
                        + " screen=" + Screen.width + "x" + Screen.height);
                }
                return scale;
            }
        }

        /// <summary>标准版闲置沉浸：当前 Screen 长边收到 1280 的系数。</summary>
        static float GmjStandardImmersiveScale
        {
            get
            {
                float scale = ScaleToLongEdge(
                    Screen.width, Screen.height, GmjStandardImmersiveLongEdge);
                if (!_loggedStandardScale && Screen.width > 8 && Screen.height > 8)
                {
                    _loggedStandardScale = true;
                    Log.Info(
                        "[IslandCamera] gmj standard stand=" + GmjStandardStandScale.ToString("F3")
                        + " walk=" + GmjStandardWalkScale.ToString("F3")
                        + " ui=" + GmjStandardUiScale.ToString("F3")
                        + " immersive=" + scale.ToString("F3")
                        + " longEdge=" + GmjStandardImmersiveLongEdge
                        + " screen=" + Screen.width + "x" + Screen.height);
                }
                return scale;
            }
        }

        /// <summary>
        /// 默认目标帧率（判定稳定后缓存）。
        /// 闺蜜机标准版 / 4K 版均为 60（<see cref="BoluoDeviceInfo.IsSkyworthGmj4K"/>）。
        /// 其它手机：低/中/高目标均为 60（画质档只压特效与分辨率）。iOS 亦按 60。
        /// Android 按 <see cref="IslandGraphicsTier"/> 分画质；帧率不再按档降。
        /// 高刷屏默认仍不超过 60。热状态严重 30 / 危急 15，不把系统降频写回去。
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

        const int GmjStandardFps = 60;
        const int GmjUltraFps = 60;
        const int LowFps = 60;
        const int MidFps = 60;
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
        /// 再取热状态上限。原生半层 YieldsGpu 时不抢（overlay 最低 20，interval 1）。
        /// </summary>
        public static void ApplyTargetFrameRate()
        {
            if (NativeOverlayGate.YieldsGpu)
                return;

            int requested = IslandIdleImmersive.IsActive ? ImmersiveFrameRate : DefaultFrameRate;
            int fps = ApplyThermalCap(requested);
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
            // 海面 / 河岸拍岸沫读 _CameraDepthTexture。平面倒影走反射 RT，不拷颜色。
            // 低档关后处理时深度拷贝仍开，否则岸沫没有场景深度。
            SetField(settingsType, settings, "copyColor", false);
            SetField(settingsType, settings, "copyDepth", true);

            // 无后处理且无湿油时整机关 PostFX；否则走管线（Bloom/调色全档开）。
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
            // 闺蜜机开 FXAA；其余机油画开时关（湿油硬边会被 AA 洗软）。
            SetField(
                settingsType, settings, "allowFXAA",
                BoluoDeviceInfo.IsSkyworthGmj());
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
