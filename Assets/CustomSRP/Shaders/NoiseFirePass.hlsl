#ifndef CUSTOM_NOISE_FIRE_PASS_INCLUDED
#define CUSTOM_NOISE_FIRE_PASS_INCLUDED

// Port of Dead-Rabbit/UnityFire (FireShader1 / CutFire / CycleFire) to CustomSRP.
// https://github.com/Dead-Rabbit/UnityFire

TEXTURE2D(_MainTex);
SAMPLER(sampler_MainTex);
TEXTURE2D(_NoiseTex);
SAMPLER(sampler_NoiseTex);
TEXTURE2D(_BottomMask);
SAMPLER(sampler_BottomMask);
TEXTURE2D(_ColorGradientTex);
SAMPLER(sampler_ColorGradientTex);

CBUFFER_START(UnityPerMaterial)
    float4 _MainTex_ST;
    float4 _NoiseTex_ST;
    float _DisplaceStrength;
    float _Time0;
    float _ColorScale;
    float _AlphaScale;
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
    float2 uv : VAR_FIRE_UV;
};

Varyings NoiseFireVertex(Attributes input)
{
    Varyings output;
    float3 positionWS = TransformObjectToWorld(input.positionOS);
    output.positionCS = TransformWorldToHClip(positionWS);
    output.uv = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;
    return output;
}

float4 SampleFire(float2 uv)
{
#if defined(_FIREMODE_CUT) || defined(_FIREMODE_CYCLE)
    float2 po = uv;
    float invSize = 1.0 / max(_Size, 0.0001);
#if defined(_FIREMODE_CYCLE)
    uv.x = uv.x - frac(uv.x * _Size) * invSize + 0.5 * invSize;
    uv.y = uv.y - frac(uv.y * _Size) * invSize + 0.5 * invSize;
    float dx = po.x - uv.x;
    float dy = po.y - uv.y;
    clip(rsqrt(dx * dx + dy * dy) - _Size * 2.2);
#else
    // CutFire: quantize UV to blocky oil cells.
    uv.x = uv.x - frac(uv.x * _Size) * invSize;
    uv.y = uv.y - frac(uv.y * _Size) * invSize;
#endif
    float bottomMask = SAMPLE_TEXTURE2D(_BottomMask, sampler_BottomMask, uv).r;
#else
    // FireShader1: bottom (R) + top (G) mask.
    float2 bottomTop = SAMPLE_TEXTURE2D(_BottomMask, sampler_BottomMask, uv).rg;
    float bottomMask = bottomTop.r;
#endif

    // Scroll noise upward; blur sample to avoid oil-cell mosaic displace.
    float2 noiseUV = uv;
    noiseUV.y *= 0.6;
    noiseUV += float2(0.0, -(_Time.y * 0.8 + _Time0));
    const float2 nOff = float2(0.012, 0.012);
    float noise = (
        SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, noiseUV).r +
        SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, noiseUV + nOff).r +
        SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, noiseUV - nOff).r) * (1.0 / 3.0);
    noise -= 0.5;

    // Sway limited near base (bottom mask → 0).
    float discontinueMask = SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, uv).r;
    discontinueMask = lerp(discontinueMask, 0.0, bottomMask);

    // Default 0.1 = FireShader1 full sway; keep mild to reduce block edges.
    float displace = noise * discontinueMask * (_DisplaceStrength / 0.1) * 0.85;
    float4 shapeColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, float2(uv.x + displace, uv.y));

    float grayScale = 0.3 * shapeColor.r + 0.6 * shapeColor.g + 0.1 * shapeColor.b;

    // Brighter LUT u for color → avoids dark premult fringe at soft edges.
    float colorU = saturate(grayScale * 1.15 + 0.12);
    float4 finalColor = SAMPLE_TEXTURE2D(_ColorGradientTex, sampler_ColorGradientTex, float2(colorU, uv.y));
    finalColor.rgb *= 1.5 * _ColorScale;

    // Soft alpha (was gray*8 → hard stair-steps / mosaic silhouettes).
    float alpha = smoothstep(0.02, 0.55, grayScale);
    alpha = saturate(alpha * 0.95) * _AlphaScale;

    // Hide quad card edges.
    float edge = smoothstep(0.0, 0.06, uv.x) * smoothstep(0.0, 0.06, 1.0 - uv.x)
               * smoothstep(0.0, 0.04, uv.y) * smoothstep(0.0, 0.08, 1.0 - uv.y);
    alpha *= edge;

    finalColor.a = alpha;
    finalColor.rgb *= finalColor.a;
    return finalColor;
}

float4 NoiseFireFragment(Varyings input) : SV_TARGET
{
    return SampleFire(input.uv);
}

#endif
