#ifndef CUSTOM_OIL_IMPASTO_CORE_INCLUDED
#define CUSTOM_OIL_IMPASTO_CORE_INCLUDED

// Hue-preserving impasto (no _PaintMap bind). Safe for OilNPR / Terrain / Water.
// Ridge lift + trough dig + stacked underpaint + light-facing flanks = 颜料堆叠凹凸.

half PaintLuma(half3 paint)
{
    return max(paint.r, max(paint.g, paint.b));
}

half PaintStroke(half luma, half contrast)
{
    return saturate((luma - 0.5h) * contrast + 0.5h);
}

// stroke 0.5 leaves color alone. Peaks lift value, valleys dig cavity — no warm/cool tint.
half3 ImpastoDeviation(half3 color, half stroke, half relief)
{
    half amt = saturate(relief);
    if (amt < 1e-3h)
    {
        return color;
    }

    half w = (stroke - 0.5h) * amt;
    half luma = PaintLuma(color);
    half3 chr = color / max(luma, 1e-3h);
    // Original amplitudes — rocks/props keep authored albedo.
    half lumaOut = luma * (1.0h + w * 0.78h);
    lumaOut *= 1.0h - saturate(-w) * 0.28h;
    return saturate(chr * lumaOut);
}

// Paint maps transfer structure onto authored chroma (not a second palette).
half3 ImpastoStrokeAlbedo(half3 albedo, half3 paint, half stroke, half relief)
{
    half w = saturate(relief);
    half aL = PaintLuma(albedo);
    half3 chr = albedo / max(aL, 1e-3h);
    half pL = PaintLuma(paint);
    half structured = aL * (1.0h + (pL - 0.5h) * w * 1.25h);
    half3 outC = saturate(chr * structured);
    return ImpastoDeviation(outC, stroke, relief);
}

half ImpastoRidgeSpec(half stroke, half ndotH, half relief)
{
    half ridge = saturate((stroke - 0.52h) * 2.8h) * saturate(relief);
    return ridge * pow(saturate(ndotH), 36.0h) * 0.40h;
}

half3 ImpastoTiltNormal(half3 N, half strokeDx, half strokeDz, half relief)
{
    half s = saturate(relief) * 0.55h;
    half3 n = N;
    n.xz -= half2(strokeDx, strokeDz) * s * 2.2h;
    return normalize(n);
}

// 颜料堆叠：底层露出 + 三档色板 + 受光侧脊亮 / 背光侧槽暗。
// 在主光照之后调用，用笔触高度导数造凹凸，不改 hue。
half3 ImpastoPaintStack(
    half3 litColor,
    half stroke,
    half relief,
    half3 lightDirWS,
    half strokeDx,
    half strokeDz)
{
    half amt = saturate(relief);
    if (amt < 1e-3h)
    {
        return litColor;
    }

    half h = saturate(stroke);
    half luma = PaintLuma(litColor);
    half3 chr = litColor / max(luma, 1e-3h);

    // Buried underpaint / mid slab / raised peak — discrete stacked pigment.
    // 0.42 把海蓝槽打进近黑，远看是一块发脏的蓝黑。
    half3 under = chr * (luma * 0.62h);
    half3 mid = chr * (luma * 0.88h);
    half3 peak = chr * min(luma * 1.08h, 1.12h);

    half3 soft = under;
    soft = lerp(soft, mid, smoothstep(0.16h, 0.30h, h));
    soft = lerp(soft, peak, smoothstep(0.55h, 0.72h, h));

    half3 hard = under;
    hard = lerp(hard, mid, step(0.34h, h));
    hard = lerp(hard, peak, step(0.66h, h));
    half3 stacked = lerp(soft, hard, 0.72h);

    // Flank normal from stroke height — light hits the rising edge of each dab.
    half3 L = lightDirWS;
    half llen = max(length(L), 1e-3h);
    L /= llen;
    half3 flankN = normalize(half3(
        -strokeDx * (10.0h * amt),
        1.0h,
        -strokeDz * (10.0h * amt)));
    half facing = saturate(dot(flankN, L) * 0.55h + 0.45h);
    // 受光面停在颜料本色，背光面压暗。不再把脊顶抬成白光。
    stacked *= lerp(0.68h, 1.02h, facing);

    // Back-lit troughs fall into underpaint (layer peek-through).
    half trough = saturate((0.48h - h) * 2.4h);
    stacked = lerp(stacked, under, trough * (1.0h - facing) * 0.70h);

    return lerp(litColor, saturate(stacked), amt);
}

// 厚涂侧面：背光压暗、受光停在水色。不加暖色高光，避免凹凸看成发亮。
half3 ImpastoPigmentFlank(
    half3 color,
    float3 nTS,
    float3 tangentWS,
    float3 bitangentWS,
    float3 geoN,
    half3 lightDirWS,
    half relief)
{
    half amt = saturate(relief);
    if (amt < 0.02h)
    {
        return color;
    }

    float3 L = (float3)lightDirWS;
    float llen2 = dot(L, L);
    L = llen2 > 1e-4 ? L * rsqrt(llen2) : float3(0.25, 0.92, 0.18);
    float3 N = normalize(tangentWS * nTS.x + bitangentWS * nTS.y + geoN * nTS.z);
    half ndotl = saturate(dot((half3)N, (half3)L) * 0.55h + 0.45h);
    half flat = saturate(dot((half3)geoN, (half3)L) * 0.55h + 0.45h);
    half shade = saturate(0.5h + (ndotl - flat) * 3.4h);
    // 只改明度。暖灰侧面会把海蓝搅成发脏的蓝黑。
    half lift = lerp(0.62h, 1.04h, shade);
    half3 lit = color * lift;
    return lerp(color, lit, amt);
}

// Baked paint normal, lit like the sky canvas. Screen-space stroke derivatives
// go flat once a dab covers more than a few pixels; this flank does not.
// nTS: +X along paint U, +Y along paint V, +Z out of the paint.
// tangentWS / bitangentWS: world +U / +V. geoN: paint facing.
half3 ImpastoCanvasFlank(
    half3 color,
    float3 nTS,
    float3 tangentWS,
    float3 bitangentWS,
    float3 geoN,
    half3 lightDirWS,
    half relief)
{
    half amt = saturate(relief);
    if (amt < 0.02h)
    {
        return color;
    }

    float3 L = (float3)lightDirWS;
    float llen2 = dot(L, L);
    L = llen2 > 1e-4 ? L * rsqrt(llen2) : float3(0.25, 0.92, 0.18);
    float3 N = normalize(tangentWS * nTS.x + bitangentWS * nTS.y + geoN * nTS.z);
    // Half-Lambert difference: a high sun still darkens the steep edge of a dab.
    half ndotl = saturate(dot((half3)N, (half3)L) * 0.65h + 0.35h);
    half flat = saturate(dot((half3)geoN, (half3)L) * 0.65h + 0.35h);
    half shade = saturate(0.5h + (ndotl - flat) * 2.4h);

    half3 shadow = half3(0.62h, 0.74h, 1.00h);
    half3 light = half3(1.34h, 1.14h, 0.92h);
    half3 mid = 0.5h * (shadow + light);
    half3 lit = color * (lerp(shadow, light, shade) / mid);
    half crest = saturate((shade - 0.58h) * 2.6h) * amt;
    lit += crest * half3(1.0h, 0.93h, 0.70h) * 0.16h;
    return lerp(color, max(lit, 0.0h), amt);
}

// geoN stays the paint facing. Hints are world axes of the paint UV.
void OilPaintFrame(
    float3 geoN,
    float3 tangentHint,
    float3 bitangentHint,
    out float3 tangentWS,
    out float3 bitangentWS)
{
    float3 N = normalize(geoN);
    float3 T = tangentHint - N * dot(N, tangentHint);
    float tLen2 = dot(T, T);
    T = tLen2 > 1e-8 ? T * rsqrt(tLen2) : float3(1.0, 0.0, 0.0);
    float3 B = bitangentHint - N * dot(N, bitangentHint);
    B = B - T * dot(T, B);
    float bLen2 = dot(B, B);
    B = bLen2 > 1e-8 ? B * rsqrt(bLen2) : cross(N, T);
    tangentWS = T;
    bitangentWS = B;
}

// River / waterfall UV is not world XZ. Build +U/+V from the paint coordinates.
void OilPaintFrameFromUv(
    float3 positionWS,
    float2 paintSt,
    float3 geoN,
    out float3 tangentWS,
    out float3 bitangentWS)
{
    float3 dpdx = ddx(positionWS);
    float3 dpdy = ddy(positionWS);
    float2 duvdx = ddx(paintSt);
    float2 duvdy = ddy(paintSt);
    float det = duvdx.x * duvdy.y - duvdx.y * duvdy.x;
    float3 hintT;
    float3 hintB;
    if (abs(det) < 1e-6)
    {
        hintT = float3(1.0, 0.0, 0.0);
        hintB = float3(0.0, 0.0, 1.0);
    }
    else
    {
        float invDet = rcp(det);
        hintT = (duvdy.y * dpdx - duvdx.y * dpdy) * invDet;
        hintB = (duvdx.x * dpdy - duvdy.x * dpdx) * invDet;
    }

    OilPaintFrame(geoN, hintT, hintB, tangentWS, bitangentWS);
}

// No derivative path (mobile / missing ddx): fake facing from height alone.
half3 ImpastoPaintStackCheap(half3 litColor, half stroke, half relief)
{
    half amt = saturate(relief);
    if (amt < 1e-3h)
    {
        return litColor;
    }

    half h = saturate(stroke);
    half luma = PaintLuma(litColor);
    half3 chr = litColor / max(luma, 1e-3h);
    half3 under = chr * (luma * 0.50h);
    half3 peak = chr * min(luma * 1.25h + 0.04h, 1.30h);
    half3 stacked = lerp(under, peak, smoothstep(0.22h, 0.78h, h));
    half3 hard = lerp(under, peak, step(0.5h, h));
    stacked = lerp(stacked, hard, 0.65h);
    stacked *= lerp(0.70h, 1.18h, h);
    return lerp(litColor, saturate(stacked), amt);
}

#endif
