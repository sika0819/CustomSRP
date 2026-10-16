using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// [Obsolete] 静态内陆水体的 collider 最近点采样器。每 tick 走 PhysX.ClosestPoint + 子节点枚举
    /// 性能成本高（移动端 1.5–12ms/tick），已被 <see cref="StaticWaterStationIndex"/> 取代。
    /// 保留文件仅作历史参考，不再被 AmbienceZoneService / RainWaterProximityService 调用。
    /// </summary>
    [Obsolete("Replaced by StaticWaterStationIndex (editor-baked data). Do not call from runtime code.")]
    public static class InlandWaterSampler
    {
        /// <summary>
        /// 找距听者最近的水体三角面世界坐标点（通过 MeshCollider 路径）。
        /// 若水体无 collider（未挂载），fallback 到按子节点中心做粗略判定。
        /// </summary>
        /// <param name="listener">听者世界坐标（通常取玩家相机）</param>
        /// <param name="maxDistance">最大判定距离（超过则返回 false）</param>
        /// <param name="closest">输出的最近点世界坐标</param>
        /// <param name="dist3D">输出的 3D 距离</param>
        /// <returns>是否在距离内命中了水体区域</returns>
        public static bool TrySample(Vector3 listener, float maxDistance,
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

                // 使用 collider 的 bounds 做快速预过滤
                float boundDist = (col.bounds.center - listener).sqrMagnitude;
                // bounds 对角线半径近似 collider 尺寸，展开一点安全余量
                float expandedBoundSq = (boundDist - col.bounds.size.sqrMagnitude * 0.25f);
                if (expandedBoundSq > maxSq + col.bounds.size.sqrMagnitude)
                    continue;

                // Physics.ClosestPoint 不需要 Mesh.isReadable
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

            // Fallback：无 collider 时用子节点中心粗略判定（兜底，跳过也行）
            if (!anyHit)
            {
                var transforms = root.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < transforms.Length; i++)
                {
                    var t = transforms[i];
                    if (t == null) continue;

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
    }
}
