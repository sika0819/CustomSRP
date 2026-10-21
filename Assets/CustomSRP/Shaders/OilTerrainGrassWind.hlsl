#ifndef CUSTOM_OIL_TERRAIN_GRASS_WIND_INCLUDED
#define CUSTOM_OIL_TERRAIN_GRASS_WIND_INCLUDED

float3 _OilGrassWindDir;
float _OilGrassWindStrength;
float _OilGrassMaxDraw;

float OilGrassWindDisplace(float3 positionWS, float windStrength, float tip, float dry)
{
    float gust = 0.65 + 0.35 * sin(_Time.y * 1.7 + positionWS.x * 0.05 + positionWS.z * 0.04);
    float wind = sin(_Time.y * 3.2 + positionWS.x * 0.08 + positionWS.z * 0.08) * 0.42
        + sin(_Time.y * 6.4 + positionWS.x * 0.31 + positionWS.z * 2.4) * 0.12;
    float amp = _OilGrassWindStrength * windStrength;
    return wind * amp * gust * tip * lerp(1.0, 1.55, dry);
}

float OilGrassDistanceFade(float3 pivotWS)
{
    float maxD = max(_OilGrassMaxDraw, 8.0);
    float3 cam = _WorldSpaceCameraPos;
    float dx = pivotWS.x - cam.x;
    float dz = pivotWS.z - cam.z;
    float d = sqrt(dx * dx + dz * dz);
    return saturate(1.0 - smoothstep(maxD * 0.82, maxD, d));
}

#endif
