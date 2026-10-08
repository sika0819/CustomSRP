Shader "Hidden/CustomSRP/Camera Renderer"
{
    Properties
    {
        _CameraSrcBlend ("Src Blend", Float) = 1
        _CameraDstBlend ("Dst Blend", Float) = 0
    }

    SubShader
    {
        Cull Off
        ZTest Always
        ZWrite Off

        HLSLINCLUDE
        #include "../ShaderLibrary/Common.hlsl"
        #include "CameraRendererPasses.hlsl"
        ENDHLSL

        Pass
        {
            Name "Copy"

            Blend [_CameraSrcBlend] [_CameraDstBlend]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex DefaultPassVertex
            #pragma fragment CopyPassFragment
            ENDHLSL
        }

        Pass
        {
            Name "Copy Depth"

            ColorMask 0
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex DefaultPassVertex
            #pragma fragment CopyDepthPassFragment
            ENDHLSL
        }
    }
}
