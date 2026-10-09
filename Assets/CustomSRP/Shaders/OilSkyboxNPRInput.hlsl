#ifndef CUSTOM_OIL_SKYBOX_NPR_INPUT_INCLUDED
#define CUSTOM_OIL_SKYBOX_NPR_INPUT_INCLUDED

// Grayscale impasto ridges. Sampled in the sky pass; the slot is not a leftover.
TEXTURE2D(_OilCanvas);
SAMPLER(sampler_OilCanvas);
TEXTURE2D(_SunTex);
SAMPLER(sampler_SunTex);
TEXTURE2D(_MoonTex);
SAMPLER(sampler_MoonTex);
TEXTURE2D(_StarsTex);
SAMPLER(sampler_StarsTex);

CBUFFER_START(UnityPerMaterial)
    float4 _OilCanvas_ST;
    float _BrushScale;
    float _BrushStrength;
    float _BrushContrast;
    float _BrushRelief;
    float _Period;
    float _StarDensity;
    float _StarSize;
CBUFFER_END

#endif
