using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 按与焦点距离开关环境非 Trigger MeshCollider，降低 CharacterController 对远距三角网格的 PhysX 开销。
    /// 近处保留启用；仅远距预关；刷新用 bounds.ClosestPoint。
    /// Mountain / Cliff / Cave / Ramp / Spike / Large 常开（可视远、关碰撞会穿模）。
    /// </summary>
    public sealed class EnvMeshColliderLod : MonoBehaviour
    {
        readonly List<MeshCollider> _colliders = new List<MeshCollider>(512);
        // 与 _colliders 同步的集合：Register 去重、Unregister O(1) 移除，避免同一实例重复登记。
        readonly HashSet<MeshCollider> _colliderSet = new HashSet<MeshCollider>();
        Transform _focus;
        float _enableSq;
        float _disableSq;
        float _refreshInterval = 0.25f;
        Coroutine _loop;

        public void Begin(Transform focus)
        {
            _focus = focus;
            float enable = EnvStreamingConfig.MeshColliderEnableRadius;
            float disable = EnvStreamingConfig.MeshColliderDisableRadius;
            _enableSq = enable * enable;
            _disableSq = disable * disable;
            if (_loop != null)
                StopCoroutine(_loop);
            _loop = StartCoroutine(RefreshLoop());
            enabled = true;
        }

        /// <summary>
        /// 登记并按相对焦点距离预关远距碰撞；近处保持启用。
        /// 结构大石 / 地标始终启用。
        /// </summary>
        public void Register(GameObject go, Vector3 worldPos)
        {
            if (go == null)
                return;

            Vector3 focusPos = _focus != null ? _focus.position : worldPos;
            bool alwaysOn = EnvStreamingConfig.ShouldKeepMeshColliderAlwaysOn(go.name);
            var colliders = go.GetComponentsInChildren<MeshCollider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                MeshCollider mc = colliders[i];
                if (mc == null || mc.isTrigger)
                    continue;

                // 同一实例可能被重复登记（重生成 / 预填充与流式重叠），去重后再加。
                if (!_colliderSet.Add(mc))
                    continue;
                _colliders.Add(mc);

                if (alwaysOn)
                {
                    mc.enabled = true;
                    continue;
                }

                // 用 AABB 最近点，避免大 mesh 出生 pivot 在中心却人已在坡上。
                if (HorizontalDistSqToBounds(mc.bounds, focusPos) > _enableSq)
                    mc.enabled = false;
            }
        }

        public void Unregister(GameObject go)
        {
            if (go == null)
                return;
            for (int i = _colliders.Count - 1; i >= 0; i--)
            {
                MeshCollider c = _colliders[i];
                // 注意：所有 MapItem 都挂在同一个 root 下，不能用 transform.root 比较，
                // 否则卸载任一实例会把整张碰撞体表清空，距离 LOD 随之失效。
                if (c == null || c.gameObject == go || IsChildOf(c.transform, go.transform))
                {
                    _colliders.RemoveAt(i);
                    if (c != null)
                        _colliderSet.Remove(c);
                }
            }
        }

        public void ForceRefresh()
        {
            Refresh();
        }

        /// <summary>切预设/传送：只刷新半径内碰撞，避免同步扫全岛。</summary>
        public void ForceRefreshNear(Vector3 worldPos, float radius)
        {
            float rSq = radius * radius;
            Vector3 p = worldPos;
            for (int i = _colliders.Count - 1; i >= 0; i--)
            {
                MeshCollider mc = _colliders[i];
                if (mc == null)
                {
                    _colliders.RemoveAt(i);
                    _colliderSet.Remove(mc);
                    continue;
                }

                if (IsAlwaysOnCollider(mc))
                {
                    if (!mc.enabled)
                        mc.enabled = true;
                    continue;
                }

                float nearSq = HorizontalDistSqToBounds(mc.bounds, p);
                if (nearSq > rSq)
                    continue;

                ApplyDistance(mc, nearSq);
            }
        }

        IEnumerator RefreshLoop()
        {
            var wait = new WaitForSeconds(_refreshInterval);
            while (true)
            {
                Refresh();
                yield return wait;
            }
        }

        void Refresh()
        {
            if (_focus == null)
                return;

            Vector3 p = _focus.position;
            for (int i = _colliders.Count - 1; i >= 0; i--)
            {
                MeshCollider mc = _colliders[i];
                if (mc == null)
                {
                    _colliders.RemoveAt(i);
                    _colliderSet.Remove(mc);
                    continue;
                }

                if (IsAlwaysOnCollider(mc))
                {
                    if (!mc.enabled)
                        mc.enabled = true;
                    continue;
                }

                // 到 AABB 最近点（非 center）：大物体站在坡上时离 center 可达数百米。
                ApplyDistance(mc, HorizontalDistSqToBounds(mc.bounds, p));
            }
        }

        void ApplyDistance(MeshCollider mc, float horizontalDistSq)
        {
            if (mc.enabled)
            {
                if (horizontalDistSq > _disableSq)
                    mc.enabled = false;
            }
            else if (horizontalDistSq < _enableSq)
            {
                mc.enabled = true;
            }
        }

        static bool IsAlwaysOnCollider(MeshCollider mc)
        {
            Transform t = mc != null ? mc.transform : null;
            while (t != null)
            {
                if (EnvStreamingConfig.ShouldKeepMeshColliderAlwaysOn(t.name))
                    return true;
                t = t.parent;
            }

            return false;
        }

        /// <summary>XZ 距离到 bounds 最近点；点在盒内时为 0。</summary>
        static float HorizontalDistSqToBounds(Bounds bounds, Vector3 worldPos)
        {
            Vector3 closest = bounds.ClosestPoint(worldPos);
            float dx = closest.x - worldPos.x;
            float dz = closest.z - worldPos.z;
            return dx * dx + dz * dz;
        }

        void OnDisable()
        {
            if (_loop != null)
            {
                StopCoroutine(_loop);
                _loop = null;
            }
        }

        static bool IsChildOf(Transform t, Transform root)
        {
            while (t != null)
            {
                if (t == root)
                    return true;
                t = t.parent;
            }

            return false;
        }
    }
}
