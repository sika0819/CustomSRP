using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP.Editor
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(Light))]
    [SupportedOnRenderPipeline(typeof(CustomRenderPipelineAsset))]
    public class CustomLightEditor : LightEditor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            if (!settings.lightType.hasMultipleDifferentValues &&
                (LightType)settings.lightType.enumValueIndex == LightType.Spot)
            {
                settings.DrawInnerAndOuterSpotAngle();
            }

            settings.ApplyModifiedProperties();

            var light = target as Light;
            if (light != null && light.cullingMask != -1)
            {
                EditorGUILayout.HelpBox(
                    "Culling Mask only affects shadows.", MessageType.Warning);
            }
        }
    }
}
