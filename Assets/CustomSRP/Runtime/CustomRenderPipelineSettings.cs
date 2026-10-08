using UnityEngine;

namespace CustomSRP
{
    [System.Serializable]
    public class CustomRenderPipelineSettings
    {
        public CameraBufferSettings cameraBuffer = new CameraBufferSettings
        {
            allowHDR = true,
            copyColor = true,
            copyDepth = true,
            renderScale = 1f,
            bicubicRescaling = CameraBufferSettings.BicubicRescalingMode.UpOnly,
            fxaa = new CameraBufferSettings.FXAA
            {
                enabled = true,
                fixedThreshold = 0.0833f,
                relativeThreshold = 0.166f,
                subpixelBlending = 0.75f,
                quality = CameraBufferSettings.FXAA.Quality.High
            }
        };

        public bool useSRPBatcher = true;

        public ForwardPlusSettings forwardPlus;

        public ShadowSettings shadows = new ShadowSettings();

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
    }
}

