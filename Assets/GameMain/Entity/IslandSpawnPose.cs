using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// Boluo DaoYu default beach spawn (EnvTerrainTileController / GameControl.InitPlayerPos).
    ///
    /// Values come from <see cref="IslandSpawnPoseAsset"/> (YooAsset location
    /// <see cref="SceneAssets.IslandSpawnPoseLocation"/>), loaded by
    /// <see cref="IslandWorldService"/> before terrain spawn. The asset derives the
    /// pose from a preset (default <see cref="SourcePresetId"/> / 清晨起床) so the
    /// landing point can never drift from the preset it mirrors; a hand-authored
    /// fallback keeps callers working when no asset or preset is available.
    /// </summary>
    public static class IslandSpawnPose
    {
        /// <summary>Preset the spawn mirrors when the asset derives from a preset.</summary>
        public const int SourcePresetId = 1;

        /// <summary>IslandPreset_01 清晨起床 TargetPos — used when nothing resolves.</summary>
        static readonly Vector3 FallbackPos = new Vector3(610.0767f, 12.80065f, 706.1781f);

        /// <summary>IslandPreset_01 TargetRotateY. The old "+95 right-back" fudge is gone.</summary>
        public const float FallbackYawDegrees = 130.443f;

        static IslandSpawnPoseAsset _asset;

        /// <summary>False until the config asset loads (or after <see cref="Unload"/>).</summary>
        public static bool IsConfigured => _asset != null;

        /// <summary>The loaded config asset, or null when using fallback values.</summary>
        public static IslandSpawnPoseAsset Asset => _asset;

        public static Vector3 DefaultPos => Resolve().pos;

        public static float DefaultYawDegrees => Resolve().yawDegrees;

        /// <summary>Applies the loaded config. Null asset clears back to fallback values.</summary>
        public static void Load(IslandSpawnPoseAsset asset)
        {
            _asset = asset;
            (Vector3 pos, float yaw) = Resolve();
            Log.Info(
                "[IslandSpawnPose] configured=" + (asset != null) +
                " derived=" + (asset != null && asset.DeriveFromPreset) +
                " pos=" + pos + " yaw=" + yaw.ToString("F2"));
        }

        /// <summary>Drops the config handle so the next access uses fallback values.</summary>
        public static void Unload()
        {
            _asset = null;
        }

        static (Vector3 pos, float yawDegrees) Resolve()
        {
            if (_asset != null && !_asset.DeriveFromPreset)
                return (_asset.DefaultPos, _asset.DefaultYawDegrees);

            int presetId = _asset != null ? _asset.DerivePresetId : SourcePresetId;
            IslandPresetAsset preset = GameEntry.Presets?.GetById(presetId);
            if (preset != null)
                return (preset.TargetPos, preset.TargetRotateY);

            return (FallbackPos, FallbackYawDegrees);
        }
    }
}