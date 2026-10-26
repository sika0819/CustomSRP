using UnityEngine;

namespace CustomSRP
{
    [System.Serializable]
    public class CustomRenderPipelineSettings
    {
        public CameraBufferSettings cameraBuffer = new CameraBufferSettings
        {
            allowHDR = false,
            copyColor = true,
            copyDepth = true,
            renderScale = 1f,
            bicubicRescaling = CameraBufferSettings.BicubicRescalingMode.Off
        };

        public bool useSRPBatcher = true;

        public ForwardPlusSettings forwardPlus;

        public ShadowSettings shadows = new ShadowSettings();

        public FogSettings fog = new FogSettings();

        public PostFXSettings postFXSettings;

        public enum ColorLUTResolution
        {
            _16 = 16,
            _32 = 32,
            _64 = 64
        }

        public ColorLUTResolution colorLUTResolution = ColorLUTResolution._32;

        public Shader cameraRendererShader;
        public Shader cameraDebuggerShader;

        [Tooltip("Optional rain system configuration asset. Leave null to disable rain.")]
        public OilRainSettings rainSettings;
    }
}

