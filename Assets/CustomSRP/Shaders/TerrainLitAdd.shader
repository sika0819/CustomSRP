// Extra splat layers (>4). Stub: this SRP MVP uses the first 4 layers only.
Shader "Hidden/CustomSRP/TerrainLitAdd"
{
    Properties
    {
        [HideInInspector] _Control ("Control (RGBA)", 2D) = "black" {}
        [HideInInspector] _Splat0 ("Layer 0", 2D) = "grey" {}
        [HideInInspector] _Splat1 ("Layer 1", 2D) = "grey" {}
        [HideInInspector] _Splat2 ("Layer 2", 2D) = "grey" {}
        [HideInInspector] _Splat3 ("Layer 3", 2D) = "grey" {}
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry-99"
            "RenderType" = "Opaque"
            "TerrainCompatible" = "True"
        }

        Pass
        {
            Tags { "LightMode" = "CustomLit" }
            Blend One One
            ZWrite Off
            ZTest Equal

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "../ShaderLibrary/Common.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(input.positionOS);
                return o;
            }

            float4 Frag(Varyings input) : SV_TARGET
            {
                return 0;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
