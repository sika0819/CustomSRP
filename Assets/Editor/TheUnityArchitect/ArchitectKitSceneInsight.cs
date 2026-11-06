using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace UnityArchitect.Editor.Tools
{
    /// <summary>
    /// Architect Kit: Scene Insight
    /// Generates a full scene hierarchy dump for AI analysis or technical debugging.
    /// </summary>
    public class ArchitectKitSceneInsight : EditorWindow
    {
        private const string MenuPath = "Architect Kit/Tools/Dump Scene Insight";

        [MenuItem(MenuPath)]
        public static void DumpScene()
        {
            var sceneObjects = new List<GameObjectData>();
            var allObjects = Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var go in allObjects)
            {
                if (go.transform.parent == null)
                {
                    sceneObjects.Add(ProcessObject(go));
                }
            }

            string json = JsonConvert.SerializeObject(sceneObjects, Formatting.Indented);
            string fileName = $"SceneDump_{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}_{System.DateTime.Now:yyyyMMdd_HHmmss}.json";
            string path = Path.Combine(Application.dataPath, "..", fileName);

            File.WriteAllText(path, json);

            Debug.Log($"<b><color=#4CAF50>[Architect Kit]</color></b> Scene exported successfully to: <color=white>{path}</color>");
            EditorUtility.RevealInFinder(path);
        }

        private static GameObjectData ProcessObject(GameObject go)
        {
            var data = new GameObjectData
            {
                Name = go.name,
                EntityId = go.GetEntityId().ToString(),
                Tag = go.tag,
                Layer = LayerMask.LayerToName(go.layer),
                IsActive = go.activeInHierarchy,
                Position = go.transform.position,
                Rotation = go.transform.eulerAngles,
                Components = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name)
                    .ToList()
            };

            if (go.transform.childCount > 0)
            {
                data.Children = new List<GameObjectData>();
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    data.Children.Add(ProcessObject(go.transform.GetChild(i).gameObject));
                }
            }

            return data;
        }

        [System.Serializable]
        public class GameObjectData
        {
            public string Name;
            public string EntityId;
            public string Tag;
            public string Layer;
            public bool IsActive;
            public Vector3 Position;
            public Vector3 Rotation;
            public List<string> Components;
            public List<GameObjectData> Children;
        }
    }
}
