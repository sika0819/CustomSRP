using UnityEngine;

namespace Game
{
    /// <summary>
    /// ShiTou 碰撞：Cliff/Cave/Ramp/Spike/Large 按名；其余仅世界尺寸够大才补碰撞体。
    /// 预制体已有 MeshCollider 时保留三角网格（勿降成 Box，悬崖轮廓会穿模）。
    /// 仅在无碰撞体且 mesh 不可用于 PhysX 时才退回 BoxCollider。
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
                    if (mesh == null)
                    {
                        MeshFilter mf = mc.GetComponent<MeshFilter>();
                        mesh = mf != null ? mf.sharedMesh : null;
                        if (mesh != null)
                            mc.sharedMesh = mesh;
                    }

                    if (mesh == null)
                    {
                        ReplaceWithBoxCollider(mc.gameObject, new Bounds(Vector3.zero, Vector3.one));
                        Object.Destroy(mc);
                        continue;
                    }

                    // 已有三角碰撞：保持 MeshCollider。运行时改 cookingOptions 会强制 recook，
                    // 真机非可读 / 无 *_Collision 时会把有效预烘焙碰撞弄坏。
                    mc.convex = false;
                    if (CanRecookMeshCollider(mesh) && mc.cookingOptions != LeanCooking)
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
                // immediately — only do that for readable / *_Collision meshes.
                if (CanRecookMeshCollider(mesh))
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
        /// 运行时新建 / recook MeshCollider 需要 CPU 可读 mesh。
        /// Android AssetBundle 上渲染 FBX 的 isReadable 不可靠，故真机要求 *_Collision。
        /// </summary>
        static bool CanRecookMeshCollider(Mesh mesh)
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
