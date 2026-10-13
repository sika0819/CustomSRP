using UnityEngine;
using UnityEngine.Rendering;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>进岛后移动端灯光 / 云 / 地形轻量调参（从 ProcedureIsland 抽出）。</summary>
    public static class IslandMobileTune
    {
        public static void ApplyAfterTerrainSpawn(GameObject terrainInstance)
        {
            DisableLaunchDirectionalLights();
            TuneLightsForMobileOil();
            TuneCloudsForMobile();
            TuneTerrainForMobile(terrainInstance);
            TuneTerrainShadowReceive(terrainInstance);
        }

        public static void DisableLaunchDirectionalLights()
        {
            Light[] lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            for (int i = 0; i < lights.Length; i++)
            {
                Light light = lights[i];
                if (light == null || light.type != LightType.Directional)
                    continue;

                Transform root = light.transform.root;
                if (root != null && root.name.StartsWith("IslandTerrain", System.StringComparison.Ordinal))
                    continue;

                light.enabled = false;
            }
        }

        public static void TuneLightsForMobileOil()
        {
#if !UNITY_EDITOR
            if (QualitySettings.shadows == ShadowQuality.Disable)
                QualitySettings.shadows = ShadowQuality.HardOnly;
            // Shorter than Ultra's 40; matches CRP maxDistance for near contact shadows.
            // Match CRP maxDistance; slightly longer so near trees paint ground contact.
            // Match CRP maxDistance; keep contact for near trees without filling atlas with far casters.
            QualitySettings.shadowDistance = 26f;
            QualitySettings.shadowResolution = ShadowResolution.Low;

            Light[] lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            for (int i = 0; i < lights.Length; i++)
            {
                Light light = lights[i];
                if (light == null || !light.enabled)
                    continue;

                if (light.type == LightType.Directional)
                {
                    if (light.shadows == LightShadows.None || light.shadows == LightShadows.Soft)
                        light.shadows = LightShadows.Hard;
                    light.shadowStrength = Mathf.Max(light.shadowStrength, 0.9f);
                    // Casters = trees; lower normalBias so contact umbra sits on flat ground.
                    light.shadowBias = Mathf.Clamp(light.shadowBias, 0.02f, 0.045f);
                    light.shadowNormalBias = Mathf.Clamp(light.shadowNormalBias, 0.2f, 0.35f);
                }
                else
                {
                    light.shadows = LightShadows.None;
                }
            }

            Log.Info(
                "[IslandMobileTune] lights quality=" +
                QualitySettings.names[QualitySettings.GetQualityLevel()] +
                " shadows=" + QualitySettings.shadows +
                " dist=" + QualitySettings.shadowDistance);
#endif
        }

        public static void TuneCloudsForMobile()
        {
#if !UNITY_EDITOR
            // Full cloud ring is a large transparent fill + Load/Store; fog already softens sky.
            // Disable the component so CloudPass early-outs (Current cleared on OnDisable).
            System.Type layerType = System.Type.GetType(
                "CustomSRP.OilCloudLayer, Assembly-CSharp");
            if (layerType == null)
                return;

            Object[] layers = Object.FindObjectsByType(layerType, FindObjectsSortMode.None);
            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i] is Behaviour behaviour)
                    behaviour.enabled = false;
            }
#endif
        }

        public static void TuneTerrainForMobile(GameObject terrainRoot)
        {
            if (terrainRoot == null)
                return;

            Terrain[] terrains = terrainRoot.GetComponentsInChildren<Terrain>(true);
            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain t = terrains[i];
                if (t == null)
                    continue;

                // Coarser heightmesh → fewer tris; screen clarity is renderScale, not geo density.
                t.heightmapPixelError = Mathf.Max(t.heightmapPixelError, 14f);
                t.heightmapMaximumLOD = Mathf.Max(t.heightmapMaximumLOD, 1);
                t.detailObjectDistance = 0f;
                t.detailObjectDensity = 0f;
                t.treeDistance = Mathf.Min(t.treeDistance, 60f);
                t.treeBillboardDistance = Mathf.Min(t.treeBillboardDistance, 16f);
                t.treeCrossFadeLength = Mathf.Min(t.treeCrossFadeLength, 5f);
                if (t.basemapDistance < 1000f)
                    t.basemapDistance = 1000f;
                // Off: terrain self-shadow caused stripe acne; receive stays on in shader.
                t.shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        /// <summary>
        /// 降低 ShadowLift / 过高环境光，让 TERRAIN_SHADOW_RECEIVER 接触影在地面可读。
        /// </summary>
        public static void TuneTerrainShadowReceive(GameObject terrainRoot)
        {
            if (terrainRoot == null)
                return;

            Terrain[] terrains = terrainRoot.GetComponentsInChildren<Terrain>(true);
            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain t = terrains[i];
                if (t == null)
                    continue;

                Material mat = t.materialTemplate;
                if (mat == null || mat.shader == null)
                    continue;
                if (mat.shader.name.IndexOf("TerrainOilNPR", System.StringComparison.Ordinal) < 0
                    && mat.shader.name.IndexOf("TerrainLit", System.StringComparison.Ordinal) < 0)
                    continue;

                // Instance so we do not dirty the shared project asset from every device session.
                Material runtime = Object.Instantiate(mat);
                runtime.name = mat.name + " (MobileShadow)";
                if (runtime.HasProperty("_ShadowLift"))
                    runtime.SetFloat("_ShadowLift", 0.62f);
                if (runtime.HasProperty("_ShadowWobble"))
                    runtime.SetFloat("_ShadowWobble", 0.4f);
                if (runtime.HasProperty("_AmbientColor"))
                {
                    Color a = runtime.GetColor("_AmbientColor");
                    // Cap sky-blue ambient that washed contact umbra to invisibility.
                    float peak = Mathf.Max(a.r, Mathf.Max(a.g, a.b));
                    if (peak > 0.55f)
                        runtime.SetColor("_AmbientColor", a * (0.5f / peak));
                }

                if (runtime.HasProperty("_ReceiveShadows"))
                    runtime.SetFloat("_ReceiveShadows", 1f);
                runtime.EnableKeyword("_RECEIVE_SHADOWS");
                t.materialTemplate = runtime;
            }
        }
    }
}
