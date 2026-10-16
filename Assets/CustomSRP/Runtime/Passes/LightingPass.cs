using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityGameFramework.Runtime;
using static Unity.Mathematics.math;

namespace CustomSRP
{
    public partial class LightingPass
    {
        public readonly struct Handles
        {
            public readonly BufferHandle directionalBuffer, otherBuffer, tilesBuffer;

            public Handles(
                BufferHandle directionalBuffer,
                BufferHandle otherBuffer,
                BufferHandle tilesBuffer)
            {
                this.directionalBuffer = directionalBuffer;
                this.otherBuffer = otherBuffer;
                this.tilesBuffer = tilesBuffer;
            }

            public void Use(IBaseRenderGraphBuilder builder)
            {
                builder.UseBuffer(directionalBuffer);
                builder.UseBuffer(otherBuffer);
                builder.UseBuffer(tilesBuffer);
            }
        }

        const int
            MaxDirLightCount = 4,
            MaxOtherLightCount = 128;

        public const int MaxDirectionalLightCount = MaxDirLightCount;
        public const int MaxOtherLights = MaxOtherLightCount;

        static readonly ProfilingSampler Sampler = new ProfilingSampler("Lighting");

        static readonly int
            DirLightCountId = Shader.PropertyToID("_DirectionalLightCount"),
            DirectionalLightDataId = Shader.PropertyToID("_DirectionalLightData"),
            OtherLightCountId = Shader.PropertyToID("_OtherLightCount"),
            OtherLightDataId = Shader.PropertyToID("_OtherLightData"),
            TilesId = Shader.PropertyToID("_ForwardPlusTiles"),
            TileSettingsId = Shader.PropertyToID("_ForwardPlusTileSettings");

        static readonly DirectionalLightData[] DirectionalLightDataArray =
            new DirectionalLightData[MaxDirLightCount];

        static readonly OtherLightData[] OtherLightDataArray =
            new OtherLightData[MaxOtherLightCount];

        static NativeArray<int> TileData;

        CullingResults _cullingResults;
        int _dirLightCount;
        int _otherLightCount;
        Handles _handles;

        Vector2 _screenUVToTileCoordinates;
        Vector2Int _tileCount;
        int _maxLightsPerTile;
        int _tileDataSize;
        int _maxTileDataSize;
        NativeArray<float4> _lightBounds;
        JobHandle _forwardPlusJobHandle;

        int TileCount => _tileCount.x * _tileCount.y;

        public static void Cleanup()
        {
            if (TileData.IsCreated)
            {
                TileData.Dispose();
            }
        }

        void Setup(
            CullingResults cullingResults,
            Vector2Int attachmentSize,
            ForwardPlusSettings forwardPlusSettings,
            Shadows shadows,
            int renderingLayerMask)
        {
            _cullingResults = cullingResults;

            _maxLightsPerTile = forwardPlusSettings.maxLightsPerTile <= 0
                ? 31
                : forwardPlusSettings.maxLightsPerTile;
            _maxTileDataSize = _maxLightsPerTile + 1;

            _lightBounds = new NativeArray<float4>(
                MaxOtherLightCount, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);

            // Mobile: prefer 128 px tiles (1/4 the number of tiles vs 64 px),
            // which cuts CPU tile-classification work proportionally.
            float tileScreenPixelSize = forwardPlusSettings.tileSize <= 0
                ? (Application.isMobilePlatform ? 128f : 64f)
                : (float)forwardPlusSettings.tileSize;
            _screenUVToTileCoordinates.x =
                attachmentSize.x / tileScreenPixelSize;
            _screenUVToTileCoordinates.y =
                attachmentSize.y / tileScreenPixelSize;
            _tileCount.x = Mathf.CeilToInt(_screenUVToTileCoordinates.x);
            _tileCount.y = Mathf.CeilToInt(_screenUVToTileCoordinates.y);

            SetupLights(renderingLayerMask, shadows);
        }

        void SetupForwardPlus(int lightIndex, ref VisibleLight visibleLight)
        {
            Rect r = visibleLight.screenRect;
            _lightBounds[lightIndex] = float4(r.xMin, r.yMin, r.xMax, r.yMax);
        }

        void SetupLights(int renderingLayerMask, Shadows shadows)
        {
            NativeArray<VisibleLight> visibleLights = _cullingResults.visibleLights;
            int requiredMaxLightsPerTile = Mathf.Min(
                _maxLightsPerTile, visibleLights.Length);
            _tileDataSize = requiredMaxLightsPerTile + 1;
            _dirLightCount = _otherLightCount = 0;
            for (int i = 0; i < visibleLights.Length; i++)
            {
                VisibleLight visibleLight = visibleLights[i];
                Light light = visibleLight.light;
                bool layerOk = (light.renderingLayerMask & renderingLayerMask) != 0;
                bool isShadowCapable = light.shadows != LightShadows.None && light.shadowStrength > 0f;
                Vector4 shadowData = default;

                if (layerOk)
                {
                    switch (visibleLight.lightType)
                    {
                        case LightType.Directional:
                            if (_dirLightCount < MaxDirLightCount)
                            {
                                shadowData = shadows.directionalShadows.ReserveShadows(
                                    light, i, _cullingResults);
                                DirectionalLightDataArray[_dirLightCount++] =
                                    new DirectionalLightData(
                                        ref visibleLight, light, shadowData);
                            }
                            break;
                        case LightType.Point:
                            if (_otherLightCount < MaxOtherLightCount)
                            {
                                shadowData = shadows.otherShadows.ReserveShadows(
                                    light, i, _cullingResults);
                                SetupForwardPlus(_otherLightCount, ref visibleLight);
                                OtherLightDataArray[_otherLightCount++] =
                                    OtherLightData.CreatePointLight(
                                        ref visibleLight, light, shadowData);
                            }
                            break;
                        case LightType.Spot:
                            if (_otherLightCount < MaxOtherLightCount)
                            {
                                shadowData = shadows.otherShadows.ReserveShadows(
                                    light, i, _cullingResults);
                                SetupForwardPlus(_otherLightCount, ref visibleLight);
                                OtherLightDataArray[_otherLightCount++] =
                                    OtherLightData.CreateSpotLight(
                                        ref visibleLight, light, shadowData);
                            }
                            break;
                    }
                }

                // [SRP][Shadow.Light] 每 60 帧把每个光源过一次,看 ReserveShadows 给的是什么
                if ((Time.frameCount & 63) == 0)
                {
                    Log.Info(string.Format(
                        "[SRP][Shadow.Light] idx={0} type={1} layerOk={2} shadowCapable={3} shadows={4} strength={5:F2} layer={6} reserved=({7:F2},{8:F1},{9:F3},{10:F2})",
                        i, visibleLight.lightType, layerOk, isShadowCapable,
                        light.shadows, light.shadowStrength,
                        light.renderingLayerMask,
                        shadowData.x, shadowData.y, shadowData.z, shadowData.w));
                }
            }

            if ((Time.frameCount & 63) == 0)
            {
                Log.Info(string.Format(
                    "[SRP][Shadow.LightSummary] visible={0} dir={1} other={2}",
                    visibleLights.Length, _dirLightCount, _otherLightCount));
            }

            int tileDataLength = TileCount * _tileDataSize;
            if (!TileData.IsCreated || TileData.Length != tileDataLength)
            {
                if (TileData.IsCreated)
                {
                    TileData.Dispose();
                }

                TileData = new NativeArray<int>(
                    tileDataLength, Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
            }

            var tilesJob = new ForwardPlusTilesJob
            {
                lightBounds = _lightBounds,
                tileData = TileData,
                otherLightCount = _otherLightCount,
                tileScreenUVSize = float2(
                    1f / _screenUVToTileCoordinates.x,
                    1f / _screenUVToTileCoordinates.y),
                maxLightsPerTile = requiredMaxLightsPerTile,
                tilesPerRow = _tileCount.x,
                tileDataSize = _tileDataSize
            };
            _forwardPlusJobHandle = tilesJob.ScheduleParallelByRef(
                TileCount, _tileCount.x, default);
            // Kick the worker immediately so the job overlaps the GPU pass recording
            // phase. Render() still calls Complete() before SetBufferData.
            JobHandle.ScheduleBatchedJobs();
        }

        void Render(UnsafeGraphContext context)
        {
            UnsafeCommandBuffer buffer = context.cmd;

            buffer.SetGlobalInt(DirLightCountId, _dirLightCount);
            buffer.SetBufferData(
                _handles.directionalBuffer, DirectionalLightDataArray,
                0, 0, _dirLightCount);
            buffer.SetGlobalBuffer(DirectionalLightDataId, _handles.directionalBuffer);

            buffer.SetGlobalInt(OtherLightCountId, _otherLightCount);
            buffer.SetBufferData(
                _handles.otherBuffer, OtherLightDataArray, 0, 0, _otherLightCount);
            buffer.SetGlobalBuffer(OtherLightDataId, _handles.otherBuffer);

            _forwardPlusJobHandle.Complete();
            buffer.SetBufferData(_handles.tilesBuffer, TileData, 0, 0, TileData.Length);
            buffer.SetGlobalBuffer(TilesId, _handles.tilesBuffer);
            buffer.SetGlobalVector(TileSettingsId, new Vector4(
                _screenUVToTileCoordinates.x, _screenUVToTileCoordinates.y,
                _tileCount.x.ReinterpretAsFloat(),
                _tileDataSize.ReinterpretAsFloat()));
            _lightBounds.Dispose();
        }

        public static Handles Record(
            RenderGraph renderGraph,
            CullingResults cullingResults,
            Vector2Int attachmentSize,
            ForwardPlusSettings forwardPlusSettings,
            Shadows shadows,
            int renderingLayerMask)
        {
            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out LightingPass pass, Sampler);
            pass.Setup(
                cullingResults, attachmentSize, forwardPlusSettings, shadows,
                renderingLayerMask);

            var handles = pass._handles = new Handles(
                renderGraph.CreateBuffer(new BufferDesc(
                    MaxDirLightCount, DirectionalLightData.stride)
                {
                    name = "Directional Light Data"
                }),
                renderGraph.CreateBuffer(new BufferDesc(
                    MaxOtherLightCount, OtherLightData.stride)
                {
                    name = "Other Light Data"
                }),
                renderGraph.CreateBuffer(new BufferDesc(
                    pass.TileCount * pass._maxTileDataSize, 4)
                {
                    name = "Forward+ Tiles"
                }));
            builder.UseBuffer(handles.directionalBuffer, AccessFlags.WriteAll);
            builder.UseBuffer(handles.otherBuffer, AccessFlags.WriteAll);
            builder.UseBuffer(handles.tilesBuffer, AccessFlags.WriteAll);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc<LightingPass>(
                static (pass, context) => pass.Render(context));
            return handles;
        }
    }
}
