# DirectionalLights 场景

路径：`Assets/CustomSRP/Scenes/DirectionalLights.unity`  
生成脚本：`Assets/CustomSRP/Editor/CreateDirectionalLightsScene.cs`  
示例组件：`Assets/CustomSRP/Examples/PerObjectMaterialProperties.cs`、`MeshBall.cs`  
光照：`Assets/CustomSRP/Runtime/Lighting.cs` + `Assets/CustomSRP/ShaderLibrary/`

灯数、分组球数、材质默认值均以 [`CreateDirectionalLightsScene.cs`](../Assets/CustomSRP/Editor/CreateDirectionalLightsScene.cs) 为准。MeshBall 实例数以 [`MeshBall.cs`](../Assets/CustomSRP/Examples/MeshBall.cs) 为准。核对方式：打开该脚本常量 / `for` 上界，或打开场景 Hierarchy。

## 场景是干什么的

**Lit / 多平行光 / BRDF 高光 / 透明预乘验证场景**：在 Custom SRP 下观察最多 4 盏 Directional 的直接光照、Metallic/Smoothness 对高光的影响，以及 Fade vs Transparent（预乘）差异。

它**不是**开篇管线验收场（[CustomRPTest](custom-rp-test.md)），也**不是** Batcher / Instancing 对照场（[DrawCalls](draw-calls.md)）。本场景假设管线已挂好、`CustomSRP/Lit` 已可用。

| 你期望看到 / 验证 | 对应能力 |
|-------------------|----------|
| 中央网格球：横向 Metallic、纵向 Smoothness | Lit + `PerObjectMaterialProperties` |
| 多色侧光叠在球体上 | 最多 4 盏可见 Directional → GPU |
| 光滑球尖高光 / 金属球几乎无漫反射 | BRDF（`MIN_REFLECTIVITY`、SpecularStrength） |
| Fade 排 vs Transparent 排 | 整色淡出 vs 仅 diffuse × alpha、高光保持 |
| Play 后远处实例团 | Lit `MeshBall`（`LitInstanced`） |

## 怎么打开 / 重建

1. 先挂管线：菜单 **CustomSRP → Create & Assign Pipeline Asset**（Graphics 已挂可跳过）。
2. 打开已有场景：Project 里双击 `Assets/CustomSRP/Scenes/DirectionalLights.unity`。
3. 若场景或材质缺失：菜单 **CustomSRP → Create Directional Lights Scene**  
   - 会重建 Lit 材质与贴图引用（`LitAlbedo` / `LitFabric` / `UVAlpha`）  
   - 加入 Build Settings（**不**抢第一位）  
   - 自动打开该场景  

进入 **Play Mode** 才能看到 `MeshBall`（`DrawMeshInstanced` 写在 `Update` 里）。

## 场景里有什么

### 相机与灯光

以生成脚本为准：

| 对象 | 要点 |
|------|------|
| Main Camera | 位置 `(0, 3.5, -14)`，欧拉 `(12, 0, 0)` |
| Directional Light | 欧拉 `(50, -30, 0)`，色 `(1, 0.956, 0.839)`，强度 `1` |
| Directional Light Red | 欧拉 `(30, 40, 0)`，色 `(1, 0.25, 0.2)`，强度 `0.55` |
| Directional Light Green | 欧拉 `(40, -120, 0)`，色 `(0.25, 1, 0.35)`，强度 `0.45` |
| Directional Light Blue | 欧拉 `(20, 160, 0)`，色 `(0.25, 0.45, 1)`，强度 `0.5` |

无副相机、无 Overlay UI、无 Batcher 对照实验布局。

### Hierarchy 分区

| 父物体 | 内容（脚本出处） | 材质 / 组件 |
|--------|------------------|-------------|
| `MetallicSmoothnessGrid` | `MetallicSteps.Length × SmoothnessSteps.Length`（各 5 档：`{0,0.25,0.5,0.75,1}` / `{0,0.25,0.5,0.75,0.95}`） | `LitDefault` + `PerObjectMaterialProperties` |
| `TexturedSpheres` | `for i < 5` | `LitTextured` + MPB |
| `BlueMetalSpheres` | `for i < 5` | `LitBlueMetal` + MPB（Metallic=1） |
| `FadeSpheres` | `for i < 4` | `LitFade` |
| `TransparentSpheres` | `for i < 4` | `LitTransparent`（预乘） |
| `ClipSpheres` | `for i < 4` | `LitClip` + MPB |
| `MeshBall` | 1 个空物体，位置 `(0, 1.5, 14)` | `MeshBall` + `LitInstanced` |

### 材质一览

均在 `Assets/CustomSRP/Materials/`，Shader 均为 **`CustomSRP/Lit`**（`LightMode = CustomLit`）。

| 材质 | Surface | 队列 | Enable GPU Instancing | 用途 |
|------|---------|------|----------------------|------|
| `LitDefault` | Opaque | Geometry 2000 | 关 | 网格球共享材质 + MPB |
| `LitTextured` | Opaque | Geometry 2000 | 关 | 贴图球（`LitAlbedo`） |
| `LitBlueMetal` | Opaque | Geometry 2000 | 关 | 蓝金属高光色 |
| `LitFade` | Transparent | Transparent 3000 | 关 | SrcAlpha 整色淡出 |
| `LitTransparent` | TransparentPremultiply | Transparent 3000 | 关 | `_PREMULTIPLY_ALPHA` |
| `LitClip` | Clip | AlphaTest 2450 | **开** | Cutout + `UVAlpha` |
| `LitInstanced` | Opaque | Geometry 2000 | **开** | MeshBall |

## 多光源数据路径

```
Cull → visibleLights
  → Lighting.SetupLights（仅 LightType.Directional，最多 4）
  → SetGlobal：_DirectionalLightCount / Colors / Directions
  → LitPass GetLighting for-loop 累加
```

- C#：`Lighting.MaxDirLightCount = 4`
- HLSL：`MAX_DIRECTIONAL_LIGHT_COUNT 4`
- 颜色：`VisibleLight.finalColor`；方向：`-localToWorldMatrix.GetColumn(2)`
- 超出 4 盏：静默丢弃
- `Lighting.Setup` 调用位置见 [渲染循环](render-loop.md)（含阴影；此处不复述顺序）

核对两端上限：

```bash
rg -n "MaxDirLightCount|MAX_DIRECTIONAL_LIGHT_COUNT" \
  Assets/CustomSRP/Runtime/Lighting.cs Assets/CustomSRP/ShaderLibrary/Light.hlsl
```

## BRDF / 高光要点

- Metallic 工作流：`specular = lerp(MIN_REFLECTIVITY, color, metallic)`，`MIN_REFLECTIVITY = 0.04`
- Smoothness → perceptual roughness → roughness（Core `CommonMaterial`）
- 每盏光：`saturate(N·L) * lightColor * DirectBRDF`（高光 + 漫反射）
- **`_PREMULTIPLY_ALPHA`**：`GetBRDF(surface, true)` **仅** `diffuse *= alpha`；specular **不**乘 alpha（`BRDF.hlsl`）
- Fade（无预乘）：GPU SrcAlpha 混合，高光也会随 alpha 变淡
- 不在此文展开完整 BRDF 公式；实现见 `ShaderLibrary/BRDF.hlsl`、`Lighting.hlsl`

## MeshBall

- `Update` 里 `Graphics.DrawMeshInstanced`；Edit Mode 不跑 `Update`，故空物体属正常
- 实例数以 `MeshBall.cs` 的 `MaxInstances` 为准（当前 1023）
- MPB 数组：`_BaseColor` / `_Metallic` / `_Smoothness`
- 可见性：Play + 材质 Enable GPU Instancing + Shader 支持 + 视锥 / 层；**不依赖** Asset `Use GPU Instancing`
- 批处理对照细节见 [DrawCalls](draw-calls.md)，本文不重复

## 预期现象速查

| 条件 | 预期 |
|------|------|
| 0 盏可见 Directional | Lit 近黑 |
| 多盏彩色 Directional | 漫反射 + 高光叠色 |
| Smoothness↑ | 高光更尖更亮 |
| Metallic=1 + 彩色 base | 高光带 base 色；漫反射近 0（**预期**） |
| Fade | 整色（含高光）随 alpha 变淡 |
| Transparent（预乘） | 高光保持；仅漫反射 × alpha |
| Edit Mode MeshBall | 不可见（`Update` 未跑） |
| Play + `LitInstanced` Enable GPU Instancing | 远处实例团 |

## 建议怎么用

### 第一次验收

1. Graphics 已挂 Custom Pipeline Asset；Player Linear Color Space。  
2. 打开本场景 → Scene 视图应已有高光与多色侧光。  
3. **Play** → 远处 Lit MeshBall 实例团。  
4. Frame Debugger：Opaque（2000）→ AlphaTest（2450）→ Skybox（2500）→ Transparent（3000）。

### 对照实验（推荐顺序）

1. 关掉场景里全部 Directional → Lit 近黑。  
2. 只开主光 → 漫反射恢复。  
3. 开红/绿/蓝侧光 → 叠色。  
4. 拉高网格球 Smoothness → 高光变尖。  
5. Metallic=1（蓝金属列）→ 几乎只有高光，属预期。  
6. 对比 Fade 排与 Transparent 排：后者高光更不随 alpha 消失。

### 不要用本场景做的事

- 验证 Standard → 品红 Error、双相机、Overlay UI → [CustomRPTest](custom-rp-test.md)  
- 验证 SRP Batcher / Asset Instancing 对照 → [DrawCalls](draw-calls.md)  
- 在 Edit Mode 判断 MeshBall「丢了」  

## 相关代码

| 文件 | 用途 |
|------|------|
| [`CreateDirectionalLightsScene.cs`](../Assets/CustomSRP/Editor/CreateDirectionalLightsScene.cs) | 场景 / 材质生成；数量与布局以它为准 |
| [`Lighting.cs`](../Assets/CustomSRP/Runtime/Lighting.cs) | 可见 Directional → GPU globals |
| [`Lit.shader`](../Assets/CustomSRP/Shaders/Lit.shader) / [`LitPass.hlsl`](../Assets/CustomSRP/Shaders/LitPass.hlsl) | `LightMode=CustomLit` |
| [`ShaderLibrary/Light.hlsl`](../Assets/CustomSRP/ShaderLibrary/Light.hlsl) 等 | 光结构、BRDF、累加循环 |
| [`CustomShaderGUI.cs`](../Assets/CustomSRP/Editor/CustomShaderGUI.cs) | Opaque / Clip / Fade / Transparent presets |
| [`PerObjectMaterialProperties.cs`](../Assets/CustomSRP/Examples/PerObjectMaterialProperties.cs) | MPB：`_BaseColor` / `_Cutoff` / `_Metallic` / `_Smoothness` |
| [`MeshBall.cs`](../Assets/CustomSRP/Examples/MeshBall.cs) | `DrawMeshInstanced` + metallic/smoothness 数组 |

## 相关文档

- [快速开始](getting-started.md)
- [DrawCalls 场景](draw-calls.md) — MeshBall / Batcher 对照
- [渲染循环](render-loop.md) — `Lighting.Setup` 精确位置
- [Shader 规范](shader-conventions.md) — Lit / CustomLit
- [排查](troubleshooting.md)
- 根目录 [README](../README.md)
