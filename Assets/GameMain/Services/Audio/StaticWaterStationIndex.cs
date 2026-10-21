// StaticWaterStationIndex.cs
//
// Runtime lookup table for baked water stations. Holds an immutable copy of
// `BakedWaterStationDatabase` arrays and provides O(1)/O(N) nearest-point
// queries that replace the legacy `Physics.ClosestPoint` path.
//
// Used by:
// - `AmbienceZoneService.Evaluate` (River / Waterfall / Ocean emitter anchors)
// - `RainWaterProximityService.RebuildWaterPoints`
//
// Failure mode: if `Load` is never called (e.g. asset missing in a build),
// `IsReady` returns false. Callers must fall back to the catalog's hardcoded
// `AppendNativeRivers` points in that case (see `AmbienceZoneService`).

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game
{
    public static class StaticWaterStationIndex
    {
        // 2^-7 ≈ 0.0078f — sq of (≈0.09m/tick). Stored as integer math to avoid
        // relying on hex float literal support across all C# language levels
        // accepted by this Unity project.
        const int ListenerStillThresholdNum = 1;
        const int ListenerStillThresholdDen = 128; // 1/128 = 0.0078125
        static float ListenerStillThresholdSq => (float)ListenerStillThresholdNum / ListenerStillThresholdDen;

        static BakedWaterStation[] _all;
        static BakedWaterStation[] _rivers;       // River || Waterfall
        static BakedWaterStation[] _oceanLakes;   // Ocean || Lake
        static bool _loaded;

        // For "listener is still" skip
        static Vector3 _lastListener;
        static bool _hasLastListener;

        public static bool IsReady => _loaded && _all != null && _all.Length > 0;

        public static void Load(BakedWaterStationDatabase db)
        {
            if (db == null || db.Stations == null || db.Stations.Length == 0)
            {
                _loaded = false;
                _all = null;
                _rivers = null;
                _oceanLakes = null;
                return;
            }

            _all = db.Stations;
            var riverList = new List<BakedWaterStation>(4);
            var oceanLakeList = new List<BakedWaterStation>(4);
            for (int i = 0; i < _all.Length; i++)
            {
                var s = _all[i];
                if (s == null) continue;
                if (s.Kind == AmbienceEmitterKind.River || s.Kind == AmbienceEmitterKind.Waterfall)
                    riverList.Add(s);
                else if (s.Kind == AmbienceEmitterKind.Ocean || s.Kind == AmbienceEmitterKind.Lake)
                    oceanLakeList.Add(s);
            }
            _rivers = riverList.Count == 0 ? Array.Empty<BakedWaterStation>() : riverList.ToArray();
            _oceanLakes = oceanLakeList.Count == 0 ? Array.Empty<BakedWaterStation>() : oceanLakeList.ToArray();
            _loaded = true;
            _hasLastListener = false;
        }

        public static void Unload()
        {
            _loaded = false;
            _all = null;
            _rivers = null;
            _oceanLakes = null;
            _hasLastListener = false;
        }

        /// <summary>
        /// Drop the listener-still cache so the next <see cref="ShouldEvaluate"/> always runs.
        /// Used after preset teleport / hard clear so zones re-trigger even if the delta is tiny.
        /// </summary>
        public static void InvalidateListenerStill()
        {
            _hasLastListener = false;
        }

        /// <summary>
        /// True if the listener moved enough since the last tick to make re-evaluation
        /// worthwhile. If false, the audio service can skip its tick entirely.
        /// </summary>
        public static bool ShouldEvaluate(Vector3 listener)
        {
            if (!_hasLastListener)
            {
                _lastListener = listener;
                _hasLastListener = true;
                return true;
            }
            float dx = listener.x - _lastListener.x;
            float dy = listener.y - _lastListener.y;
            float dz = listener.z - _lastListener.z;
            float sq = dx * dx + dy * dy + dz * dz;
            _lastListener = listener;
            return sq >= ListenerStillThresholdSq;
        }

        /// <summary>
        /// Find nearest river/waterfall point to listener. Returns false if no station
        /// is within `maxDist` along the listener's relevant axis.
        /// </summary>
        public static bool TryNearestRiver(
            Vector3 listener, float maxDist,
            out Vector3 closest, out float dist3D)
        {
            closest = default;
            dist3D = float.MaxValue;
            if (!_loaded || _rivers == null || _rivers.Length == 0)
                return false;

            float maxSq = maxDist * maxDist;
            for (int i = 0; i < _rivers.Length; i++)
            {
                var s = _rivers[i];
                if (s == null) continue;
                if (s.ChainPoints == null || s.ChainPoints.Length == 0) continue;
                // AABB coarse filter
                if (listener.x < s.BoundsMin.x - maxDist || listener.x > s.BoundsMax.x + maxDist) continue;
                if (listener.y < s.BoundsMin.y - maxDist || listener.y > s.BoundsMax.y + maxDist) continue;
                if (listener.z < s.BoundsMin.z - maxDist || listener.z > s.BoundsMax.z + maxDist) continue;

                Vector3 nearest;
                if (s.ChainPoints.Length == 1)
                {
                    nearest = s.ChainPoints[0];
                }
                else
                {
                    // Walk each segment, find point on segment nearest listener.
                    nearest = s.ChainPoints[0];
                    float bestSq = SqDist(nearest, listener);
                    for (int j = 1; j < s.ChainPoints.Length; j++)
                    {
                        Vector3 a = s.ChainPoints[j - 1];
                        Vector3 b = s.ChainPoints[j];
                        Vector3 ab = b - a;
                        float abLenSq = ab.sqrMagnitude;
                        float t = abLenSq > 1e-6f ? Vector3.Dot(listener - a, ab) / abLenSq : 0f;
                        t = Mathf.Clamp01(t);
                        Vector3 p = a + ab * t;
                        float d = SqDist(p, listener);
                        if (d < bestSq) { bestSq = d; nearest = p; }
                    }
                }

                float dFull = Vector3.Distance(listener, nearest);
                if (dFull < dist3D) { dist3D = dFull; closest = nearest; }
            }

            return dist3D <= maxSq ? dist3D <= maxDist : false;
        }

        /// <summary>
        /// Find nearest ocean/lake surface to listener using the CoarseGrid.
        /// For cells outside the grid the result is "out of grid" (returns false).
        /// </summary>
        public static bool TryNearestOceanOrLake(
            Vector3 listener, float maxDist,
            out Vector3 closest, out float dist3D)
        {
            closest = default;
            dist3D = float.MaxValue;
            if (!_loaded || _oceanLakes == null || _oceanLakes.Length == 0)
                return false;

            bool any = false;
            for (int i = 0; i < _oceanLakes.Length; i++)
            {
                var s = _oceanLakes[i];
                if (s == null) continue;

                float d = QueryGridNearest(s, listener, out Vector3 nearest);
                if (d < dist3D) { dist3D = d; closest = nearest; any = true; }
            }
            return any && dist3D <= maxDist;
        }

        static float QueryGridNearest(BakedWaterStation s, Vector3 listener, out Vector3 nearest)
        {
            nearest = default;
            if (s.CellSize <= 0f || s.GridSizeX <= 0 || s.GridSizeZ <= 0 || s.Cells == null)
            {
                // No real grid — fall back to AABB-center distance.
                Vector3 center = (s.BoundsMin + s.BoundsMax) * 0.5f;
                nearest = center;
                return Vector3.Distance(listener, center);
            }

            int cx = Mathf.FloorToInt((listener.x - s.GridOrigin.x) / s.CellSize);
            int cz = Mathf.FloorToInt((listener.z - s.GridOrigin.z) / s.CellSize);

            // If inside the grid, use the cell's stored DistToWater directly
            // (plus the cell-center offset for a tight answer). Otherwise
            // search a small neighborhood + AABB distance.
            if (cx >= 0 && cx < s.GridSizeX && cz >= 0 && cz < s.GridSizeZ)
            {
                BakedGridCell cell = FindCell(s, cx, cz);
                if (cell != null)
                {
                    // Distance to water surface ≈ cell.DistToWater + tiny offset from cell center
                    float offset = Vector2.Distance(
                        new Vector2(listener.x - cell.Center.x, listener.z - cell.Center.z),
                        Vector2.zero);
                    nearest = new Vector3(cell.Center.x, listener.y, cell.Center.z);
                    return cell.DistToWater + offset * 0.0f; // DistToWater already encodes the
                                                              // xz offset via per-cell sampling
                }
            }

            // Outside the grid: search 3x3 neighborhood; if still no cell, snap to nearest grid edge.
            float best = float.MaxValue;
            BakedGridCell bestCell = null;
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int ncx = cx + dx;
                int ncz = cz + dz;
                if (ncx < 0 || ncx >= s.GridSizeX || ncz < 0 || ncz >= s.GridSizeZ) continue;
                BakedGridCell c = FindCell(s, ncx, ncz);
                if (c == null) continue;
                float d = Vector3.Distance(
                    new Vector3(listener.x, 0f, listener.z),
                    new Vector3(c.Center.x, 0f, c.Center.z)) + c.DistToWater;
                if (d < best) { best = d; bestCell = c; }
            }
            if (bestCell != null)
            {
                nearest = bestCell.Center;
                return best;
            }

            // No nearby cell — fall back to AABB distance.
            Vector3 aabbCenter = (s.BoundsMin + s.BoundsMax) * 0.5f;
            nearest = aabbCenter;
            return Vector3.Distance(listener, aabbCenter);
        }

        // Cells are stored as a flat array indexed by cz * GridSizeX + cx, but
        // some cells (far from water) are dropped. Map (cx, cz) → flat index and
        // check it's within Cells.Length and that the cell matches the expected
        // (cx, cz). If the cells were packed densely, this is O(1).
        static BakedGridCell FindCell(BakedWaterStation s, int cx, int cz)
        {
            if (s.Cells == null || s.Cells.Length == 0) return null;
            int flat = cz * s.GridSizeX + cx;
            if (flat < 0 || flat >= s.Cells.Length) return null;
            return s.Cells[flat];
        }

        /// <summary>
        /// Look up the baked station for a given `AmbienceEmitterSpec.Id` and return
        /// the listener-relative nearest point + shouldPlay flag (hysteresis-aware).
        /// </summary>
        public static void GetEmitterAnchor(
            AmbienceEmitterSpec spec,
            Vector3 listener,
            bool currentlyPlaying,
            out Vector3 pos,
            out bool shouldPlay)
        {
            pos = spec.Position;
            shouldPlay = false;
            if (!_loaded || _all == null) return;

            BakedWaterStation s = FindForSpec(spec);
            if (s == null) return;

            float radius = currentlyPlaying ? spec.ExitRadius : spec.EnterRadius;

            if (s.Shape == WaterShape.SegmentChain)
            {
                // 只在本 station 的折线上取最近点（不要扫全部河，否则多床位会抢同一锚点）。
                if (!TryNearestOnStation(s, listener, radius, out Vector3 nearest, out float d3D))
                    return;
                pos = nearest;
                shouldPlay = d3D <= radius;
            }
            else if (s.Shape == WaterShape.CoarseGrid)
            {
                float d = QueryGridNearest(s, listener, out Vector3 nearest);
                if (d > radius)
                    return;
                pos = nearest;
                shouldPlay = true;
            }
        }

        /// <summary>
        /// Id 精确匹配 → PrefabHint / RiverMeshName → 同 Kind 的唯一 Ocean/Lake 网格。
        /// MapItem 源 id（river_3_River_01）与 baker（river_native_River_01）不一致时仍能命中。
        /// </summary>
        static BakedWaterStation FindForSpec(AmbienceEmitterSpec spec)
        {
            if (spec == null || _all == null)
                return null;

            BakedWaterStation byId = FindById(spec.Id);
            if (byId != null)
                return byId;

            string hint = spec.RiverMeshName;
            if (!string.IsNullOrEmpty(hint))
            {
                for (int i = 0; i < _all.Length; i++)
                {
                    var s = _all[i];
                    if (s == null)
                        continue;
                    if (!string.IsNullOrEmpty(s.PrefabHint)
                        && string.Equals(s.PrefabHint, hint, StringComparison.OrdinalIgnoreCase))
                        return s;
                    if (!string.IsNullOrEmpty(s.Id)
                        && s.Id.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                        return s;
                }
            }

            // ocean_shore_8 等无独立 bake：复用唯一 Ocean CoarseGrid。
            if (spec.Kind == AmbienceEmitterKind.Ocean || spec.Kind == AmbienceEmitterKind.Lake)
            {
                BakedWaterStation only = null;
                for (int i = 0; i < _all.Length; i++)
                {
                    var s = _all[i];
                    if (s == null || s.Kind != spec.Kind || s.Shape != WaterShape.CoarseGrid)
                        continue;
                    if (only != null)
                        return null; // 多个同 Kind 网格时不猜
                    only = s;
                }
                return only;
            }

            return null;
        }

        static BakedWaterStation FindById(string id)
        {
            if (string.IsNullOrEmpty(id) || _all == null) return null;
            for (int i = 0; i < _all.Length; i++)
            {
                var s = _all[i];
                if (s != null && s.Id == id) return s;
            }
            return null;
        }

        static bool TryNearestOnStation(
            BakedWaterStation s, Vector3 listener, float maxDist,
            out Vector3 closest, out float dist3D)
        {
            closest = default;
            dist3D = float.MaxValue;
            if (s == null || s.ChainPoints == null || s.ChainPoints.Length == 0)
                return false;

            if (listener.x < s.BoundsMin.x - maxDist || listener.x > s.BoundsMax.x + maxDist)
                return false;
            if (listener.y < s.BoundsMin.y - maxDist || listener.y > s.BoundsMax.y + maxDist)
                return false;
            if (listener.z < s.BoundsMin.z - maxDist || listener.z > s.BoundsMax.z + maxDist)
                return false;

            if (s.ChainPoints.Length == 1)
            {
                closest = s.ChainPoints[0];
                dist3D = Vector3.Distance(listener, closest);
                return dist3D <= maxDist;
            }

            closest = s.ChainPoints[0];
            float bestSq = SqDist(closest, listener);
            for (int j = 1; j < s.ChainPoints.Length; j++)
            {
                Vector3 a = s.ChainPoints[j - 1];
                Vector3 b = s.ChainPoints[j];
                Vector3 ab = b - a;
                float abLenSq = ab.sqrMagnitude;
                float t = abLenSq > 1e-6f ? Vector3.Dot(listener - a, ab) / abLenSq : 0f;
                t = Mathf.Clamp01(t);
                Vector3 p = a + ab * t;
                float d = SqDist(p, listener);
                if (d < bestSq)
                {
                    bestSq = d;
                    closest = p;
                }
            }

            dist3D = Mathf.Sqrt(bestSq);
            return dist3D <= maxDist;
        }

        /// <summary>
        /// Collect representative points for all stations of the given kinds into `dst`.
        /// Used by `RainWaterProximityService` as a one-shot list of all water anchors.
        /// </summary>
        public static void GetAllWaterPoints(List<Vector3> dst)
        {
            if (dst == null || !_loaded || _all == null) return;
            for (int i = 0; i < _all.Length; i++)
            {
                var s = _all[i];
                if (s == null) continue;
                switch (s.Kind)
                {
                    case AmbienceEmitterKind.Ocean:
                    case AmbienceEmitterKind.Lake:
                        dst.Add((s.BoundsMin + s.BoundsMax) * 0.5f);
                        break;
                    case AmbienceEmitterKind.River:
                    case AmbienceEmitterKind.Waterfall:
                        if (s.ChainPoints != null && s.ChainPoints.Length > 0)
                        {
                            // Add a few representative points from the chain to keep
                            // RainWater's linear scan short and accurate.
                            int step = Mathf.Max(1, s.ChainPoints.Length / 4);
                            for (int j = 0; j < s.ChainPoints.Length; j += step) dst.Add(s.ChainPoints[j]);
                        }
                        break;
                }
            }
        }

        static float SqDist(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dy = a.y - b.y;
            float dz = a.z - b.z;
            return dx * dx + dy * dy + dz * dz;
        }
    }
}
