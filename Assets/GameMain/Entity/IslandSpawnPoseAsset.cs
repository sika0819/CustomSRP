using UnityEngine;

namespace Game
{
    /// <summary>
    /// Boluo DaoYu default beach spawn, authored as an asset at
    /// <c>Assets/AssetRaw/Configs/IslandSpawnPose.asset</c> so the beach landing
    /// point can be retuned without touching code.
    /// Loaded through YooAsset (<see cref="SceneAssets.IslandSpawnPoseLocation"/>);
    /// see <see cref="IslandSpawnPose"/> for the runtime accessor and its fallback.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Island Spawn Pose", fileName = "IslandSpawnPose")]
    public sealed class IslandSpawnPoseAsset : ScriptableObject
    {
        [Tooltip("出生点直接取自预设（默认 IslandPreset_01 清晨起床）。关掉才用下面的手填值。")]
        public bool DeriveFromPreset = true;

        [Tooltip("DeriveFromPreset = true 时对齐的预设 Id。")]
        public int DerivePresetId = 1;

        [Tooltip("仅 DeriveFromPreset = false 时生效：出生点世界坐标。")]
        public Vector3 DefaultPos = new Vector3(610.0767f, 12.80065f, 706.1781f);

        [Tooltip("仅 DeriveFromPreset = false 时生效：出生朝向 yaw（度）。")]
        public float DefaultYawDegrees = 130.443f;
    }
}