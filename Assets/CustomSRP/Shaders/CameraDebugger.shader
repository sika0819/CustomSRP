Shader "Hidden/CustomSRP/Camera Debugger"
{
    SubShader
    {
        Cull Off
        ZTest Always
        ZWrite Off

        HLSLINCLUDE
        #include "../ShaderLibrary/Common.hlsl"
        #include "CameraDebuggerPasses.hlsl"
        ENDHLSL

        Pass
        {
            Name "Forward+ Tiles"

            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DefaultPassVertex
            #pragma fragment ForwardPlusTilesPassFragment
            ENDHLSL
        }

        Pass
        {
            Name "Show Color LUT"

            Blend One Zero

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ColorLUTPassVertex
            #pragma fragment ColorLUTPassFragment
            ENDHLSL
        }
    }
}
