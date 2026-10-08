# Complex Maps

Mask / Detail / Normal / Emission。布局与材质默认以**当前工程**为准。

间接光 / 环境反射见主 Skill §6 与 [lod-and-reflections.md](lod-and-reflections.md)。Meta / `LIGHTMAP_ON` 是否接入先 grep。

## 怎么用

1. 确认 Pipeline Asset；用当前场景或项目 Complex Maps 验证内容（有则用）。
2. 验 Mask 金属线、法线起伏、Emission；对照无复杂贴图的 Plain Lit（若有）。
3. 逐个关 `_MASK_MAP` / `_DETAIL_MAP` / `_NORMAL_MAP`（或项目等价 keyword）。
4. 调 Occlusion：应影响**间接**（环境 / SH），不影响直射（若已乘 `IndirectBRDF`）。

| 能力 | 说明 |
|------|------|
| Mask MODS / Detail / Normal / Emission | 以源码为准 |
| Occlusion → 间接 | 通常只乘间接 |
| 阴影 bias 用法线 | 用几何插值法线，非扰动法线 |
| Meta / lightmap multi_compile | 常见缺口；先 grep |

调用链（概念）：LitPass 填 Surface（occlusion / interpolatedNormal）→ `GetGI` → `GetLighting` → `IndirectBRDF * occlusion` → `+ Emission`。

## Agent 规范

### MODS（通道以项目 LitInput 为准，常见）

| 通道 | 含义 |
|------|------|
| R | Metallic 乘子 |
| G | Occlusion |
| B | Detail mask |
| A | Smoothness 乘子 |

- 导入：Mask/Detail **非 sRGB**；法线 Type=Normal。
- Keywords 按材质 feature 控制变体。
- Occlusion **通常只乘间接**；直射不受影响；间接弱则观感弱 ≠ 未接线。
- 若环境反射 + Probe SH 已接入，**禁止**写「Occlusion 因 GI 未接入而不可见」。
- Detail：独立 tiling；常 R→albedo、B→smoothness；Detail Normal 经 Mask B 加权。
- Emission：贴图 × HDR 色；色为黑则不可见。

## 技巧 / 排障

| 现象 | 查 |
|------|-----|
| 全表面同金属/光滑 | Mask keyword；Mask 是否误开 sRGB |
| 无法线凹凸 | Normal keyword；Type=Normal；网格切线 |
| Detail 不显示/过强 | Detail keyword；Mask B；Detail 强度 |
| Emission 不亮 | Emission 色非黑；贴图已赋 |
| 调 Occlusion 几乎无感 | 只乘间接；间接弱；确认是否已乘 occlusion |
| 与 Plain Lit 相同 | keywords/贴图未挂 |

对照：复杂材质 vs Plain → 关 Mask → 关 Normal → 关 Detail → 拉 Occlusion（弱直射/只留环境）→ 看金属环境反射明暗。

## 相关核对

```bash
rg -n "_MASK_MAP|_DETAIL_MAP|_NORMAL_MAP"
rg -n "IndirectBRDF|surface\.occlusion"
rg -n "interpolatedNormal|DecodeNormal|NormalTangentToWorld"
rg -n "LightMode.*=.*Meta|multi_compile _ LIGHTMAP_ON"
```

阴影 acne：bias 必须用插值几何法线。Detail tiling / 强度以生成脚本或材质为准，勿写死记忆值。

## 验收

- [ ] Mask/Normal/Detail/Emission 开关可见差异
- [ ] Occlusion 只影响间接；未误判为未接线（若间接路径已有）
- [ ] 导入设置（非 sRGB / Normal）正确
- [ ] 未声称 Meta / lightmap 已接（除非 grep 命中）
