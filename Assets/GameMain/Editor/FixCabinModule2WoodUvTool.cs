#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// TAI_Cabin_01A_Module_2 是沿 Z 的水平圆木，不是立板墙。
    /// 箱式/按面换轴投影会在 LOD2 低模圆角上撕出花斑侧墙。
    /// 这里统一烙 YZ 平面：U=Z、V=Y（×0.42），仅用 Nx 符号翻背面。
    /// 菜单: Window/Game/Fix Cabin Module_2 Wood UV
    /// 触发: Temp/run_fix_cabin_module2_wood_uv
    /// </summary>
    public static class FixCabinModule2WoodUvTool
    {
        const string TriggerPath = "Temp/run_fix_cabin_module2_wood_uv";
        const float UvScale = 0.42f;

        static readonly string[] MeshPaths =
        {
            "Assets/GameMain/Environment/Models/WoodUV/TAI_Cabin_01A_Module_2_LOD1_WoodUV.asset",
            "Assets/GameMain/Environment/Models/WoodUV/TAI_Cabin_01A_Module_2_LOD2_WoodUV.asset",
        };

        static bool s_Watching;

        [InitializeOnLoadMethod]
        static void Boot()
        {
            if (s_Watching)
                return;
            s_Watching = true;
            EditorApplication.update += WatchTrigger;
            EditorApplication.delayCall += TryRunTrigger;
        }

        static void WatchTrigger()
        {
            if (!File.Exists(TriggerPath))
                return;
            TryRunTrigger();
        }

        static void TryRunTrigger()
        {
            if (!File.Exists(TriggerPath))
                return;
            try { File.Delete(TriggerPath); }
            catch { return; }

            try
            {
                Debug.Log($"[FixCabinWoodUV] OK\n{FixAll()}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[FixCabinWoodUV] Failed: {ex}");
            }
        }

        [MenuItem("Window/Game/Fix Cabin Module_2 Wood UV")]
        public static void MenuFix()
        {
            EditorUtility.DisplayDialog("Fix Cabin Module_2 Wood UV", FixAll(), "OK");
        }

        public static string FixAll()
        {
            var sb = new System.Text.StringBuilder();
            foreach (string path in MeshPaths)
            {
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (mesh == null)
                {
                    sb.AppendLine($"missing {path}");
                    continue;
                }

                ReprojectPlanarYZ(mesh);
                EditorUtility.SetDirty(mesh);
                sb.AppendLine($"{mesh.name}: verts={mesh.vertexCount} planarYZ ok");
            }

            AssetDatabase.SaveAssets();
            return sb.ToString();
        }

        static void ReprojectPlanarYZ(Mesh mesh)
        {
            Vector3[] pos = mesh.vertices;
            Vector3[] nrm = mesh.normals;
            var uv = new Vector2[pos.Length];
            for (int i = 0; i < pos.Length; i++)
            {
                float sgn = nrm[i].x > -0.01f ? 1f : -1f;
                uv[i] = new Vector2(pos[i].z * sgn, pos[i].y) * UvScale;
            }

            mesh.uv = uv;
            mesh.RecalculateTangents();
        }
    }
}
#endif
