# LOD and Reflections

LOD Group、环境反射、Fresnel、间接 Specular。布局以**当前工程**场景数据为准。

接线状态见主 Skill §6（先 grep SpecCube / Probe / `LOD_FADE_CROSSFADE`）。

## 怎么用

1. 确认 Pipeline Asset；用当前场景或项目 LOD / Reflections 验证内容（有则用）。
2. 验 LOD：拉相机或调 Quality **LOD Bias**（硬切默认可用）。
3. 验反射：高 Metallic/Smoothness；Reflection Probe → **Bake**（否则多半是天空盒）。
4. 调材质 Fresnel（如 `_Fresnel`）。

| 能力 | 说明 |
|------|------|
| LOD 硬切 | LOD Group 组件即可 |
| LOD Cross-Fade dither | 需 `LOD_FADE_CROSSFADE` / `ClipLOD` 等消费 `unity_LODFade` |
| 环境反射 / SpecCube | `SampleEnvironment` + `PerObjectData.ReflectionProbes`（若已接） |
| Fresnel / Occlusion×间接 | 以项目 `IndirectBRDF` 为准 |
| lightmap | 独立路径，见 [baked-light.md](baked-light.md) |

实现要点（已接线时）：`GI` → `IndirectBRDF` → `GetLighting` 先间接再直接光；`perObjectData` 含 `ReflectionProbes`。

## Agent 规范

- 探针模式以管线实际支持为准（常见仅 `ReflectionProbeUsage.Simple`；勿把 Blend / Box Projection 写成已支持）。
- 「无反射」先查：`PerObjectData.ReflectionProbes`、GI/环境采样、探针 Bake、Metallic/Smoothness、Linear。
- LOD：硬切 ≠ Cross-Fade；无 dither 时勿说「fade 已接」。
- `unity_LODFade` 字段已声明 ≠ 已消费做 clip。
- 高 Metallic/Smoothness 才明显；暗金属 + 未 Bake 探针 ≠ 反射坏了。
- lightmap / shadow mask 缺口见 [baked-light.md](baked-light.md) / [shadow-masks.md](shadow-masks.md)。

## 技巧 / 排障

| 现象 | 查 |
|------|-----|
| 完全无反射 | PerObjectData；环境采样；Linear；Lit LightMode |
| 只有天空 | 探针 Bake；Box Size；影响范围 |
| 金属仍暗 | Metallic / Smoothness；探针强度 |
| Fresnel 过亮 | Fresnel 强度 / Smoothness |
| LOD 无 dither | Cross-Fade shader 未接 |

对照：高金属高光滑 → Bake 探针 → 降 Smoothness 反射变糊 → 调 Fresnel → 拉相机验 LOD 硬切。

间接 diffuse：非 lightmap 物体走 Light Probe SH；lightmap 未 multi_compile 时静态 Contribute 不会从 lightmap 上色。金属高光环境色主要靠 SpecCube。

## 相关核对

```bash
rg -n "SampleEnvironment|DecodeHDREnvironment|IndirectBRDF|PerObjectData\.ReflectionProbes"
rg -n "LOD_FADE_CROSSFADE|ClipLOD|unity_LODFade"
rg -n "Fresnel|ReflectionProbeUsage"
```

**组件开 CrossFade ≠ shader 已 dither**。

## 验收

- [ ] 高金属物体可见天空/探针反射（Bake 后，且已接 SpecCube）
- [ ] 未声称支持未实现的探针模式
- [ ] LOD 硬切可验；未把 dither 说成已接（除非 grep 命中）
- [ ] 布局数值以场景数据为准
