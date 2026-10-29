using Unity.Collections;
using UnityEngine;
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
            ScriptableRenderContext context,
            bool isReflection = false)
        {
            // Planar mirror: Cull() above sets shadowDistance=0, _shadows.Setup was
            // skipped so directional/other HasLights carry stale true from the
            // previous main-camera frame. Force-reset HasLights here so
            // DirectionalShadowsPass / OtherShadowsPass skip BuildRendererLists,
            // and skip context.CullShadowCasters (expensive shadow caster cull).
            if (isReflection)
            {
                shadows.ResetHasLights();
            }

            int visibleLightCount = cullingResults.visibleLights.Length;
            var cullingInfos = new ShadowCastersCullingInfos
            {
                perLightInfos = new NativeArray<LightShadowCasterCullingInfo>(
                    visibleLightCount, Allocator.Temp),
                splitBuffer = new NativeArray<ShadowSplitData>(
                    visibleLightCount * Shadows.MaxTilesPerLight,
                    Allocator.Temp, NativeArrayOptions.UninitializedMemory)
            };

            try
            {
                var handles = new Handles(
                    DirectionalShadowsPass.Record(
                        renderGraph, cullingResults, cullingInfos, shadows),
                    OtherShadowsPass.Record(
                        renderGraph, cullingResults, cullingInfos, shadows));

                if (!isReflection &&
                    (shadows.directionalShadows.HasLights
                    || shadows.otherShadows.HasLights))
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
            finally
            {
                // CullShadowCasters copies what it needs before returning.
                // Leaving these undisposed keeps a safety record every frame.
                if (cullingInfos.perLightInfos.IsCreated)
                    cullingInfos.perLightInfos.Dispose();
                if (cullingInfos.splitBuffer.IsCreated)
                    cullingInfos.splitBuffer.Dispose();
            }
        }
    }
}
