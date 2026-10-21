#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Play → wait for island → bake four island-wide cubemaps (06/12/18/00).
    /// Probe sits at the island center on the water plane. Water meshes stay hidden.
    /// Fragments under the water are clipped. Face size is <see cref="FaceSize"/>.
    /// </summary>
    [InitializeOnLoad]
    public static class OilWaterReflectBakeTool
    {
        const string Folder = "Assets/GameMain/Resources/IslandWaterProbes";
        const string SetPath = Folder + "/IslandWaterReflectionSet.asset";
        const string FlagRelPath = "Temp/bake_water_reflect_4.flag";
        const string SessionKey = "CustomSRP.WaterReflect.Pending";
        const string SessionPhase = "CustomSRP.WaterReflect.Phase";
        /// <summary>Face size of the island probe. Change this, then run the menu again.</summary>
        const int FaceSize = 32;
        const float WorldReadyTimeout = 120f;
        const float SettleSeconds = 1.2f;

        static readonly float[] Hours = { 6f, 12f, 18f, 0f };
        static readonly string[] SlotNames = { "Dawn", "Day", "Dusk", "Night" };

        static readonly int CustomFogEnabledId = Shader.PropertyToID("_CustomFogEnabled");
        static readonly int AtmosphereFogAmountId = Shader.PropertyToID("_OilAtmosphereFogAmount");
        static readonly int BakeClipOnId = Shader.PropertyToID("_OilBakeClipOn");
        static readonly int BakeClipYId = Shader.PropertyToID("_OilBakeClipY");

        static bool _busy;
        static bool _cancel;
        static double _phaseStart;

        static OilWaterReflectBakeTool()
        {
            EditorApplication.update += Poll;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static void OnPlayMode(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                _cancel = true;
            }

            if (!SessionState.GetBool(SessionKey, false))
            {
                return;
            }

            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                SessionState.SetInt(SessionPhase, 2);
                _phaseStart = EditorApplication.timeSinceStartup;
            }
        }

        static void Poll()
        {
            if (!_busy && File.Exists(FlagPath()))
            {
                try
                {
                    File.Delete(FlagPath());
                }
                catch
                {
                    return;
                }

                Begin();
            }

            if (!SessionState.GetBool(SessionKey, false) || _busy)
            {
                return;
            }

            int phase = SessionState.GetInt(SessionPhase, 0);
            if (phase == 1 && !EditorApplication.isPlaying)
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    return;
                }

                EditorApplication.isPlaying = true;
                return;
            }

            if (phase == 2 && EditorApplication.isPlaying)
            {
                if (EditorApplication.timeSinceStartup - _phaseStart < 1.0)
                {
                    return;
                }

                _ = RunAsync();
            }
        }

        static string FlagPath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", FlagRelPath));
        }

        [MenuItem("CustomSRP/Bake Water Reflection")]
        public static void Begin()
        {
            if (_busy)
            {
                Debug.LogWarning("[OilWaterReflect] Bake already running.");
                return;
            }

            _cancel = false;
            SessionState.SetBool(SessionKey, true);
            SessionState.SetInt(SessionPhase, EditorApplication.isPlaying ? 2 : 1);
            _phaseStart = EditorApplication.timeSinceStartup;
            Debug.Log("[OilWaterReflect] Bake queued. Play will start if needed.");
        }

        static async Task RunAsync()
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
            float savedHours = 12f;
            bool savedAnimate = false;
            global::CustomSRP.OilSkyboxTime sky = null;
            try
            {
                if (!await WaitWorldReady())
                {
                    Debug.LogError("[OilWaterReflect] Island world did not load.");
                    return;
                }

                await DelaySeconds(2.0f);

                sky = Object.FindAnyObjectByType<global::CustomSRP.OilSkyboxTime>();
                if (sky == null)
                {
                    Debug.LogError("[OilWaterReflect] OilSkyboxTime not found.");
                    return;
                }

                savedHours = sky.timeOfDay;
                savedAnimate = sky.animate;
                sky.animate = false;

                var waters = CollectWater();
                if (waters.Count == 0)
                {
                    Debug.LogError("[OilWaterReflect] No ocean/river renderers.");
                    return;
                }

                if (!TryLargestTerrain(out Terrain terrain, out Bounds island))
                {
                    Debug.LogError("[OilWaterReflect] No terrain.");
                    return;
                }

                float waterY = AverageWaterY(waters);
                Vector3 center = island.center;
                Vector3 probe = new Vector3(center.x, waterY + 1.5f, center.z);
                float farClip = CornerDistance(probe, island) + 4f;
                Vector3 size = island.size;

                var cubes = new Cubemap[Hours.Length];
                for (int i = 0; i < Hours.Length; i++)
                {
                    if (_cancel || !EditorApplication.isPlaying)
                    {
                        return;
                    }

                    EditorUtility.DisplayProgressBar(
                        "Water reflection",
                        SlotNames[i] + " " + Hours[i].ToString("0") + ":00",
                        i / (float)Hours.Length);
                    sky.SetTimeOfDay(Hours[i]);
                    await DelaySeconds(SettleSeconds);
                    if (_cancel || !EditorApplication.isPlaying)
                    {
                        return;
                    }

                    Cubemap cube = CaptureEverything(probe, waters, farClip, waterY);
                    if (cube == null)
                    {
                        Debug.LogError("[OilWaterReflect] Capture failed for " + SlotNames[i]);
                        continue;
                    }

                    cubes[i] = Save(cube, SlotNames[i]);
                }

                SaveSet(probe, size, cubes);
                DeleteLegacyProbes(SlotNames);
                AssetDatabase.SaveAssets();
                Debug.Log(
                    "[OilWaterReflect] Island probe at " + probe +
                    " size=" + size +
                    " far=" + farClip.ToString("0") +
                    " waterY=" + waterY.ToString("0.0") +
                    " slots=" + CountNonNull(cubes));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (sky != null)
                {
                    sky.animate = savedAnimate;
                    sky.SetTimeOfDay(savedHours);
                }

                SessionState.SetBool(SessionKey, false);
                _busy = false;
            }
        }

        /// <summary>
        /// RenderToCubemap keeps a true 90° face, so neighboring edges meet.
        /// Skybox stays on. Water is on the Water layer and culled from the bake camera.
        /// Fragments under the water plane are clipped.
        /// </summary>
        static Cubemap CaptureEverything(
            Vector3 probe, List<Renderer> waters, float farClip, float waterY)
        {
            var camGo = new GameObject("OilWaterReflectBakeCam")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            float prevFogEnabled = Shader.GetGlobalFloat(CustomFogEnabledId);
            float prevAtmFog = Shader.GetGlobalFloat(AtmosphereFogAmountId);
            bool prevFog = RenderSettings.fog;
            float prevClipOn = Shader.GetGlobalFloat(BakeClipOnId);
            float prevClipY = Shader.GetGlobalFloat(BakeClipYId);
            try
            {
                for (int i = 0; i < waters.Count; i++)
                {
                    OilWaterLayers.ApplyTo(waters[i]);
                }

                Shader.SetGlobalFloat(CustomFogEnabledId, 0f);
                Shader.SetGlobalFloat(AtmosphereFogAmountId, 0f);
                RenderSettings.fog = false;
                Shader.SetGlobalFloat(BakeClipOnId, 1f);
                Shader.SetGlobalFloat(BakeClipYId, waterY + 0.05f);

                var cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.clearFlags = CameraClearFlags.Skybox;
                cam.backgroundColor = Color.black;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = farClip;
                cam.fieldOfView = 90f;
                cam.aspect = 1f;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.cullingMask = OilWaterLayers.PlanarReflectionCullingMask;
                cam.transform.position = probe;

                var cube = new Cubemap(FaceSize, TextureFormat.RGBA32, false)
                {
                    name = "OilWaterReflectBake"
                };
                if (!cam.RenderToCubemap(cube))
                {
                    Object.DestroyImmediate(cube);
                    Debug.LogError("[OilWaterReflect] RenderToCubemap failed.");
                    return null;
                }

                cube.Apply(false, false);
                return cube;
            }
            finally
            {
                Shader.SetGlobalFloat(BakeClipOnId, prevClipOn);
                Shader.SetGlobalFloat(BakeClipYId, prevClipY);
                Shader.SetGlobalFloat(CustomFogEnabledId, prevFogEnabled);
                Shader.SetGlobalFloat(AtmosphereFogAmountId, prevAtmFog);
                RenderSettings.fog = prevFog;
                Object.DestroyImmediate(camGo);
            }
        }

        static Cubemap Save(Cubemap baked, string slot)
        {
            Directory.CreateDirectory(Folder);
            string name = "Probe_Island_" + slot;
            string path = Folder + "/" + name + ".asset";
            var existing = AssetDatabase.LoadAssetAtPath<Cubemap>(path);
            if (existing == null)
            {
                baked.name = name;
                AssetDatabase.CreateAsset(baked, path);
                return baked;
            }

            if (existing.width != baked.width)
            {
                AssetDatabase.DeleteAsset(path);
                baked.name = name;
                AssetDatabase.CreateAsset(baked, path);
                return baked;
            }

            for (int face = 0; face < 6; face++)
            {
                existing.SetPixels(baked.GetPixels((CubemapFace)face), (CubemapFace)face);
            }

            existing.Apply(false, false);
            Object.DestroyImmediate(baked);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        static void SaveSet(Vector3 position, Vector3 size, Cubemap[] cubes)
        {
            Directory.CreateDirectory(Folder);
            var set = AssetDatabase.LoadAssetAtPath<OilWaterReflectionSet>(SetPath);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<OilWaterReflectionSet>();
                AssetDatabase.CreateAsset(set, SetPath);
            }

            set.position = position;
            set.size = size;
            set.dawn = cubes.Length > 0 ? cubes[0] : null;
            set.day = cubes.Length > 1 ? cubes[1] : null;
            set.dusk = cubes.Length > 2 ? cubes[2] : null;
            set.night = cubes.Length > 3 ? cubes[3] : null;
            EditorUtility.SetDirty(set);
        }

        static void DeleteLegacyProbes(string[] keepSlots)
        {
            var keep = new HashSet<string> { "IslandWaterReflectionSet" };
            for (int i = 0; i < keepSlots.Length; i++)
            {
                keep.Add("Probe_Island_" + keepSlots[i]);
            }

            string[] guids = AssetDatabase.FindAssets(
                "t:Cubemap",
                new[] { Folder });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                string file = Path.GetFileNameWithoutExtension(path);
                if (file == null || keep.Contains(file))
                {
                    continue;
                }

                AssetDatabase.DeleteAsset(path);
            }
        }

        static int CountNonNull(Cubemap[] cubes)
        {
            int n = 0;
            for (int i = 0; i < cubes.Length; i++)
            {
                if (cubes[i] != null)
                {
                    n++;
                }
            }

            return n;
        }

        static List<Renderer> CollectWater()
        {
            var waters = new List<Renderer>();
            Renderer[] renderers = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (UsesWaterShader(renderers[i]))
                {
                    waters.Add(renderers[i]);
                }
            }

            return waters;
        }

        static float AverageWaterY(List<Renderer> waters)
        {
            float sum = 0f;
            int count = 0;
            for (int i = 0; i < waters.Count; i++)
            {
                Renderer renderer = waters[i];
                if (renderer == null)
                {
                    continue;
                }

                sum += renderer.bounds.center.y;
                count++;
            }

            return count > 0 ? sum / count : 5.7f;
        }

        static bool TryLargestTerrain(out Terrain terrain, out Bounds island)
        {
            terrain = null;
            island = default;
            float bestArea = 0f;
            Terrain[] terrains = Object.FindObjectsByType<Terrain>(FindObjectsInactive.Exclude);
            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain candidate = terrains[i];
                if (candidate == null || candidate.terrainData == null)
                {
                    continue;
                }

                Vector3 size = candidate.terrainData.size;
                float area = size.x * size.z;
                if (area <= bestArea)
                {
                    continue;
                }

                bestArea = area;
                terrain = candidate;
                Vector3 origin = candidate.transform.position;
                island = new Bounds(origin + size * 0.5f, size);
            }

            return terrain != null;
        }

        static float CornerDistance(Vector3 probe, Bounds island)
        {
            float best = 0f;
            Vector3 min = island.min;
            Vector3 max = island.max;
            for (int ix = 0; ix < 2; ix++)
            {
                for (int iy = 0; iy < 2; iy++)
                {
                    for (int iz = 0; iz < 2; iz++)
                    {
                        Vector3 corner = new Vector3(
                            ix == 0 ? min.x : max.x,
                            iy == 0 ? min.y : max.y,
                            iz == 0 ? min.z : max.z);
                        best = Mathf.Max(best, Vector3.Distance(probe, corner));
                    }
                }
            }

            return best;
        }

        static bool IsWaterMaterial(Material material)
        {
            if (material == null || material.shader == null)
            {
                return false;
            }

            string name = material.shader.name;
            return name == "CustomSRP/OilOceanNPR" || name == "CustomSRP/OilRiverNPR";
        }

        static bool UsesWaterShader(Renderer renderer)
        {
            Material[] materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                if (IsWaterMaterial(materials[i]))
                {
                    return true;
                }
            }

            return false;
        }

        static async Task<bool> WaitWorldReady()
        {
            double start = EditorApplication.timeSinceStartup;
            while (EditorApplication.timeSinceStartup - start < WorldReadyTimeout)
            {
                if (_cancel || !EditorApplication.isPlaying)
                {
                    return false;
                }

                if (IsWorldReady())
                {
                    return true;
                }

                await Task.Delay(250);
            }

            return IsWorldReady();
        }

        static bool IsWorldReady()
        {
            var world = global::Game.IslandWorldService.Instance;
            if (world != null && world.IsLoaded)
            {
                return true;
            }

            var found = Object.FindAnyObjectByType<global::Game.IslandWorldService>();
            return found != null && found.IsLoaded;
        }

        static async Task DelaySeconds(float seconds)
        {
            double end = EditorApplication.timeSinceStartup + seconds;
            while (EditorApplication.timeSinceStartup < end)
            {
                if (_cancel || !EditorApplication.isPlaying)
                {
                    return;
                }

                await Task.Delay(100);
            }
        }
    }
}
#endif

