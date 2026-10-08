#ifndef CUSTOM_OIL_NPR_OUTLINE_PASS_INCLUDED
#define CUSTOM_OIL_NPR_OUTLINE_PASS_INCLUDED

half OilHash(float2 p)
{
    return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
}

half OilNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    half a = OilHash(i);
    half b = OilHash(i + float2(1.0, 0.0));
    half c = OilHash(i + float2(0.0, 1.0));
    half d = OilHash(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

half OilFbm(float2 p)
{
    half v = 0.0h;
    half a = 0.5h;
    UNITY_UNROLL
    for (int i = 0; i < 3; i++)
    {
        v += OilNoise(p) * a;
        p = p * 2.03 + 17.1;
        a *= 0.5h;
    }
    return v;
}

struct OutlineAttributes
{
    float3 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float2 baseUV : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct OutlineVaryings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : VAR_POSITION;
    float2 brushUV : VAR_BRUSH_UV;
    half widthMul : VAR_WIDTH;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

OutlineVaryings OilNPROutlineVertex(OutlineAttributes input)
{
    OutlineVaryings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    float3 positionWS = TransformObjectToWorld(input.positionOS);
    float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

    float2 nUV = positionWS.xz * _OutlineNoiseScale + input.baseUV * 3.0;
    half n0 = OilFbm((half2)nUV);
    half n1 = OilNoise((half2)(positionWS.xy * _OutlineNoiseScale * 1.7 + 5.3));
    half widthMul = lerp(1.0h - (half)_OutlineNoise, 1.0h + (half)_OutlineNoise, n0);
    widthMul *= lerp(0.65h, 1.35h, n1);

    float3 viewDir = normalize(_WorldSpaceCameraPos - positionWS);
    float3 side = normalize(cross(normalWS, viewDir));
    half sidePush = (n0 - 0.5h) * (half)_OutlineWobble * (half)_OutlineWidth;

    positionWS += normalWS * (_OutlineWidth * (float)widthMul);
    positionWS += side * (float)sidePush;

    output.positionWS = positionWS;
    output.positionCS = TransformWorldToHClip(positionWS);
    output.widthMul = widthMul;
    output.brushUV =
        input.baseUV * _OutlineBrushMap_ST.xy * _OutlineBrushScale +
        _OutlineBrushMap_ST.zw +
        positionWS.xz * (_OutlineBrushScale * 0.35);
    return output;
}

half4 OilNPROutlineFragment(OutlineVaryings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);

    half brush = (half)SAMPLE_TEXTURE2D(
        _OutlineBrushMap, sampler_OutlineBrushMap, input.brushUV).r;

    half dry = OilFbm((half2)(input.positionWS.xz * _OutlineNoiseScale * 2.5));
    brush = saturate(brush * 0.85h + dry * 0.35h);

    half breakThreshold = (half)_OutlineBreak + (1.0h - input.widthMul) * 0.12h;
    clip(brush - breakThreshold);

    half pressure = saturate(brush * input.widthMul);
    half3 ink = (half3)_OutlineColor.rgb;
    ink *= lerp(1.15h, 0.75h, pressure);
    ink = lerp(ink, ink * half3(1.1h, 0.95h, 0.8h), 1.0h - pressure);

    return half4(ink, 1.0h);
}

#endif
