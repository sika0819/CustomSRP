using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP
{
    [CreateAssetMenu(menuName = "Rendering/Custom Render Pipeline Asset")]
    public class CustomRenderPipelineAsset : RenderPipelineAsset<CustomRenderPipeline>
    {
        [SerializeField]
        CustomRenderPipelineSettings settings = new CustomRenderPipelineSettings();

        public CustomRenderPipelineSettings Settings => settings;

        protected override RenderPipeline CreatePipeline() =>
            new CustomRenderPipeline(settings);
    }
}
