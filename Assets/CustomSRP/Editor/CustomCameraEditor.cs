using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP.Editor
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(Camera))]
    [SupportedOnRenderPipeline(typeof(CustomRenderPipelineAsset))]
    public class CustomCameraEditor : UnityEditor.Editor
    {
    }
}
