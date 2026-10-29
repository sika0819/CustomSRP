using UnityEngine;
using UnityEngine.Rendering;

namespace Game
{
    /// <summary>
    /// 远树单面公告板。挂在 Island 场景里。
    /// 单面网格、绕 Y 的正方形剔除盒、Y 朝向和关闭剔除写在 *_Billboard 网格和材质上，这里只取出 LOD_Billboard 做成轻量代理。
    /// </summary>
    public sealed class EnvTreeBillboardService : MonoBehaviour
    {
        public static EnvTreeBillboardService Instance { get; private set; }

        public static EnvTreeBillboardService Ensure()
        {
            EnvTreeBillboardService service = IslandServiceHost.Ensure(Instance, "EnvTreeBillboardService");
            Instance = service;
            return service;
        }

        void Awake()
        {
            Instance = this;
            GameEntry.Billboards = this;
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            if (GameEntry.Billboards == this)
                GameEntry.Billboards = null;
        }

        public bool TryExtractBillboard(
            GameObject prefab, out Mesh mesh, out Material sharedMat, out Bounds localBounds)
        {
            mesh = null;
            sharedMat = null;
            localBounds = new Bounds(Vector3.zero, Vector3.one);
            if (!TryFindBillboardRenderer(prefab, out MeshRenderer mr, out MeshFilter filter))
                return false;
            mesh = filter.sharedMesh;
            sharedMat = mr.sharedMaterial;
            localBounds = mesh.bounds;
            return true;
        }

        /// <summary>
        /// Lightweight proxy: single MeshFilter/MeshRenderer with the baked camera-facing material.
        /// No LODGroup / bark / leaf.
        /// </summary>
        public GameObject BuildBillboardProxy(
            GameObject prefab, Transform parent, string name,
            Vector3 position, Vector3 localEuler, Vector3 localScale)
        {
            if (!TryExtractBillboard(prefab, out Mesh mesh, out Material sharedMat, out _))
                return null;

            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            go.transform.localEulerAngles = localEuler;
            go.transform.localScale = localScale;

            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = sharedMat;
            ApplyNoShadows(mr);
            return go;
        }

        bool TryFindBillboardRenderer(GameObject prefab, out MeshRenderer mr, out MeshFilter filter)
        {
            mr = null;
            filter = null;
            if (prefab == null)
                return false;

            Transform bb = FindChildNamed(prefab.transform, "LOD_Billboard");
            if (bb == null)
                return false;

            mr = bb.GetComponent<MeshRenderer>();
            if (mr == null)
                mr = bb.GetComponentInChildren<MeshRenderer>(true);
            if (mr == null)
                return false;

            filter = mr.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null || mr.sharedMaterial == null)
                return false;
            Material[] mats = mr.sharedMaterials;
            if (mats != null && mats.Length > 1)
                return false;
            return true;
        }

        static void ApplyNoShadows(MeshRenderer mr)
        {
            if (mr == null)
                return;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        static Transform FindChildNamed(Transform root, string name)
        {
            if (root == null)
                return null;
            if (root.name == name)
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChildNamed(root.GetChild(i), name);
                if (found != null)
                    return found;
            }

            return null;
        }
    }
}
