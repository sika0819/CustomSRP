using System.IO;
using UnityEditor;
using UnityEngine;

namespace CustomSRP.Editor
{
    /// <summary>
    /// When moorea_heightmap.raw is reimported, push heights into MooreaTerrainData.
    /// </summary>
    public sealed class MooreaHeightmapPostprocessor : AssetPostprocessor
    {
        const string RawPath = "Assets/Terrain/Moorea/moorea_heightmap.raw";
        const string ApplyFlag =
            TestSceneUtility.Root + "/Editor/.force-apply-MooreaHeightmap";

        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            for (int i = 0; i < importedAssets.Length; i++)
            {
                if (importedAssets[i] != RawPath)
                {
                    continue;
                }

                const string SkipApply =
                    TestSceneUtility.Root + "/Editor/.skip-apply-MooreaHeightmap";
                // Export menu writes RAW FROM terrain — do not push it back.
                if (File.Exists(SkipApply))
                {
                    File.Delete(SkipApply);
                    return;
                }

                // Defer: TerrainData write during import is unsafe.
                if (!File.Exists(ApplyFlag))
                {
                    File.WriteAllText(ApplyFlag, "raw");
                }

                EditorApplication.delayCall += () =>
                {
                    if (!File.Exists(ApplyFlag))
                    {
                        return;
                    }

                    File.Delete(ApplyFlag);
                    CreateMooreaTerrainScene.ApplyHeightmapInPlace();
                };
                return;
            }
        }
    }
}
