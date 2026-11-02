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

        /// <summary>手机与闺蜜机硬影距离。CRP 再收到 25。</summary>
        public const float PhoneShadowDistance = 26f;

        /// <summary>平板硬影距离，对齐 Quality 里 Ultra 的 40，中景还能看到接触影。</summary>
        public const float TabletShadowDistance = 40f;

        /// <summary>移动端远处换成 basemap，避免整岛 splat 常驻。</summary>
        const float MobileBasemapDistance = 100f;

        /// <summary>
        /// Basemap shader 缺失时的兜底：超过岛相机 farClip(750)，全程走 TerrainOilNPR splat，
        /// 避免 Hidden basemap 粉红。正常包仍用 <see cref="MobileBasemapDistance"/>。
        /// </summary>
        const float MissingBasemapSafeDistance = 800f;

        public static void ApplyAfterTerrainSpawn(GameObject terrainInstance)
        {
            DisableLaunchDirectionalLights();
            TuneLightsForMobileOil();
            TuneTerrainForMobile(terrainInstance);
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
            // 手机 / 闺蜜机短于 Ultra 的 40。平板拉到 40，中景接触影还能看见。
            QualitySettings.shadowDistance = UseTabletShadowDistance
                ? TabletShadowDistance
                : PhoneShadowDistance;
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
            // 远处换成一张 basemap，避免整张岛的 splat 常驻。
            // BaseMap* / AddPass 只写在 TerrainOilNPR Dependency 字符串里，Yoo AutoCollect
            // 收不到；靠 Always Included + Resources/EngineKeepAlive 材质硬引用进包。
            // 缺 shader 时绝不能压到 100——Unity 会换 InternalErrorShader，整片洋红。
            terrain.basemapDistance = ResolveBasemapDistance();
            // Off: terrain self-shadow caused stripe acne; receive stays on in shader.
            terrain.shadowCastingMode = ShadowCastingMode.Off;
            // 全档保持网格路径。Draw Instanced 会让每个顶点采样高度图，占带宽。
            terrain.drawInstanced = false;
        }

        /// <summary>
        /// 平板加长阴影。闺蜜机即使长宽比像平板，仍走手机距离。
        /// </summary>
        public static bool UseTabletShadowDistance =>
            ScreenLayout.IsPad && !BoluoDeviceInfo.IsSkyworthGmj();

        static float ResolveBasemapDistance()
        {
            Shader basemap = Shader.Find("Hidden/CustomSRP/TerrainLitBasemap");
            Shader gen = Shader.Find("Hidden/CustomSRP/TerrainLitBasemapGen");
            if (basemap != null && gen != null)
                return MobileBasemapDistance;

            Log.Error(
                "[IslandMobileTune] TerrainLitBasemap/Gen missing — keep splat path" +
                " (basemap=" + (basemap != null) + " gen=" + (gen != null) +
                "). Rebuild player with EngineKeepAlive mats + Always Included.");
            return MissingBasemapSafeDistance;
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
