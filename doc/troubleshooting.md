# 排查

按「现象 → 检查点」定位；改场景优先用 Editor API / 菜单，避免手改 `.unity` YAML。

| 现象 | 检查点 |
|------|--------|
| `UNITY_MATRIX_M` undeclared / `GetObjectToWorldMatrix` must return a value | `UnityInput.hlsl` 是否在 include `SpaceTransforms.hlsl` **前**定义了矩阵宏 |
| `QuaternionToEuler: Input quaternion was not normalized` | `CameraRenderer.Cull` 是否对 SceneView/Preview（及非单位四元数）做了 `Quaternion.Normalize`；场景旋转用 `Quaternion.Euler`，勿手填 `m_LocalRotation` |
| Unity 6：`must inherit RenderPipelineAsset<T>` | Asset 是否为 `RenderPipelineAsset<CustomRenderPipeline>` |
| Scene 视图看不到 Overlay UI | Editor partial 是否调用 `EmitWorldGeometryForSceneView` |
| Legacy / Standard 物体不可见，或不该是粉红 | Editor `DrawUnsupportedShaders` + `Hidden/InternalErrorShader`；`CustomRPTest` 里红 Cube / 蓝 Sphere 为**故意** Error |
| 自定义材质粉红或全黑 | Graphics 是否已挂本管线 Asset；材质 Shader 是否为 `CustomSRP/Unlit` 或 `CustomSRP/Lit` |
| 透明物体排序错乱 | Frame Debugger：Opaque（2000）→ AlphaTest / Cutout（2450）→ Skybox（2500）→ Transparent（3000）；确认 Queue / Blend / ZWrite |
| MeshBall「丢了」 / 看不见 | 是否 **Play Mode**；材质 **Enable GPU Instancing**；视锥内 / 层未剔除；MPB 数组长度与实例数匹配（以 `MeshBall.cs` 为准）。**勿查** Asset `Use GPU Instancing`（MeshBall 走 `DrawMeshInstanced`） |
| 合批不如预期 | 是否挂了 MPB；Asset SRP Batcher 开关；是否同 shader variant |
| Clip / Cutout 无挖洞 | 材质 `_CLIPPING` / Surface=Clip；`_Cutoff`；贴图 alpha |
| Lit 全黑 | 场景是否有可见 Directional；`Lighting.Setup` 是否在 Draw 前；Pass 是否 `LightMode=CustomLit`；Linear Color Space |
| Lit 有漫反射无高光 | Smoothness 是否过低；视角是否背对高光；Metallic=0 且 base 很暗（高光基线 ≈ 0.04） |
| Lit 只有高光几乎无漫反射 | Metallic=1，**属预期**，不是 bug |
| 第 5 盏 Directional 无效 | 两端 max=4（`Lighting.cs` / `Light.hlsl`），超出静默丢弃 |
| BakedLight 里 Point/Spot 无贡献 / Bake 后无间接光 | 见 Skill『Baked Light』节与 [BakedLight 场景](baked-light.md)（实时仅 Directional；GI 采样未接入） |
| ShadowMasks Bake 后无 mask 影 / 与 BakedLight 一样 | 见 Skill『Shadow Masks』节与 [ShadowMasks 场景](shadow-masks.md)（可烘 mask；运行时未采样；管线未读 Quality） |
| LodAndReflections LOD 不切换 / 无 dither | 见 Skill『LOD and Reflections』节与 [LOD and Reflections 场景](lod-and-reflections.md)（硬切可验；Cross-Fade dither 未接入） |
| 金属无环境反射 / Bake 探针画面不变 | 见 Skill『LOD and Reflections』节（查 `GI.hlsl` / `PerObjectData.ReflectionProbes` / 探针是否 Bake / `Simple`） |
| Circuitry 无法线 / 金属线不分 | `_NORMAL_MAP` / `_MASK_MAP`；Normal Type；Mask 非 sRGB；见 [Complex Maps 场景](complex-maps.md) |
| 调 `_Occlusion` 几乎无感 | 只乘间接；查探针/环境/`IndirectBRDF`；见 [complex-maps.md](complex-maps.md) |
| Emission 不亮 | `_EmissionColor` + Emission 贴图；LitPass `GetEmission`；见 [complex-maps.md](complex-maps.md) |
| Spot/Point 有光无实时影 / 与 Many Lights 搞混 | 见 Skill『Point and Spot Shadows』与 [PointAndSpotShadows 场景](point-and-spot-shadows.md)（本仓 Other 影未接；直接光已接） |
| 无 Bloom / 无 glow | Asset 是否挂 `PostFXSettings`；intensity>0；Game/Scene 相机（非 Preview）；Scene Image Effects 开；见 [post-processing.md](post-processing.md) |
| Bloom 全图糊 / 过曝 | 降 `intensity` / 升 `threshold`；查 Frame Debugger Bloom 金字塔 |
| Post FX 后画面上下颠倒 | `UnityInput` 是否声明 `_ProjectionParams`；顶点是否在 `_ProjectionParams.x < 0` 时翻 V |
| 调 Color Grading 无效 / 过曝过灰 | Settings 是否挂上；调参是否可见；见 [color-grading.md](color-grading.md) 与 Skill『Color Grading』 |
| LUT banding 条带 | 升 Asset `colorLUTResolution`；强 HDR 渐变预期；见 [color-grading.md](color-grading.md) |
| 一侧相机整屏无几何 / 「同场景不同灯」失败 | Rendering Layer：物体是否 Everything；相机 mask 是否滤掉默认 Layer 1；`maskLights`；见 [multiple-cameras.md](multiple-cameras.md) |
| `GetTemporaryRT (width \|\| height <= 0)` | pixel 尺寸；Bloom `downscaleLimit`；见 [multiple-cameras.md](multiple-cameras.md) |
| Instanced / MeshBall 仅一侧消失 | Instanced renderingLayer 常默认 Layer 1；勿只查 MeshRenderer；见 [multiple-cameras.md](multiple-cameras.md) |

Draw Calls 开关组合的预期现象，详见 [DrawCalls 场景 — 预期现象速查](draw-calls.md#预期现象速查)。  
Lit / 多光 / 高光预期，详见 [DirectionalLights 场景 — 预期现象速查](directional-lights.md#预期现象速查)。  
阴影排障 / 性能，详见 Skill『Directional Shadows』节与 [DirectionalShadows 场景](directional-shadows.md)（此处不复述现象表）。  
Point/Spot 实时阴影，详见 Skill『Point and Spot Shadows』与 [PointAndSpotShadows 场景](point-and-spot-shadows.md)。  
Baked / 多光源（Point·Spot），详见 Skill『Baked Light』节与 [BakedLight 场景](baked-light.md)。  
Shadow Masks，详见 Skill『Shadow Masks』节与 [ShadowMasks 场景](shadow-masks.md)。  
LOD / 环境反射，详见 Skill『LOD and Reflections』节与 [LOD and Reflections 场景](lod-and-reflections.md)。  
Complex Maps，详见 Skill『Complex Maps』节与 [Complex Maps 场景](complex-maps.md)。  
Post FX / Bloom，详见 [Post Processing 场景](post-processing.md)。  
Color Grading / LUT / tone mapping，详见 [Color Grading 场景](color-grading.md) 与 Skill [references/color-grading.md](../.cursor/skills/custom-srp/references/color-grading.md)。  
Multiple Cameras / viewport / Rendering Layer，详见 [Multiple Cameras 场景](multiple-cameras.md) 与 Skill [references/multiple-cameras.md](../.cursor/skills/custom-srp/references/multiple-cameras.md)。

## 验证手段

- **Frame Debugger**：确认 Opaque（2000）→ AlphaTest（2450）→ Skybox（2500）→ Transparent（3000）；Error 绘制为单独一段
- **双相机**：Main（Depth −1，Skybox）+ Secondary（Depth 0，Depth only）是否都出现在绘制列表中

## 已修勿回退

缺矩阵宏、非泛型 `RenderPipelineAsset`、Cull 不 Normalize —— 合入上表检查点，回退会立刻复现对应报错。
