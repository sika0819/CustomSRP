using UnityEngine;

namespace Game
{
    /// <summary>
    /// Boluo DaoYu default beach spawn (EnvTerrainTileController / GameControl.InitPlayerPos).
    /// </summary>
    public static class IslandSpawnPose
    {
        public static readonly Vector3 DefaultPos = new Vector3(610.0767f, 10.39065f, 706.1781f);

        /// <summary>PresetsScheme_1 yaw + 95° right-back offset used on first enter.</summary>
        public const float DefaultYawDegrees = 130.443f + 95f;
    }
}
