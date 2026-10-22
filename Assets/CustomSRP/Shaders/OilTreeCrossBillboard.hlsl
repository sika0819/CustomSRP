#ifndef CUSTOM_OIL_TREE_CROSS_BILLBOARD_INCLUDED
#define CUSTOM_OIL_TREE_CROSS_BILLBOARD_INCLUDED

// Port of BoluoTreeCrossBillboardRotateYaw (Arts Common.hlsl):
// Snap to front/side atlas card, yaw the kept card to face the camera,
// collapse the orthogonal card so only one face is lit (no cross seam / edge-on strip).

float3 OilTreeCrossBillboardRotateYaw(
    float3 positionOS, float3 normalOS,
    out float3 rotatedNormalOS, out float3 normalWS, out float keep)
{
    float3 pivotWS = TransformObjectToWorld(float3(0.0, 0.0, 0.0));
    float3 toCamOS = TransformWorldToObjectDir(_WorldSpaceCameraPos - pivotWS);
    toCamOS.y = 0.0;
    float lenSq = max(dot(toCamOS.xz, toCamOS.xz), 1e-8);
    float invLen = rsqrt(lenSq);
    float2 dir = toCamOS.xz * invLen;

    float yaw = atan2(dir.x, dir.y);
    // 1/π ≈ 0.3183 → *2 = 0.6366 rounds yaw to 0/±1/±2 (= 0°/±90°/±180°)
    float snapIdx = round(yaw * 0.6366197723675814);
    float snapped = snapIdx * 1.5707963267948966;
    // 0°/180° keep front card (normal −Z); ±90° keep side card (normal −X).
    bool wantSide = abs(snapIdx) == 1.0;
    bool dropCard = wantSide ? (normalOS.z < -0.5) : (normalOS.x < -0.5);
    float rot = yaw - snapped;
    float c = cos(rot);
    float s = sin(rot);

    float3 p;
    p.x = positionOS.x * c + positionOS.z * s;
    p.y = positionOS.y;
    p.z = -positionOS.x * s + positionOS.z * c;

    float3 n;
    n.x = normalOS.x * c + normalOS.z * s;
    n.y = normalOS.y;
    n.z = -normalOS.x * s + normalOS.z * c;
    rotatedNormalOS = n;
    normalWS = TransformObjectToWorldNormal(n);

    keep = dropCard ? 0.0 : 1.0;
    if (dropCard)
        p = float3(0.0, 0.0, 0.0);
    return p;
}

// Atlas stores near-tree object-space normals (bake at prefab origin), same as BoluoUnity.
half3 OilTreeCrossBillboardDecodeObjectNormal(half3 encoded)
{
    half3 n = encoded * 2.0h - 1.0h;
    return normalize(n);
}

half3 OilTreeCrossBillboardDecodeWorldNormal(half3 encoded)
{
    return OilTreeCrossBillboardDecodeObjectNormal(encoded);
}

#endif
