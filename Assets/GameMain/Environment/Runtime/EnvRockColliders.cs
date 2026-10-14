using UnityEngine;

namespace Game
{
    /// <summary>
    /// ShiTou 碰撞：Cliff/Cave/Ramp/Spike/Large 按名；其余仅世界尺寸够大才补碰撞体。
    /// MeshCollider 仅在 sharedMesh.isReadable（或已预烘焙可读 Collision mesh）时使用；
    /// 否则用 BoxCollider，避免真机 "mesh has been marked as non-accessible"。
    /// </summary>
    public static class EnvRockColliders
    {
        // CookForFasterSimulation | EnableMeshCleaning | UseFastMidphase (no WeldColocatedVertices).
        const MeshColliderCookingOptions LeanCooking =
            MeshColliderCookingOptions.CookForFasterSimulation
            | MeshColliderCookingOptions.EnableMeshCleaning
            | MeshColliderCookingOptions.UseFastMidphase;

        public static void Ensure(GameObject go, string prefabName)
        {
            if (go == null || !EnvStreamingConfig.ShouldHaveRockCollider(prefabName, go))
                return;

            MeshCollider[] existing = go.GetComponentsInChildren<MeshCollider>(true);
            if (existing != null && existing.Length > 0)
            {
                for (int i = 0; i < existing.Length; i++)
                {
                    MeshCollider mc = existing[i];
                    if (mc == null)
                        continue;

                    Mesh mesh = mc.sharedMesh;
                    if (mesh == null || !CanUseMeshCollider(mesh))
                    {
                        Bounds b = mesh != null ? mesh.bounds : new Bounds(Vector3.zero, Vector3.one);
                        ReplaceWithBoxCollider(mc.gameObject, b);
                        Object.Destroy(mc);
                        continue;
                    }

                    mc.convex = false;
                    mc.cookingOptions = LeanCooking;
                }

                return;
            }

            MeshFilter[] filters = go.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                if (mf == null || mf.sharedMesh == null)
                    continue;

                Mesh mesh = mf.sharedMesh;
                // AddComponent<MeshCollider>() auto-binds MeshFilter.sharedMesh and cooks
                // immediately — only do that for pre-baked readable *_Collision meshes.
                // Non-readable / render FBX → BoxCollider (avoids device PhysX spam).
                if (CanUseMeshCollider(mesh))
                {
                    MeshCollider mc = mf.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mesh;
                    mc.convex = false;
                    mc.cookingOptions = LeanCooking;
                }
                else
                {
                    ReplaceWithBoxCollider(mf.gameObject, mesh.bounds);
                }
            }
        }

        /// <summary>
        /// MeshCollider + LeanCooking needs a CPU-readable mesh. Prefab bake writes
        /// *_Collision assets; render FBX often reports isReadable inconsistently on
        /// Android AssetBundles, so require the Collision suffix on device.
        /// </summary>
        static bool CanUseMeshCollider(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable)
                return false;
#if UNITY_ANDROID && !UNITY_EDITOR
            return mesh.name.EndsWith("_Collision", System.StringComparison.Ordinal);
#else
            return true;
#endif
        }

        static void ReplaceWithBoxCollider(GameObject host, Bounds localMeshBounds)
        {
            if (host == null)
                return;

            BoxCollider box = host.GetComponent<BoxCollider>();
            if (box == null)
                box = host.AddComponent<BoxCollider>();

            box.center = localMeshBounds.center;
            box.size = localMeshBounds.size;
            box.isTrigger = false;
        }
    }
}
