Shader "Mine/CutFireShader"
{
    // Dead-Rabbit UnityFire Cut flame — Custom SRP.
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _noiseTex ("noiseTex", 2D) = "white" {}
        _bottomMask ("bottomMask", 2D) = "white" {}
        _colorGridentTex ("colorGridentMap", 2D) = "white" {}
        _displaceStrength ("displaceStrength", Range(0, 1)) = 0.1
        _time0 ("time0", Float) = 0
        _colorScale ("colorScale", Range(0, 2)) = 1
        _alphaScale ("alpha", Range(0, 2)) = 1
        _Size ("Size", Range(0.0, 100)) = 40
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend One OneMinusSrcAlpha

        HLSLINCLUDE
        #include "../../../CustomSRP/ShaderLibrary/Common.hlsl"
        ENDHLSL

        Pass
        {
            Name "CutFire"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_noiseTex);
            SAMPLER(sampler_noiseTex);
            TEXTURE2D(_bottomMask);
            SAMPLER(sampler_bottomMask);
            TEXTURE2D(_colorGridentTex);
            SAMPLER(sampler_colorGridentTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _displaceStrength;
                float _time0;
                float _colorScale;
                float _alphaScale;
                float _Size;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;
                return output;
            }

            float4 Frag(Varyings input) : SV_TARGET
            {
                float2 uv = input.uv;
                float invSize = 1.0 / max(_Size, 0.0001);
                uv.x = uv.x - frac(uv.x * _Size) * invSize;
                uv.y = uv.y - frac(uv.y * _Size) * invSize;

                float bottomMask = SAMPLE_TEXTURE2D(_bottomMask, sampler_bottomMask, uv).r;

                float2 noiseUV = uv;
                noiseUV.y *= 0.6;
                noiseUV += float2(0.0, -(_Time.y * 0.8 + _time0));
                float noise = SAMPLE_TEXTURE2D(_noiseTex, sampler_noiseTex, noiseUV).r;
                noise -= 0.5;

                float discontinueMask = SAMPLE_TEXTURE2D(_noiseTex, sampler_noiseTex, uv).r;
                discontinueMask = lerp(discontinueMask, 0.0, bottomMask);

                float displace = noise * discontinueMask * (_displaceStrength / 0.1);
                float4 shapeColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, float2(uv.x + displace, uv.y));

                float grayScale = 0.3 * shapeColor.r + 0.6 * shapeColor.g + 0.1 * shapeColor.b;
                float4 finalColor = SAMPLE_TEXTURE2D(_colorGridentTex, sampler_colorGridentTex, float2(grayScale, uv.y));
                finalColor = finalColor * 1.5 * _colorScale;
                finalColor.a = saturate(grayScale * 8.0) * 0.6 * _alphaScale;
                finalColor.rgb *= finalColor.a;
                return finalColor;
            }
            ENDHLSL
        }
    }
}
