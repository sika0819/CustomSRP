Shader "CustomSRP/OilRainSplash"
{
    Properties
    {
        [HDR] _SplashColor ("Splash Color", Color) = (0.85, 0.92, 1.0, 1.0)
        _SplashOpacity ("Opacity", Range(0, 1)) = 0.85
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "CustomSRP"
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull Off
        Lighting Off

        Pass
        {
            Name "OilRainSplash"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex SplashVertex
            #pragma fragment SplashFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "UnityInput.hlsl"

            TEXTURE2D(_SplashFlipbook);
            SAMPLER(sampler_SplashFlipbook);

            CBUFFER_START(UnityPerMaterial)
                float4 _SplashColor;             // matches [HDR] _SplashColor in Properties
                float4 _SplashData;              // x = 0..1 playback progress, y = random seed, z = alpha scale, w = unused
                float4 _SplashFlipbookParams;    // x = columns, y = rows, z = frame count, w = global opacity
                float2 _SplashFlipbookTexel;     // x = 1/w, y = 1/h
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(SplashProps)
                UNITY_DEFINE_INSTANCED_PROP(float4, _SplashInstData)
            UNITY_INSTANCING_BUFFER_END(SplashProps)

            struct Attributes
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings SplashVertex(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(input.vertex.xyz);
                o.uv = input.uv;
                return o;
            }

            half4 SplashFragment(Varyings input) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float4 inst = UNITY_ACCESS_INSTANCED_PROP(SplashProps, _SplashInstData);

                float cols = max(_SplashFlipbookParams.x, 1.0);
                float frames = max(_SplashFlipbookParams.z, 1.0);

                // Frame index, with a per-instance row offset so a batch of
                // simultaneous splashes does not all animate in lockstep.
                float f = floor(frac(inst.x + inst.y) * frames);
                float2 cellPos = float2(fmod(f, cols), floor(f / cols));

                // Half-texel inset stops bilinear taps bleeding across frames.
                float2 inset = _SplashFlipbookTexel * 0.5;
                float2 cellUV = (1.0 - inset * 2.0 * float2(cols, _SplashFlipbookParams.y))
                              / float2(cols, _SplashFlipbookParams.y);

                float2 uv = cellPos + inset + input.uv * cellUV;

                half a = (half)SAMPLE_TEXTURE2D(_SplashFlipbook, sampler_SplashFlipbook, uv).a;

                // Fade the quad border so no square edge is ever visible.
                float2 edge = smoothstep(0.0, 0.08, input.uv) * smoothstep(0.0, 0.08, 1.0 - input.uv);
                a *= edge.x * edge.y * inst.z * _SplashFlipbookParams.w;

                // Overdraw control: a fully transparent splash writes nothing.
                clip(a - 0.004);

                return half4(_SplashColor.rgb, a);
            }
            ENDHLSL
        }
    }

    FallBack Off
}