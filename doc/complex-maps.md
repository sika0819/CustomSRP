# Complex Maps 场景

路径：`Assets/CustomSRP/Scenes/ComplexMaps.unity`  
生成脚本：`Assets/CustomSRP/Editor/CreateComplexMapsScene.cs`  
材质：`Assets/CustomSRP/Materials/LitCircuitry.mat`  
贴图：`Assets/CustomSRP/Textures/Circuitry/`

Skill：『Complex Maps』节（`.cursor/skills/custom-srp/SKILL.md`）

**布局 / 材质默认值以生成脚本为准**（Albedo tile、Detail tile、Detail 强度、`_Occlusion` 等）。

主循环顺序见 [渲染循环](render-loop.md)。间接光 / 环境反射见 [LOD and Reflections](lod-and-reflections.md)。

## 当前状态

| 项 | 状态 |
|----|------|
| 场景（Circuitry + PlainLit + Ground） | 已建；菜单可重建 |
| Mask MODS / Detail / Normal / Emission | **已接入**（`LitInput` + keywords） |
| Occlusion → `IndirectBRDF` | **已接线**（只乘间接） |
| 阴影 bias `interpolatedNormal` | **已接线** |
| 间接源 | Light Probe SH + 环境 SpecCube（同 Lit） |
| Meta Pass | **未接入** |
| `#pragma multi_compile _ LIGHTMAP_ON` | **未接入**（`GI.hlsl` 有分支，Lit 未开） |

## 怎么打开 / 重建

1. 挂管线：菜单 **CustomSRP → Create & Assign Pipeline Asset**。
2. 菜单 **CustomSRP → Create Complex Maps Scene**。
3. 验 Circuitry：金属线（Mask）、法线起伏、Emission 发光。
4. 对照 PlainLit（`LitDefault`）：无复杂贴图。
5. 逐个关材质 `_MASK_MAP` / `_DETAIL_MAP` / `_NORMAL_MAP` 看差异。
6. 调 `_Occlusion`：应影响间接（环境反射 / SH），不影响直射。

## 实现要点（已接线）

调用链：`LitPass` 填 `Surface`（含 `occlusion` / `interpolatedNormal`）→ `GetGI` → `GetLighting` → `IndirectBRDF(..., gi) * surface.occlusion` → `+ GetEmission`。

| 文件 | 作用 |
|------|------|
| `Shaders/Lit.shader` | `_MASK_MAP` / `_DETAIL_MAP` / `_NORMAL_MAP`；Mask/Detail/Normal/Emission 属性 |
| `Shaders/LitInput.hlsl` | `InputConfig`；`GetMask` / `GetDetail` / `GetNormalTS` / `GetOcclusion` / `GetEmission` |
| `Shaders/LitPass.hlsl` | 切线 / detail UV；Surface；Emission 累加 |
| `ShaderLibrary/Common.hlsl` | `DecodeNormal` / `NormalTangentToWorld` |
| `ShaderLibrary/BRDF.hlsl` | `IndirectBRDF` 末尾 `* surface.occlusion` |
| `ShaderLibrary/Lighting.hlsl` | 先间接再平行光 |
| `ShaderLibrary/Shadows.hlsl` | bias 用 `interpolatedNormal` |
| `ShaderLibrary/GI.hlsl` | Light Probe + SpecCube（间接输入） |

## MODS / Detail / Normal / Emission 约定

### Mask（MODS）

| 通道 | 含义 |
|------|------|
| R | Metallic 乘子 |
| G | Occlusion |
| B | Detail mask |
| A | Smoothness 乘子 |

导入：**关闭 sRGB**（非颜色数据）。

### Detail

- 独立 tiling（`_DetailMap_ST`）；脚本默认相对 Albedo 更高倍率。
- R → albedo 调制；B → smoothness 调制；Detail Normal 单独贴图。
- 导入：非 sRGB；可开 Fadeout Mip Maps（Trilinear）。

### Normal

- Texture Type = **Normal map**；`_NORMAL_MAP` 才传切线 / 采样。
- Detail Normal 经 Mask B 加权后 `BlendNormalRNM`。

### Emission

- `_EmissionMap` × `_EmissionColor`（HDR）；默认材质 Emission 色为白才可见。

## 待接清单

| 符号 / 能力 | 标注 |
|-------------|------|
| Meta Pass | 未实现；Bake 漫反射率可能不对 |
| `#pragma multi_compile _ LIGHTMAP_ON` | Lit 未开；lightmap 分支不生效 |

Lightmap / Shadow Mask 详见 [baked-light.md](baked-light.md) / [shadow-masks.md](shadow-masks.md)。

## 排障

| 现象 | 查 |
|------|-----|
| 全表面同金属/光滑 | `_MASK_MAP`；Mask 是否误开 sRGB |
| 无法线凹凸 | `_NORMAL_MAP`；Texture Type=Normal；网格切线 |
| Detail 不显示 / 过强 | `_DETAIL_MAP`；Mask B；`_DetailAlbedo` / `_DetailSmoothness`（以脚本默认为准） |
| Emission 不亮 | `_EmissionColor` 非黑；Emission 贴图已赋 |
| 调 `_Occlusion` 几乎无感 | 只乘间接；间接弱（环境暗 / SH 近 0 / 无有效反射）；确认 `IndirectBRDF` 已接线，**不是**「未接入」 |
| 与 PlainLit 观感相同 | Circuitry 贴图/keywords 未挂；材质非 `LitCircuitry` |

## 建议对照实验

1. Circuitry vs PlainLit。  
2. 关 `_MASK_MAP` → 金属/光滑变均匀。  
3. 关 `_NORMAL_MAP` → 凹凸消失。  
4. 关 `_DETAIL_MAP` → 近景细节变平。  
5. 拉 `_Occlusion` → 看间接（金属环境反射）变暗/变亮；直射高光应基本不变。  
6. 关灯只留环境/探针贡献时再调 Occlusion（对比更明显）。

## 验收命令

```bash
rg '_MASK_MAP|_DETAIL_MAP|_NORMAL_MAP' Assets/CustomSRP/Shaders/
rg 'IndirectBRDF|surface\.occlusion' Assets/CustomSRP/ShaderLibrary/BRDF.hlsl
rg 'MetaPass|#pragma multi_compile _ LIGHTMAP_ON' Assets/CustomSRP/Shaders/Lit.shader
test -f doc/complex-maps.md
rg -n 'Occlusion' .cursor/skills/custom-srp/SKILL.md Assets/CustomSRP/Editor/CreateComplexMapsScene.cs
```

约定：前二条有命中；第三条在 `Lit.shader` **0 命中**（缺口）；第四条文件存在；第五条命中行不得写「未采样 GI / 遮挡不可见属预期」。
