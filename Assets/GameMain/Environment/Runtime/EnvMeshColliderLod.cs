using System.Collections.Generic;
using UnityEngine;

namespace Game
{
    /// <summary>近距才启用 MeshCollider，降低物理开销。</summary>
    public sealed class EnvMeshColliderLod : MonoBehaviour
    {
        struct Entry
        {
            public Vector3 Pos;
            public MeshCollider Collider;
            public bool Enabled;
        }

        readonly List<Entry> _entries = new List<Entry>(512);
        Transform _focus;
        float _radius = EnvStreamingConfig.MeshColliderEnableRadius;
        float _radiusSq;
        int _cursor;

        public void Begin(Transform focus)
        {
            _focus = focus;
            _radius = EnvStreamingConfig.MeshColliderEnableRadius;
            _radiusSq = _radius * _radius;
            _cursor = 0;
            enabled = true;
        }

        public void Register(GameObject go, Vector3 worldPos)
        {
            if (go == null)
                return;
            var colliders = go.GetComponentsInChildren<MeshCollider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                MeshCollider c = colliders[i];
                if (c == null)
                    continue;
                c.enabled = false;
                _entries.Add(new Entry { Pos = worldPos, Collider = c, Enabled = false });
            }
        }

        public void Unregister(GameObject go)
        {
            if (go == null)
                return;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                MeshCollider c = _entries[i].Collider;
                if (c == null || c.transform.root == go.transform.root || c.gameObject == go
                    || IsChildOf(c.transform, go.transform))
                {
                    _entries.RemoveAt(i);
                }
            }
        }

        void Update()
        {
            if (_focus == null || _entries.Count == 0)
                return;

            Vector3 p = _focus.position;
            int budget = 64;
            int n = _entries.Count;
            while (budget-- > 0 && n > 0)
            {
                if (_cursor >= n)
                    _cursor = 0;
                Entry e = _entries[_cursor];
                if (e.Collider == null)
                {
                    _entries.RemoveAt(_cursor);
                    n = _entries.Count;
                    continue;
                }

                float dx = e.Pos.x - p.x;
                float dz = e.Pos.z - p.z;
                bool want = dx * dx + dz * dz <= _radiusSq;
                if (want != e.Enabled)
                {
                    e.Collider.enabled = want;
                    e.Enabled = want;
                    _entries[_cursor] = e;
                }

                _cursor++;
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
