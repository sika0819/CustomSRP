using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    public partial class DirectionalShadows
    {
        public readonly struct Handles
        {
            public readonly TextureHandle atlas;
            public readonly BufferHandle cascadeBuffer, matrixBuffer;

            public Handles(
                TextureHandle atlas,
                BufferHandle cascadeBuffer,
                BufferHandle matrixBuffer)
            {
                this.atlas = atlas;
                this.cascadeBuffer = cascadeBuffer;
                this.matrixBuffer = matrixBuffer;
            }

            public void Use(IBaseRenderGraphBuilder builder)
            {
                builder.UseTexture(atlas);
                builder.UseBuffer(cascadeBuffer);
                builder.UseBuffer(matrixBuffer);
            }
        }

        const int MaxLightCount = 4;
        const int MaxCascades = 4;

        static readonly int
            AtlasId = Shader.PropertyToID("_DirectionalShadowAtlas"),
            CascadesId = Shader.PropertyToID("_DirectionalShadowCascades"),
            MatricesId = Shader.PropertyToID("_DirectionalShadowMatrices"),
            CascadeCountId = Shader.PropertyToID("_CascadeCount"),
            ShadowPancakingId = Shader.PropertyToID("_ShadowPancaking");

        static readonly GlobalKeyword SoftCascadeBlendKeyword =
            GlobalKeyword.Create("_SOFT_CASCADE_BLEND");

        static readonly DirectionalShadowCascade[] Cascades =
            new DirectionalShadowCascade[MaxCascades];

        static readonly Matrix4x4[] Matrices =
            new Matrix4x4[MaxLightCount * MaxCascades];

        struct ShadowedLight
        {
            public int visibleLightIndex;
            public float slopeScaleBias;
            public float nearPlaneOffset;
        }

        struct RenderInfo
        {
            public RendererListHandle handle;
            public Matrix4x4 view, projection;
        }

        readonly ShadowedLight[] _lights = new ShadowedLight[MaxLightCount];

        readonly RenderInfo[] _renderInfo =
            new RenderInfo[MaxLightCount * MaxCascades];

        ShadowSettings.Directional _settings;
        float _filterSize;
        int _lightCount;
        Handles _handles;
        int _split;
        int _tileSize;

        public bool HasLights => _lightCount > 0;

        public bool UsesShadowMask { get; private set; }

        public void Setup(ShadowSettings settings)
        {
            _settings = settings.directional;
            _filterSize = settings.DirectionalFilterSize;
            _lightCount = 0;
            UsesShadowMask = false;
        }

        public Vector4 ReserveShadows(
            Light light,
            int visibleLightIndex,
            CullingResults cullingResults)
        {
            if (_lightCount < MaxLightCount
                && light.shadows != LightShadows.None
                && light.shadowStrength > 0f)
            {
                float maskChannel = -1f;
                LightBakingOutput lightBaking = light.bakingOutput;
                if (lightBaking.lightmapBakeType == LightmapBakeType.Mixed
                    && lightBaking.mixedLightingMode == MixedLightingMode.Shadowmask)
                {
                    UsesShadowMask = true;
                    maskChannel = lightBaking.occlusionMaskChannel;
                }

                if (!cullingResults.GetShadowCasterBounds(visibleLightIndex, out _))
                {
                    return new Vector4(-light.shadowStrength, 0f, 0f, maskChannel);
                }

                _lights[_lightCount] = new ShadowedLight
                {
                    visibleLightIndex = visibleLightIndex,
                    slopeScaleBias = light.shadowBias,
                    nearPlaneOffset = light.shadowNearPlane
                };
                return new Vector4(
                    light.shadowStrength,
                    _settings.cascadeCount * _lightCount++,
                    light.shadowNormalBias,
                    maskChannel);
            }

            return new Vector4(0f, 0f, 0f, -1f);
        }

        public Handles GetHandles(
            RenderGraph renderGraph,
            IUnsafeRenderGraphBuilder builder)
        {
            TextureHandle atlas;
            if (_lightCount > 0)
            {
                int atlasSize = (int)_settings.atlasSize;
                atlas = renderGraph.CreateTexture(new TextureDesc(atlasSize, atlasSize)
                {
                    depthBufferBits = DepthBits.Depth32,
                    isShadowMap = true,
                    name = "Directional Shadow Atlas"
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
                    MaxCascades, DirectionalShadowCascade.stride)
                {
                    name = "Shadow Cascades"
                }),
                renderGraph.CreateBuffer(new BufferDesc(
                    MaxLightCount * MaxCascades, 4 * 16)
                {
                    name = "Directional Shadow Matrices"
                }));
            builder.UseBuffer(_handles.cascadeBuffer, AccessFlags.WriteAll);
            builder.UseBuffer(_handles.matrixBuffer, AccessFlags.WriteAll);
            return _handles;
        }

        public void BuildRendererLists(
            RenderGraph renderGraph,
            IUnsafeRenderGraphBuilder builder,
            CullingResults cullingResults,
            ShadowCastersCullingInfos cullingInfos)
        {
            int atlasSize = (int)_settings.atlasSize;
            int tiles = _lightCount * _settings.cascadeCount;
            _split = tiles <= 1 ? 1 : tiles <= 4 ? 2 : 4;
            _tileSize = atlasSize / _split;

            for (int i = 0; i < _lightCount; i++)
            {
                BuildRendererLists(
                    i, renderGraph, builder, cullingResults, cullingInfos);
            }
        }

        void BuildRendererLists(
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

            int cascadeCount = _settings.cascadeCount;
            Vector3 ratios = _settings.CascadeRatios;
            float cullingFactor = Mathf.Max(0f, 0.8f - _settings.cascadeFade);
            int splitOffset = light.visibleLightIndex * Shadows.MaxTilesPerLight;
            for (int i = 0; i < cascadeCount; i++)
            {
                ref RenderInfo info = ref _renderInfo[index * MaxCascades + i];
                cullingResults.ComputeDirectionalShadowMatricesAndCullingPrimitives(
                    light.visibleLightIndex, i, cascadeCount, ratios,
                    _tileSize, light.nearPlaneOffset, out info.view,
                    out info.projection, out ShadowSplitData splitData);
                splitData.shadowCascadeBlendCullingFactor = cullingFactor;
                cullingInfos.splitBuffer[splitOffset + i] = splitData;
                if (index == 0)
                {
                    Cascades[i] = new DirectionalShadowCascade(
                        splitData.cullingSphere, _tileSize, _filterSize);
                }

                info.handle = renderGraph.CreateShadowRendererList(ref shadowSettings);
                builder.UseRendererList(info.handle);
            }

            cullingInfos.perLightInfos[light.visibleLightIndex] =
                new LightShadowCasterCullingInfo
                {
                    projectionType = BatchCullingProjectionType.Orthographic,
                    splitRange = new RangeInt(splitOffset, cascadeCount)
                };
        }

        public void RenderDirectionalShadows(UnsafeCommandBuffer buffer)
        {
            buffer.BeginSample("Directional Shadows");
            if (_lightCount > 0)
            {
                buffer.SetRenderTarget(
                    _handles.atlas,
                    RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
                buffer.ClearRenderTarget(true, false, Color.clear);
                buffer.SetGlobalFloat(ShadowPancakingId, 1f);

                for (int i = 0; i < _lightCount; i++)
                {
                    RenderDirectionalShadows(i, buffer);
                }

                buffer.SetGlobalFloat(ShadowPancakingId, 0f);
            }

            buffer.SetGlobalDepthBias(0f, 0f);
            buffer.SetGlobalBuffer(CascadesId, _handles.cascadeBuffer);
            buffer.SetGlobalBuffer(MatricesId, _handles.matrixBuffer);
            buffer.SetGlobalTexture(AtlasId, _handles.atlas);
            buffer.SetGlobalInt(
                CascadeCountId, _lightCount > 0 ? _settings.cascadeCount : 0);
            buffer.SetBufferData(
                _handles.cascadeBuffer, Cascades, 0, 0, _settings.cascadeCount);
            buffer.SetBufferData(
                _handles.matrixBuffer, Matrices,
                0, 0, _lightCount * _settings.cascadeCount);
            buffer.SetKeyword(
                SoftCascadeBlendKeyword, _settings.softCascadeBlend);
            buffer.EndSample("Directional Shadows");
        }

        void RenderDirectionalShadows(int index, UnsafeCommandBuffer buffer)
        {
            int tileOffset = index * _settings.cascadeCount;
            float tileScale = 1f / _split;
            buffer.SetGlobalDepthBias(0f, _lights[index].slopeScaleBias);
            for (int i = 0; i < _settings.cascadeCount; i++)
            {
                RenderInfo info = _renderInfo[index * MaxCascades + i];
                int tileIndex = tileOffset + i;
                Matrices[tileIndex] = Shadows.ConvertToAtlasMatrix(
                    info.projection * info.view,
                    Shadows.SetTileViewport(buffer, tileIndex, _split, _tileSize),
                    tileScale);
                buffer.SetViewProjectionMatrices(info.view, info.projection);
                buffer.DrawRendererList(info.handle);
            }
        }
    }
}
