#ifndef CUSTOM_TERRAIN_LIT_INPUT_INCLUDED
#define CUSTOM_TERRAIN_LIT_INPUT_INCLUDED

// Unity Terrain injects these when using a materialTemplate.
TEXTURE2D(_Control);
TEXTURE2D(_Splat0);
TEXTURE2D(_Splat1);
TEXTURE2D(_Splat2);
TEXTURE2D(_Splat3);
SAMPLER(sampler_Control);
SAMPLER(sampler_Splat0);

float4 _Control_ST;
float4 _Splat0_ST;
float4 _Splat1_ST;
float4 _Splat2_ST;
float4 _Splat3_ST;

float _Metallic0, _Metallic1, _Metallic2, _Metallic3;
float _Smoothness0, _Smoothness1, _Smoothness2, _Smoothness3;
float _NormalScale0, _NormalScale1, _NormalScale2, _NormalScale3;

CBUFFER_START(UnityPerMaterial)
    float4 _BaseColor;
    float _Metallic;
    float _Smoothness;
    float _Fresnel;
CBUFFER_END

struct TerrainSurface
{
    float3 albedo;
    float alpha;
    float metallic;
    float smoothness;
};

float2 TransformTerrainUV(float2 uv, float4 st)
{
    return uv * st.xy + st.zw;
}

TerrainSurface SampleTerrainSurface(float2 controlUV)
{
    float4 control = SAMPLE_TEXTURE2D(_Control, sampler_Control, controlUV);
    // Normalize weights so missing layers still look stable.
    float wSum = max(dot(control, 1.0), 1e-5);
    control /= wSum;

    float2 uv0 = TransformTerrainUV(controlUV, _Splat0_ST);
    float2 uv1 = TransformTerrainUV(controlUV, _Splat1_ST);
    float2 uv2 = TransformTerrainUV(controlUV, _Splat2_ST);
    float2 uv3 = TransformTerrainUV(controlUV, _Splat3_ST);

    float4 s0 = SAMPLE_TEXTURE2D(_Splat0, sampler_Splat0, uv0);
    float4 s1 = SAMPLE_TEXTURE2D(_Splat1, sampler_Splat0, uv1);
    float4 s2 = SAMPLE_TEXTURE2D(_Splat2, sampler_Splat0, uv2);
    float4 s3 = SAMPLE_TEXTURE2D(_Splat3, sampler_Splat0, uv3);

    TerrainSurface s;
    s.albedo =
        s0.rgb * control.r +
        s1.rgb * control.g +
        s2.rgb * control.b +
        s3.rgb * control.a;
    s.albedo *= _BaseColor.rgb;
    s.alpha = 1.0;
    s.metallic =
        _Metallic0 * control.r +
        _Metallic1 * control.g +
        _Metallic2 * control.b +
        _Metallic3 * control.a;
    // Terrain splat A often stores smoothness; blend with layer defaults.
    float splatSmooth =
        s0.a * control.r +
        s1.a * control.g +
        s2.a * control.b +
        s3.a * control.a;
    float layerSmooth =
        _Smoothness0 * control.r +
        _Smoothness1 * control.g +
        _Smoothness2 * control.b +
        _Smoothness3 * control.a;
    s.smoothness = saturate(splatSmooth * layerSmooth);
    s.metallic = saturate(s.metallic);
    return s;
}

#endif
