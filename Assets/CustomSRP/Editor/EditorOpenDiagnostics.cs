using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Batch-mode helpers: Unity -batchmode -executeMethod CustomSRP.Editor.EditorOpenDiagnostics.LoadMooreaAndExit
    /// </summary>
    public static class EditorOpenDiagnostics
    {
        static int delayTicksLeft;

        public static void LoadMooreaAndExit()
        {
            EditorSceneManager.OpenScene("Assets/CustomSRP/Scenes/MooreaTerrain.unity");
            Debug.Log("[CustomSRP] MooreaTerrain scene loaded in batch mode.");
            // Pump delayCall a few times — OilSkyboxTime used to stack-overflow here.
            delayTicksLeft = 8;
            EditorApplication.delayCall += TickThenExit;
        }

        static void TickThenExit()
        {
            delayTicksLeft--;
            if (delayTicksLeft > 0)
            {
                EditorApplication.delayCall += TickThenExit;
                return;
            }

            Debug.Log("[CustomSRP] MooreaTerrain survived delayCall ticks.");
            EditorApplication.Exit(0);
        }


        public static void ApplySkyPeriodsAndExit()
        {
            EditorSceneManager.OpenScene("Assets/CustomSRP/Scenes/MooreaTerrain.unity");
            var sky = Object.FindAnyObjectByType<CustomSRP.OilSkyboxTime>();
            if (sky == null)
            {
                Debug.LogError("[CustomSRP] No OilSkyboxTime");
                EditorApplication.Exit(1);
                return;
            }
            foreach (float hours in new[] { 0f, 6f, 12f, 18f, 12f })
            {
                sky.ApplyEditorHours(hours);
            }
            Debug.Log("[CustomSRP] ApplyEditorHours x5 OK");
            EditorApplication.Exit(0);
        }

        public static void LoadEmptyAndExit()
        {
            EditorSceneManager.OpenScene("Assets/CustomSRP/Scenes/Empty.unity");
            Debug.Log("[CustomSRP] Empty scene loaded in batch mode.");
            EditorApplication.Exit(0);
        }
    }
}
