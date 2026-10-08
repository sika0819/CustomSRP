# CustomSRP 文档

路径相对仓库根。Unity：`6000.6.4f1`。  
场景总表与接线摘要见根目录 [`README.md`](../README.md)。  
跨项目约定：Skill [`.cursor/skills/custom-srp/SKILL.md`](../.cursor/skills/custom-srp/SKILL.md)。

## 场景

| 文档 | 场景 | 验什么 |
|------|------|--------|
| [CustomRPTest](custom-rp-test.md) | `CustomRPTest` | 开篇管线 / Unlit / Unsupported |
| [DrawCalls](draw-calls.md) | `DrawCalls` | Batcher / Instancing / Clip / MeshBall |
| [DirectionalLights](directional-lights.md) | `DirectionalLights` | Lit / 多平行光 / BRDF / 预乘 |
| [DirectionalShadows](directional-shadows.md) | `DirectionalShadows` | CSM / PCF / 透明投射 |
| [BakedLight](baked-light.md) | `BakedLight` | Contribute GI / probes / Mixed 布局 |
| [ShadowMasks](shadow-masks.md) | `ShadowMasks` | 同布局 + Mixed Shadowmask |
| [LOD and Reflections](lod-and-reflections.md) | `LodAndReflections` | LOD Groups / Reflection Probes |
| [Complex Maps](complex-maps.md) | `ComplexMaps` | Mask/Detail/Normal/Emission |
| [Point and Spot Lights](point-and-spot-lights.md) | `PointAndSpotLights` | Many Lights / Other 直接光 |
| [Point and Spot Shadows](point-and-spot-shadows.md) | `PointAndSpotShadows` | Spot/Point 实时阴影 |
| [Post Processing](post-processing.md) | `PostProcessing` | LDR Bloom |
| [HDR](hdr.md) | `Hdr` | HDR / 散射 Bloom / Tone Mapping |
| [Color Grading](color-grading.md) | `ColorGrading` | Adjustments / LUT / Tone Mapping |
| [Multiple Cameras](multiple-cameras.md) | `MultipleCameras` | 分屏 / Overlay / Rendering Layer |
| [Particles](particles.md) | `Particles` | Soft / Flipbook / Distortion |
| [Render Scale](render-scale.md) | `RenderScale` | bufferSize / Final Rescale / Bicubic |
| [FXAA](fxaa.md) | `FXAA` | FXAA Pass / `allowFXAA` |
| [Oil NPR](oil-npr.md) | `Empty` | 油画 NPR / Kuwahara / Outline / 硬阴影 |
| [Terrain](terrain.md) | `MonurikiTerrain` | Terrain + TerrainLit / CustomLit Opaque |

## 管线

| 文档 | 内容 |
|------|------|
| [快速开始](getting-started.md) | 打开工程、挂管线、跑测试场景 |
| [材质清单](materials.md) | 测试材质、跨场景共用 |
| [架构](architecture.md) | Asset → Pipeline → CameraRenderer → Lighting |
| [渲染循环](render-loop.md) | 每帧顺序、`Lighting.Setup`、ShaderTag |
| [Shader 规范](shader-conventions.md) | HLSL include、矩阵宏、Unlit / Lit / TerrainLit |
| [排查](troubleshooting.md) | 常见现象 → 检查点 |
| [Unity 6.0.0](unity-6-0-0.md) | Native Render Passes / EntityId / ImportBackbuffer |
| [Unity 6.1.0](unity-6-1-0.md) | cameraTarget / Final / debugger |
| [Unity 6.2.0](unity-6-2-0.md) | Color LUT 3D compute |
| [Unity 7.0.0](unity-7-0-0.md) | RenderTargetInfo / 去 LPPV / 去 rendererListCulling |
| [Unity 7.1.0](unity-7-1-0.md) | 拆分 Directional / Other Shadows |
| [Unity 7.2.0](unity-7-2-0.md) | 独立 Directional / Other / Shadows Pass |
