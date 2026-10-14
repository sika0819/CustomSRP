using UnityEngine;

namespace Game
{
    /// <summary>
    /// Mountain 等远景地标：保证 MeshCollider 有 mesh，且挂在与渲染同缩放的节点上。
    /// （根节点 scale=5、子 LOD scale≈0.13 时，根上的 collider 会与视觉错位或引用丢失。）
    /// </summary>
    public static class EnvLandmarkColliders
    {
        const MeshColliderCookingOptions LeanCooking =
            MeshColliderCookingOptions.CookForFasterSimulation
            | MeshColliderCookingOptions.EnableMeshCleaning
            | MeshColliderCookingOptions.UseFastMidphase;

        public static void Ensure(GameObject go, string prefabName)
        {
            if (go == null || !EnvStreamingConfig.IsFarFieldLandmark(prefabName))
                return;

            MeshFilter bestMf = FindBestRenderFilter(go);
            if (bestMf == null || bestMf.sharedMesh == null)
                return;

            // Drop broken / mis-scaled colliders (null mesh, or not on the render host).
            MeshCollider[] existing = go.GetComponentsInChildren<MeshCollider>(true);
            bool hasGood = false;
            for (int i = 0; i < existing.Length; i++)
            {
                MeshCollider mc = existing[i];
                if (mc == null)
                    continue;

                if (mc.sharedMesh != null && mc.gameObject == bestMf.gameObject)
                {
                    mc.convex = false;
                    mc.cookingOptions = LeanCooking;
                    mc.enabled = true;
                    hasGood = true;
                    continue;
                }

                Object.Destroy(mc);
            }

            if (hasGood)
                return;

            Mesh mesh = bestMf.sharedMesh;
            if (!mesh.isReadable)
            {
                // Non-readable render mesh: approximate with box on the render host.
                Bounds b = mesh.bounds;
                BoxCollider box = bestMf.GetComponent<BoxCollider>();
                if (box == null)
                    box = bestMf.gameObject.AddComponent<BoxCollider>();
                box.center = b.center;
                box.size = b.size;
                box.isTrigger = false;
                return;
            }

            MeshCollider created = bestMf.gameObject.AddComponent<MeshCollider>();
            created.sharedMesh = mesh;
            created.convex = false;
            created.cookingOptions = LeanCooking;
            created.enabled = true;
        }

        static MeshFilter FindBestRenderFilter(GameObject go)
        {
            MeshFilter[] filters = go.GetComponentsInChildren<MeshFilter>(true);
            MeshFilter best = null;
            int bestVerts = 0;
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                Mesh mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null || mesh.vertexCount <= bestVerts)
                    continue;
                if (mesh.name.EndsWith("_Collision", System.StringComparison.Ordinal))
                    continue;
                best = mf;
                bestVerts = mesh.vertexCount;
            }

            return best;
        }
    }
}
