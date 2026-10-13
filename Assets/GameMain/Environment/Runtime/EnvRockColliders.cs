using UnityEngine;

namespace Game
{
    /// <summary>
    /// Only large ShiTou (cliff/cave/ramp/spike) get MeshCollider.
    /// Cooking uses the cheapest static flags that still query quickly.
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
            if (go == null || !EnvStreamingConfig.IsLargeRockPrefab(prefabName))
                return;

            MeshCollider[] existing = go.GetComponentsInChildren<MeshCollider>(true);
            if (existing != null && existing.Length > 0)
            {
                for (int i = 0; i < existing.Length; i++)
                {
                    MeshCollider mc = existing[i];
                    if (mc == null)
                        continue;
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

                MeshCollider mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = false;
                mc.cookingOptions = LeanCooking;
            }
        }
    }
}
