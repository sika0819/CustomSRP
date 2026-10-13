#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace CustomSRP.Editor
{
    /// <summary>
    /// 修复树木网格的 UV 映射问题，将 UV 重新生成为圆柱形贴图映射。
    /// 圆柱方向沿着树干方向（Y 轴）。
    /// </summary>
    public static class CylindricalUVFixUtility
    {
        [MenuItem("Assets/Fix UV to Cylindrical", true)]
        private static bool ValidateFixUV()
        {
            return Selection.activeObject is Mesh;
        }

        [MenuItem("Assets/Fix UV to Cylindrical")]
        public static void FixUVFromSelection()
        {
            var mesh = Selection.activeObject as Mesh;
            if (mesh == null)
            {
                Debug.LogError("[CylindricalUVFix] No mesh selected.");
                return;
            }

            FixMeshCylindricalUV(mesh);
        }

        [MenuItem("CustomSRP/Fix All Tree Bark UVs")]
        public static void FixAllTreeBarkUVs()
        {
            var guids = AssetDatabase.FindAssets("t:Mesh", new[] { "Assets/GameMain/Environment/Models" });
            int fixedCount = 0;

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (mesh == null) continue;

                // 只处理树皮/树干相关的网格
                var meshName = mesh.name.ToLower();
                if (!meshName.Contains("bark") && !meshName.Contains("trunk") && 
                    !meshName.Contains("dead") && !meshName.Contains("tree") &&
                    !meshName.Contains("maple") && !meshName.Contains("oak") &&
                    !meshName.Contains("larch") && !meshName.Contains("birch"))
                {
                    continue;
                }

                FixMeshCylindricalUV(mesh);
                EditorUtility.SetDirty(mesh);
                fixedCount++;
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[CylindricalUVFix] Fixed UV for {fixedCount} tree bark meshes.");
        }

        [MenuItem("CustomSRP/Fix Maple 3 Dead UV")]
        public static void FixMaple3DeadUV()
        {
            const string meshPath = "Assets/GameMain/Environment/Models/Maple 3 Dead_LOD0.asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            
            if (mesh == null)
            {
                Debug.LogError($"[CylindricalUVFix] Mesh not found at path: {meshPath}");
                return;
            }

            FixMeshCylindricalUV(mesh);
            EditorUtility.SetDirty(mesh);
            AssetDatabase.SaveAssets();
            Debug.Log($"[CylindricalUVFix] Fixed UV for Maple 3 Dead_LOD0.");
        }

        public static void FixMeshCylindricalUV(Mesh mesh)
        {
            if (mesh == null)
            {
                Debug.LogError("[CylindricalUVFix] Mesh is null.");
                return;
            }

            Vector3[] vertices = mesh.vertices;
            Vector2[] uvs = new Vector2[vertices.Length];

            // 获取网格的包围盒，计算中心轴
            Bounds bounds = mesh.bounds;
            Vector3 center = bounds.center;
            
            // 圆柱轴心从底部到顶部（Y轴方向）
            float minY = bounds.min.y;
            float maxY = bounds.max.y;
            float heightRange = maxY - minY;
            
            if (heightRange < 0.0001f)
            {
                heightRange = 1f; // 防止除零
            }

            // 圆柱形 UV 生成
            // U: 基于顶点绕 Y 轴的角度 (0-1)
            // V: 基于顶点的高度 (0-1)
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 vertex = vertices[i];

                // 计算相对于包围盒中心的水平偏移
                float dx = vertex.x - center.x;
                float dz = vertex.z - center.z;

                // 计算角度 (0 到 2π)
                float angle = Mathf.Atan2(dz, dx);
                
                // 将角度归一化到 0-1 范围
                float u = (angle + Mathf.PI) / (2f * Mathf.PI);
                
                // 将高度归一化到 0-1 范围
                float v = (vertex.y - minY) / heightRange;

                uvs[i] = new Vector2(u, v);
            }

            // 应用新的 UV
            mesh.uv = uvs;

            // 如果有第二个 UV 通道，也一并处理（用于光照贴图等）
            if (mesh.uv2 != null && mesh.uv2.Length == vertices.Length)
            {
                mesh.uv2 = uvs;
            }

            Debug.Log($"[CylindricalUVFix] Applied cylindrical UV to mesh '{mesh.name}' ({vertices.Length} vertices).");
        }

        /// <summary>
        /// 高级版本：允许指定包围中心和处理模式
        /// </summary>
        public static void FixMeshCylindricalUVAdvanced(
            Mesh mesh, 
            Vector3 cylinderCenter, 
            Vector3 cylinderAxis,
            float radius = 0f,
            bool preserveOriginalUVScale = false)
        {
            if (mesh == null) return;

            Vector3[] vertices = mesh.vertices;
            Vector2[] uvs = new Vector2[vertices.Length];

            // 标准化轴向量
            cylinderAxis = cylinderAxis.normalized;

            // 计算最小/最大投影高度
            float minProj = float.MaxValue;
            float maxProj = float.MinValue;

            foreach (var vert in vertices)
            {
                float proj = Vector3.Dot(vert - cylinderCenter, cylinderAxis);
                minProj = Mathf.Min(minProj, proj);
                maxProj = Mathf.Max(maxProj, proj);
            }

            float heightRange = maxProj - minProj;
            if (heightRange < 0.0001f) heightRange = 1f;

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 vert = vertices[i];
                Vector3 toVertex = vert - cylinderCenter;

                // 投影到垂直于轴的平面
                float alongAxis = Vector3.Dot(toVertex, cylinderAxis);
                Vector3 perpVec = toVertex - alongAxis * cylinderAxis;

                float dist = perpVec.magnitude;
                if (dist < 0.0001f)
                {
                    dist = radius > 0 ? radius : 0.1f;
                }

                // 计算角度 (dx, dz 需要在 angle 计算之前声明)
                float dx = Vector3.Dot(perpVec, Vector3.right);
                float dz = Vector3.Dot(perpVec, Vector3.forward);
                float angle = Mathf.Atan2(dz, dx);

                float u = (angle + Mathf.PI) / (2f * Mathf.PI);
                float v = (alongAxis - minProj) / heightRange;

                if (preserveOriginalUVScale)
                {
                    // 可以在这里添加原始 UV 缩放逻辑
                    u *= 4f; // 默认缩放 4 倍
                }

                uvs[i] = new Vector2(u, v);
            }

            mesh.uv = uvs;
            Debug.Log($"[CylindricalUVFix] Applied advanced cylindrical UV to '{mesh.name}'.");
        }
    }
}
#endif
