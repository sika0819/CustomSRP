#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using CustomSRP;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Adds the rain system GameObject (OilRainManager + OilRainRenderHook +
    /// OilRainRippleManager + OilRainFlipbookDriver) as a child
    /// of IslandTerrain.prefab so it is instantiated at runtime with the island world.
    ///
    /// All rain components find OilRainSettings via OilRainSettings.Current
    /// (initialised by OilAtmosphereDriver.Awake()).
    ///
    /// Splashes are rendered by OilRainFlipbookDriver using a single GPU-instanced
    /// horizontal-quad draw call driven by a flipbook atlas (no ParticleSystem).
    ///
    /// Run once via: Assets > CustomSRP > Add Rain System to IslandTerrain Prefab
    /// </summary>
    public static class AddRainSystemToPrefab
    {
        const string TerrainPrefabPath = "Assets/AssetRaw/Island/IslandTerrain.prefab";
        const string SettingsAssetPath = "Assets/CustomSRP/Settings/OilRainSettings.asset";

        [MenuItem("CustomSRP/Add Rain System to IslandTerrain Prefab", true)]
        static bool ValidateMenu() =>
            AssetDatabase.LoadAssetAtPath<GameObject>(TerrainPrefabPath) != null;

        [MenuItem("CustomSRP/Add Rain System to IslandTerrain Prefab")]
        static void AddRainSystem()
        {
            var terrainPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(TerrainPrefabPath);
            if (terrainPrefab == null)
            {
                Debug.LogError("[AddRainSystem] IslandTerrain.prefab not found at " + TerrainPrefabPath);
                return;
            }

            var settings = AssetDatabase.LoadAssetAtPath<OilRainSettings>(SettingsAssetPath);
            if (settings == null)
            {
                // Self-heal: the on-disk asset is missing its real MonoScript GUID (placeholder
                // GUID or hand-rolled stub) so Unity can't resolve the type. Re-create it.
                Debug.LogWarning("[AddRainSystem] OilRainSettings asset missing or invalid at " +
                                 SettingsAssetPath + " — recreating it from script reference.");
                settings = ScriptableObject.CreateInstance<OilRainSettings>();
                AssetDatabase.CreateAsset(settings, SettingsAssetPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(SettingsAssetPath, ImportAssetOptions.ForceUpdate);

                settings = AssetDatabase.LoadAssetAtPath<OilRainSettings>(SettingsAssetPath);
                if (settings == null)
                {
                    Debug.LogError("[AddRainSystem] Failed to create OilRainSettings at " +
                                   SettingsAssetPath + " — aborting.");
                    return;
                }
            }

            // Load the prefab asset (not an instance) so we can modify it.
            var prefabRoot = PrefabUtility.LoadPrefabContents(TerrainPrefabPath);
            if (prefabRoot == null)
            {
                Debug.LogError("[AddRainSystem] Failed to load prefab contents.");
                return;
            }

            Transform existing = prefabRoot.transform.Find("OilRain");
            GameObject rainRoot;
            bool created = false;
            if (existing != null)
            {
                rainRoot = existing.gameObject;
                Debug.Log("[AddRainSystem] OilRain child exists — repairing missing components.");
            }
            else
            {
                rainRoot = new GameObject("OilRain");
                rainRoot.transform.SetParent(prefabRoot.transform, false);
                created = true;
            }

            // Order: OilRainManager first, then passive components.
            // Private [SerializeField] fields are assigned via SerializedObject below.
            var manager    = GetOrAdd<OilRainManager>(rainRoot);
            var renderHook = GetOrAdd<OilRainRenderHook>(rainRoot);
            var rippleMgr  = GetOrAdd<OilRainRippleManager>(rainRoot);
            var flipbook   = GetOrAdd<OilRainFlipbookDriver>(rainRoot);
            var streakQuad = GetOrAdd<OilRainStreakQuad>(rainRoot);

            // --- Wire inspector references ---
            // The rain MonoBehaviours keep their [SerializeField] fields private, so we go
            // through SerializedObject/SerializedProperty rather than direct field access
            // (which would not compile across the Editor / Runtime assembly boundary).
            AssignObject(manager,    "_settings",      settings);
            AssignObject(manager,    "_rippleManager", rippleMgr);
            AssignObject(manager,    "_renderHook",    renderHook);
            AssignObject(manager,    "_streakQuad",    streakQuad);
            // _autoFindInScene keeps its default value (true). Explicit refs above win.

            AssignObject(renderHook, "_settings",      settings);
            AssignObject(renderHook, "_rippleManager", rippleMgr);

            AssignObject(rippleMgr,  "_settings", settings);
            // Note: _rippleDiscPrefab is no longer used — ripples are fully shader-driven.

            AssignObject(flipbook,   "_settings",      settings);
            // _targetCamera resolves to Camera.main at runtime via OilRainFlipbookDriver.OnEnable.

            AssignObject(streakQuad, "_settings", settings);
            // Camera-space curtain attaches to Camera.main at runtime.

            // Save the modified prefab.
            PrefabUtility.SaveAsPrefabAsset(prefabRoot, TerrainPrefabPath);
            PrefabUtility.UnloadPrefabContents(prefabRoot);

            Debug.Log("[AddRainSystem] Rain system " + (created ? "added" : "repaired") +
                " on " + TerrainPrefabPath +
                "\n  Components: OilRainManager, OilRainRenderHook, OilRainRippleManager," +
                " OilRainFlipbookDriver, OilRainStreakQuad.");
        }

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }

        /// <summary>
        /// Assigns an Object reference to a private [SerializeField] property on a
        /// Component via Unity's serialization API (Editor only).
        /// </summary>
        static void AssignObject(Component target, string propertyPath, UnityEngine.Object value)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(propertyPath);
            if (prop == null)
            {
                Debug.LogError($"[AddRainSystem] SerializedObject for {target.GetType().Name} " +
                               $"has no property '{propertyPath}'.");
                return;
            }
            prop.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
#endif
