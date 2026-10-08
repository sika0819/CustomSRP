# Directional Shadows

级联阴影（CSM）、多平行光阴影、PCF、Bias、透明投射。字段 / 上限 / tile 以 Shadows / **DirectionalShadows** C# + Shadows HLSL + ShadowSettings grep 为准。

**顺序与 Cleanup 只引用主 Skill §3**。阴影必须在相机 color Setup 冲掉 target 之前画完。

结构演进（是否已升级一律 grep）：单体 `Shadows` → 专类 `DirectionalShadows`（[unity-upgrades §7.1](unity-upgrades.md)）→ 专 Pass `DirectionalShadowsPass`（[§7.2](unity-upgrades.md)）。Geometry 经 `Handles.Use` 声明读依赖。

## 怎么用

1. 确认 Pipeline Asset；用当前场景或项目 Directional Shadows 验证内容（有则用）。
2. Asset → Shadows：`maxDistance`、atlas、filter（PCF）、cascadeCount / ratios / fade / blend（字段以工程为准）。
3. 灯：Shadow Type≠None、Strength>0；新建灯默认 Bias 易 Peter-Panning → 常调 **Bias≈0、Normal Bias≈1**（以项目 ShadowSettings 语义为准）。

| 期望 | 能力 |
|------|------|
| 地面清晰投影 | ShadowCaster + atlas 采样 |
| 近细远粗 | cascades |
| 软影 / 级联交界 | PCF、`softCascadeBlend` / 级联淡出（字段以工程为准） |
| Clip 挖洞影 vs Fade dither | `_SHADOWS_CLIP` / `_SHADOWS_DITHER` |
| 投射但不接影 | 关接影关键字（如 `_RECEIVE_SHADOWS`） |

数据路径（概念）：`ReserveDirectionalShadows → 每光 shadow data → GetDirectionalShadowAttenuation`。

常见 shadow data 分量：strength、tileOffset（+ cascadeIndex）、normalBias。

Unity 6：`CreateShadowRendererList` + `DrawRendererList`；禁止已弃用 `DrawShadows`。

## Agent 规范

- 改阴影：Shadows C# + ShadowSettings + Shadows HLSL + ShadowCaster Pass。
- 透明投射：`_SHADOWS_CLIP` / `_SHADOWS_DITHER`；关投射：`SetShaderPassEnabled("ShadowCaster", false)`。
- 接影关 → attenuation=1。
- tile 数 ≈ shadowedLights × cascadeCount；改 GPU 定长数组后可能需重启 Editor。
- 灯 Bias = slope-scale depth bias；Normal Bias = 采样法线偏移（与 Built-in 滑条原意可能不同）。
- 不展开世界→atlas 矩阵公式，除非用户要求。
- 阴影平行光上限常与 Directional max 一致（两端 grep）。
- Point/Spot 阴影：共用 ShadowCaster，但 atlas / tile / 投影不同 → [point-and-spot-shadows.md](point-and-spot-shadows.md)。

## 技巧 / 排障

### 性能量级

| 条件 | 量级 |
|------|------|
| ShadowCaster 绘制 | ≈ shadowedLights × cascadeCount |
| atlas split | tiles≤1→1；≤4→2；否则 4；tile 边长 = atlasSize / split |
| PCF 2×2/3×3/5×5/7×7 | compare tap ≈ 1 / 4 / 9 / 16 |
| Soft cascade blend | 交界区可能再采下一级联 |
| 0 盏阴影灯 | 常仍申请 1×1 dummy atlas（绑 sampler） |

### 排障

| 现象 | 查 |
|------|-----|
| 有光无影 | Type/Strength；ShadowCaster；bounds；maxDistance；顺序 §3；接影关键字 |
| acne | ↑Normal Bias / atlas；或先降 PCF |
| Peter-Panning | Bias 过大 → Bias≈0、Normal Bias≈1 |
| 影抖 / dither 脏 | 半透 Dither 投射或影矩阵随相机 |
| 长物体影变形 | pancaking → 灯 Near Plane |
| 级联硬边 | Soft blend 或 ↑cascadeFade |

对照：无影灯 → Hard → 缩 maxDistance → cascade → PCF → Soft → Clip vs Fade 投射。

## 相关核对

```bash
rg -n "MaxShadowedDirectionalLightCount|MaxCascades|MAX_CASCADE"
rg -n "CreateShadowRendererList|DrawShadows"
rg -n "_SHADOWS_CLIP|_SHADOWS_DITHER|_RECEIVE_SHADOWS|ShadowCaster"
rg -n "GetDirectionalShadowAttenuation|ReserveDirectionalShadows"
```

无阴影灯 / 无 caster bounds：Reserve 常返回 0、不占 tile。整帧无阴影灯时仍可能申请 dummy atlas。

## 验收

- [ ] Hard 阴影地面可见；调 maxDistance 覆盖区变化
- [ ] Cascade / PCF / Soft 行为与性能表一致
- [ ] Clip / Dither 投射差异可辨
- [ ] 接影关闭物体投射但不接影
- [ ] 未在相机 Setup 之后才画阴影
