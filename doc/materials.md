# 材质清单

路径：`Assets/CustomSRP/Materials/`（由各场景菜单生成 / 更新）。

本仓**不**按场景分子目录（全部平铺），用命名前缀区分用途。默认值以对应 `Create*Scene.cs` 为准；改完菜单重建会覆盖。

## 跨场景共用

多场景菜单会复用或依赖这些资产；改属性会影响多个验证场。

| 材质 | Shader | Surface / 队列 | Instancing | 主要场景 |
|------|--------|----------------|------------|----------|
| `LitDefault` | Lit | Opaque 2000 | 关 | DirectionalLights、DirectionalShadows、ComplexMaps |
| `LitClip` | Lit | Clip 2450 | **开** | DirectionalLights / Shadows、ManyLights 系、Post/HDR/ColorGrading、Baked |
| `LitTransparent` | Lit | Premul 3000 | 关 | 同上（除 ComplexMaps / MultipleCameras） |
| `LitFade` | Lit | Fade 3000 | 关 | DirectionalLights、DirectionalShadows |
| `LitInstanced` | Lit | Opaque 2000 | **开** | DirectionalLights、Baked、ManyLights 系、Post/HDR/ColorGrading |
| `LitShadowGround` | Lit | Opaque 2000 | 关 | DirectionalShadows、ComplexMaps |

## 按场景分类

### Empty（Oil NPR）

| 材质 | Shader | 用途 |
|------|--------|------|
| `OilOchre` / `OilTeal` / `OilClay` | OilNPR | 球 / 方块 / 胶囊（Outline 开） |
| `OilGround` | OilNPR | 地面（Outline 关） |

贴图：`OilAlbedo.png` / `OilGround.png`（笔触底图）、`OilCanvas.png`（线性 RG+B）、`OilCanvas_Weave.png`（织纹参考）。生成：`Create Empty Scene`。详见 [oil-npr.md](oil-npr.md)。

### CustomRPTest

| 材质 | Shader | 用途 |
|------|--------|------|
| `UnlitGreen` / `UnlitYellow` | Unlit Opaque | 绿/黄 Cube |
| `UnlitWhiteTransparent` | Unlit Transparent | 白半透球 + `UVAlpha` |
| `UnsupportedOpaque` / `UnsupportedTransparent` | Built-in Standard | 故意品红 Error 对照 |

生成：`Create Test Scene`。`UnlitGreen` / `UnlitYellow` 亦被 DrawCalls 复用。

### DrawCalls

| 材质 | Surface / 队列 | Instancing | 用途 |
|------|----------------|------------|------|
| `UnlitRed` / `Green` / `Yellow` / `Blue` | Opaque 2000 | 关 | 共享材质 → SRP Batcher |
| `UnlitInstanced` | Opaque 2000 | **开** | MPB 球（可走 Instancing） |
| `UnlitYellowTransparent` | Transparent 3000 | 关 | 透明球 |
| `UnlitClip` | Clip 2450 | **开** | Cutout + MeshBall 默认 |

### DirectionalLights

| 材质 | Surface | Instancing | 用途 |
|------|---------|------------|------|
| `LitDefault` | Opaque | 关 | 网格球 + MPB |
| `LitTextured` | Opaque | 关 | `LitAlbedo` 贴图球 |
| `LitBlueMetal` | Opaque | 关 | 蓝金属（Metallic=1） |
| `LitFade` | Transparent | 关 | SrcAlpha 淡出 |
| `LitTransparent` | Premul | 关 | 预乘透明 |
| `LitClip` | Clip | **开** | Cutout |
| `LitInstanced` | Opaque | **开** | MeshBall |

### DirectionalShadows

复用：`LitDefault`、`LitClip`、`LitFade`、`LitTransparent`。

| 专用材质 | 用途 |
|----------|------|
| `LitShadowGround` | 地面 Plane |
| `LitShadowRed` / `LitShadowBlue` | 着色对照 caster |
| `UnlitShadowCaster` | Unlit 仍投射阴影 |
| `LitNoReceiveShadows` | 关 `_RECEIVE_SHADOWS` |

### BakedLight / ShadowMasks

| 材质 | 用途 |
|------|------|
| `LitBakeOpaque` | 白基色 + MPB（≈ Opague） |
| `LitBakeSphere` | Clip + UVAlpha（动态球） |
| `LitBakeEmission` | 自发光小方块（≈ Opague Baked Emissive） |
| `LitInstanced` | MeshBall（复用） |

ShadowMasks 与 BakedLight **共用**上述材质与布局（仅 Mixed 模式不同）。

### LOD and Reflections

| 材质 | 用途 |
|------|------|
| `LitLodShared` | 白 Opaque Lit；LOD 子物体经 MPB 上色（≈ Opague） |

### Complex Maps

| 材质 | 用途 |
|------|------|
| `LitCircuitry` | Circuitry 全套贴图 + Mask/Detail/Normal；TransparentPremultiply |
| `LitDefault` / `LitShadowGround` | Plain Lit / 地面对照（复用） |

### Point and Spot Lights

| 材质 | 用途 |
|------|------|
| `LitManyLightsOpaque` | 灰 0.5 Opaque + Instancing |
| `LitInstanced` | MeshBall（复用） |

### Point and Spot Shadows / Post Processing

复用：`LitManyLightsOpaque`、`LitClip`、`LitTransparent`、`LitInstanced`。

### HDR

| 材质 | 用途 |
|------|------|
| `LitHdrOpaque` | Tone Mapping 场主 Opaque（+ 复用 Clip / Transparent / Instanced） |

### Color Grading

| 材质 | 用途 |
|------|------|
| `LitColorGradingOpaque` | Color Grading 场主 Opaque（+ 复用 Clip / Transparent / Instanced） |

### Multiple Cameras

| 材质 | 用途 |
|------|------|
| `LitMultipleCamerasOpaque` | 分屏主几何 |
| `LitMultipleCamerasEmission` | 自发光对照 |
| `UnlitMultipleCamerasRT` | Render Texture 显示用 Unlit |

### Monuriki Terrain

| 材质 | Shader | 用途 |
|------|--------|------|
| `TerrainOilNPRMonuriki` | TerrainOilNPR | `Terrain.materialTemplate`（splat + 油画） |
| `LitMonurikiOcean` | Lit Premul | 环岛海面 Plane |

生成：`Create Monuriki Terrain Scene (1:1)`。Splat 程序化细节；alphamap 高度+坡度；油画见 [terrain.md](terrain.md) / [oil-npr.md](oil-npr.md)。

## 全量速查（按文件名）

共 **35** 份在用（不含已清理残留）。目录下若再出现无引用 `.mat`，以生成脚本为准，可删。

| 文件 | Shader | 队列 | Inst | Keywords（摘要） | Owner 场景 |
|------|--------|------|------|------------------|------------|
| `LitBakeEmission` | Lit | 2000 | 关 | ReceiveShadows | BakedLight |
| `LitBakeOpaque` | Lit | 2000 | 关 | ReceiveShadows | BakedLight |
| `LitBakeSphere` | Lit | 2450 | 关 | Clipping, ShadowsClip | BakedLight |
| `LitBlueMetal` | Lit | 2000 | 关 | ReceiveShadows | DirectionalLights |
| `LitCircuitry` | Lit | 3000 | 开 | Mask/Detail/Normal, Premul | ComplexMaps |
| `LitClip` | Lit | 2450 | 开 | Clipping, ShadowsClip | 多场景共用 |
| `LitColorGradingOpaque` | Lit | 2000 | 开 | ReceiveShadows | ColorGrading |
| `LitDefault` | Lit | 2000 | 关 | ReceiveShadows | 多场景共用 |
| `LitFade` | Lit | 3000 | 关 | ShadowsDither | DirLights / DirShadows |
| `LitHdrOpaque` | Lit | 2000 | 开 | ReceiveShadows | Hdr |
| `LitInstanced` | Lit | 2000 | 开 | ReceiveShadows | 多场景共用 |
| `LitLodShared` | Lit | 2000 | 关 | ReceiveShadows | LodAndReflections |
| `LitManyLightsOpaque` | Lit | 2000 | 开 | ReceiveShadows | ManyLights 系 |
| `LitMultipleCamerasEmission` | Lit | 2000 | 关 | ReceiveShadows | MultipleCameras |
| `LitMultipleCamerasOpaque` | Lit | 2000 | 开 | ReceiveShadows | MultipleCameras |
| `LitNoReceiveShadows` | Lit | 2000 | 关 | （无 ReceiveShadows） | DirectionalShadows |
| `LitShadowBlue` / `LitShadowRed` | Lit | 2000 | 关 | ReceiveShadows | DirectionalShadows |
| `LitShadowGround` | Lit | 2000 | 关 | ReceiveShadows | DirShadows / ComplexMaps |
| `LitTextured` | Lit | 2000 | 关 | ReceiveShadows | DirectionalLights |
| `LitTransparent` | Lit | 3000 | 关 | Premul, ShadowsDither | 多场景共用 |
| `LitMonurikiOcean` | Lit | 3000 | 关 | Premul | MonurikiTerrain |
| `TerrainOilNPRMonuriki` | TerrainOilNPR | 1900 | 关 | Kuwahara, Canvas, Edge, ReceiveShadows | MonurikiTerrain |
| `UnlitBlue` / `Green` / `Red` / `Yellow` | Unlit | 2000 | 关 | — | DrawCalls（Green/Yellow 亦 Test） |
| `UnlitClip` | Unlit | 2450 | 开 | Clipping | DrawCalls |
| `UnlitInstanced` | Unlit | 2000 | 开 | — | DrawCalls |
| `UnlitMultipleCamerasRT` | Unlit | 2000 | 关 | — | MultipleCameras |
| `UnlitShadowCaster` | Unlit | 2000 | 关 | — | DirectionalShadows |
| `UnlitWhiteTransparent` | Unlit | 3000 | 关 | — | CustomRPTest |
| `UnlitYellowTransparent` | Unlit | 3000 | 关 | — | DrawCalls |
| `UnsupportedOpaque` | Standard | -1 | 关 | — | CustomRPTest |
| `UnsupportedTransparent` | Standard | 3000 | 关 | AlphaPremultiply | CustomRPTest |

## 自建 / 重建约定

1. 新测试材质只用 `CustomSRP/Lit`、`CustomSRP/Unlit`、`CustomSRP/OilNPR` 或 `CustomSRP/TerrainLit`；Built-in Standard 仅作 Unsupported 对照。  
2. 优先复用上表「跨场景共用」项，避免再造一份 Opague/Clip/Transparent。  
3. 场景缺失时用对应菜单重建；不要手改 `.unity` 里的材质 GUID。  
4. 数值以 `Create*Scene.cs` / `TestSceneUtility.CreateOrUpdate*` 为准。

## 相关文档

- 各场景「材质一览」：见 [doc/README](README.md) 场景条目  
- Shader 属性约定：[shader-conventions.md](shader-conventions.md)  
- 贴图：`Assets/CustomSRP/Textures/`（`UVAlpha` / `LitAlbedo` / `LitFabric` / `OilCanvas` / `Circuitry/`）
- 油画 NPR：[oil-npr.md](oil-npr.md)
