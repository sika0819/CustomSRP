# Directional Lights

Lit / 多平行光 / BRDF / Fade vs 预乘透明。灯数与材质默认以**当前工程**为准；两端 max 以 Lighting C# + Light HLSL grep 为准。

渲染循环见主 Skill §3。

## 怎么用

1. 确认 Pipeline Asset + Player Linear；用当前场景或项目 Directional / Lit 验证内容。
2. Scene 应有高光与多色侧光（若有）；**Play** 才见 `DrawMeshInstanced` 的 Lit 实例（若场景有）。
3. Frame Debugger：2000 → 2450 → 2500 → 3000。

| 期望 | 能力 |
|------|------|
| Metallic × Smoothness 网格 | Lit + 逐物体属性 |
| 多色侧光叠色 | 最多 N 盏可见 Directional → GPU |
| 金属几乎无漫反射 | Metallic=1，**预期** |
| Fade vs Transparent | 整色淡出 vs 仅 diffuse × alpha |
| Play 实例团 | Lit + `DrawMeshInstanced` |

数据路径：`Cull → visibleLights → SetupLights（过滤 Directional，最多 max）→ globals → Lit GetLighting 循环`。

## Agent 规范

- 改光数据：Lighting C# + Light / Lighting / BRDF HLSL；**两端上限同步**，改前改后 grep。
- Lit Pass `LightMode` 与绘制过滤一致；光 globals 在 `Lighting.Setup`。
- Metallic=1 → 漫近 0、主要靠高光 / 环境反射上色，属预期。
- Fade：整色（含高光）随 alpha；Transparent 预乘：仅漫反射 × alpha，高光保持（`_PREMULTIPLY_ALPHA`）；预乘路径下 `surface.alpha` 通常置 1，避免二次乘 alpha。
- 超出 max：静默丢弃。
- 不展开完整 BRDF 公式，除非用户要求。
- 批处理 → [draw-calls.md](draw-calls.md)；阴影 → [directional-shadows.md](directional-shadows.md)。

### 速查

| 条件 | 预期 |
|------|------|
| 0 盏可见 Directional | Lit 近黑 |
| Smoothness↑ | 高光更尖 |
| Metallic=1 + 彩色 base | 高光带 base 色；漫近 0 |
| 超出 max | 静默丢弃 |
| Edit Mode `DrawMeshInstanced` | 不可见 |

## 技巧 / 排障

| 现象 | 查 |
|------|-----|
| Lit 全黑 | 无可见 Directional / 未 `Lighting.Setup` / 错 LightMode / 非 Linear |
| 只有高光几乎无漫 | Metallic=1，预期 |
| 第 N+1 盏无效 | 两端 max；可见光类型过滤 |
| 玻璃高光不该淡 | 是否误用 Fade；预乘应只乘 diffuse |
| 实例 Edit 不见 | `Update` 未跑，正常 |

对照：关全部 Directional → 近黑 → 只开主光 → 开侧光叠色 → 拉 Smoothness → Metallic=1 → 对比 Fade / Transparent。

BRDF 要点（不展开公式）：

- Metallic：`specular = lerp(MIN_REFLECTIVITY, color, metallic)`（常见 `MIN_REFLECTIVITY=0.04`）
- 每光：`saturate(N·L) * lightColor * DirectBRDF`
- 颜色：`VisibleLight.finalColor`；方向：`-localToWorldMatrix.GetColumn(2)`

## 相关核对

```bash
rg -n "MaxDirLightCount|MAX_DIRECTIONAL_LIGHT_COUNT|MAX_VISIBLE_LIGHT"
rg -n "_PREMULTIPLY_ALPHA|GetBRDF|GetLighting"
rg -n "LightType\.Directional|SetupLights|visibleLights"
```

环境反射是否接入见主 Skill §6；已接入时 Metallic=1 仍可靠 SpecCube 上色（见 [lod-and-reflections.md](lod-and-reflections.md)）。

## 验收

- [ ] 多盏 Directional 叠色可见
- [ ] Metallic/Smoothness 行为符合速查
- [ ] Transparent 预乘高光保持；Fade 整色淡出
- [ ] C# / HLSL max 同值；超出丢弃
- [ ] Play 后 Lit 实例可见（若场景含 `DrawMeshInstanced`）
