using System;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 未下载区域的隐形门。体积和 bank 名单对齐旧工程
    /// <c>BanksChecker.prefab</c>（Forest / GrassLand / Plateau）。
    /// AllMapItem 没有 BanksChecker 行，这些门的 MapItemIndex 为 -1。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Scene Bank Gate Catalog", fileName = "SceneBankGateCatalog")]
    public sealed class SceneBankGateCatalog : ScriptableObject
    {
        public const string Category = "BanksChecker";

        /// <summary>实心墙最薄的一边至少这么厚，避免胶囊一步穿过去。</summary>
        public const float MinThicknessMeters = 2f;

        /// <summary>
        /// Trigger 比实心墙每边伸出的距离。人先碰到这层再被墙挡住。
        /// 必须大于胶囊半径（约 1.1）加 skin，否则真机贴墙时身体采样点落在壳外，弹窗不出现。
        /// </summary>
        public const float TriggerPaddingMeters = 3f;

        public SceneBankGateEntry[] Entries = Array.Empty<SceneBankGateEntry>();

        public SceneBankGateEntry Find(int mapItemIndex)
        {
            if (Entries == null)
                return null;
            for (int i = 0; i < Entries.Length; i++)
            {
                SceneBankGateEntry entry = Entries[i];
                if (entry != null && entry.MapItemIndex == mapItemIndex)
                    return entry;
            }

            return null;
        }

        /// <summary>
        /// 真机离线包若还没打进 <c>SceneBankGateCatalog</c>，用这份和资产一致的区域，避免门整扇消失。
        /// </summary>
        public static SceneBankGateCatalog CreateBuiltin()
        {
            var catalog = CreateInstance<SceneBankGateCatalog>();
            catalog.Entries = new[]
            {
                Entry("Forest", new Vector3(711.71438f, 22.042757f, 556.0592f),
                    new Vector3(576.52136f, 65.79881f, 127.68161f), SceneBankGateShape.Box, 0f,
                    "Boluodao_Forest"),
                Entry("GrassLand", new Vector3(548.9f, 41.3f, 313.9f),
                    Vector3.zero, SceneBankGateShape.Sphere, 138.2f,
                    "Boluodao_Grassland"),
                Entry("Plateau c0", new Vector3(368.8f, 32.9f, 560.4f),
                    Vector3.zero, SceneBankGateShape.Sphere, 77.62f,
                    "Boluodao_Plateau"),
                Entry("Plateau c1", new Vector3(243.2f, 32.9f, 350.6f),
                    Vector3.zero, SceneBankGateShape.Sphere, 77.62f,
                    "Boluodao_Plateau"),
            };
            return catalog;
        }

        static SceneBankGateEntry Entry(
            string objectName,
            Vector3 position,
            Vector3 boxSize,
            SceneBankGateShape shape,
            float radius,
            string bank)
        {
            return new SceneBankGateEntry
            {
                MapItemIndex = -1,
                ObjectName = objectName,
                Position = position,
                Rotation = Vector3.zero,
                Scale = Vector3.one,
                BoxSize = boxSize,
                Shape = shape,
                Radius = radius,
                BankNames = new[] { bank },
            };
        }

        /// <summary>
        /// 盒子本地尺寸（米）。<paramref name="boxSize"/> 非零时用它，否则用摆放缩放。
        /// 缩放接近 1 且没有覆盖时返回 false，避免生成 1 米小方块。
        /// </summary>
        public static bool TryResolveBoxSize(Vector3 scale, Vector3 boxSize, out Vector3 size)
        {
            bool hasOverride = boxSize.sqrMagnitude > 0.0001f;
            Vector3 raw = hasOverride ? boxSize : scale;
            size = new Vector3(Mathf.Abs(raw.x), Mathf.Abs(raw.y), Mathf.Abs(raw.z));
            float max = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            if (max < 0.05f)
                return false;
            if (!hasOverride && max < 1.5f)
                return false;

            if (size.x <= size.y && size.x <= size.z)
                size.x = Mathf.Max(size.x, MinThicknessMeters);
            else if (size.y <= size.z)
                size.y = Mathf.Max(size.y, MinThicknessMeters);
            else
                size.z = Mathf.Max(size.z, MinThicknessMeters);
            return true;
        }
    }

    [Serializable]
    public sealed class SceneBankGateEntry
    {
        [Tooltip("AllMapItem 的 Index。不在摆放表里、只在本资源手写的门填 -1。")]
        public int MapItemIndex = -1;

        public string ObjectName;

        public Vector3 Position;

        public Vector3 Rotation;

        public Vector3 Scale = Vector3.one;

        [Tooltip("盒子的本地尺寸（米）。Shape 为 Box 且摆放缩放接近 1 时必须填。")]
        public Vector3 BoxSize;

        [Tooltip("Box 用 BoxSize。Sphere 用 Radius，Position 是球心。")]
        public SceneBankGateShape Shape;

        [Tooltip("球体半径（米）。只在 Shape 为 Sphere 时使用。")]
        public float Radius;

        [Tooltip("这一扇门要下完的 bank。空着表示先不挡路。")]
        public string[] BankNames = Array.Empty<string>();
    }

    public enum SceneBankGateShape
    {
        Box = 0,
        Sphere = 1,
    }
}
