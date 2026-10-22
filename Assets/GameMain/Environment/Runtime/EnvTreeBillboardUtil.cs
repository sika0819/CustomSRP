using UnityEngine;
using UnityEngine.Rendering;

namespace Game
{
    /// <summary>
    /// Far-tree single-card billboard helpers: extract LOD_Billboard mesh/mat,
    /// enable GPU camera-facing, build lightweight proxy GOs.
    /// </summary>
    public static class EnvTreeBillboardUtil
    {
        public static readonly int OilYBillboardId = Shader.PropertyToID("_OilYBillboard");
        public static readonly int OilCrossBillboardId = Shader.PropertyToID("_OilCrossBillboard");
        public static readonly int CullId = Shader.PropertyToID("_Cull");

        /// <summary>MatchBillboardHeight rebuilds 8-vert front/side atlas crosses.</summary>
        const int CrossBillboardVertCount = 8;

        public static bool TryFindBillboardRenderer(GameObject prefab, out MeshRenderer mr, out MeshFilter filter)
        {
            mr = null;
            filter = null;
            if (prefab == null)
                return false;

            Transform bb = FindChildNamed(prefab.transform, "LOD_Billboard");
            if (bb != null)
            {
                mr = bb.GetComponent<MeshRenderer>();
                if (mr == null)
                    mr = bb.GetComponentInChildren<MeshRenderer>(true);
                if (mr != null)
                {
                    filter = mr.GetComponent<MeshFilter>();
                    if (filter != null && filter.sharedMesh != null && mr.sharedMaterial != null)
                    {
                        Material[] mats = mr.sharedMaterials;
                        if (mats == null || mats.Length <= 1)
                            return true;
                    }
                }
            }

            // Fallback: last LOD with exactly one MeshRenderer + one material.
            var lodGroup = prefab.GetComponentInChildren<LODGroup>(true);
            if (lodGroup == null)
                return false;
            LOD[] lods = lodGroup.GetLODs();
            if (lods == null || lods.Length == 0)
                return false;
            LOD last = lods[lods.Length - 1];
            if (last.renderers == null)
                return false;
            MeshRenderer only = null;
            int count = 0;
            for (int i = 0; i < last.renderers.Length; i++)
            {
                if (last.renderers[i] is MeshRenderer candidate && candidate != null)
                {
                    count++;
                    only = candidate;
                }
            }

            if (count != 1 || only == null)
                return false;
            filter = only.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null || only.sharedMaterial == null)
                return false;
            Material[] lastMats = only.sharedMaterials;
            if (lastMats != null && lastMats.Length > 1)
                return false;
            mr = only;
            return true;
        }

        public static bool TryExtractBillboard(
            GameObject prefab, out Mesh mesh, out Material sharedMat, out Bounds localBounds)
        {
            mesh = null;
            sharedMat = null;
            localBounds = new Bounds(Vector3.zero, Vector3.one);
            if (!TryFindBillboardRenderer(prefab, out MeshRenderer mr, out MeshFilter filter))
                return false;
            mesh = filter.sharedMesh;
            sharedMat = mr.sharedMaterial;
            // Expand XZ so frustum culling survives GPU yaw (card swings around Y).
            Bounds b = mesh.bounds;
            float horiz = Mathf.Max(b.extents.x, b.extents.z);
            localBounds = new Bounds(b.center, new Vector3(horiz * 2f, b.size.y, horiz * 2f));
            return true;
        }

        static bool IsCrossBillboardMesh(Mesh mesh) =>
            mesh != null && mesh.vertexCount == CrossBillboardVertCount;

        /// <summary>
        /// Runtime material instance that faces the camera. Does not dirty the asset.
        /// 8-vert atlas crosses → <c>_OilCrossBillboard</c> (snap + drop + mip bias).
        /// High-poly impostors → <c>_OilYBillboard</c> (continuous yaw).
        /// </summary>
        public static Material CreateYBillboardMaterial(Material shared, Mesh mesh = null)
        {
            if (shared == null)
                return null;
            Material inst = Object.Instantiate(shared);
            bool cross = IsCrossBillboardMesh(mesh);
            inst.name = shared.name + (cross ? " (CrossBillboard)" : " (YBillboard)");

            if (inst.HasProperty(OilCrossBillboardId))
                inst.SetFloat(OilCrossBillboardId, cross ? 1f : 0f);
            if (inst.HasProperty(OilYBillboardId))
                inst.SetFloat(OilYBillboardId, cross ? 0f : 1f);
            // Dual-card / thin impostors: never lose the facing side to back-face cull.
            if (inst.HasProperty(CullId))
                inst.SetFloat(CullId, (float)CullMode.Off);

            inst.enableInstancing = true;
            return inst;
        }

        /// <summary>Swap LOD_Billboard MeshRenderer materials to camera-facing instances.</summary>
        public static void ApplyYBillboardToLodBillboardChildren(GameObject instance)
        {
            if (instance == null)
                return;
            Transform bb = FindChildNamed(instance.transform, "LOD_Billboard");
            if (bb == null)
                return;
            var renderers = bb.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer mr = renderers[i];
                if (mr == null || mr.sharedMaterial == null)
                    continue;
                MeshFilter filter = mr.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                Material[] shared = mr.sharedMaterials;
                if (shared == null || shared.Length == 0)
                    continue;
                var copies = new Material[shared.Length];
                for (int m = 0; m < shared.Length; m++)
                    copies[m] = CreateYBillboardMaterial(shared[m], mesh);
                mr.sharedMaterials = copies;
            }
        }

        /// <summary>
        /// Lightweight proxy: single MeshFilter/MeshRenderer with camera-facing material.
        /// No LODGroup / bark / leaf.
        /// </summary>
        public static GameObject BuildBillboardProxy(
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
            mr.sharedMaterial = CreateYBillboardMaterial(sharedMat, mesh);
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go;
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
