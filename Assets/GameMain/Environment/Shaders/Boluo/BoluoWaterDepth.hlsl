#ifndef BOLUO_WATER_DEPTH_INCLUDED
#define BOLUO_WATER_DEPTH_INCLUDED

// 海面 / 瀑布共用的场景深度重建。调用方需先 include URP DeclareDepthTexture.hlsl。

/// <summary>
/// 透视相机：用 eyeDepth × 未归一化视线重建场景世界坐标（对齐 StylizedWater2 ReconstructWorldPosition）。
/// 不走 UNITY_MATRIX_I_VP + rawDepth：Android 锁 OpenGLES3（非 reversed-Z）时，
/// 直接把深度图 [0,1] 喂给 ComputeWorldSpacePosition 会把点甩到约两倍远处，
/// verticalDepth 被撑大后 exp() 把 intersection 岸沫压成 0；编辑器 Metal 看不出来。
/// viewDelta = 相机位置 - 水面世界坐标（未归一化）；screenPos 为 ComputeScreenPos 结果（未除 w）。
/// </summary>
float3 BoluoSceneWorldPosFromEyeDepth(float4 screenPos, float3 viewDelta, float eyeDepth)
{
    float3 camPos = GetCurrentViewPosition().xyz;
    float w = max(screenPos.w, 1e-5);
    float3 worldPos = eyeDepth * (viewDelta / w) - camPos;
    return -worldPos;
}

/// <summary>
/// 兼容旧调用：仍做 OpenGL NDC remap 后走 I_VP。新代码请优先用 BoluoSceneWorldPosFromEyeDepth。
/// </summary>
float3 BoluoSceneWorldPosFromRawDepth(float2 screenUV, float rawDepth)
{
#if UNITY_REVERSED_Z
    float ndcDepth = rawDepth;
#else
    float ndcDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, rawDepth);
#endif
    return ComputeWorldSpacePosition(screenUV, ndcDepth, UNITY_MATRIX_I_VP);
}

/// <summary>对齐 SW2 DepthDistance：沿水面法线量取水面点到场景点的距离。</summary>
float BoluoWaterDepthDistanceWS(float3 waterWS, float3 sceneWS, float3 normalWS)
{
    return length((waterWS - sceneWS) * normalWS);
}

#endif
