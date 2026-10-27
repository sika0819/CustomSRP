using Game.BoluoApi;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 四档画质：Low / Mid / High / Gmj。档名 Android/iOS 统一；Detect 分平台评分；Profile 共用。
    /// </summary>
    public enum DeviceGraphicsClass
    {
        Low = 0,
        Mid = 1,
        High = 2,
        Gmj = 3,
    }

    /// <summary>单档策略数值（静态表，不引入 SO）。</summary>
    public readonly struct DeviceGraphicsProfile
    {
        public readonly float DpiScale;
        public readonly float LodBias;
        public readonly bool OilMobilePerf;
        public readonly float StandRenderScale;
        public readonly float WalkRenderScale;
        public readonly float ImmersiveRenderScale;
        public readonly float ShadowDistance;
        public readonly bool DisableCloudHighRing;
        public readonly bool ApplyMobileQualityBoot;
        public readonly bool ApplyStreamingTune;
        public readonly bool ApplyMobileIslandTune;

        public DeviceGraphicsProfile(
            float dpiScale,
            float lodBias,
            bool oilMobilePerf,
            float standRenderScale,
            float walkRenderScale,
            float immersiveRenderScale,
            float shadowDistance,
            bool disableCloudHighRing,
            bool applyMobileQualityBoot,
            bool applyStreamingTune,
            bool applyMobileIslandTune)
        {
            DpiScale = dpiScale;
            LodBias = lodBias;
            OilMobilePerf = oilMobilePerf;
            StandRenderScale = standRenderScale;
            WalkRenderScale = walkRenderScale;
            ImmersiveRenderScale = immersiveRenderScale;
            ShadowDistance = shadowDistance;
            DisableCloudHighRing = disableCloudHighRing;
            ApplyMobileQualityBoot = applyMobileQualityBoot;
            ApplyStreamingTune = applyStreamingTune;
            ApplyMobileIslandTune = applyMobileIslandTune;
        }
    }

    /// <summary>
    /// 启动分档 + 套用 Quality / keyword / 相机 scale 表。流式与岛面调参见
    /// <see cref="EnvStreamingConfig.ApplyGraphicsTierDefaults"/> /
    /// <see cref="IslandMobileTune"/>。
    /// </summary>
    public static class DeviceGraphicsPolicy
    {
        public const string EditorTierOverrideKey = "Boluo.EditorTest.GraphicsTier";

        static readonly int OilBrushMobileScaleId = Shader.PropertyToID("_OilBrushMobileScale");

        static bool _detected;
        static DeviceGraphicsClass _current = DeviceGraphicsClass.High;
        static bool _isDesktopHigh;

        public static DeviceGraphicsClass Current
        {
            get
            {
                EnsureDetected();
                return _current;
            }
        }

        /// <summary>Editor / Standalone 的 High（不做移动 boot / 流式收紧）。</summary>
        public static bool IsDesktopHigh
        {
            get
            {
                EnsureDetected();
                return _isDesktopHigh;
            }
        }

        public static DeviceGraphicsProfile Profile
        {
            get
            {
                EnsureDetected();
                return GetProfile(_current, _isDesktopHigh);
            }
        }

        public static void DetectAndApplyBoot()
        {
            Detect();
            ApplyBoot(Profile);
        }

        public static void EnsureDetected()
        {
            if (!_detected)
                Detect();
        }

        public static void Detect()
        {
            _detected = true;
            _isDesktopHigh = false;

#if UNITY_EDITOR
            if (TryParseEditorOverride(out DeviceGraphicsClass forced))
            {
                _current = forced;
                _isDesktopHigh = forced == DeviceGraphicsClass.High
                    && !Application.isMobilePlatform;
                Log.Info("[DeviceGraphicsPolicy] Editor override=" + _current
                    + " desktopHigh=" + _isDesktopHigh);
                return;
            }
#endif

            if (BoluoDeviceInfo.IsSkyworthGmj())
            {
                _current = DeviceGraphicsClass.Gmj;
                Log.Info("[DeviceGraphicsPolicy] Detect=Gmj channel/originEnd");
                return;
            }

#if UNITY_EDITOR
            _current = DeviceGraphicsClass.High;
            _isDesktopHigh = true;
            Log.Info("[DeviceGraphicsPolicy] Detect=High (Editor desktop)");
            return;
#else
            if (!Application.isMobilePlatform)
            {
                _current = DeviceGraphicsClass.High;
                _isDesktopHigh = true;
                Log.Info("[DeviceGraphicsPolicy] Detect=High (Standalone)");
                return;
            }

            int ramMb = SystemInfo.systemMemorySize;
            string gpu = SystemInfo.graphicsDeviceName ?? string.Empty;
            int gfxMb = SystemInfo.graphicsMemorySize;
            int pixels = Mathf.Max(0, Screen.width) * Mathf.Max(0, Screen.height);

            if (Application.platform == RuntimePlatform.IPhonePlayer)
                _current = ScoreIos(ramMb, gpu, SystemInfo.deviceModel ?? string.Empty);
            else
                _current = ScoreAndroid(ramMb, gpu, gfxMb, pixels);

            Log.Info(
                "[DeviceGraphicsPolicy] Detect=" + _current
                + " platform=" + Application.platform
                + " ramMb=" + ramMb
                + " gpu=" + gpu
                + " gfxMb=" + gfxMb
                + " pixels=" + pixels);
#endif
        }

        public static DeviceGraphicsProfile GetProfile(DeviceGraphicsClass cls, bool desktopHigh)
        {
            // Stand / Walk / Immersive: 全档 0.5 / 0.375 / 0.375。
            if (desktopHigh)
            {
                // Desktop High: inherit CRP; no mobile boot.
                return new DeviceGraphicsProfile(
                    dpiScale: 1f,
                    lodBias: 1f,
                    oilMobilePerf: false,
                    standRenderScale: 0.5f,
                    walkRenderScale: 0.375f,
                    immersiveRenderScale: 0.375f,
                    shadowDistance: 40f,
                    disableCloudHighRing: false,
                    applyMobileQualityBoot: false,
                    applyStreamingTune: false,
                    applyMobileIslandTune: false);
            }

            switch (cls)
            {
                case DeviceGraphicsClass.Low:
                    return new DeviceGraphicsProfile(
                        0.7f, 1f, true,
                        0.5f, 0.375f, 0.375f,
                        26f, true, true, true, true);
                case DeviceGraphicsClass.Mid:
                    return new DeviceGraphicsProfile(
                        0.7f, 1f, true,
                        0.5f, 0.375f, 0.375f,
                        32f, true, true, true, true);
                case DeviceGraphicsClass.High:
                    // Phone High (flagship). Same DPI and OIL_MOBILE_PERF as 416c485;
                    // renderScale stays 0.5 / 0.375 / 0.375.
                    return new DeviceGraphicsProfile(
                        0.7f, 1f, true,
                        0.5f, 0.375f, 0.375f,
                        40f, true, true, true, true);
                case DeviceGraphicsClass.Gmj:
                    return new DeviceGraphicsProfile(
                        1f, 1f, true,
                        0.5f, 0.375f, 0.375f,
                        40f, true, true, true, true);
                default:
                    return GetProfile(DeviceGraphicsClass.Low, false);
            }
        }

        static void ApplyBoot(DeviceGraphicsProfile profile)
        {
            if (!profile.ApplyMobileQualityBoot)
            {
                Shader.DisableKeyword("OIL_MOBILE_PERF");
                Shader.SetGlobalFloat(OilBrushMobileScaleId, 1f);
                Log.Info("[DeviceGraphicsPolicy] Boot skip mobile quality (desktop High)"
                    + " class=" + _current);
                return;
            }

            int veryLow = 0;
            string[] names = QualitySettings.names;
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == "Very Low")
                {
                    veryLow = i;
                    break;
                }
            }

            QualitySettings.SetQualityLevel(veryLow, true);
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
            QualitySettings.resolutionScalingFixedDPIFactor = profile.DpiScale;
            QualitySettings.lodBias = profile.LodBias;
            QualitySettings.maximumLODLevel = 0;

            if (profile.OilMobilePerf)
                Shader.EnableKeyword("OIL_MOBILE_PERF");
            else
                Shader.DisableKeyword("OIL_MOBILE_PERF");

            // Soft brush restore under OIL_MOBILE_PERF (no extra keyword / variants).
            // Mid/Gmj: half brush+weave; Low and phone High: hard cut (0).
            float brushScale = 0f;
            if (profile.OilMobilePerf
                && (_current == DeviceGraphicsClass.Mid
                    || _current == DeviceGraphicsClass.Gmj))
                brushScale = 0.5f;
            Shader.SetGlobalFloat(OilBrushMobileScaleId, brushScale);

            Log.Info(
                "[DeviceGraphicsPolicy] Boot class=" + _current
                + " Quality=" + QualitySettings.names[QualitySettings.GetQualityLevel()]
                + " dpiScale=" + profile.DpiScale
                + " lodBias=" + profile.LodBias
                + " standScale=" + profile.StandRenderScale
                + " OIL_MOBILE_PERF=" + profile.OilMobilePerf
                + " brushScale=" + brushScale
                + " res=" + Screen.width + "x" + Screen.height);
        }

        /// <summary>
        /// Android：偏保守。强 Low 封顶 Mid；High 需 RAM≥8192 且 GPU High。
        /// </summary>
        public static DeviceGraphicsClass ScoreAndroid(int ramMb, string gpuName, int gfxMb, int pixels)
        {
            GpuBand gpu = ClassifyAndroidGpu(gpuName);
            RamBand ram = ClassifyAndroidRam(ramMb);

            bool strongLow = ram == RamBand.StrongLow || gpu == GpuBand.Low;
            bool highFill = pixels >= 1440 * 3200;
            bool lowVram = gfxMb > 0 && gfxMb <= 512;

            DeviceGraphicsClass tier;
            if (ram == RamBand.HighEligible && gpu == GpuBand.High)
                tier = DeviceGraphicsClass.High;
            else if (ram >= RamBand.Mid || gpu >= GpuBand.Mid)
                tier = DeviceGraphicsClass.Mid;
            else
                tier = DeviceGraphicsClass.Low;

            if (strongLow && tier == DeviceGraphicsClass.High)
                tier = DeviceGraphicsClass.Mid;
            if (strongLow && ram == RamBand.StrongLow)
                tier = DeviceGraphicsClass.Low;

            if (lowVram || highFill)
            {
                if (tier == DeviceGraphicsClass.High)
                    tier = DeviceGraphicsClass.Mid;
                else if (tier == DeviceGraphicsClass.Mid && (ram == RamBand.Low || gpu == GpuBand.Low))
                    tier = DeviceGraphicsClass.Low;
            }

            return tier;
        }

        /// <summary>
        /// iOS：A11− → Low；A12–A14 → Mid；A15+ / M 系列 → High。
        /// </summary>
        public static DeviceGraphicsClass ScoreIos(int ramMb, string gpuName, string deviceModel)
        {
            int appleGen = ResolveAppleChipGeneration(gpuName, deviceModel);
            if (appleGen > 0)
            {
                if (appleGen <= 11)
                    return DeviceGraphicsClass.Low;
                if (appleGen <= 14)
                    return ramMb > 0 && ramMb < 2048
                        ? DeviceGraphicsClass.Low
                        : DeviceGraphicsClass.Mid;
                return DeviceGraphicsClass.High;
            }

            // Parse failed: do not grant High.
            if (ramMb >= 4096)
                return DeviceGraphicsClass.Mid;
            return DeviceGraphicsClass.Low;
        }

        enum RamBand
        {
            StrongLow = 0,
            Low = 1,
            Mid = 2,
            MidPlus = 3,
            HighEligible = 4,
        }

        enum GpuBand
        {
            Low = 0,
            Mid = 1,
            High = 2,
        }

        static RamBand ClassifyAndroidRam(int ramMb)
        {
            if (ramMb <= 0)
                return RamBand.Low;
            if (ramMb <= 3072)
                return RamBand.StrongLow;
            if (ramMb <= 4096)
                return RamBand.Low;
            if (ramMb < 6144)
                return RamBand.Mid;
            if (ramMb < 8192)
                return RamBand.MidPlus;
            return RamBand.HighEligible;
        }

        static GpuBand ClassifyAndroidGpu(string gpuName)
        {
            string g = (gpuName ?? string.Empty).ToLowerInvariant();
            if (string.IsNullOrEmpty(g))
                return GpuBand.Low;

            // Adreno NNN (names often "adreno (tm) 650")
            int adreno = ExtractFirstNumberAfter(g, "adreno");
            if (adreno > 0)
            {
                if (adreno >= 700)
                    return GpuBand.High;
                if (adreno >= 600)
                    return GpuBand.Mid;
                return GpuBand.Low;
            }

            // Mali-Gxxx / Mali-Txxx
            if (g.Contains("mali"))
            {
                if (g.Contains("mali-t") || g.Contains("mali t"))
                    return GpuBand.Low;

                int maliG = ExtractFirstNumberAfter(g, "mali-g");
                if (maliG <= 0)
                    maliG = ExtractFirstNumberAfter(g, "mali g");
                if (maliG <= 0)
                    maliG = ExtractFirstNumberAfter(g, "mali");
                if (maliG > 0)
                {
                    // G710/G720/G78 flagship-ish; G57/G52 mid-low; G51/G31 low
                    if (maliG >= 710 || maliG == 78 || maliG == 77)
                        return GpuBand.High;
                    if (maliG >= 57 || maliG == 76 || maliG == 72 || maliG == 71)
                        return GpuBand.Mid;
                    return GpuBand.Low;
                }

                return GpuBand.Low;
            }

            if (g.Contains("powervr") || g.Contains("rogue"))
                return GpuBand.Low;

            // Unknown Android GPU: mid-lean if name looks modern, else low.
            if (g.Contains("xclipse") || g.Contains("immortalis"))
                return GpuBand.High;

            return GpuBand.Low;
        }

        /// <summary>
        /// 返回 Apple 芯片代数（A11→11，M1→101 当作 High）。解析失败返回 0。
        /// </summary>
        public static int ResolveAppleChipGeneration(string gpuName, string deviceModel)
        {
            string g = (gpuName ?? string.Empty).ToLowerInvariant();
            string m = (deviceModel ?? string.Empty);

            // "Apple A15 GPU" / "Apple A17 Pro GPU"
            int aFromGpu = ExtractFirstNumberAfter(g, "apple a");
            if (aFromGpu > 0 && aFromGpu < 30)
                return aFromGpu;

            if (g.Contains("apple m") || g.StartsWith("m1") || g.Contains(" m1")
                || g.Contains(" m2") || g.Contains(" m3") || g.Contains(" m4"))
                return 15; // treat as High band

            // deviceModel: iPhone10,3 → A11; iPhone11,x → A12; …
            if (m.StartsWith("iPhone", System.StringComparison.OrdinalIgnoreCase))
            {
                int major = ExtractModelMajor(m, "iPhone");
                // https://gist: iPhone10 = A11, 11=A12, 12=A13, 13=A14, 14=A15, 15=A16, 16=A17/A18…
                if (major <= 0)
                    return 0;
                if (major <= 10)
                    return 11;
                if (major == 11)
                    return 12;
                if (major == 12)
                    return 13;
                if (major == 13)
                    return 14;
                if (major == 14)
                    return 15;
                if (major == 15)
                    return 16;
                if (major >= 16)
                    return 17;
            }

            if (m.StartsWith("iPad", System.StringComparison.OrdinalIgnoreCase))
            {
                // Coarse: modern iPads with Apple GPU string often already parsed;
                // unknown iPad → Mid-lean via caller (gen 12).
                int major = ExtractModelMajor(m, "iPad");
                if (major <= 0)
                    return 12;
                if (major <= 6)
                    return 10; // older → Low-ish
                if (major <= 8)
                    return 12;
                return 15;
            }

            // "Apple GPU" without generation — assume Mid (A12-class) not High.
            if (g.Contains("apple gpu") || g.Contains("applegpu"))
                return 12;

            return 0;
        }

        static int ExtractModelMajor(string model, string prefix)
        {
            if (string.IsNullOrEmpty(model) || model.Length <= prefix.Length)
                return 0;
            string rest = model.Substring(prefix.Length);
            int comma = rest.IndexOf(',');
            if (comma > 0)
                rest = rest.Substring(0, comma);
            return int.TryParse(rest, out int n) ? n : 0;
        }

        static int ExtractFirstNumberAfter(string source, string key)
        {
            int idx = source.IndexOf(key, System.StringComparison.Ordinal);
            if (idx < 0)
                return 0;
            for (int i = idx + key.Length; i < source.Length; i++)
            {
                if (!char.IsDigit(source[i]))
                    continue;
                int start = i;
                while (i < source.Length && char.IsDigit(source[i]))
                    i++;
                return int.TryParse(source.Substring(start, i - start), out int n) ? n : 0;
            }

            return 0;
        }

#if UNITY_EDITOR
        static bool TryParseEditorOverride(out DeviceGraphicsClass cls)
        {
            cls = DeviceGraphicsClass.High;
            string raw = UnityEditor.EditorPrefs.GetString(EditorTierOverrideKey, string.Empty);
            if (string.IsNullOrEmpty(raw))
                return false;
            return System.Enum.TryParse(raw, true, out cls);
        }
#endif
    }
}
