using System;
using UnityEngine;

namespace CustomSRP
{
    [Serializable]
    public struct CameraBufferSettings
    {
        public bool allowHDR;
        public bool copyColor;
        public bool copyColorReflection;
        public bool copyDepth;
        public bool copyDepthReflection;

        [Range(CameraRenderer.renderScaleMin, CameraRenderer.renderScaleMax)]
        public float renderScale;

        public enum BicubicRescalingMode
        {
            Off,
            UpOnly,
            UpAndDown
        }

        public BicubicRescalingMode bicubicRescaling;
    }
}
