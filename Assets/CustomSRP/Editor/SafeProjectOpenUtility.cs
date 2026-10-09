using System.IO;
using UnityEditor;
using UnityEngine;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Hub / crash recovery helpers (stale lock, default scene).
    /// </summary>
    public static class SafeProjectOpenUtility
    {
        const string EmptyScene = "Assets/CustomSRP/Scenes/Empty.unity";

        [MenuItem("CustomSRP/Use Empty Scene On Next Editor Launch")]
        public static void PreferEmptySceneOnNextLaunch()
        {
            string library = Path.Combine(Application.dataPath, "..", "Library");
            string setupPath = Path.Combine(library, "LastSceneManagerSetup.txt");
            File.WriteAllText(
                setupPath,
                "sceneSetups:\n" +
                $"- path: {EmptyScene}\n" +
                "  isLoaded: 1\n" +
                "  isActive: 1\n" +
                "  isSubScene: 0\n");
            Debug.Log(
                "[CustomSRP] Next launch will open Empty.unity instead of MooreaTerrain " +
                "(avoids heavy terrain until you open Moorea manually).");
        }

        [MenuItem("CustomSRP/Clear Stale Unity Lock File")]
        public static void ClearStaleLockFile()
        {
            string lockPath = Path.Combine(Application.dataPath, "..", "Temp", "UnityLockfile");
            if (!File.Exists(lockPath))
            {
                Debug.Log("[CustomSRP] No Temp/UnityLockfile — nothing to clear.");
                return;
            }

            if (System.Diagnostics.Process.GetProcessesByName("Unity").Length > 0)
            {
                Debug.LogWarning(
                    "[CustomSRP] Unity Editor is running; quit it before deleting the lock file.");
                return;
            }

            File.Delete(lockPath);
            Debug.Log("[CustomSRP] Removed stale Temp/UnityLockfile. Try opening the project from Hub again.");
        }
    }
}
