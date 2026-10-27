using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game
{
    /// <summary>
    /// Far-tree cross-billboard helpers: extract LOD_Billboard mesh/mat,
    /// enable GPU front/side snap + yaw-to-camera (Boluo Cross path).
    /// Contour meshes hug atlas alpha so OilNPR can skip cutout black fringe.
    /// </summary>
    public static class EnvTreeBillboardUtil
    {
        public static readonly int OilYBillboardId = Shader.PropertyToID("_OilYBillboard");
        public static readonly int OilCrossBillboardId = Shader.PropertyToID("_OilCrossBillboard");
        public static readonly int CullId = Shader.PropertyToID("_Cull");
        public static readonly int ReceiveShadowsId = Shader.PropertyToID("_ReceiveShadows");
        public static readonly int ShadowsId = Shader.PropertyToID("_Shadows");

        /// <summary>OilNPR KeywordEnum(On, Clip, Dither, Off).</summary>
        const float ShadowsOff = 3f;

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

        /// <summary>
        /// Runtime material instance: Cross front/side snap + yaw to camera.
        /// Does not dirty the shared asset. Far impostors skip shadow cast/receive.
        /// </summary>
        static readonly Dictionary<EntityId, Material> CrossBillboardCache = new Dictionary<EntityId, Material>(8);

        public static Material CreateCrossBillboardMaterial(Material shared)
        {
            if (shared == null)
                return null;
            EntityId key = shared.GetEntityId();
            if (CrossBillboardCache.TryGetValue(key, out Material cached) && cached != null)
                return cached;

            Material inst = Object.Instantiate(shared);
            inst.name = shared.name + " (CrossBillboard)";

            if (inst.HasProperty(OilYBillboardId))
                inst.SetFloat(OilYBillboardId, 0f);
            if (inst.HasProperty(OilCrossBillboardId))
                inst.SetFloat(OilCrossBillboardId, 1f);
            // Thin cards: keep facing side if snap briefly mis-predicts.
            if (inst.HasProperty(CullId))
                inst.SetFloat(CullId, (float)CullMode.Off);

            ApplyNoShadows(inst);
            // 这份给近树 LOD_Billboard 的 MeshRenderer，走 SRP Batcher。
            // RenderMeshInstanced 另拷材质再开 Instancing，两边不能共用。
            inst.enableInstancing = false;
            CrossBillboardCache[key] = inst;
            return inst;
        }

        /// <summary>Deprecated alias — prefer <see cref="CreateCrossBillboardMaterial"/>.</summary>
        public static Material CreateYBillboardMaterial(Material shared) =>
            CreateCrossBillboardMaterial(shared);

        /// <summary>OilNPR: Shadows=Off + disable ShadowCaster; ReceiveShadows off.</summary>
        public static void ApplyNoShadows(Material mat)
        {
            if (mat == null)
                return;
            if (mat.HasProperty(ShadowsId))
            {
                mat.SetFloat(ShadowsId, ShadowsOff);
                mat.DisableKeyword("_SHADOWS_CLIP");
                mat.DisableKeyword("_SHADOWS_DITHER");
                mat.SetShaderPassEnabled("ShadowCaster", false);
            }

            if (mat.HasProperty(ReceiveShadowsId))
            {
                mat.SetFloat(ReceiveShadowsId, 0f);
                mat.DisableKeyword("_RECEIVE_SHADOWS");
            }
        }

        static void ApplyNoShadows(MeshRenderer mr)
        {
            if (mr == null)
                return;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        /// <summary>
        /// Full-prefab LOD_Billboard uses the same contour mesh as far proxies.
        /// Keep it off the planar mirror; nearby bark/leaf still reflect.
        /// </summary>
        public static void ExcludeLodBillboardFromReflection(GameObject instance)
        {
            if (instance == null)
                return;
            Transform bb = FindChildNamed(instance.transform, "LOD_Billboard");
            if (bb == null)
                return;
            EnvStreamerReflectionSkip.ApplySubtree(bb);
        }

        /// <summary>Swap LOD_Billboard mats to Cross camera-facing instances (keep contour mesh).</summary>
        public static void ApplyCrossBillboardToLodBillboardChildren(GameObject instance)
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

                Material[] shared = mr.sharedMaterials;
                if (shared == null || shared.Length == 0)
                    continue;
                var copies = new Material[shared.Length];
                for (int m = 0; m < shared.Length; m++)
                    copies[m] = CreateCrossBillboardMaterial(shared[m]);
                mr.sharedMaterials = copies;
                ApplyNoShadows(mr);
            }
        }

        /// <summary>Deprecated alias — prefer <see cref="ApplyCrossBillboardToLodBillboardChildren"/>.</summary>
        public static void ApplyYBillboardToLodBillboardChildren(GameObject instance) =>
            ApplyCrossBillboardToLodBillboardChildren(instance);

        /// <summary>
        /// Lightweight proxy: single MeshFilter/MeshRenderer with Cross material.
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
            mr.sharedMaterial = CreateCrossBillboardMaterial(sharedMat);
            ApplyNoShadows(mr);
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
