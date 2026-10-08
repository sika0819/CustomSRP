using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP
{
    [Serializable]
    public class CameraSettings
    {
        [Serializable]
        public struct FinalBlendMode
        {
            public BlendMode source, destination;
        }

        public enum RenderScaleMode
        {
            Inherit,
            Multiply,
            Override
        }

        public bool copyColor = true;

        public bool copyDepth = true;

        public bool overridePostFX;

        public PostFXSettings postFXSettings = default;

        public FinalBlendMode finalBlendMode = new FinalBlendMode
        {
            source = BlendMode.One,
            destination = BlendMode.Zero
        };

        [UnityEngine.Serialization.FormerlySerializedAs("newRenderingLayerMask")]
        public RenderingLayerMask renderingLayerMask = -1;

        public bool maskLights;

        public RenderScaleMode renderScaleMode = RenderScaleMode.Inherit;

        [Range(CameraRenderer.renderScaleMin, CameraRenderer.renderScaleMax)]
        public float renderScale = 1f;

        public bool allowFXAA = false;

        public bool keepAlpha = false;

        public float GetRenderScale(float scale)
        {
            return renderScaleMode == RenderScaleMode.Inherit
                ? scale
                : renderScaleMode == RenderScaleMode.Override
                    ? renderScale
                    : scale * renderScale;
        }
    }
}
