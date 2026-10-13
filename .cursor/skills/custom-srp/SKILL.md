---
name: custom-srp
description: >-
  Unity Custom Scriptable Render Pipeline 的架构、Shader 约定、Unity 6/7 升级、
  调试与移动端性能规范。
  当用户修改 RenderPipeline / CameraRenderer / 自定义 SRP Shader，或提到
  Custom SRP、升级、Unity 6.0/6.1/6.2/7.0/7.1/7.2、Render Graph、
  ImportBackbuffer、RenderTargetInfo、EntityId、Native Render Pass、
  DirectionalShadows、OtherShadows、ShadowsPass、LPPV、Color LUT、ApplyLut3D、
  Draw Calls、Directional Lights/Shadows、ShadowCaster、Lit、Unlit、
  Lighting.Setup、SRP Batcher、GPU Instancing、MaterialPropertyBlock、Alpha Clip、
  PCF、Cascade、Baked Light、Light Probe、Shadow Mask、LOD、Reflection Probe、
  Mask Map、Point/Spot Lights、Point/Spot Shadows、OtherShadow、透视阴影、
  Color Grading、Color Adjustments、Volume Color Grading、LUT、Tone Mapping、
  White Balance、Split Toning、Channel Mixer、ACES、
  Multiple Cameras、viewport、Rendering Layer、DrawFinal、maskLights、分屏、Overlay、
  Particles、Color/Depth Texture、Near Fade、Soft Particles、Flipbook、Distortion、
  Post FX、Bloom、HDR、FXAA、
  Render Scale、bufferSize、Final Rescale、Bicubic Rescaling、_CameraBufferSize、
  性能优化、移动端、SetPass、Store Action、Shader Stripping、Depth/Opaque Texture、
  ASTC、Texture Atlas、合批、带宽时使用。
  跨项目通用（不含具体工程路径/菜单/接线进度）；细节读 references/。
---

# Custom SRP（跨项目）

自建 Scriptable Render Pipeline 的**可移植约定**（架构、Shader、合批、光影概念、Unity 6/7 升级、排障符号）。可随仓库拷贝或放到个人 skills，在任意自建 SRP 工程复用。

### 跨项目边界（硬）

| 写入 Skill / references | **禁止**写进 Skill（现场发现） |
|-------------------------|--------------------------------|
| 架构角色、Pass 顺序、队列号、合批事实 | 具体 `Assets/…` 路径、菜单名、场景名 |
| API / keyword / grep 符号（目标符号名可点） | 「本仓已接 / 未接」进度断言 |
| 通用排障表、验收原则、升级迁移约定 | 布局数字、灯数、Bias 默认、atlas 尺寸 |
| 外部手册链接（可选） | 把外部步骤写成当前工程已实现 |

**项目本地事实**（路径 / 菜单 / 场景 / 接线进度 / 布局 / 某版已接勾选）→ 当前仓库 `doc/`、Editor 生成脚本、或 grep 源码；回答前先查，**勿从本 Skill 臆造**。

**加载方式**：本文件是唯一 Skill 入口。主题细节在 `references/`，按路由表**按需 Read**——不要一次性读全。

## 1. 架构与职责

用 C# 接管剔除、清除与 Pass 顺序，替代 Built-in / URP。Unity 6+ 常用 **Render Graph**（Unsafe / Raster / Compute Pass）。

| 角色 | 职责 |
|------|------|
| **Asset** | 配置 + 工厂（`RenderPipelineAsset<T>`） |
| **Pipeline** | 每帧入口；构造时读 Asset；异常时 Reset Graph |
| **CameraRenderer** | 逐相机：Cull → 录 Pass → Execute → Submit；可持有 `Shadows` / PostFX |
| **LightingPass** | 可见光 → GPU buffers；**Reserve** 阴影槽位（不画 atlas） |
| **Shadows** | 捆绑 Directional / Other；全局关键词与距离淡出 |
| **DirectionalShadows** / **OtherShadows** | 各自 atlas / 数据 / 绘制（可经专 Pass） |
| **ShadowsPass** 族 | 7.2+：Directional / Other / Shadows 独立录图 |

现代阴影数据流（7.2 目标形态；未升级工程可能仍揉在 Lighting 里——以 grep 为准）：

```
Shadows.Setup → LightingPass(Reserve) → DirectionalShadowsPass
  → OtherShadowsPass → CullShadowCasters → ShadowsPass(globals)
  → Geometry(Use light+shadow Handles)
```

## 2. 使用前提

1. 依赖 `com.unity.render-pipelines.core`；Player **Linear** Color Space。
2. 创建并指定 Pipeline Asset（Graphics / Quality）。
3. 新材质只用本管线 Shader；Built-in Standard 仅作「不支持」对照（粉红 Error）。
4. 验证用 Frame Debugger + Render Graph Viewer + Asset 开关。
5. 若工程按主题拆验证场景：**各场只验本主题**，勿串 Batcher / BRDF / CSM / GI 当他场失败标准。

## 3. 渲染循环（不可乱序）

**概念顺序**（具体类名以工程为准）：

Cull(`shadowDistance`) → **灯光 Setup / Reserve 阴影** → **阴影 atlas 绘制**（须在相机 color Setup 冲掉 target 之前完成）→ Setup（`SetupCameraProperties` + Clear / 中间 RT）→ Opaque → Skybox →（可选 Color/Depth copy）→ Transparent → Post FX / Final Present → Gizmos(Editor)。

队列号：**Opaque 2000 → AlphaTest 2450 → Skybox 2500 → Transparent 3000**

- Unlit LightMode：`SRPDefaultUnlit`
- Lit LightMode：自定义（如 `CustomLit`）；须与 `DrawRendererList` 过滤一致
- Unity 6：`CreateRendererList` + `DrawRendererList`；阴影用 `CreateShadowRendererList`，禁止已弃用 `DrawShadows`
- Present：经 `ImportBackbuffer` 得到的目标句柄（6.5+ 必带 `RenderTargetInfo`）→ [unity-upgrades](references/unity-upgrades.md)

## 4. Shader 规范

1. `CBUFFER` / 字段声明 `UnityPerDraw`（`unity_ObjectToWorld` 等）。
2. **在** include `SpaceTransforms.hlsl` **之前**定义：`UNITY_MATRIX_M/I_M/V/I_V/VP/P` 与 `UNITY_PREV_MATRIX_M/I_M`。
3. 材质属性：`CBUFFER_START(UnityPerMaterial)`；GPU Instancing 时用 `UNITY_INSTANCING_BUFFER_START(UnityPerMaterial)`。
4. Alpha Clip：`#pragma shader_feature _CLIPPING` + `clip()`；Surface=Clip → 队列 **2450**。透明预乘：`_PREMULTIPLY_ALPHA`（仅 diffuse × alpha）。
5. Pass 带正确 `LightMode`；阴影 Pass：`ShadowCaster`。
6. 法线：`DecodeNormal` + `NormalTangentToWorld`；阴影 bias 用几何插值法线（非扰动法线）。
7. **勿**再依赖 LPPV（`unity_ProbeVolume*`）；Unity 6.5+ SRP 侧已废弃 → [unity-upgrades §7.0](references/unity-upgrades.md)。

## 5. 性能事实

1. Asset 开 SRP Batcher 时，GameObject 路径 **优先 Batcher**，通常勿指望与 GPU Instancing 同时生效。
2. **默认勿用 MaterialPropertyBlock**（打断 Batcher）；例外仅在逐物体动态属性 / `DrawMeshInstanced`。
3. Asset「Use GPU Instancing」只影响常规 `DrawRendererList`；**不影响** `Graphics.DrawMeshInstanced`。
4. `DrawMeshInstanced` 可见性：Play + 材质 Enable GPU Instancing + Shader 支持 + **matrices/count** + 视锥 / 层。
5. 灯光上限：C# 上传数组与 HLSL `#define` / `CBUFFER` **必须同值**。
6. 勿开启已不推荐的 `rendererListCulling`（多数情况有性能回退）→ [unity-upgrades §7.0](references/unity-upgrades.md)。

移动端审查清单 → [references/mobile-perf.md](references/mobile-perf.md)。

## 6. 接线状态（禁止写死）

下列能力**是否已实现一律以当前工程 grep 为准**。

**未命中以下符号时，回答中不得声称该能力已接线；已命中时也不得说「完全未接线」。** `PerObjectData.*` 已设 ≠ 对应采样已实现。

| 能力 | 建议核对符号 |
|------|----------------|
| 环境反射 | `SampleEnvironment` / SpecCube / `PerObjectData.ReflectionProbes` |
| Light Probe SH | `SampleLightProbe` / `PerObjectData.LightProbe` |
| lightmap | `LIGHTMAP_ON` multi_compile + Meta Pass + 采样（`PerObjectData.Lightmaps` 已设 ≠ 已采样） |
| shadow mask | `_SHADOW_MASK_*` / `SampleBakedShadows` / `UsesShadowMask` |
| LOD Cross-Fade | `LOD_FADE_CROSSFADE` / `ClipLOD` / 消费 `unity_LODFade` |
| Meta Pass | `LightMode=Meta` |
| Other Lights | Point/Spot 上传 + HLSL 循环；与 Directional max 两套常量 |
| Other 实时阴影 | `OtherShadows` / `_OtherShadowAtlas` / `ReserveShadows` / `RenderOtherShadows` / `GetOtherShadowAttenuation` |
| Directional 阴影专类 | `DirectionalShadows` / `RenderDirectionalShadows` / `_DirectionalShadowAtlas` |
| 独立 Shadow Pass（7.2） | `ShadowsPass` / `DirectionalShadowsPass` / `OtherShadowsPass` |
| cameraTarget / Present | `ImportBackbuffer` / `RenderTargetInfo` / `cameraTarget` |
| Color LUT 3D | `ApplyLut3D` / `ColorLUT` compute / `AddComputePass` |
| Multiple Cameras | `renderingLayerMask` / `SetViewport` / `maskLights` |
| Particles / copy | `CopyAttachments` / `_SOFT_PARTICLES` / `_DISTORTION` / `Fragment.hlsl` |
| Render Scale | `renderScale` / `_CameraBufferSize` / `FinalRescale` / `Bicubic` |
| 已弃用勿再用 | `DrawShadows`、`rendererListCulling=true`、LPPV / `unity_ProbeVolume*`、无 Info 的 `ImportBackbuffer` |

## 7. Agent 硬约束

- **跨项目**：不把本仓路径/菜单/场景/接线进度写进回答当默认事实；先 grep / 读项目 `doc/`。
- Asset 与 Pipeline **分文件**；配置在 Asset，Instance 构造读取。
- **不**为「省事」引入完整 URP/HDRP 替代自建管线（除非用户明确要求迁移）。
- 勿破坏 Unlit / Lit LightMode 与绘制过滤约定。
- 改场景优先 Editor API（如 `Quaternion.Euler`）。
- 阴影用 `CreateShadowRendererList`（Unity 6+）；设置经 ShadowSettings（或等价）挂在 Asset。
- Quality `shadowmaskMode`：写**枚举名**，禁止裸 `0`/`1`。
- **升级**：按 [unity-upgrades](references/unity-upgrades.md) 分档；7.1/7.2 以「外观不变」验收；6.5+ 缺 `RenderTargetInfo` 优先修编译。
- 不展开完整 BRDF / PCF / 世界→atlas 矩阵公式，除非用户要求。
- 扩展本 Skill：新 reference 只写可移植约定；项目路径/菜单/布局/「已接」勾选进项目 `doc/`。

## 8. 路由表（按需读 references）

| 用户提到 | 读 |
|----------|-----|
| **升级、6.0/6.1/6.2/7.0/7.1/7.2、ImportBackbuffer、RenderTargetInfo、EntityId、LPPV、拆阴影、ShadowsPass、3D LUT、ApplyLut3D** | [references/unity-upgrades.md](references/unity-upgrades.md) |
| Draw Calls、MPB、SRP Batcher、Instancing、Clip、DrawMeshInstanced | [references/draw-calls.md](references/draw-calls.md) |
| Directional Lights、Lit、BRDF、Metallic、预乘 | [references/directional-lights.md](references/directional-lights.md) |
| Directional Shadows、PCF、CSM、Bias、ShadowCaster、DirectionalShadows | [references/directional-shadows.md](references/directional-shadows.md) |
| Baked Light、Contribute GI、Light Probe、lightmap | [references/baked-light.md](references/baked-light.md) |
| Shadow Masks、DistanceShadowmask、Mixed Shadowmask | [references/shadow-masks.md](references/shadow-masks.md) |
| LOD、Reflection Probe、Fresnel、SpecCube | [references/lod-and-reflections.md](references/lod-and-reflections.md) |
| Complex Maps、Mask Map、MODS、Detail、Normal、Emission | [references/complex-maps.md](references/complex-maps.md) |
| Point/Spot、Other Lights、Lights Per Object | [references/point-and-spot-lights.md](references/point-and-spot-lights.md) |
| Point/Spot Shadows、OtherShadows、透视阴影、第二 atlas | [references/point-and-spot-shadows.md](references/point-and-spot-shadows.md) |
| Color Grading、LUT、tone mapping、White Balance、Split Toning、Volume Color Grading、Color Adjustments | [references/color-grading.md](references/color-grading.md) |
| Multiple Cameras、分屏、Overlay、viewport、Rendering Layer Mask、DrawFinal、maskLights | [references/multiple-cameras.md](references/multiple-cameras.md) |
| Particles、Color/Depth Texture、Near Fade、Soft Particles、Flipbook、Distortion | [references/particles.md](references/particles.md) |
| Render Scale、bufferSize、Final Rescale、Bicubic、每相机 Override、`_CameraBufferSize` | [references/render-scale.md](references/render-scale.md) |
| 性能优化、移动端、Store Action、Shader Stripping、ASTC、带宽、Depth/Opaque Texture | [references/mobile-perf.md](references/mobile-perf.md) |

## 9. 调试 / 验收路由

| 现象 / 问题 | 查 |
|-------------|-----|
| `ImportBackbuffer` 编译错 / obsolete | → unity-upgrades §7.0 |
| `UNITY_MATRIX_M` undeclared | SpaceTransforms 前矩阵宏 → §4 |
| `must inherit RenderPipelineAsset<T>` | Unity 6 泛型 Asset |
| Scene 无 Overlay UI | Editor `EmitWorldGeometryForSceneView` |
| Legacy/Standard 粉红 | Unsupported + Error；可能故意对照 |
| DrawMeshInstanced「丢了」 | Play？材质 Enable？matrices/count？视锥/层？→ draw-calls |
| 合批差 | MPB？Batcher？同 variant？→ draw-calls |
| Clip 无挖洞 | clipping keyword / Surface=Clip / Cutoff |
| Stats 负 batches saved | Batcher 开时常见；**不是错误** |
| Lit 全黑 | 无可见灯 / 未灯光 Setup / 错 LightMode / 非 Linear → directional-lights |
| 只有高光几乎无漫反射 | Metallic=1，预期 |
| 第 N+1 盏灯无效 | 两端 max；超出丢弃 |
| 有光无影 / acne / Peter-Panning | → directional-shadows；Spot/Point → point-and-spot-shadows |
| 拆阴影后无影 / Viewer 无三 Pass | → unity-upgrades §7.1/7.2 |
| Spot/Point 有光无影 / tile 串扰 / 点光漏光 | → point-and-spot-shadows |
| Bake 后无 lightmap / mask 影 | 是否采样 → baked-light / shadow-masks |
| LOD 无 dither | `LOD_FADE_CROSSFADE` → lod-and-reflections |
| 金属无环境色 | 探针 Bake + SpecCube + Metallic/Smoothness |
| Point/Spot 无实时贡献 | → point-and-spot-lights |
| 调 Color Grading 无效 / LUT banding | → color-grading；3D LUT → unity-upgrades §6.2 |
| RT 相机画错目标 | → unity-upgrades §6.1 |
| LPPV / ProbeVolume 警告 | → unity-upgrades §7.0 |
| 分屏 Post FX 后一屏盖全屏 / Overlay 不透 | → multiple-cameras |
| Soft 粒子硬切 / Distortion 异常 | → particles |
| Render Scale 无效 / Distortion 错位 | → render-scale |

验收触发：改绘制顺序 → §3；升级 → unity-upgrades 对应节；Instanced 看不到 → Play+材质+matrices/count；只有 N 盏光 → 两端 max；玻璃高光不淡 → 预乘仅 diffuse；Bake 仍只有直射 → 先确认采样；Soft/Distortion 无效 → Asset×相机 copy + opaque 后 copy。

官方 Volume 调色入口（可选）：[URP Color Adjustments](https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@16.0/manual/Post-Processing-Color-Adjustments.html)、[HDRP Color Adjustments](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.0/manual/Post-Processing-Color-Adjustments.html)。
