#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// <c>SM_Env_Ground_Cliff_Large_01_Collision</c> 曾按 FBX 未缩放顶点烘焙（半边长约 2000m），
    /// 而 ModelImporter.globalScale=0.01 的渲染网格约 20m。差正好 100×。
    /// NavMesh 用 PhysicsColliders，会把这块崖当成百倍大障碍。
    /// </summary>
    public static class FixCliffLargeCollisionScale
    {
        const string PrefabPath =
            "Assets/GameMain/Environment/Prefabs/DaoYu1/ShiTou/SM_Env_Ground_Cliff_Large_01.prefab";
        const string CollisionPath =
            "Assets/GameMain/Environment/Models/Collision/SM_Env_Ground_Cliff_Large_01_Collision.asset";

        [MenuItem("GameMain/Boluo/Fix Cliff Large Collision Scale + Rebake Nav")]
        public static void MenuFix()
        {
            EditorUtility.DisplayDialog("Fix Cliff Large Collision", FixAndRebakeNav(), "OK");
        }

        public static string FixAndRebakeNav()
        {
            if (!RebakeCollision(out string bakeMsg))
                return bakeMsg;

            IslandNavBakeSceneBuilder.BuildScene();
            bool ok = IslandNavBakeSceneBuilder.BakeNavMesh(out string navMsg);
            return bakeMsg + "\n" + (ok ? navMsg : "Nav bake failed: " + navMsg);
        }

        static bool RebakeCollision(out string message)
        {
            Mesh oldCol = AssetDatabase.LoadAssetAtPath<Mesh>(CollisionPath);
            float before = oldCol != null ? MaxExtent(oldCol.bounds) : -1f;

            int n = BakeEnvMeshColliders.BakePrefabPath(PrefabPath);
            Mesh newCol = AssetDatabase.LoadAssetAtPath<Mesh>(CollisionPath);
            if (newCol == null)
            {
                message = "Collision asset missing after BakePrefabPath";
                return false;
            }

            float after = MaxExtent(newCol.bounds);
            // 若渲染源仍是 cm（重烤后仍 >200），再 ×0.01。
            if (after > 200f)
            {
                ScaleMeshInPlace(newCol, 0.01f);
                after = MaxExtent(newCol.bounds);
                message =
                    $"Scaled collision ×0.01 (was still cm).\n" +
                    $"before={before:F1} after={after:F1} bakeCount={n}";
            }
            else
            {
                message =
                    $"Rebaked collision from render mesh.\n" +
                    $"before={before:F1} after={after:F1} bakeCount={n}";
            }

            AssetDatabase.SaveAssets();
            return after < 200f;
        }

        static void ScaleMeshInPlace(Mesh mesh, float s)
        {
            Vector3[] v = mesh.vertices;
            for (int i = 0; i < v.Length; i++)
                v[i] *= s;
            mesh.vertices = v;
            mesh.RecalculateBounds();
            try
            {
                Physics.BakeMesh(mesh.GetEntityId(), false);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[FixCliffLarge] Physics.BakeMesh: " + ex.Message);
            }

            EditorUtility.SetDirty(mesh);
        }

        static float MaxExtent(Bounds b) =>
            Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z));
    }
}
#endif
