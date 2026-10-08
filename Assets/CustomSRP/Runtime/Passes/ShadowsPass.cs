using Unity.Collections;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    public class ShadowsPass
    {
        public readonly ref struct Handles
        {
            public readonly DirectionalShadows.Handles directional;
            public readonly OtherShadows.Handles other;

            public Handles(
                DirectionalShadows.Handles directional,
                OtherShadows.Handles other)
            {
                this.directional = directional;
                this.other = other;
            }

            public void Use(IBaseRenderGraphBuilder builder)
            {
                directional.Use(builder);
                other.Use(builder);
            }
        }

        static readonly ProfilingSampler Sampler = new ProfilingSampler("Shadows");

        Shadows _shadows;

        void Render(UnsafeGraphContext context) =>
            _shadows.Render(context.cmd);

        public static Handles Record(
            RenderGraph renderGraph,
            CullingResults cullingResults,
            Shadows shadows,
            ScriptableRenderContext context)
        {
            int visibleLightCount = cullingResults.visibleLights.Length;
            var cullingInfos = new ShadowCastersCullingInfos
            {
                perLightInfos = new NativeArray<LightShadowCasterCullingInfo>(
                    visibleLightCount, Allocator.Temp),
                splitBuffer = new NativeArray<ShadowSplitData>(
                    visibleLightCount * Shadows.MaxTilesPerLight,
                    Allocator.Temp, NativeArrayOptions.UninitializedMemory)
            };

            var handles = new Handles(
                DirectionalShadowsPass.Record(
                    renderGraph, cullingResults, cullingInfos, shadows),
                OtherShadowsPass.Record(
                    renderGraph, cullingResults, cullingInfos, shadows));

            if (shadows.directionalShadows.HasLights
                || shadows.otherShadows.HasLights)
            {
                context.CullShadowCasters(cullingResults, cullingInfos);
            }

            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out ShadowsPass pass, Sampler);
            pass._shadows = shadows;
            builder.AllowPassCulling(false);
            builder.SetRenderFunc<ShadowsPass>(
                static (pass, context) => pass.Render(context));

            return handles;
        }
    }
}
