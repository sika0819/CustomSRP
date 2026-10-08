using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    /// <summary>
    /// Light and shadow resources shared while recording a camera's render graph.
    /// </summary>
    public readonly ref struct LightResources
    {
        public readonly LightingPass.Handles lightHandles;
        public readonly ShadowsPass.Handles shadowHandles;

        public LightResources(
            LightingPass.Handles lightHandles,
            ShadowsPass.Handles shadowHandles)
        {
            this.lightHandles = lightHandles;
            this.shadowHandles = shadowHandles;
        }

        public void Use(IBaseRenderGraphBuilder builder)
        {
            lightHandles.Use(builder);
            shadowHandles.Use(builder);
        }
    }
}
