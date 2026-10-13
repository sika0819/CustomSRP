# LOD and Reflections 场景

路径：`Assets/CustomSRP/Scenes/LodAndReflections.unity`  
生成脚本：`Assets/CustomSRP/Editor/CreateLodAndReflectionsScene.cs`  
布局数据：`Assets/CustomSRP/Editor/LodAndReflectionsSceneData/*.json`  
Lighting Settings：`Assets/CustomSRP/Settings/LodAndReflectionsLightingSettings.asset`

**规模 / 变换 / 灯参以 JSON / 生成脚本为准**。

主循环顺序见 [渲染循环](render-loop.md)。

## 当前状态

| 项 | 状态 |
|----|------|
| 场景（Plane + LOD Groups + Probes） | 已建；菜单可重建 |
| LOD Groups（Cross Fade + Animate） | 场景组件已配；**shader dither 未接入** |
| 环境反射（`GI.hlsl` / `IndirectBRDF` / `_Fresnel`） | **已接入** |
| `PerObjectData.ReflectionProbes` + LightProbe 等 | CameraRenderer **已设** |
| Lightmap / Shadow Mask 采样 | **未接入**（无 `LIGHTMAP_ON` / `_SHADOW_MASK_*` multi_compile） |
| diffuse GI | Light Probe SH（非 lightmap 物体） |

## 怎么打开 / 重建

1. 挂管线：菜单 **CustomSRP → Create & Assign Pipeline Asset**。
2. 菜单 **CustomSRP → Create LOD and Reflections Scene**。
3. 验 LOD：拉相机或调 Quality **LOD Bias**。
4. 验反射：选中高 metallic / smoothness 实例；Reflection Probe → **Bake**（否则多半是天空盒）。
5. 调材质 `_Fresnel`（默认 1）。

## 实现要点（已接线）

| 文件 | 作用 |
|------|------|
| `ShaderLibrary/GI.hlsl` | `SampleEnvironment` / `GetGI`；`DecodeHDREnvironment` |
| `ShaderLibrary/BRDF.hlsl` | `IndirectBRDF` + `perceptualRoughness` / `fresnel` |
| `ShaderLibrary/Lighting.hlsl` | `GetLighting(..., GI)` 先加间接再累加平行光 |
| `Shaders/UnityInput.hlsl` | `unity_SpecCube0_HDR`、SH、LightmapST |
| `Runtime/CameraRenderer.cs` | `perObjectData` 含 `ReflectionProbes` |
| `Lit.shader` / `LitInput` | `_Fresnel` |

## LOD 约定（场景侧）

- 球组：黄 / 青 / 红；阈值 `0.6` / `0.3` / `0.1`；CrossFade + Animate。
- 立方体金字塔：叠加 LOD；变换以生成脚本为准。

## 探针约定

- `ReflectionProbeUsage.Simple`（不支持 Blend / Box Projection）。
- size / 位置以 `probes.json` + `meta.json` 为准。

## 待接清单（LOD Cross-Fade 仍未实现）

| 符号 | 标注 |
|------|------|
| `LOD_FADE_CROSSFADE` | 未实现 |
| `ClipLOD` | 未实现 |
| 消费 `unity_LODFade.x` 做 dither clip | 字段已声明；消费未接 |

Lightmap / Shadow Mask 相关符号仍见 [baked-light.md](baked-light.md) / [shadow-masks.md](shadow-masks.md)。

## 排障

| 现象 | 查 |
|------|-----|
| 完全无反射 | `PerObjectData.ReflectionProbes`；`GI.hlsl`；Linear；CustomLit |
| 只有天空 | 探针 Bake；Box Size；物体是否在影响范围内 |
| 金属仍暗 | Metallic / Smoothness；探针强度 |
| Fresnel 过亮 | `_Fresnel` / Smoothness |
| LOD 无 dither | Cross-Fade shader 未接 |

## 建议对照实验

1. 高金属 + 高 Smoothness → 应见天空/探针。  
2. Bake 四角探针 → 映入周围 LOD 物体。  
3. 降 Smoothness → 反射变糊。  
4. 调 `_Fresnel`。  
5. 拉相机验 LOD 硬切。
