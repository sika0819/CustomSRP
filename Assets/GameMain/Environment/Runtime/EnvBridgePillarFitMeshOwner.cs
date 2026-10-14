using UnityEngine;

namespace Game
{
    /// <summary>
    /// 持有 EnvBridgePillarFit Clone 出的 mesh；随 Renderer 销毁时释放，避免泄漏。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EnvBridgePillarFitMeshOwner : MonoBehaviour
    {
        Mesh _mesh;

        public void Set(Mesh mesh) => _mesh = mesh;

        void OnDestroy()
        {
            if (_mesh != null)
                Destroy(_mesh);
            _mesh = null;
        }
    }
}
