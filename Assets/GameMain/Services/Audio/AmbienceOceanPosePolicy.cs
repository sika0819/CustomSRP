using UnityEngine;

namespace Game
{
    /// <summary>
    /// 单点岸/河落点门控（非四角中心）。河/海 Quad 中心跟 Boluo：连续 SmoothDamp，
    /// 不用本门控（见 <see cref="AmbienceQuadChannelLayout.MoveThresholdMeters"/>）。
    /// </summary>
    public static class AmbienceOceanPosePolicy
    {
        /// <summary>
        /// 听者到当前落点的水平距离 ≥ 此值才允许更新单点 pose。
        /// 仅给非 Quad 路径；Quad 四角禁止套用（会粘住再猛跳）。
        /// </summary>
        public const float PoseUpdateMinMeters = 8f;

        /// <summary>
        /// 是否允许把该声道挪到新岸点。
        /// <paramref name="hasCurrentPose"/> 为 false（尚未落点 / 首帧）时始终允许。
        /// </summary>
        public static bool ShouldUpdateChannelPose(
            Vector3 listener,
            Vector3 channelWorldPos,
            bool hasCurrentPose,
            float minMeters = PoseUpdateMinMeters)
        {
            if (!hasCurrentPose)
                return true;
            if (minMeters <= 0f)
                return true;
            float dx = listener.x - channelWorldPos.x;
            float dz = listener.z - channelWorldPos.z;
            float minSq = minMeters * minMeters;
            return dx * dx + dz * dz >= minSq;
        }

        /// <summary>
        /// 已播海/河：按门控选实际写入引擎的落点。增益仍用调用方传入的 shell 距离。
        /// </summary>
        public static Vector3 ResolveChannelPose(
            Vector3 listener,
            Vector3 currentPose,
            Vector3 targetShorePose,
            bool hasCurrentPose,
            float minMeters = PoseUpdateMinMeters)
        {
            if (ShouldUpdateChannelPose(listener, currentPose, hasCurrentPose, minMeters))
                return targetShorePose;
            return currentPose;
        }
    }
}
