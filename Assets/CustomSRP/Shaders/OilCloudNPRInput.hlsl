#ifndef CUSTOM_OIL_CLOUD_NPR_INPUT_INCLUDED
#define CUSTOM_OIL_CLOUD_NPR_INPUT_INCLUDED

TEXTURE2D(_CloudMap);
SAMPLER(sampler_CloudMap);
TEXTURE2D(_NoiseMap);
SAMPLER(sampler_NoiseMap);
TEXTURE2D(_CloudBrush);
SAMPLER(sampler_CloudBrush);

// Per camera. Set from the cloud pass, not the material.
float3 _OilCloudSunDir;
float3 _OilCloudMoonDir;
float4 _OilCloudColor;
float4 _OilCloudHighlight;
float4 _OilCloudZenith;
float4 _OilCloudHorizon;
float4 _OilCloudAmbientSky;
float4 _OilCloudAmbientEquator;
float4 _OilCloudAmbientGround;
float4 _OilCloudParams; // x moonBlend, y coverage, z unused, w unused
float4 _OilCloudWind;   // x yaw speed, y yaw radians, z cell disturb, w wind 0-1
float _OilCloudDawn;
float _OilCloudNight;
float _OilCloudRingBias;

CBUFFER_START(UnityPerMaterial)
    float4 _NoiseMap_ST;
    float _UVDisturbance;
    float _SdfSoftness;
    float _SdfMin;
    float _SdfMax;
    float _TopShadow;
    float _TopHighlight;
    float _EdgeIntensity;
CBUFFER_END

#endif
