// Camera-space rain curtain for CustomSRP (ported from Boluo/Mobile/Rain).
Shader "CustomSRP/OilRainStreakQuad"
{
    Properties
    {
        [HDR] _RainColor ("Color", Color) = (0.9, 0.9, 0.92, 0.36)
        _Intensity ("Intensity", Range(0, 1.5)) = 0
        _Density ("Density", Range(0.2, 4)) = 1
        _RainTime ("Rain Time", Float) = 0
        _Wind ("Wind", Range(-2, 2)) = 0.1
        _Quality ("Quality", Range(0.25, 1)) = 1
        _Mist ("Mist", Range(0, 1)) = 0.3
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "CustomSRP"
            "Queue" = "Transparent+50"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        // Additive streaks — matches reference RainCurtain.
        Blend SrcAlpha One
        ZWrite Off
        ZTest Always
        Cull Off
        ColorMask RGB

        Pass
        {
            Name "OilRainStreakQuad"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex StreakQuadVertex
            #pragma fragment StreakQuadFragment

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "UnityInput.hlsl"
            #include "OilRainStreakQuadPass.hlsl"

            struct Attributes
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uvScroll   : TEXCOORD0;
                float2 uvMesh     : TEXCOORD1;
            };

            Varyings StreakQuadVertex(Attributes input)
            {
                Varyings o;
                float3 pos = input.vertex.xyz;
                // Wind shear in object space (quad is camera-local).
                pos.x += _Wind * 0.10 * input.uv.y;
                float3 positionWS = TransformObjectToWorld(pos);
                o.positionCS = TransformWorldToHClip(positionWS);

                float2 uv = input.uv;
                uv.x += _Wind * 0.045 * input.uv.y;
                uv.y += _RainTime;
                o.uvScroll = uv;
                o.uvMesh = input.uv;
                return o;
            }

            half4 StreakQuadFragment(Varyings input) : SV_TARGET
            {
                return OilRainStreakQuadFragment(input.uvScroll, input.uvMesh);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
