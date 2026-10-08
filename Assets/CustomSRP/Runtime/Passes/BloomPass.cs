using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using static CustomSRP.PostFXStack;

namespace CustomSRP
{
    public class BloomPass
    {
        const int MaxBloomPyramidLevels = 16;

        static readonly int
            bicubicUpsamplingId = Shader.PropertyToID("_BloomBicubicUpsampling"),
            intensityId = Shader.PropertyToID("_BloomIntensity"),
            thresholdId = Shader.PropertyToID("_BloomThreshold");

        static readonly ProfilingSampler Sampler = new ProfilingSampler("Bloom");

        readonly TextureHandle[] _pyramid =
            new TextureHandle[2 * MaxBloomPyramidLevels + 1];

        TextureHandle _colorSource;
        TextureHandle _bloomResult;
        PostFXStack _stack;
        int _stepCount;

        void Render(UnsafeGraphContext context)
        {
            UnsafeCommandBuffer buffer = context.cmd;
            PostFXSettings.BloomSettings bloom = _stack.Settings.Bloom;

            Vector4 threshold;
            threshold.x = Mathf.GammaToLinearSpace(bloom.threshold);
            threshold.y = threshold.x * bloom.thresholdKnee;
            threshold.z = 2f * threshold.y;
            threshold.w = 0.25f / (threshold.y + 0.00001f);
            threshold.y -= threshold.x;
            buffer.SetGlobalVector(thresholdId, threshold);

            _stack.Draw(
                buffer, _colorSource, _pyramid[0],
                bloom.fadeFireflies ? Pass.BloomPrefilterFireflies : Pass.BloomPrefilter);

            int fromId = 0, toId = 2;
            int i;
            for (i = 0; i < _stepCount; i++)
            {
                int midId = toId - 1;
                _stack.Draw(buffer, _pyramid[fromId], _pyramid[midId], Pass.BloomHorizontal);
                _stack.Draw(buffer, _pyramid[midId], _pyramid[toId], Pass.BloomVertical);
                fromId = toId;
                toId += 2;
            }

            buffer.SetGlobalFloat(
                bicubicUpsamplingId, bloom.bicubicUpsampling ? 1f : 0f);

            Pass combinePass, finalPass;
            float finalIntensity;
            if (bloom.mode == PostFXSettings.BloomSettings.Mode.Additive)
            {
                combinePass = finalPass = Pass.BloomAdd;
                buffer.SetGlobalFloat(intensityId, 1f);
                finalIntensity = bloom.intensity;
            }
            else
            {
                combinePass = Pass.BloomScatter;
                finalPass = Pass.BloomScatterFinal;
                buffer.SetGlobalFloat(intensityId, bloom.scatter);
                finalIntensity = Mathf.Min(bloom.intensity, 1f);
            }

            if (i > 1)
            {
                toId -= 5;
                for (i -= 1; i > 0; i--)
                {
                    buffer.SetGlobalTexture(fxSource2Id, _pyramid[toId + 1]);
                    _stack.Draw(buffer, _pyramid[fromId], _pyramid[toId], combinePass);
                    fromId = toId;
                    toId -= 2;
                }
            }

            buffer.SetGlobalFloat(intensityId, finalIntensity);
            buffer.SetGlobalTexture(fxSource2Id, _colorSource);
            _stack.Draw(buffer, _pyramid[fromId], _bloomResult, finalPass);
        }

        public static TextureHandle Record(
            RenderGraph renderGraph,
            PostFXStack stack,
            in CameraRendererTextures textures)
        {
            PostFXSettings.BloomSettings bloom = stack.Settings.Bloom;
            Vector2Int size = (bloom.ignoreRenderScale
                ? new Vector2Int(stack.Camera.pixelWidth, stack.Camera.pixelHeight)
                : stack.BufferSize) / 2;

            int downscaleLimit = Mathf.Max(1, bloom.downscaleLimit);

            if (bloom.maxIterations == 0 ||
                bloom.intensity <= 0f ||
                size.y < downscaleLimit * 2 ||
                size.x < downscaleLimit * 2)
            {
                return textures.colorAttachment;
            }

            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out BloomPass pass, Sampler);
            pass._stack = stack;
            pass._colorSource = textures.colorAttachment;
            builder.UseTexture(textures.colorAttachment);

            var desc = new TextureDesc(size.x, size.y)
            {
                colorFormat = SystemInfo.GetGraphicsFormat(
                    stack.BufferSettings.allowHDR
                        ? DefaultFormat.HDR : DefaultFormat.LDR),
                name = "Bloom Prefilter",
                filterMode = FilterMode.Bilinear
            };
            TextureHandle[] pyramid = pass._pyramid;
            pyramid[0] = builder.CreateTransientTexture(desc);
            size /= 2;

            int pyramidIndex = 1;
            int i;
            for (i = 0; i < bloom.maxIterations; i++, pyramidIndex += 2)
            {
                if (size.y < downscaleLimit || size.x < downscaleLimit)
                {
                    break;
                }

                desc.width = size.x;
                desc.height = size.y;
                desc.name = "Bloom Pyramid H";
                pyramid[pyramidIndex] = builder.CreateTransientTexture(desc);
                desc.name = "Bloom Pyramid V";
                pyramid[pyramidIndex + 1] = builder.CreateTransientTexture(desc);
                size /= 2;
            }

            pass._stepCount = i;

            desc.width = stack.BufferSize.x;
            desc.height = stack.BufferSize.y;
            desc.name = "Bloom Result";
            pass._bloomResult = renderGraph.CreateTexture(desc);
            builder.UseTexture(pass._bloomResult, AccessFlags.WriteAll);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc<BloomPass>(
                static (pass, context) => pass.Render(context));
            return pass._bloomResult;
        }
    }
}
