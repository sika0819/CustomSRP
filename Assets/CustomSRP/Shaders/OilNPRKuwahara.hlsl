#ifndef CUSTOM_OIL_NPR_KUWAHARA_INCLUDED
#define CUSTOM_OIL_NPR_KUWAHARA_INCLUDED

// UV-space Kuwahara. Mobile: 1 tap / quadrant + center (~5 fetches).
// Desktop: sparse 2x2 + center per quadrant (~20 fetches).
half4 SampleKuwaharaUV(float2 uv, half radiusUV)
{
    radiusUV = max(radiusUV, 0.001h);
    half2 stepUV = half2(radiusUV, radiusUV) * 0.5h;

    half3 mean[4];
    half var[4];

    UNITY_UNROLL
    for (int q = 0; q < 4; q++)
    {
        half2 qSign = half2(
            (q == 0 || q == 3) ? 1.0h : -1.0h,
            (q == 0 || q == 1) ? 1.0h : -1.0h);

        half3 sum = 0.0h;
        half3 sumSq = 0.0h;
        half count = 0.0h;

#if defined(SHADER_API_MOBILE)
        half2 offset = stepUV * qSign;
        half3 c = (half3)SAMPLE_TEXTURE2D(
            _BaseMap, sampler_BaseMap, uv + (float2)offset).rgb;
        sum += c;
        sumSq += c * c;
        count = 1.0h;
#else
        UNITY_UNROLL
        for (int y = 0; y <= 1; y++)
        {
            UNITY_UNROLL
            for (int x = 0; x <= 1; x++)
            {
                half2 offset = half2((half)x, (half)y) * stepUV * qSign;
                half3 c = (half3)SAMPLE_TEXTURE2D(
                    _BaseMap, sampler_BaseMap, uv + (float2)offset).rgb;
                sum += c;
                sumSq += c * c;
                count += 1.0h;
            }
        }
#endif

        half3 center = (half3)SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).rgb;
        sum += center;
        sumSq += center * center;
        count += 1.0h;

        half inv = rcp(count);
        mean[q] = sum * inv;
        half3 m2 = sumSq * inv;
        var[q] = dot(m2 - mean[q] * mean[q], half3(1.0h, 1.0h, 1.0h));
    }

    half bestVar = var[0];
    half3 bestMean = mean[0];
    UNITY_UNROLL
    for (int i = 1; i < 4; i++)
    {
        if (var[i] < bestVar)
        {
            bestVar = var[i];
            bestMean = mean[i];
        }
    }

    return half4(bestMean, 1.0h);
}

#endif
