// Used by Unity to bake the terrain basemap. Outputs albedo RGB.
Shader "Hidden/CustomSRP/TerrainLitBasemapGen"
{
    Properties
    {
        [HideInInspector] _Control ("Control", 2D) = "red" {}
        [HideInInspector] _Splat0 ("Splat0", 2D) = "grey" {}
        [HideInInspector] _Splat1 ("Splat1", 2D) = "grey" {}
        [HideInInspector] _Splat2 ("Splat2", 2D) = "grey" {}
        [HideInInspector] _Splat3 ("Splat3", 2D) = "grey" {}
        [HideInInspector] _DstBlend ("DstBlend", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            ZTest Always Cull Off ZWrite Off
            Blend One [_DstBlend]

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "../ShaderLibrary/Common.hlsl"
            #include "TerrainLitInput.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 texcoord : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(input.positionOS);
                o.uv = input.texcoord;
                return o;
            }

            float4 Frag(Varyings input) : SV_TARGET
            {
                TerrainSurface s = SampleTerrainSurface(input.uv);
                return float4(s.albedo, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
