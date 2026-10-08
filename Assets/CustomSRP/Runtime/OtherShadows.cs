using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    public partial class OtherShadows
    {
        public readonly struct Handles
        {
            public readonly TextureHandle atlas;
            public readonly BufferHandle buffer;

            public Handles(TextureHandle atlas, BufferHandle buffer)
            {
                this.atlas = atlas;
                this.buffer = buffer;
            }

            public void Use(IBaseRenderGraphBuilder builder)
            {
                builder.UseTexture(atlas);
                builder.UseBuffer(buffer);
            }
        }

        const int MaxLightCount = 16;

        static readonly int
            AtlasId = Shader.PropertyToID("_OtherShadowAtlas"),
            DataId = Shader.PropertyToID("_OtherShadowData");

        static readonly OtherShadowData[] Data = new OtherShadowData[MaxLightCount];

        struct ShadowedLight
        {
            public int visibleLightIndex;
            public float slopeScaleBias;
            public float normalBias;
            public bool isPoint;
        }

        struct RenderInfo
        {
            public RendererListHandle handle;
            public Matrix4x4 view, projection;
        }

        readonly ShadowedLight[] _lights = new ShadowedLight[MaxLightCount];

        readonly RenderInfo[] _renderInfo =
            new RenderInfo[MaxLightCount * Shadows.MaxTilesPerLight];

        ShadowSettings.Other _settings;
        float _filterSize;
        int _lightCount;
        Handles _handles;
        int _split;
        int _tileSize;

        public bool HasLights => _lightCount > 0;

        public bool UsesShadowMask { get; private set; }

        public void Setup(ShadowSettings settings)
        {
            _settings = settings.other;
            _filterSize = settings.OtherFilterSize;
            _lightCount = 0;
            UsesShadowMask = false;
        }

        public Vector4 ReserveShadows(
            Light light,
            int visibleLightIndex,
            CullingResults cullingResults)
        {
            if (light.shadows == LightShadows.None || light.shadowStrength <= 0f)
            {
                return new Vector4(0f, 0f, 0f, -1f);
            }

            float maskChannel = -1f;
            LightBakingOutput lightBaking = light.bakingOutput;
            if (lightBaking.lightmapBakeType == LightmapBakeType.Mixed
                && lightBaking.mixedLightingMode == MixedLightingMode.Shadowmask)
            {
                UsesShadowMask = true;
                maskChannel = lightBaking.occlusionMaskChannel;
            }

            bool isPoint = light.type == LightType.Point;
            int newLightCount = _lightCount + (isPoint ? 6 : 1);
            if (newLightCount > MaxLightCount
                || !cullingResults.GetShadowCasterBounds(visibleLightIndex, out _))
            {
                return new Vector4(-light.shadowStrength, 0f, 0f, maskChannel);
            }

            _lights[_lightCount] = new ShadowedLight
            {
                visibleLightIndex = visibleLightIndex,
                slopeScaleBias = light.shadowBias,
                normalBias = light.shadowNormalBias,
                isPoint = isPoint
            };

            var data = new Vector4(
                light.shadowStrength, _lightCount,
                isPoint ? 1f : 0f, maskChannel);
            _lightCount = newLightCount;
            return data;
        }

        public Handles GetHandles(
            RenderGraph renderGraph, IUnsafeRenderGraphBuilder builder)
        {
            TextureHandle atlas;
            if (_lightCount > 0)
            {
                int atlasSize = (int)_settings.atlasSize;
                atlas = renderGraph.CreateTexture(new TextureDesc(atlasSize, atlasSize)
                {
                    depthBufferBits = DepthBits.Depth32,
                    isShadowMap = true,
                    name = "Other Shadow Atlas"
                });
                builder.UseTexture(atlas, AccessFlags.WriteAll);
            }
            else
            {
                atlas = renderGraph.defaultResources.defaultShadowTexture;
            }

            _handles = new Handles(
                atlas,
                renderGraph.CreateBuffer(new BufferDesc(
                    MaxLightCount, OtherShadowData.stride)
                {
                    name = "Other Shadow Data"
                }));
            builder.UseBuffer(_handles.buffer, AccessFlags.WriteAll);
            return _handles;
        }

        public void BuildRendererLists(
            RenderGraph renderGraph,
            IUnsafeRenderGraphBuilder builder,
            CullingResults cullingResults,
            ShadowCastersCullingInfos cullingInfos)
        {
            int atlasSize = (int)_settings.atlasSize;
            int tiles = _lightCount;
            _split = tiles <= 1 ? 1 : tiles <= 4 ? 2 : 4;
            _tileSize = atlasSize / _split;

            for (int i = 0; i < _lightCount;)
            {
                if (_lights[i].isPoint)
                {
                    BuildPointShadowsRendererList(
                        i, renderGraph, builder, cullingResults, cullingInfos);
                    i += 6;
                }
                else
                {
                    BuildSpotShadowsRendererList(
                        i, renderGraph, builder, cullingResults, cullingInfos);
                    i += 1;
                }
            }
        }

        void BuildSpotShadowsRendererList(
            int index,
            RenderGraph renderGraph,
            IUnsafeRenderGraphBuilder builder,
            CullingResults cullingResults,
            ShadowCastersCullingInfos cullingInfos)
        {
            ShadowedLight light = _lights[index];
            var shadowSettings = new ShadowDrawingSettings(
                cullingResults, light.visibleLightIndex)
            {
                useRenderingLayerMaskTest = true
            };
            ref RenderInfo info =
                ref _renderInfo[index * Shadows.MaxTilesPerLight];
            cullingResults.ComputeSpotShadowMatricesAndCullingPrimitives(
                light.visibleLightIndex, out info.view, out info.projection,
                out ShadowSplitData splitData);

            int splitOffset = light.visibleLightIndex * Shadows.MaxTilesPerLight;
            cullingInfos.splitBuffer[splitOffset] = splitData;
            info.handle = renderGraph.CreateShadowRendererList(ref shadowSettings);
            builder.UseRendererList(info.handle);
            cullingInfos.perLightInfos[light.visibleLightIndex] =
                new LightShadowCasterCullingInfo
                {
                    projectionType = BatchCullingProjectionType.Perspective,
                    splitRange = new RangeInt(splitOffset, 1)
                };
        }

        void BuildPointShadowsRendererList(
            int index,
            RenderGraph renderGraph,
            IUnsafeRenderGraphBuilder builder,
            CullingResults cullingResults,
            ShadowCastersCullingInfos cullingInfos)
        {
            ShadowedLight light = _lights[index];
            var shadowSettings = new ShadowDrawingSettings(
                cullingResults, light.visibleLightIndex)
            {
                useRenderingLayerMaskTest = true
            };
            float texelSize = 2f / _tileSize;
            float filterTexelSize = texelSize * _filterSize;
            float bias = light.normalBias * filterTexelSize * 1.4142136f;
            float fovBias =
                Mathf.Atan(1f + bias + filterTexelSize) * Mathf.Rad2Deg * 2f - 90f;
            int splitOffset = light.visibleLightIndex * Shadows.MaxTilesPerLight;
            for (int i = 0; i < 6; i++)
            {
                ref RenderInfo info =
                    ref _renderInfo[index * Shadows.MaxTilesPerLight + i];
                cullingResults.ComputePointShadowMatricesAndCullingPrimitives(
                    light.visibleLightIndex, (CubemapFace)i, fovBias,
                    out info.view, out info.projection,
                    out ShadowSplitData splitData);
                cullingInfos.splitBuffer[splitOffset + i] = splitData;
                info.handle = renderGraph.CreateShadowRendererList(ref shadowSettings);
                builder.UseRendererList(info.handle);
            }

            cullingInfos.perLightInfos[light.visibleLightIndex] =
                new LightShadowCasterCullingInfo
                {
                    projectionType = BatchCullingProjectionType.Perspective,
                    splitRange = new RangeInt(splitOffset, 6)
                };
        }

        public void RenderOtherShadows(UnsafeCommandBuffer buffer)
        {
            buffer.BeginSample("Other Shadows");
            if (_lightCount > 0)
            {
                buffer.SetRenderTarget(
                    _handles.atlas,
                    RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
                buffer.ClearRenderTarget(true, false, Color.clear);
                float border = 0.5f / (float)_settings.atlasSize;
                for (int i = 0; i < _lightCount;)
                {
                    if (_lights[i].isPoint)
                    {
                        RenderPointShadows(i, buffer, border);
                        i += 6;
                    }
                    else
                    {
                        RenderSpotShadows(i, buffer, border);
                        i += 1;
                    }
                }
            }

            buffer.SetGlobalDepthBias(0f, 0f);
            buffer.SetGlobalTexture(AtlasId, _handles.atlas);
            buffer.SetGlobalBuffer(DataId, _handles.buffer);
            buffer.SetBufferData(_handles.buffer, Data, 0, 0, _lightCount);
            buffer.EndSample("Other Shadows");
        }

        void RenderSpotShadows(int index, UnsafeCommandBuffer buffer, float border)
        {
            ShadowedLight light = _lights[index];
            RenderInfo info = _renderInfo[index * Shadows.MaxTilesPerLight];
            float texelSize = 2f / (_tileSize * info.projection.m00);
            float bias = light.normalBias * _filterSize * texelSize * 1.4142136f;
            Vector2 offset = Shadows.SetTileViewport(
                buffer, index, _split, _tileSize);
            float tileScale = 1f / _split;
            Data[index] = new OtherShadowData(
                offset, tileScale, bias, border,
                Shadows.ConvertToAtlasMatrix(
                    info.projection * info.view, offset, tileScale));
            buffer.SetViewProjectionMatrices(info.view, info.projection);
            buffer.SetGlobalDepthBias(0f, light.slopeScaleBias);
            buffer.DrawRendererList(info.handle);
        }

        void RenderPointShadows(int index, UnsafeCommandBuffer buffer, float border)
        {
            ShadowedLight light = _lights[index];
            float texelSize = 2f / _tileSize;
            float bias = light.normalBias * _filterSize * texelSize * 1.4142136f;
            float tileScale = 1f / _split;
            buffer.SetGlobalDepthBias(0f, light.slopeScaleBias);
            for (int i = 0; i < 6; i++)
            {
                RenderInfo info = _renderInfo[index * Shadows.MaxTilesPerLight + i];
                info.view.m11 = -info.view.m11;
                info.view.m12 = -info.view.m12;
                info.view.m13 = -info.view.m13;
                int tileIndex = index + i;
                Vector2 offset = Shadows.SetTileViewport(
                    buffer, tileIndex, _split, _tileSize);
                Data[tileIndex] = new OtherShadowData(
                    offset, tileScale, bias, border,
                    Shadows.ConvertToAtlasMatrix(
                        info.projection * info.view, offset, tileScale));
                buffer.SetViewProjectionMatrices(info.view, info.projection);
                buffer.DrawRendererList(info.handle);
            }
        }
    }
}
