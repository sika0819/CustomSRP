using System.Collections.Generic;
using Game.BoluoApi;
using UnityEngine;
using UnityEngine.Rendering;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>进岛后移动端灯光 / 地形轻量调参（从 ProcedureIsland 抽出）。</summary>
    public static class IslandMobileTune
    {
        /// <summary>B4：略抬于 Boluo Heavy(12)，少高度网格三角形；河岸仍可读。</summary>
        const float HeavyContentTerrainPixelError = 18f;

        /// <summary>闺蜜机标准版：再抬一点像素误差，远景高度网格略粗，近景几乎看不出。4K 不用。</summary>
        const float GmjTerrainPixelError = 36f;

        public static void ApplyAfterTerrainSpawn(GameObject terrainInstance)
        {
            DisableLaunchDirectionalLights();
            TuneLightsForMobileOil();
            TuneTerrainForMobile(terrainInstance);
            ApplyGmjPerfGuards();
        }

        /// <summary>
        /// 闺蜜机标准版：关平面倒影（水面走解析天空）。
        /// 实时阴影保留。4K 对齐低端机，不走这里。不改 renderScale / 树距离。
        /// </summary>
        static void ApplyGmjPerfGuards()
        {
#if !UNITY_EDITOR
            if (!BoluoDeviceInfo.IsSkyworthGmjStandard())
                return;

            System.Type reflectType = System.Type.GetType(
                "CustomSRP.OilPlanarReflection, Assembly-CSharp");
            reflectType?.GetMethod(
                    "Suppress",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?.Invoke(null, null);

            Log.Info("[IslandMobileTune] GMJ perf: reflect=off (shadows kept)");
#endif
        }

        public static void DisableLaunchDirectionalLights()
        {
            IslandSceneComponent.Active?.DisableDirectionalsExceptSun();
        }

        public static void TuneLightsForMobileOil()
        {
#if !UNITY_EDITOR
            if (QualitySettings.shadows == ShadowQuality.Disable)
                QualitySettings.shadows = ShadowQuality.HardOnly;
            // Shorter than Ultra's 40; matches CRP maxDistance for near contact shadows.
            // 闺蜜机保留实时硬影；略收距离省转头时 caster 提交（仍有近景接触影）。
            // 林间走动最低 15 / 走转 20：阴影收到 8m（近景接触影保留）。
            QualitySettings.shadowDistance = BoluoDeviceInfo.IsSkyworthGmjStandard() ? 8f : 26f;
            QualitySettings.shadowResolution = ShadowResolution.Low;

            IslandSceneComponent scene = IslandSceneComponent.Active;
            if (scene != null)
            {
                var lights = new List<Light>(4);
                scene.CopyLights(lights);
                for (int i = 0; i < lights.Count; i++)
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
            }

            Log.Info(
                "[IslandMobileTune] lights quality=" +
                QualitySettings.names[QualitySettings.GetQualityLevel()] +
                " shadows=" + QualitySettings.shadows +
                " dist=" + QualitySettings.shadowDistance);
#endif
        }

        public static void TuneTerrainForMobile(GameObject terrainRoot)
        {
            Terrain terrain = RegisteredTerrain(terrainRoot);
            if (terrain == null)
                return;

            // Boluo Heavy budget: keep LOD0 and cap pixelError. Old Max(14)+MaximumLOD=1
            // skipped fine heightmesh near river banks → big triangular stairs.
            bool gmj = BoluoDeviceInfo.IsSkyworthGmjStandard();
            float pixelErrorCap = gmj ? GmjTerrainPixelError : HeavyContentTerrainPixelError;
            terrain.heightmapMinimumLODSimplification = 0;
            terrain.heightmapMaximumLOD = 0;
            terrain.heightmapPixelError = Mathf.Min(
                Mathf.Max(terrain.heightmapPixelError, 5f),
                pixelErrorCap);
            terrain.detailObjectDistance = 0f;
            terrain.detailObjectDensity = 0f;
            // 树距离不改（手机 / 闺蜜机同档）。
            terrain.treeDistance = Mathf.Min(terrain.treeDistance, 60f);
            terrain.treeBillboardDistance = Mathf.Min(terrain.treeBillboardDistance, 16f);
            terrain.treeCrossFadeLength = Mathf.Min(terrain.treeCrossFadeLength, 5f);
            if (terrain.basemapDistance < 1000f)
                terrain.basemapDistance = 1000f;
            // Off: terrain self-shadow caused stripe acne; receive stays on in shader.
            terrain.shadowCastingMode = ShadowCastingMode.Off;
            // 全档保持网格路径。Draw Instanced 会让每个顶点采样高度图，占带宽。
            terrain.drawInstanced = false;
        }

        static Terrain RegisteredTerrain(GameObject terrainRoot)
        {
            if (terrainRoot == null)
                return null;
            Terrain terrain = terrainRoot.GetComponentInChildren<Terrain>(true);
            if (terrain != null && terrain.terrainData != null)
                return terrain;
            terrain = IslandSceneComponent.Active?.Terrain;
            if (terrain == null || terrain.terrainData == null)
                return null;
            return terrain;
        }

    }
}
