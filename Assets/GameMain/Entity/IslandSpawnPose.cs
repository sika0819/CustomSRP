using UnityEngine;

namespace Game
{
    /// <summary>
    /// 出生点回退坐标。加载到的 <see cref="IslandSpawnPoseAsset"/> 由
    /// <see cref="IslandWorldService"/> 持有，出岛时随世界释放。
    /// </summary>
    public static class IslandSpawnPose
    {
        /// <summary>Preset the spawn mirrors when the asset derives from a preset.</summary>
        public const int SourcePresetId = 1;

        /// <summary>IslandPreset_01 清晨起床 TargetPos — used when nothing resolves.</summary>
        static readonly Vector3 FallbackPos = new Vector3(610.0767f, 12.80065f, 706.1781f);

        /// <summary>IslandPreset_01 TargetRotateY. The old "+95 right-back" fudge is gone.</summary>
        public const float FallbackYawDegrees = 130.443f;

        public static Vector3 DefaultPos => Resolve().pos;

        public static float DefaultYawDegrees => Resolve().yawDegrees;

        public static (Vector3 pos, float yawDegrees) Resolve(IslandSpawnPoseAsset asset = null)
        {
            if (asset == null)
                asset = GameEntry.World?.SpawnPoseAsset;

            if (asset != null && !asset.DeriveFromPreset)
                return (asset.DefaultPos, asset.DefaultYawDegrees);

            int presetId = asset != null ? asset.DerivePresetId : SourcePresetId;
            IslandPresetAsset preset = GameEntry.Presets?.GetById(presetId);
            if (preset != null)
                return (preset.TargetPos, preset.TargetRotateY);

            return (FallbackPos, FallbackYawDegrees);
        }
    }
}
