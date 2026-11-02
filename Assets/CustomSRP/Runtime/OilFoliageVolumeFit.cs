using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// Per MeshRenderer: map oil leaf volume to that mesh's local Y bounds.
    /// LOD0 / LOD1 / LOD2… share the same crown→base band even when absolute
    /// OS.y ranges differ (short bushes + multi-mesh LODs).
    /// Skips billboard / non-OilFoliage materials.
    /// </summary>
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public sealed class OilFoliageVolumeFit : MonoBehaviour
    {
        static readonly int FoliageHeightId = Shader.PropertyToID("_FoliageHeight");
        static readonly int FoliageYMinId = Shader.PropertyToID("_FoliageYMin");
        static readonly int OilFoliageId = Shader.PropertyToID("_OilFoliage");

        static MaterialPropertyBlock _block;

        void OnEnable() => Apply();

#if UNITY_EDITOR
        void OnValidate() => Apply();
#endif

        public void Apply()
        {
            _block ??= new MaterialPropertyBlock();
            var renderers = GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
                ApplyRenderer(renderers[i]);
        }

        public static void ApplyOn(GameObject root)
        {
            if (root == null)
                return;

            var fit = root.GetComponent<OilFoliageVolumeFit>();
            if (fit == null)
                fit = root.AddComponent<OilFoliageVolumeFit>();
            fit.Apply();
        }

        static void ApplyRenderer(MeshRenderer renderer)
        {
            if (renderer == null)
                return;

            Material[] mats = renderer.sharedMaterials;
            if (mats == null || mats.Length == 0)
                return;

            bool foliage = false;
            for (int m = 0; m < mats.Length; m++)
            {
                Material mat = mats[m];
                if (mat == null || !mat.HasProperty(OilFoliageId))
                    continue;
                if (mat.GetFloat(OilFoliageId) > 0.5f)
                {
                    foliage = true;
                    break;
                }
            }

            if (!foliage)
                return;

            // Billboard path uses its own card OS.y; leave material defaults.
            for (int m = 0; m < mats.Length; m++)
            {
                Material mat = mats[m];
                if (mat == null)
                    continue;
                if (mat.HasProperty("_OilYBillboard") && mat.GetFloat("_OilYBillboard") > 0.5f)
                    return;
                if (mat.HasProperty("_OilCrossBillboard") && mat.GetFloat("_OilCrossBillboard") > 0.5f)
                    return;
            }

            var filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null)
                return;

            Bounds b = mesh.bounds;
            float yMin = b.min.y;
            float span = Mathf.Max(0.01f, b.size.y);

            renderer.GetPropertyBlock(_block);
            _block.SetFloat(FoliageYMinId, yMin);
            _block.SetFloat(FoliageHeightId, span);
            renderer.SetPropertyBlock(_block);
        }
    }
}
