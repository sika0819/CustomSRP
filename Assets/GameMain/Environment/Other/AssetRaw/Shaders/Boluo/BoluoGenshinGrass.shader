// 族: 植被 | 光: BoluoLitAlbedoObject | 雾: BoluoMixFoliageFog | 语言: docs/油画世界.md
// Mobile drop-in for Distant Lands Stylized Grass.
// Same property names / look; 1 albedo sample; no snow/SSR/extra lights (4K bandwidth friendly).
Shader "Boluo/Genshin/Grass"
{
    Properties
    {
        _GrassTexture("Grass Texture", 2D) = "white" {}
        _AlphaClip("Alpha Clip", Float) = 0.5
        _TopColor("Top Color", Color) = (0.36, 0.80, 0.51, 0)
        _BottomColor("Bottom Color", Color) = (0.36, 0.80, 0.51, 0)
        _GradientAmount("Gradient Amount", Float) = 0.3
        _WindScale("Wind Scale", Float) = 1
        _WindSpeed("Wind Speed", Float) = 0.3
        _WindStrength("Wind Strength", Vector) = (0.2, 0, 0.2, 0)

        [HideInInspector] _Cutoff("Cutoff", Range(0, 1)) = 0.5
        [HideInInspector][ToggleOff] _ReceiveShadows("Receive Shadows", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "TransparentCutout"
            "Queue" = "AlphaTest"
            "IgnoreProjector" = "True"
        }
        LOD 100
        Cull Back
        ZWrite On
        AlphaToMask Off

        // -------- Forward (single albedo sample) --------
        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #define BOLUO_FOLIAGE_SKIP_OBJECT_BRUSH 1
            #include "BoluoLightingCommon.hlsl"
            #include "BoluoSeason.hlsl"
            #include "BoluoHashedAlpha.hlsl"

            TEXTURE2D(_GrassTexture);
            SAMPLER(sampler_GrassTexture);

            CBUFFER_START(UnityPerMaterial)
                float4 _GrassTexture_ST;
                half4 _TopColor;
                half4 _BottomColor;
                half _GradientAmount;
                half _AlphaClip;
                half _Cutoff;
                float _WindScale;
                float _WindSpeed;
                float3 _WindStrength;
            CBUFFER_END

            #include "BoluoStylizedGrassWind.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                half gradient : TEXCOORD3;
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord : TEXCOORD4;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                half windMask = input.color.r;
                float3 positionOS = BoluoStylizedGrassApplyWind(input.positionOS.xyz, input.uv, windMask);
                VertexPositionInputs posInputs = GetVertexPositionInputs(positionOS);
                VertexNormalInputs nrmInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS = nrmInputs.normalWS;
                output.uv = input.uv;
                // Original lerp factor: saturate(vertexColor * GradientAmount); grass uses .r as height weight.
                output.gradient = saturate(input.color.r * _GradientAmount);

                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                output.shadowCoord = GetShadowCoord(posInputs);
                #endif
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Single albedo+alpha fetch — primary bandwidth cost.
                float2 albedoUv = TRANSFORM_TEX(input.uv, _GrassTexture);
                half4 tex = SAMPLE_TEXTURE2D(_GrassTexture, sampler_GrassTexture, albedoUv);
                half clipRef = max(_AlphaClip, _Cutoff);
                BoluoClipHashedAlpha(tex.a, clipRef, input.positionCS, input.positionWS);

                half3 tint = BoluoSeasonGrassTint(_TopColor.rgb, _BottomColor.rgb, input.gradient);
                half luma = dot(tex.rgb, half3(0.299h, 0.587h, 0.114h));
                half blackFringe = (tex.a < 0.92h) * step(luma, 0.20h);
                half3 ramp = lerp(half3(luma, luma, luma), half3(1, 1, 1), blackFringe);
                half3 albedo = BoluoSeasonColorRamp(ramp, tint);

                half3 normalWS = NormalizeNormalPerPixel(input.normalWS);
                albedo = BoluoSeasonApplyGrassSnow(albedo, input.gradient, input.positionWS);
#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                Light mainLight = BoluoGetMainLight(input.positionWS, input.shadowCoord);
#else
                Light mainLight = BoluoGetMainLight(input.positionWS);
#endif
                half3 color = BoluoLitAlbedoObject(albedo, mainLight, normalWS, input.positionWS);
                color = BoluoNightKeepFoliage(color, albedo, normalWS);
                color = BoluoPainterlyContourWash(color, input.positionWS, tex.a);
                return half4(BoluoMixFoliageFog(color, input.positionWS), 1);
            }
            ENDHLSL
        }

        // -------- ShadowCaster --------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            // Shadows.hlsl 用 LerpWhiteTo（定义在 CommonMaterial）；缺 include 时 Metal 报 undeclared。
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "BoluoOpaqueShadow.hlsl"
            #include "BoluoHashedAlpha.hlsl"

            TEXTURE2D(_GrassTexture);
            SAMPLER(sampler_GrassTexture);

            CBUFFER_START(UnityPerMaterial)
                float4 _GrassTexture_ST;
                half4 _TopColor;
                half4 _BottomColor;
                half _GradientAmount;
                half _AlphaClip;
                half _Cutoff;
                float _WindScale;
                float _WindSpeed;
                float3 _WindStrength;
            CBUFFER_END

            #include "BoluoStylizedGrassWind.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 positionOS = BoluoStylizedGrassApplyWind(input.positionOS.xyz, input.uv, input.color.r);
                float3 positionWS = TransformObjectToWorld(positionOS);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionCS = BoluoShadowPositionHClip(positionWS, normalWS);
                output.positionWS = positionWS;
                output.uv = TRANSFORM_TEX(input.uv, _GrassTexture);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half a = SAMPLE_TEXTURE2D(_GrassTexture, sampler_GrassTexture, input.uv).a;
                BoluoClipHashedAlpha(a, max(_AlphaClip, _Cutoff), input.positionCS, input.positionWS);
                return 0;
            }
            ENDHLSL
        }

        // -------- DepthOnly (early-Z, cuts 4K overdraw) --------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "BoluoHashedAlpha.hlsl"

            TEXTURE2D(_GrassTexture);
            SAMPLER(sampler_GrassTexture);

            CBUFFER_START(UnityPerMaterial)
                float4 _GrassTexture_ST;
                half4 _TopColor;
                half4 _BottomColor;
                half _GradientAmount;
                half _AlphaClip;
                half _Cutoff;
                float _WindScale;
                float _WindSpeed;
                float3 _WindStrength;
            CBUFFER_END

            #include "BoluoStylizedGrassWind.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                float3 positionOS = BoluoStylizedGrassApplyWind(input.positionOS.xyz, input.uv, input.color.r);
                float3 positionWS = TransformObjectToWorld(positionOS);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.positionWS = positionWS;
                output.uv = TRANSFORM_TEX(input.uv, _GrassTexture);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half a = SAMPLE_TEXTURE2D(_GrassTexture, sampler_GrassTexture, input.uv).a;
                BoluoClipHashedAlpha(a, max(_AlphaClip, _Cutoff), input.positionCS, input.positionWS);
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
