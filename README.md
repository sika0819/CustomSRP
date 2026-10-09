# CustomSRP

Unity 6 (`6000.6.4f1`) 自建 Scriptable Render Pipeline 练习工程（Render Graph）。

文档索引：[`doc/README.md`](doc/README.md)  
跨项目约定（Skill）：[`.cursor/skills/custom-srp/SKILL.md`](.cursor/skills/custom-srp/SKILL.md)

## 打开工程

1. Unity Hub → Open → 本目录
2. **CustomSRP → Create & Assign Pipeline Asset**（若 Graphics 未挂管线）
3. 按下方场景表用对应 **Create … Scene** 菜单重建或打开

颜色空间建议 **Linear**。

## 场景与效果

每场只验本主题；接线状态以源码 / 对应 `doc/` 为准。

| 场景 | 菜单 | 验什么 | 接线（摘要） | 文档 |
|------|------|--------|--------------|------|
| `CustomRPTest` | Create Test Scene | 管线 Clear / Unlit / Unsupported→Error / 双相机 / Overlay UI | 基础绘制 | [doc](doc/custom-rp-test.md) |
| `DrawCalls` | Create Draw Calls Scene | SRP Batcher / MPB / Clip / `DrawMeshInstanced` | Batcher + Instancing | [doc](doc/draw-calls.md) |
| `DirectionalLights` | Create Directional Lights Scene | Lit BRDF / 多平行光 / 预乘透明 | Directional Lights | [doc](doc/directional-lights.md) |
| `DirectionalShadows` | Create Directional Shadows Scene | CSM / PCF / Bias / 透明投射 | Directional Shadows | [doc](doc/directional-shadows.md) |
| `BakedLight` | Create Baked Light Scene | Contribute GI / probes / Mixed 布局 | 布局已建；lightmap keyword / Meta 待补 | [doc](doc/baked-light.md) |
| `ShadowMasks` | Create Shadow Masks Scene | 同布局 + Mixed Shadowmask | 布局已建；shadow mask 采样未接 | [doc](doc/shadow-masks.md) |
| `LodAndReflections` | Create LOD and Reflections Scene | LOD Groups / Reflection Probes | 环境反射已接；LOD Cross-Fade 未接 | [doc](doc/lod-and-reflections.md) |
| `ComplexMaps` | Create Complex Maps Scene | Mask/Detail/Normal/Emission | Occlusion×间接已接；Meta 未接 | [doc](doc/complex-maps.md) |
| `PointAndSpotLights` | Create Point and Spot Lights Scene | Many Lights 衰减 / Forward+ | Other 直接光已接 | [doc](doc/point-and-spot-lights.md) |
| `PointAndSpotShadows` | Create Point and Spot Shadows Scene | Spot/Point Hard 影 / tile / pancaking | Other 实时阴影已接 | [doc](doc/point-and-spot-shadows.md) |
| `PostProcessing` | Create Post Processing Scene | LDR Bloom | Post FX Stack 已接 | [doc](doc/post-processing.md) |
| `Hdr` | Create HDR Scene | HDR 缓冲 / 散射 Bloom / Tone Mapping | 已接 | [doc](doc/hdr.md) |
| `ColorGrading` | Create Color Grading Scene | Adjustments / LUT / ACES | Color LUT（含 3D compute）已接 | [doc](doc/color-grading.md) |
| `MultipleCameras` | Create Multiple Cameras Scene | 分屏 / Overlay / RT / Rendering Layer | viewport Final + 按相机 Post FX 已接 | [doc](doc/multiple-cameras.md) |
| `Particles` | Create Particles Scene | Soft / Near Fade / Flipbook / Distortion | Color/Depth copy 已接 | [doc](doc/particles.md) |
| `RenderScale` | Create Render Scale Scene | Inherit vs Override 0.5 / Bicubic | `renderScale` + Final Rescale 已接 | [doc](doc/render-scale.md) |
| `FXAA` | Create FXAA Scene | 分屏开/关 FXAA | FXAA Pass + `allowFXAA` 已接 | [doc](doc/fxaa.md) |
| `Empty` | Create Empty Scene | 油画 NPR / Kuwahara / Outline / 笔触本影 | OilNPR 已接 | [doc](doc/oil-npr.md) |
| `MooreaTerrain` | Create Moorea Terrain Scene (1:1) | 真实 16.5 km 全岛 + 四时段油画天空 | TerrainOilNPR + OilSkyboxNPR | [doc](doc/terrain.md) |

## 目录

```
Assets/CustomSRP/
  Runtime/           # Asset / Pipeline / CameraRenderer / Shadows / PostFX
    Passes/          # Setup / Lighting / Directional·Other Shadows / Geometry / PostFX …
  ShaderLibrary/     # Surface / Light / BRDF / Lighting / Shadows / GI / ForwardPlus
  Shaders/           # Unlit / Lit / OilNPR / OilSkyboxNPR / TerrainLit·OilNPR / Particles / PostFX / ColorLUT
  Editor/            # 管线挂载 + Create*Scene + *SceneData JSON
  Examples/          # PerObjectMaterialProperties / MeshBall
  Settings/          # Pipeline Asset / PostFX / LightingSettings
  Materials/         # 测试材质（菜单生成）
  Textures/          # UVAlpha / Lit* / Oil* / Particles …
  Scenes/            # 上表 19 个验证场景
Assets/Terrain/Moorea/     # SRTM + OSM 参考
doc/                       # 场景与管线文档
```

## 当前能力（摘要）

- **几何**：Opaque / AlphaTest / Transparent；Unlit + Lit（`CustomLit`）；Unsupported → Error（Editor）
- **风格**：OilNPR（物体 + Outline）；TerrainOilNPR（splat + control-UV Kuwahara，无 Outline）；OilSkyboxNPR（黎明 / 白天 / 黄昏 / 夜晚）；旧 TerrainLit 保留对照
- **光**：最多 4 盏 Directional；Other（Point/Spot）直接光 + Forward+；环境反射（SpecCube）
- **影**：Directional CSM + PCF；Other 第二 atlas（透视 / Point 六面）；油画笔触本影（冷/暖 tint）
- **后处理**：Bloom（LDR/散射）、Color Grading + LUT、Tone Mapping、FXAA、Render Scale / Bicubic
- **缓冲**：HDR 中间缓冲、Color/Depth copy（粒子 Soft/Distortion）、分相机 viewport Present
- **合批**：SRP Batcher / GPU Instancing / Dynamic Batching（Asset 开关）
- **未接 / 待补**：shadow mask 运行时采样、LOD Cross-Fade dither、Lit Meta Pass、`LIGHTMAP_ON` multi_compile（Lit）

## 建议下一步

1. 补 Lit `LIGHTMAP_ON` / Meta，验收 BakedLight 间接光
2. 接 shadow mask 采样，验收 ShadowMasks
3. 接 LOD Cross-Fade（`LOD_FADE_CROSSFADE` / `ClipLOD`）
