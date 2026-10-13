using UnityEngine;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Island 地形细节的编辑器参数。只在 Apply 时读取，不进播放器。
    /// 源快照放在 Assets/Terrain/Island/Editor，播放器打包不会带上这份高度图。
    /// </summary>
    public class TerrainDetailSettings : ScriptableObject
    {
        /// <summary>要写回的地形。分辨率、LOD、层数都保持不动。</summary>
        public TerrainData target;

        /// <summary>Capture 时复制的高度和 splat。每次 Apply 都从这里重算。</summary>
        public TerrainData source;

        [Header("Ridge")]
        [Tooltip("倍频数。范围 1..8。越高越碎，最高一档仍会被波长下限夹住。")]
        [Range(1, 8)]
        public int octaves = 4;

        [Tooltip("每一档频率乘上的倍数。范围 1.5..3.5。")]
        [Range(1.5f, 3.5f)]
        public float lacunarity = 2f;

        [Tooltip("每一档振幅乘上的倍数。范围 0.3..0.7。")]
        [Range(0.3f, 0.7f)]
        public float gain = 0.5f;

        [Tooltip("第一档频率，单位是周/米。例如 0.008 表示约 125 米一个起伏。最高档波长不会短于 4 个高度图像素。")]
        public float frequency = 0.008f;

        [Tooltip("山脊锐化指数。范围 1..4。1 最钝，4 最尖。")]
        [Range(1f, 4f)]
        public float sharpness = 2f;

        [Tooltip("山脊位移的最大幅度，单位米。这是加法，不会拿噪声替换整张高度图。")]
        public float amplitudeMeters = 2f;

        [Tooltip("幅度缩放。范围 0..1。0 不加山脊，1 用满 amplitudeMeters。")]
        [Range(0f, 1f)]
        public float ridgeWeight = 0.7f;

        [Tooltip("缓于这个坡度（度）的地方不加山脊。范围 0..60。平地保持原高度。")]
        [Range(0f, 60f)]
        public float minSlopeDegrees = 22f;

        [Tooltip("噪声平移种子。相同种子、相同快照，结果可复现。")]
        public int seed = 17011;

        [Header("Coast")]
        [Tooltip("海平面，世界高度，单位米。Island 场景里 Sea 在 y = 5。归一化阈值 = 米 / TerrainData.size.y。")]
        public float seaLevelMeters = 5f;

        [Tooltip("海平面以下仍要重塑的厚度，单位米。只处理这段，不改更深的海底。")]
        public float belowMeters = 6f;

        [Tooltip("海平面以上仍要重塑的厚度，单位米。带的上沿贴回原高度。")]
        public float aboveMeters = 8f;

        [Tooltip("海岸线水平凹凸的幅度，单位米。只偏移局部海平面阈值，不平移内陆山体。")]
        public float bayMeters = 4f;

        [Tooltip("海湾噪声频率，周/米。会被夹到波长不少于 64 米，避免岸线起碎点。")]
        public float bayFrequency = 0.0015f;

        [Tooltip("水下指数衰减要靠近的海床高度，单位米。带的下沿仍贴回原高度，避免台阶。")]
        public float seabedMeters = 1f;

        [Header("Rock splat")]
        [Tooltip("新山脊从草地扣到 SWS_DemoRock 的强度。范围 0..1。0 不改 splat。")]
        [Range(0f, 1f)]
        public float rockStrength = 0.9f;

        [Tooltip("山脊检测的邻域半径，单位是 alphamap 像素。范围 1..8。")]
        [Range(1, 8)]
        public int tpiRadiusTexels = 4;

        [Tooltip("比邻域高出这么多米才算脊。再低的起伏不刷岩石。")]
        public float minCrestMeters = 0.2f;
    }
}
