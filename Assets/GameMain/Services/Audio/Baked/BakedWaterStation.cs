// BakedWaterStation.cs
//
// Editor-baked static data for ambient water emitters (Ocean / River / Waterfall / Lake).
// Replaces the per-tick `Physics.ClosestPoint` path that used to drive
// `AmbienceZoneService` and `RainWaterProximityService`.
//
// Produced by `Game.Editor.WaterStationBaker` (menu GameMain/Boluo/Bake Water Stations).
// Consumed at runtime by `Game.StaticWaterStationIndex`.

using System;
using UnityEngine;

namespace Game
{
    /// <summary>Geometry shape used for nearest-point queries.</summary>
    public enum WaterShape
    {
        /// <summary>Polyline of N world-space points (O(N) scan).</summary>
        SegmentChain,

        /// <summary>Top-down cell grid storing distance-to-water per cell (O(1) lookup).</summary>
        CoarseGrid,
    }

    /// <summary>One static water emitter baked from a runtime mesh + prefab pose.</summary>
    [Serializable]
    public class BakedWaterStation
    {
        public string Id;
        public AmbienceEmitterKind Kind;
        public string PrefabHint;

        // 公共衰减参数（直接复用 spec）
        public float EnterRadius;
        public float ExitRadius;
        public bool UseXzDistance;

        // AABB 粗筛
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;

        // 形状二选一
        public WaterShape Shape;

        /// <summary>Shape == SegmentChain 时填充：沿水流的世界空间折线。</summary>
        public Vector3[] ChainPoints;

        // CoarseGrid：按 CellSize 切分，只存距离水面 ≤ 50m 的 cell
        public Vector3 GridOrigin;
        public int GridSizeX;
        public int GridSizeZ;
        public float CellSize;
        public BakedGridCell[] Cells;
    }

    /// <summary>One cell of a CoarseGrid. Center in world space + distance to nearest water surface.</summary>
    [Serializable]
    public class BakedGridCell
    {
        public Vector3 Center;
        public float DistToWater;
    }

    /// <summary>
    /// ScriptableObject database holding all baked water stations for the project.
    /// Version field is set by the baker based on prefab + mesh hashes; runtime code
    /// can refuse to load if the format version is incompatible.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Audio/Baked Water Station Database", fileName = "BakedWaterStationDatabase")]
    public class BakedWaterStationDatabase : ScriptableObject
    {
        public const int CurrentVersion = 1;

        [Tooltip("Baked format version. Bump when struct layout changes.")]
        public int Version = CurrentVersion;

        [Tooltip("All baked stations. Order is not significant; lookups are by Id.")]
        public BakedWaterStation[] Stations;
    }
}
