using Game.BoluoApi;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 进岛画质档。内存阈值按系统上报值（Android 往往只有标称的约八成）。
    /// GPU 名字只降档，不升档。闺蜜机标准版把高档压回中档。
    /// 闺蜜机 4K 整档对齐低端机（分辨率另由 renderScale 管）。
    /// </summary>
    public static class IslandGraphicsTier
    {
        public enum Tier
        {
            Low = 0,
            Mid = 1,
            High = 2,
        }

        // Android 4 GB 上报约 3600–3900；6 GB 约 5200–5800。沿用原低档线 4096。
        const int AndroidLowRamMb = 4096;

        // 8 GB 上报约 6800–7600，落在 6 GB 上报上限之上。
        const int AndroidHighRamMb = 6800;

        // iPhone 3 GB（SE / XR）上报不超过约 3.2 GB；4 GB 机约 3700 以上。
        const int IosLowRamMb = 3400;

        // 6 GB iPhone 上报接近标称；4 GB 停在 4096 附近。
        const int IosHighRamMb = 5600;

        static bool _resolved;
        static Tier _tier;
        static bool _logged;

        /// <summary>
        /// 预设过场：低档跳过退出走动、缩短黑场。编辑器与桌面恒为 false。
        /// 闺蜜机标准版不因大屏走这条路径；4K 与低端机一样走。
        /// </summary>
        public static bool IsLowEnd => IsMobilePlayer && Resolve() == Tier.Low;

        /// <summary>闺蜜机标准版：GPU 功能不超过中档。4K 走低档，不进这里。</summary>
        public static bool BandwidthConservative =>
            IsMobilePlayer && BoluoDeviceInfo.IsSkyworthGmjStandard();

        /// <summary>合批 / 草地用的档。标准版高档压成中档；4K 在 Resolve 里已是低档。</summary>
        public static Tier GpuTier
        {
            get
            {
                Tier tier = Resolve();
                if (BandwidthConservative && tier == Tier.High)
                    return Tier.Mid;
                return tier;
            }
        }

        /// <summary>
        /// 后处理分档（可与 <see cref="GpuTier"/> 不同）：
        /// 闺蜜机标准版全关；4K 按中档；其它跟 GpuTier。
        /// </summary>
        static Tier PostFxTier
        {
            get
            {
                if (BoluoDeviceInfo.IsSkyworthGmjStandard())
                    return Tier.Low;
                if (BoluoDeviceInfo.IsSkyworthGmj4K())
                    return Tier.Mid;
                return GpuTier;
            }
        }

        /// <summary>
        /// 后处理总闸。低档全关；中/高按效果细分（见 AllowsBloom / ColorGrading / OilFilter）。
        /// </summary>
        public static bool AllowsPostFx => PostFxTier != Tier.Low;

        /// <summary>Bloom：仅高档。</summary>
        public static bool AllowsBloom => PostFxTier == Tier.High;

        /// <summary>Color Adjustments + White Balance：中档及以上。</summary>
        public static bool AllowsColorGrading => PostFxTier >= Tier.Mid;

        /// <summary>Split Toning：仅高档。</summary>
        public static bool AllowsSplitToning => PostFxTier == Tier.High;

        /// <summary>Oil Filter：中档及以上。</summary>
        public static bool AllowsOilFilter => PostFxTier >= Tier.Mid;

        /// <summary>
        /// 纹理流式内存预算（MB）。低 128、中 256、高 512。跟 <see cref="GpuTier"/>。
        /// </summary>
        public static float StreamingMemoryBudgetMb
        {
            get
            {
                switch (GpuTier)
                {
                    case Tier.Low:
                        return 128f;
                    case Tier.Mid:
                        return 256f;
                    default:
                        return 512f;
                }
            }
        }

        /// <summary>每张纹理最多丢弃的 mip 级数。低档 3，其余 2。</summary>
        public static int StreamingMaxLevelReduction => GpuTier == Tier.Low ? 3 : 2;

        /// <summary>真机都走 Native 合批，含低档。编辑器关闭。</summary>
        public static bool AllowsNativeInstancing => IsMobilePlayer;

        static bool IsMobilePlayer
        {
            get
            {
#if UNITY_EDITOR
                return false;
#else
                return Application.isMobilePlatform;
#endif
            }
        }

        static Tier Resolve()
        {
            if (_resolved)
                return _tier;
            _resolved = true;
            _tier = Compute();
            LogOnce(_tier);
            return _tier;
        }

        static void LogOnce(Tier raw)
        {
#if !UNITY_EDITOR
            if (_logged)
                return;
            _logged = true;
            Tier gpu = BandwidthConservative && raw == Tier.High ? Tier.Mid : raw;
            Debug.Log(
                "[IslandGraphicsTier] tier=" + raw
                + " gpuTier=" + gpu
                + " mem=" + SystemInfo.systemMemorySize
                + " gpu=" + SystemInfo.graphicsDeviceName
                + " gmj=" + BandwidthConservative
                + " gmj4k=" + BoluoDeviceInfo.IsSkyworthGmj4K());
#else
            _ = raw;
#endif
        }

        static Tier Compute()
        {
#if UNITY_EDITOR
            return Tier.High;
#else
            if (!Application.isMobilePlatform)
                return Tier.High;

            // 4K 面板像素多，画质预算对齐低端机；renderScale 在 IslandCameraSrpSetup。
            if (BoluoDeviceInfo.IsSkyworthGmj4K())
                return Tier.Low;

            int mem = SystemInfo.systemMemorySize;

#if UNITY_IOS
            if (mem > 0 && mem <= IosLowRamMb)
                return Tier.Low;
            if (mem >= IosHighRamMb)
                return Tier.High;
            return Tier.Mid;
#else
            bool weakGpu = IsWeakMobileGpu(SystemInfo.graphicsDeviceName);
            if (weakGpu || IsSpreadtrum9863() || (mem > 0 && mem <= AndroidLowRamMb))
                return Tier.Low;
            if (mem >= AndroidHighRamMb)
                return Tier.High;
            return Tier.Mid;
#endif
#endif
        }

        /// <summary>
        /// Adreno 619 及以下、Adreno 710/720（编号更大但弱于 660）、Mali-G57 及以下（含 Mali-G52），以及更老的 Mali-T / Mali-4。
        /// PowerVR（GE8320 / GE8322 等）一律降档。认不出的名字不降档。
        /// </summary>
        static bool IsWeakMobileGpu(string gpuName)
        {
            if (string.IsNullOrEmpty(gpuName))
                return false;

            if (TryModelNumber(gpuName, "adreno", out int adreno))
            {
                if (adreno <= 0)
                    return false;
                if (adreno <= 619)
                    return true;
                // 710 = 骁龙 6 Gen 1，720 = 7s。编号大于 619，性能弱于 660，不能凭数字进高档。
                return adreno == 710 || adreno == 720;
            }

            // Mali-G52 落在 G57 这条线上，不单独列型号。
            if (TryModelNumber(gpuName, "mali-g", out int maliG))
                return maliG > 0 && maliG <= 57;

            string lower = gpuName.ToLowerInvariant();
            if (lower.IndexOf("mali-t", System.StringComparison.Ordinal) >= 0)
                return true;
            if (lower.IndexOf("mali-4", System.StringComparison.Ordinal) >= 0)
                return true;
            if (lower.IndexOf("powervr", System.StringComparison.Ordinal) >= 0)
                return true;
            return false;
        }

        /// <summary>
        /// 展锐 SP9863A。处理器或 GPU 名含 9863（含 sc9863 / sp9863）或 ums312。
        /// </summary>
        static bool IsSpreadtrum9863()
        {
            return ContainsSpreadtrum9863(SystemInfo.processorType)
                || ContainsSpreadtrum9863(SystemInfo.graphicsDeviceName);
        }

        static bool ContainsSpreadtrum9863(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            string lower = value.ToLowerInvariant();
            if (lower.IndexOf("9863", System.StringComparison.Ordinal) >= 0)
                return true;
            if (lower.IndexOf("ums312", System.StringComparison.Ordinal) >= 0)
                return true;
            return false;
        }

        /// <summary>在 marker 之后取第一段十进制数。找不到则不降档。</summary>
        static bool TryModelNumber(string gpuName, string marker, out int model)
        {
            model = 0;
            int start = gpuName.IndexOf(marker, System.StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return false;

            int i = start + marker.Length;
            while (i < gpuName.Length && !char.IsDigit(gpuName[i]))
                i++;
            if (i >= gpuName.Length)
                return false;

            int value = 0;
            while (i < gpuName.Length && char.IsDigit(gpuName[i]))
            {
                value = value * 10 + (gpuName[i] - '0');
                i++;
            }

            model = value;
            return true;
        }
    }
}
