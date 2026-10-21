using System;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 静态内陆水体 collider 最近点采样。主路径是 <see cref="StaticWaterStationIndex"/>；
    /// 当 BakedWaterStations 未打包时，<see cref="AmbienceZoneService"/> 仍用本类沿河网采样。
    /// </summary>
    public static class InlandWaterSampler
    {
        /// <summary>
        /// 找距听者最近的水体三角面世界坐标点（通过 MeshCollider 路径）。
        /// 若水体无 collider（未挂载），fallback 到按子节点中心做粗略判定。
        /// </summary>
        public static bool TrySample(Vector3 listener, float maxDistance,
            out Vector3 closest, out float dist3D)
        {
            return TrySampleNamed(listener, maxDistance, nameHint: null, out closest, out dist3D);
        }

        /// <summary>
        /// 按 <paramref name="nameHint"/>（如 River_01 / WaterfallSplash）过滤 InlandWater 子节点后再取最近点。
        /// hint 为空时扫描全部 inland collider。
        /// </summary>
        public static bool TrySampleNamed(
            Vector3 listener, float maxDistance, string nameHint,
            out Vector3 closest, out float dist3D)
        {
            closest = default;
            dist3D = float.MaxValue;

            var root = IslandWorldService.Instance?.InlandWaterInstance;
            if (root == null)
                return false;

            bool anyHit = false;
            float maxSq = maxDistance * maxDistance;
            var colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                var col = colliders[i];
                if (col == null || col is TerrainCollider)
                    continue;
                if (!NameMatchesHint(col.transform, nameHint))
                    continue;

                float boundDist = (col.bounds.center - listener).sqrMagnitude;
                float expandedBoundSq = boundDist - col.bounds.size.sqrMagnitude * 0.25f;
                if (expandedBoundSq > maxSq + col.bounds.size.sqrMagnitude)
                    continue;

                Vector3 p = Physics.ClosestPoint(listener, col, col.transform.position, col.transform.rotation);
                float dSq = (p.x - listener.x) * (p.x - listener.x)
                          + (p.y - listener.y) * (p.y - listener.y)
                          + (p.z - listener.z) * (p.z - listener.z);

                if (dSq > maxSq)
                    continue;

                if (dSq < dist3D * dist3D)
                {
                    closest = p;
                    dist3D = Mathf.Sqrt(dSq);
                    anyHit = true;
                }
            }

            if (!anyHit)
            {
                var transforms = root.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < transforms.Length; i++)
                {
                    var t = transforms[i];
                    if (t == null || t == root.transform)
                        continue;
                    if (!NameMatchesHint(t, nameHint))
                        continue;

                    Vector3 center = t.position;
                    float dSq = (center.x - listener.x) * (center.x - listener.x)
                               + (center.y - listener.y) * (center.y - listener.y)
                               + (center.z - listener.z) * (center.z - listener.z);

                    if (dSq > maxSq)
                        continue;

                    if (dSq < dist3D * dist3D)
                    {
                        closest = center;
                        dist3D = Mathf.Sqrt(dSq);
                        anyHit = true;
                    }
                }
            }

            return anyHit;
        }

        static bool NameMatchesHint(Transform t, string nameHint)
        {
            if (string.IsNullOrEmpty(nameHint))
                return true;
            for (Transform cur = t; cur != null; cur = cur.parent)
            {
                string n = cur.name;
                if (!string.IsNullOrEmpty(n)
                    && n.IndexOf(nameHint, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }
    }
}
