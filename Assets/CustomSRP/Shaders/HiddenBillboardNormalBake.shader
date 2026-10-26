// Bake-only: object-space normals from near-tree LOD0 into billboard atlas RGB.
// LightMode must be SRPDefaultUnlit so CustomSRP GeometryPass draws it via Camera.Render.
Shader "Hidden/CustomSRP/BillboardNormalBake"
{
    Properties
    {
        _BaseMap ("Albedo", 2D) = "white" {}
        _BumpMap ("Tangent Normal", 2D) = "bump" {}
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.32
        _HasBumpMap ("Has Bump", Float) = 0
        _BumpScale ("Bump Scale", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "BillboardNormalBake"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "../ShaderLibrary/Common.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BumpMap_ST;
                float _Cutoff;
                float _HasBumpMap;
                float _BumpScale;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 baseUV : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 baseUV : VAR_BASE_UV;
                float3 normalOS : VAR_NORMAL;
                float4 tangentOS : VAR_TANGENT;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.baseUV = TRANSFORM_TEX(input.baseUV, _BaseMap);
                output.normalOS = input.normalOS;
                output.tangentOS = input.tangentOS;
                return output;
            }

            float4 Frag(Varyings input) : SV_TARGET
            {
                float4 base = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.baseUV);
                clip(base.a - _Cutoff);

                float3 n = normalize(input.normalOS);
                if (_HasBumpMap > 0.5)
                {
                    float3 t = normalize(input.tangentOS.xyz);
                    float3 b = cross(n, t) * input.tangentOS.w;
                    float3 tn = DecodeNormal(
                        SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.baseUV),
                        _BumpScale);
                    n = normalize(mul(tn, float3x3(t, b, n)));
                }

                return float4(n * 0.5 + 0.5, base.a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
