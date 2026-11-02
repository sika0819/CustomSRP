using UnityEngine;

namespace Game
{
    /// <summary>
    /// Boluo <c>WanosBoxHelper</c> 四角布局 + <c>WaterFall_Sound_Big_*</c> 双落点：
    /// 河/海 L·Ls·R·Rs 分四个落点；大瀑 LsRs / LR 钉在 Big_0 / Big_1。
    /// 朝向用「水面特征 → 听者」而不是脸朝向，转头时左右声道不会对调。
    /// </summary>
    public static class AmbienceQuadChannelLayout
    {
        /// <summary>Boluo River_Audio halfLength / halfWidth。</summary>
        public const float RiverHalfMeters = 8f;

        /// <summary>Boluo Ocean_Audio halfLength / halfWidth。</summary>
        public const float OceanHalfMeters = 9f;

        /// <summary>Boluo centerSmoothTime。</summary>
        public const float CenterSmoothSeconds = 0.3f;

        /// <summary>
        /// Boluo <c>moveThreshold</c>：中心跳这么远才闪现，否则一直 SmoothDamp。
        /// 禁止再用 8m pose 门控——会粘住再猛跳，河/海走路增益刺啦。
        /// </summary>
        public const float MoveThresholdMeters = 100f;

        /// <summary>Boluo BoxHelper.refreshInterval（河 0.1 / 海 0.2）量级，布局 dt 下限。</summary>
        public const float LayoutMinDeltaSeconds = 0.05f;

        /// <summary>Boluo <c>WaterFall_Audio</c> 世界坐标。</summary>
        public static readonly Vector3 WaterfallBigRoot =
            new Vector3(436.75595f, 41.50995f, 458.25363f);

        /// <summary>Boluo <c>WaterFall_Sound_Big_0</c>（Play_Waterfall_Big_LsRs）。</summary>
        public static readonly Vector3 WaterfallBig0Local =
            new Vector3(-8.7f, 8f, -0.8f);

        /// <summary>Boluo <c>WaterFall_Sound_Big_1</c>（Play_Waterfall_Big_LR）；观景点 ~33m。</summary>
        public static readonly Vector3 WaterfallBig1Local =
            new Vector3(35.2f, -12f, 4.7f);

        /// <summary>Boluo <c>WaterFall_Sound_Small_0</c>（Play_Waterfall_Small_LR）。</summary>
        public static readonly Vector3 WaterfallSmall0Local =
            new Vector3(11.279991f, -0.39911222f, -0.45758986f);

        /// <summary>Boluo <c>WaterFall_Sound_Small_1</c>（Play_Waterfall_Small_LsRs）。</summary>
        public static readonly Vector3 WaterfallSmall1Local =
            new Vector3(0.5646448f, 7.465202f, 8.449369f);

        /// <summary>
        /// 河：朝向只跟站位到河面，不跟角色脸（Boluo StableFacingFromFeatureToListener）。
        /// 转头位置不变 → 四角不动 → 左右不会甩。
        /// </summary>
        public static Vector3 StableFacingFromFeatureToListener(
            Vector3 featurePoint, Vector3 listenerPos, Vector3 fallbackForward)
        {
            Vector3 toListener = listenerPos - featurePoint;
            toListener.y = 0f;
            if (toListener.sqrMagnitude >= 1e-4f)
                return toListener.normalized;
            Vector3 fallback = Vector3.ProjectOnPlane(fallbackForward, Vector3.up);
            if (fallback.sqrMagnitude < 1e-6f)
                fallback = Vector3.forward;
            return fallback.normalized;
        }

        /// <summary>
        /// edge0=前右、edge1=前左、edge2=后右、edge3=后左（Boluo ComputeEdges）。
        /// </summary>
        public static void ComputeEdges(
            Vector3 center,
            Vector3 fwdOnGround,
            Vector3 rightOnGround,
            float halfLen,
            float halfWid,
            out Vector3 edge0,
            out Vector3 edge1,
            out Vector3 edge2,
            out Vector3 edge3)
        {
            Vector3 front = center + fwdOnGround * halfLen;
            Vector3 back = center - fwdOnGround * halfLen;
            edge0 = front + rightOnGround * halfWid;
            edge1 = front - rightOnGround * halfWid;
            edge2 = back + rightOnGround * halfWid;
            edge3 = back - rightOnGround * halfWid;
        }

        /// <summary>
        /// Boluo River/Ocean Sound 绑定：Rs→0 前右、Ls→1 前左、R→2 后右、L→3 后左。
        /// </summary>
        public static int CornerIndexForPlayEvent(string playEvent)
        {
            if (string.IsNullOrEmpty(playEvent))
                return 0;
            if (playEvent.EndsWith("_Rs", System.StringComparison.Ordinal)
                || playEvent.EndsWith("_Clam_Rs", System.StringComparison.Ordinal))
                return 0;
            if (playEvent.EndsWith("_Ls", System.StringComparison.Ordinal)
                || playEvent.EndsWith("_Calm_Ls", System.StringComparison.Ordinal))
                return 1;
            if (playEvent.EndsWith("_R", System.StringComparison.Ordinal)
                || playEvent.EndsWith("_Clam_R", System.StringComparison.Ordinal))
                return 2;
            if (playEvent.EndsWith("_L", System.StringComparison.Ordinal)
                || playEvent.EndsWith("_Calm_L", System.StringComparison.Ordinal))
                return 3;
            // 未知：落到前右，避免 NRE。
            return 0;
        }

        public static bool UsesQuadChannels(AmbienceEmitterKind kind)
        {
            return kind == AmbienceEmitterKind.River || kind == AmbienceEmitterKind.Ocean;
        }

        /// <summary>瀑：Boluo Big_0/Big_1（或 Small_0/1）双发射器，不是四角盒。</summary>
        public static bool UsesWaterfallDualChannels(AmbienceEmitterKind kind)
        {
            return kind == AmbienceEmitterKind.Waterfall;
        }

        /// <summary>
        /// 大/小瀑用 bank / 事件名区分。禁止再比 ExitRadius——boluo-main Big 可听到 80、
        /// Small 到 50，Exit 大小会反转。
        /// </summary>
        public static bool IsWaterfallBig(AmbienceEmitterSpec spec)
        {
            if (spec == null || spec.Kind != AmbienceEmitterKind.Waterfall)
                return false;
            if (!string.IsNullOrEmpty(spec.Bank)
                && spec.Bank.IndexOf("Small", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (!string.IsNullOrEmpty(spec.PlayEvent)
                && spec.PlayEvent.IndexOf("Small", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (!string.IsNullOrEmpty(spec.Bank)
                && spec.Bank.IndexOf("Big", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (!string.IsNullOrEmpty(spec.PlayEvent)
                && spec.PlayEvent.IndexOf("Big", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            // Native / 未标 Small 的瀑床默认大瀑。
            return true;
        }

        /// <summary>
        /// 瀑双轨：0 = LsRs，1 = LR（对齐 <see cref="AmbienceEventCatalog"/> 绑定顺序）。
        /// </summary>
        public static int ChannelIndexForWaterfallPlayEvent(string playEvent)
        {
            if (string.IsNullOrEmpty(playEvent))
                return 0;
            if (playEvent.IndexOf("_LR", System.StringComparison.OrdinalIgnoreCase) >= 0
                && playEvent.IndexOf("LsRs", System.StringComparison.OrdinalIgnoreCase) < 0)
                return 1;
            return 0;
        }

        /// <summary>
        /// 瀑底河盆：河面样点落在瀑根水平这么远内时，贴河也算进瀑可听
        /// （禁止只认 Big_1 观瀑点，否则「走到发射器才响」）。
        /// 与 boluo-main Big 可听圈同量级。
        /// </summary>
        public const float WaterfallBasinRiverMeters = 80f;

        /// <summary>场景飞溅特效锚（WaterfallSplash），补 Big_* 未盖住的瀑口。</summary>
        public static readonly Vector3 WaterfallSplashFeature =
            new Vector3(423.81f, 66.34f, 457.4f);

        /// <summary>
        /// Boluo <c>DistanceToWaterfallFeature</c>：loader + Big_0/1 + Splash 取最近水平距。
        /// </summary>
        public static float DistanceToWaterfallBigFeature(Vector3 listener, out Vector3 nearest)
        {
            Vector3 root = WaterfallBigRoot;
            Vector3 p0 = root + WaterfallBig0Local;
            Vector3 p1 = root + WaterfallBig1Local;
            Vector3 splash = WaterfallSplashFeature;
            nearest = root;
            float best = HorizontalDistance(listener, root);
            Consider(listener, p0, ref best, ref nearest);
            Consider(listener, p1, ref best, ref nearest);
            Consider(listener, splash, ref best, ref nearest);
            return best;
        }

        /// <summary>
        /// 大瀑可听距：固定特征 ∪ 瀑面探针 ∪ 瀑盆内河面（底下那圈河）。
        /// </summary>
        public static float DistanceToWaterfallAudible(
            Vector3 listener,
            float fallShellDist,
            Vector3 fallShellPoint,
            bool hasFallShell,
            float riverShellDist,
            Vector3 riverShellPoint,
            bool hasRiverShell,
            out Vector3 nearest)
        {
            float best = DistanceToWaterfallBigFeature(listener, out nearest);
            if (hasFallShell && fallShellDist < best)
            {
                best = fallShellDist;
                nearest = fallShellPoint;
            }

            // 瀑底河圈：最近河样点属于瀑盆时，听者到该河面的 shell 也算瀑距。
            if (hasRiverShell
                && HorizontalDistance(riverShellPoint, WaterfallBigRoot) <= WaterfallBasinRiverMeters
                && riverShellDist < best)
            {
                best = riverShellDist;
                nearest = riverShellPoint;
            }

            return best;
        }

        static void Consider(Vector3 listener, Vector3 point, ref float best, ref Vector3 nearest)
        {
            float d = HorizontalDistance(listener, point);
            if (d >= best)
                return;
            best = d;
            nearest = point;
        }

        public static void WaterfallDualWorldPoints(
            Vector3 root, bool big, out Vector3 lsRs, out Vector3 lr)
        {
            if (big)
            {
                lsRs = root + WaterfallBig0Local;
                lr = root + WaterfallBig1Local;
                return;
            }

            // Small_1 = LsRs，Small_0 = LR（Boluo prefab 事件绑定）。
            lsRs = root + WaterfallSmall1Local;
            lr = root + WaterfallSmall0Local;
        }

        public static float HalfMetersFor(AmbienceEmitterKind kind)
        {
            return kind == AmbienceEmitterKind.Ocean ? OceanHalfMeters : RiverHalfMeters;
        }

        static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
