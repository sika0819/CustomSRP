// 族: 山 | 光: BoluoLitAlbedo | 雾: BoluoMixFog | 语言: docs/油画世界.md
// SM_Mountain：坡度出草/泥/岩（表色不动）。unique-UV 只做 ±8% 明度，不要拉对比。
// 移动端：1 次 LOD 采样，非法线贴图。远山弱接 BoluoOilGrainPerturbRock（远距几乎为 0）。
Shader "Boluo/StylizedMountain"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo (UV ridge mask)", 2D) = "grey" {}
        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        [Header(Terrain Layers No Sand)]
        _GrassColor("Grass Color", Color) = (0.5137, 0.6196, 0.3686, 1)
        _MudColor("Mud Color", Color) = (0.6078, 0.5608, 0.4235, 1)
        _RockColor("Rock Color", Color) = (0.5020, 0.4941, 0.3686, 1)
        _RidgeColor("Fold (A=0 use mud)", Color) = (0, 0, 0, 0)
        _GrassSlope("Grass Start (up)", Range(0, 1)) = 0.52
        _RockSlope("Grass Full (up)", Range(0, 1)) = 0.82
        _RidgeStart("Dark Value", Range(0, 1)) = 0.22
        _RidgeEnd("Rock Value", Range(0, 1)) = 0.62
        _RidgeStrength("Fold Strength", Range(0, 1)) = 0.55
        _LumaLod("Wide AO Mip", Range(0, 4)) = 0
        _LumaContrast("Value Contrast", Range(0.6, 2.2)) = 1.15
        _GrainStrength("UV Micro Grain", Range(0, 1)) = 0.45
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry"
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }
        LOD 100

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Stencil
            {
                Ref 1
                ReadMask 15
                WriteMask 15
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex MountainVert
            #pragma fragment MountainFrag

            #pragma multi_compile_instancing
            #pragma instancing_options procedural:BoluoGpuInstancingSetup
            #pragma multi_compile _ BOLUO_GPU_INDIRECT
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "BoluoStylizedMountain.hlsl"
            #include "BoluoLightingCommon.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord : TEXCOORD3;
#endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings MountainVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                output.shadowCoord = GetShadowCoord(posInputs);
#endif
                return output;
            }

            half4 MountainFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                // Fold / 坡度色只认几何法线，不要按视线翻面（绕山走时 n.y 变号）。
                half3 geoN = normalize(input.normalWS);
                half3 nWS;
                half3 albedo;
                half4 splatW;
                BoluoPaintMountain(geoN, input.positionWS, input.uv, albedo, nWS, splatW);
                nWS = BoluoShadingNormal(geoN, input.positionWS);
                nWS = BoluoOilGrainPerturbRock(nWS, input.positionWS, 0.45h);

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                Light mainLight = BoluoGetMainLight(input.positionWS, input.shadowCoord);
#else
                Light mainLight = BoluoGetMainLight(input.positionWS);
#endif
                half3 finalColor = BoluoLitAlbedo(albedo, mainLight, nWS, input.positionWS);
                finalColor = ApplyBoluoPainterlyLandscape(finalColor, input.positionWS, geoN);
                return half4(BoluoMixFog(finalColor, input.positionWS), 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "OilOutline"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Cull Front
            ZWrite On
            ZTest LEqual
            ColorMask RGBA
            Stencil
            {
                Ref 1
                ReadMask 15
                WriteMask 15
                Comp NotEqual
                Pass Keep
            }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex OilOutlineVert
            #pragma fragment OilOutlineFrag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:BoluoGpuInstancingSetup
            #pragma multi_compile _ BOLUO_GPU_INDIRECT
            #include "BoluoStylizedMountain.hlsl"
            #include "BoluoOilOutline.hlsl"
            #include "BoluoFog.hlsl"

            struct OilOutlineAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct OilOutlineVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionOS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                float2 uv : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            OilOutlineVaryings OilOutlineVert(OilOutlineAttributes input)
            {
                OilOutlineVaryings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                float3 posOS = BoluoOilOutlineExtrudeOS(input.positionOS.xyz, input.normalOS);
                VertexPositionInputs pos = GetVertexPositionInputs(posOS);
                o.positionCS = pos.positionCS;
                o.positionOS = posOS;
                o.positionWS = pos.positionWS;
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return o;
            }

            half4 OilOutlineFrag(OilOutlineVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half3 nWS;
                half3 albedo;
                half4 splatW;
                BoluoPaintMountain(normalize(input.normalWS), input.positionWS, input.uv, albedo, nWS, splatW);
                half4 c = BoluoOilOutlineFrag(
                    input.positionOS, input.positionWS, input.positionCS, input.normalWS, albedo);
                c.rgb = BoluoMixFog(c.rgb, input.positionWS);
                return c;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex BoluoShadowPassVertex
            #pragma fragment BoluoShadowPassFragment
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:BoluoGpuInstancingSetup
            #pragma multi_compile _ BOLUO_GPU_INDIRECT
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "BoluoStylizedMountain.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "BoluoOpaqueShadow.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex BoluoDepthOnlyVert
            #pragma fragment BoluoDepthOnlyFrag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:BoluoGpuInstancingSetup
            #pragma multi_compile _ BOLUO_GPU_INDIRECT

            #include "BoluoStylizedMountain.hlsl"
            #include "BoluoOpaqueDepth.hlsl"
            ENDHLSL
        }
    }

    FallBack Off
}
