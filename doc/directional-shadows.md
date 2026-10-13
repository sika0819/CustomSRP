# Directional Shadows 场景

路径：`Assets/CustomSRP/Scenes/DirectionalShadows.unity`  
生成脚本：`Assets/CustomSRP/Editor/CreateDirectionalShadowsScene.cs`  
Runtime：`ShadowSettings.cs`、`Shadows.cs`、`Lighting.cs`、`CameraRenderer.cs`  
Shader：`ShadowCasterPass.hlsl`、`ShaderLibrary/Shadows.hlsl`；Lit/Unlit 均含 ShadowCaster Pass

字段名、上限、tile/split 以源码为准；改前 grep（见仓库 Skill 核对块）。灯数 / 分组以 [`CreateDirectionalShadowsScene.cs`](../Assets/CustomSRP/Editor/CreateDirectionalShadowsScene.cs) 为准。

主循环顺序见 [渲染循环](render-loop.md)（**此处不复制顺序串**）。

## 场景是干什么的

验证级联阴影图（CSM）、多平行光阴影、PCF、级联混合 / 淡出、透明投射模式。

它**不是** Batcher 对照场（[DrawCalls](draw-calls.md)），也**不是** BRDF 网格场（[DirectionalLights](directional-lights.md)）。

| 你期望看到 / 验证 | 对应能力 |
|-------------------|----------|
| 地面上清晰投影 | ShadowCaster + atlas 采样 |
| 近处更细、远处更粗 | cascades / culling spheres |
| 软影 / 级联交界平滑 | `filter` PCF、`cascadeBlend` Soft |
| Clip 挖洞影 vs Fade dither 影 | `_SHADOWS_CLIP` / `_SHADOWS_DITHER` |
| 投射但不接影 / Unlit 仍投射 | `_RECEIVE_SHADOWS`、Unlit ShadowCaster |

## 怎么打开 / 重建

1. 挂管线：菜单 **CustomSRP → Create & Assign Pipeline Asset**（已挂可跳过）。
2. 打开或重建：菜单 **CustomSRP → Create Directional Shadows Scene** → `Assets/CustomSRP/Scenes/DirectionalShadows.unity`（加入 Build Settings，不抢第一位）。
3. Pipeline Asset → **Shadows**：`maxDistance`、`distanceFade`、`directional.atlasSize` / `filter` / `cascadeCount` / `cascadeRatio1..3` / `cascadeFade` / `cascadeBlend`。
4. 灯光：Shadow Type≠None、Strength>0；**Bias≈0、Normal Bias≈1**（新建灯默认 Bias 会严重 Peter-Panning）。

## 场景里有什么

以生成脚本为准：

| 分组 | 内容 |
|------|------|
| Ground | Plane，`LitShadowGround` |
| ShadowCasters | 不透明 Cube/Sphere、长条 Cube（pancaking）、Wall、Unlit 投射、`LitNoReceiveShadows` |
| TransparencyShadows | 复用 `LitClip` / `LitFade` / `LitTransparent`（Clip / Dither 投射） |
| FarReceivers | 远处 Cube，验 cascade / distance fade |
| Lights | 主光 Hard 阴影；红光半强度阴影；Fill 无阴影 |

复用：`LitDefault`、`LitClip`、`LitFade`、`LitTransparent`；新建仅阴影场景专用材质。

## 阴影数据路径

```
ReserveDirectionalShadows(light, visibleLightIndex)
  → Vector3(strength, tileOffset, normalBias)
  → _DirectionalLightShadowData[light].xyz
  → GetDirectionalShadowData（.y + cascadeIndex → tileIndex）
  → GetDirectionalShadowAttenuation（Shadows.hlsl）
```

| 分量 | 含义 |
|------|------|
| **.x** | shadowStrength（再乘全局 cascade/distance fade strength） |
| **.y** | tileOffset = `cascadeCount * shadowedLightIndex`（采样时再加 cascadeIndex） |
| **.z** | `shadowNormalBias`（采样时沿法线偏移） |

- 阴影平行光上限：`MaxShadowedDirectionalLightCount = 4`（与方向光上限一致）
- 无阴影灯 / 无 caster bounds：Reserve 返回 0，不占 tile；整帧无阴影灯时仍申请 **1×1** dummy atlas（绑 shadow sampler）
- Unity 6 绘制：`CreateShadowRendererList` + `DrawRendererList`（不用已弃用 `DrawShadows`）

不展开世界→atlas 矩阵公式。

核对：

```bash
rg -n "_DirectionalLightShadowData|GetDirectionalShadowData|GetDirectionalShadowAttenuation" \
  Assets/CustomSRP/Runtime/Lighting.cs \
  Assets/CustomSRP/ShaderLibrary/Light.hlsl \
  Assets/CustomSRP/ShaderLibrary/Shadows.hlsl
```

## 性能要点

| 条件 | 量级 / 预期 |
|------|-------------|
| ShadowCaster 绘制次数 | **= shadowedLights × cascadeCount** |
| atlas split | tiles≤1→split **1**；≤4→**2**；否则 **4**；**tile 边长 = atlasSize / split** |
| PCF | `PCF2x2/3x3/5x5/7x7` → compare **tap** **1 / 4 / 9 / 16**（`DIRECTIONAL_FILTER_SAMPLES`） |
| Soft `cascadeBlend` | 交界区对下一 cascade **再采一轮**；Hard/Dither 无双 cascade 采样 |
| maxDistance↓ / cascade cull | 远处少进更大 cascade（`shadowCascadeBlendCullingFactor`） |
| 0 盏阴影灯 | 1×1 dummy，无 ShadowCaster 绘制 |

对照实验：无影灯 → Hard → 缩 maxDistance → cascade → PCF → Soft/Dither → Clip vs Fade 投射。

## 排障

| 现象 | 查 |
|------|----|
| 有光无影 | Shadow Type/Strength；ShadowCaster Pass；`GetShadowCasterBounds`；maxDistance；顺序见 [render-loop](render-loop.md)；`_RECEIVE_SHADOWS` |
| acne | ↑Normal Bias / atlas；或 ↓PCF 后再调 |
| Peter-Panning | Bias 过大 → Bias≈0、Normal Bias≈1 |
| 影抖 / dither 脏 | 半透 Dither 投射 + 影矩阵随相机；改 Clip 或关投射 |
| 长物体影变形 | pancaking → 灯 Near Plane |
| 级联硬边 | Soft blend 或 ↑`cascadeFade`（代价见性能表） |

本管线灯 Bias = slope-scale depth bias；Normal Bias = 采样时沿法线偏移（与 Built-in 滑条原意不完全相同）。

## 相关代码

| 路径 | 职责 |
|------|------|
| `Runtime/ShadowSettings.cs` | Asset 上阴影配置（public fields） |
| `Runtime/Shadows.cs` | Reserve / atlas / cascades / keywords |
| `Runtime/Lighting.cs` | 调 Shadows；写 `_DirectionalLightShadowData` |
| `Runtime/CameraRenderer.cs` | 主循环（顺序见 render-loop） |
| `ShaderLibrary/Shadows.hlsl` | 采样 / fade / PCF / cascade blend |
| `Shaders/ShadowCasterPass.hlsl` | 深度投射 + Clip/Dither |
| `Editor/CreateDirectionalShadowsScene.cs` | 场景生成 |
| `Editor/CustomShaderGUI.cs` | Shadows / Receive / ShadowCaster pass |

## 预期现象速查

| 条件 | 预期 |
|------|------|
| 主光 Shadows=Hard | 地面上有清晰投射影 |
| maxDistance 减小 | 阴影覆盖区缩小；远处 fade |
| Cascade 4 | Frame Debugger atlas 多 tile；近处更细 |
| PCF 5×5 / 7×7 | 软影；需略增 Normal Bias |
| Soft cascade blend | 级联交界更平滑（采样加倍） |
| Clip / Fade 球 | Clip 挖洞影；Fade/Glass dither 影 |
| LitNoReceiveShadows | 投射但不接影 |
| UnlitShadowCaster | Unlit 色块仍投射 |
| Bias 保持默认大值 | 明显 Peter-Panning → Bias≈0、Normal Bias≈1 |
