using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    public static class CameraDebugger
    {
        const string PanelName = "Forward+";

        static readonly int
            ColorLUTResolutionId = Shader.PropertyToID("_ColorLUTResolution"),
            OpacityId = Shader.PropertyToID("_DebugOpacity");

        static Material _material;
        static bool _showTiles;
        static bool _showColorLUT;
        static float _opacity = 0.5f;

        public static bool IsActive =>
            (_showTiles && _opacity > 0f) || _showColorLUT;

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        public static void Initialize(Shader shader)
        {
            if (shader == null)
            {
                return;
            }

            _material = CoreUtils.CreateEngineMaterial(shader);
            DebugManager.instance.GetPanel(PanelName, true).children.Add(
                new DebugUI.FloatField
                {
                    displayName = "Opacity",
                    tooltip = "Opacity of the debug overlay.",
                    min = static () => 0f,
                    max = static () => 1f,
                    getter = static () => _opacity,
                    setter = static value => _opacity = value
                },
                new DebugUI.BoolField
                {
                    displayName = "Show Tiles",
                    tooltip = "Whether the debug overlay is shown.",
                    getter = static () => _showTiles,
                    setter = static value => _showTiles = value
                },
                new DebugUI.BoolField
                {
                    displayName = "Show Color LUT",
                    tooltip = "Whether the color lookup texture is shown.",
                    getter = static () => _showColorLUT,
                    setter = static value => _showColorLUT = value
                });
        }

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        public static void Cleanup()
        {
            CoreUtils.Destroy(_material);
            DebugManager.instance.RemovePanel(PanelName);
        }

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        public static void Render(UnsafeGraphContext context, int colorLUTResolution)
        {
            if (_material == null)
            {
                return;
            }

            UnsafeCommandBuffer buffer = context.cmd;
            if (_showTiles && _opacity > 0f)
            {
                buffer.SetGlobalFloat(OpacityId, _opacity);
                buffer.DrawProcedural(
                    Matrix4x4.identity, _material, 0, MeshTopology.Triangles, 3);
            }

            if (_showColorLUT && colorLUTResolution > 0)
            {
                buffer.SetGlobalFloat(ColorLUTResolutionId, colorLUTResolution);
                buffer.DrawProcedural(
                    Matrix4x4.identity, _material, 1, MeshTopology.Triangles, 6);
            }
        }
    }
}
