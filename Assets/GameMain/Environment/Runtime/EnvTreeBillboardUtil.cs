using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game
{
    /// <summary>
    /// Far-tree single-card billboard helpers: extract LOD_Billboard mesh/mat,
    /// enable GPU camera-facing (Y-yaw), build lightweight proxy GOs.
    /// Legacy 8-vert front/side crosses are stripped to the front card at runtime.
    /// </summary>
    public static class EnvTreeBillboardUtil
    {
        public static readonly int OilYBillboardId = Shader.PropertyToID("_OilYBillboard");
        public static readonly int OilCrossBillboardId = Shader.PropertyToID("_OilCrossBillboard");
        public static readonly int CullId = Shader.PropertyToID("_Cull");
        public static readonly int ReceiveShadowsId = Shader.PropertyToID("_ReceiveShadows");
        public static readonly int ShadowsId = Shader.PropertyToID("_Shadows");

        /// <summary>Legacy MatchBillboardHeight front/side atlas crosses.</summary>
        const int CrossBillboardVertCount = 8;

        /// <summary>OilNPR KeywordEnum(On, Clip, Dither, Off).</summary>
        const float ShadowsOff = 3f;

        static readonly Dictionary<EntityId, Mesh> SingleCardBySource = new();

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
            mesh = EnsureSingleCardMesh(filter.sharedMesh);
            sharedMat = mr.sharedMaterial;
            // Expand XZ so frustum culling survives GPU yaw (card swings around Y).
            Bounds b = mesh.bounds;
            float horiz = Mathf.Max(b.extents.x, b.extents.z);
            localBounds = new Bounds(b.center, new Vector3(horiz * 2f, b.size.y, horiz * 2f));
            return true;
        }

        /// <summary>
        /// 8-vert front/side crosses → keep front quad only (indices 0..5 / verts 0..3).
        /// Other meshes returned unchanged.
        /// </summary>
        public static Mesh EnsureSingleCardMesh(Mesh source)
        {
            if (source == null)
                return null;
            if (source.vertexCount != CrossBillboardVertCount)
                return source;

            EntityId id = source.GetEntityId();
            if (SingleCardBySource.TryGetValue(id, out Mesh cached) && cached != null)
                return cached;

            if (!source.isReadable)
            {
                Debug.LogWarning(
                    "[EnvTreeBillboard] cross mesh not readable, cannot strip to single card: " +
                    source.name);
                return source;
            }

            Vector3[] srcV = source.vertices;
            Vector3[] srcN = source.normals;
            Vector2[] srcUv = source.uv;
            int[] srcT = source.triangles;
            if (srcV == null || srcV.Length < 4 || srcT == null || srcT.Length < 6)
                return source;

            var dstV = new Vector3[4];
            var dstN = new Vector3[4];
            var dstUv = new Vector2[4];
            for (int i = 0; i < 4; i++)
            {
                dstV[i] = srcV[i];
                dstN[i] = srcN != null && srcN.Length > i ? srcN[i] : Vector3.back;
                dstUv[i] = srcUv != null && srcUv.Length > i ? srcUv[i] : Vector2.zero;
            }

            var dstT = new int[6];
            for (int i = 0; i < 6; i++)
                dstT[i] = srcT[i];

            var mesh = new Mesh
            {
                name = source.name + " (SingleCard)",
                hideFlags = HideFlags.DontSave
            };
            mesh.vertices = dstV;
            mesh.normals = dstN;
            mesh.uv = dstUv;
            mesh.triangles = dstT;
            mesh.RecalculateBounds();
            mesh.UploadMeshData(false);

            SingleCardBySource[id] = mesh;
            return mesh;
        }

        /// <summary>
        /// Runtime material instance: continuous Y-yaw to face camera. Does not dirty the asset.
        /// Always single-card (<c>_OilYBillboard</c>); Cross path stays off.
        /// Defaults: no shadow cast / receive (far impostors skip atlas + PCF).
        /// </summary>
        public static Material CreateYBillboardMaterial(Material shared)
        {
            if (shared == null)
                return null;
            Material inst = Object.Instantiate(shared);
            inst.name = shared.name + " (YBillboard)";

            if (inst.HasProperty(OilCrossBillboardId))
                inst.SetFloat(OilCrossBillboardId, 0f);
            if (inst.HasProperty(OilYBillboardId))
                inst.SetFloat(OilYBillboardId, 1f);
            // Thin impostors: never lose the facing side to back-face cull.
            if (inst.HasProperty(CullId))
                inst.SetFloat(CullId, (float)CullMode.Off);

            ApplyNoShadows(inst);
            inst.enableInstancing = true;
            return inst;
        }

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

        /// <summary>Swap LOD_Billboard to single-card mesh + camera-facing material instances.</summary>
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
                if (filter != null && filter.sharedMesh != null)
                {
                    Mesh single = EnsureSingleCardMesh(filter.sharedMesh);
                    if (single != filter.sharedMesh)
                        filter.sharedMesh = single;
                }

                Material[] shared = mr.sharedMaterials;
                if (shared == null || shared.Length == 0)
                    continue;
                var copies = new Material[shared.Length];
                for (int m = 0; m < shared.Length; m++)
                    copies[m] = CreateYBillboardMaterial(shared[m]);
                mr.sharedMaterials = copies;
                ApplyNoShadows(mr);
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
            mr.sharedMaterial = CreateYBillboardMaterial(sharedMat);
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
