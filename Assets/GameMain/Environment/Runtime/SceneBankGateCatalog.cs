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

        /// <summary>Trigger 比实心墙每边伸出的距离。人先碰到这层再被墙挡住。</summary>
        public const float TriggerPaddingMeters = 1.5f;

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
