# Point and Spot Lights

实时 Other Lights（Point / Spot）。布局以**当前工程**场景数据为准，勿写死灯数/几何。

接线状态见主 Skill §6（先 grep：Other Lights 上传 + HLSL 循环；实时阴影见 [point-and-spot-shadows.md](point-and-spot-shadows.md)，以 grep 为准）。

## 怎么用

1. 确认 Pipeline Asset；用当前场景或项目 Point/Spot 验证内容（有则用）。
2. 期望行为：Point 近亮远衰、Spot 有锥、可与 Directional 叠光——具体几何以场景为准。
3. 若 Asset 有 **Use Lights Per Object**：每物体 other lights 有上限；`DrawMeshInstanced` 可能缺光，对照时可关。
4. 验：Frame Debugger other light count；衰减与 Spot 锥；与 Directional 叠光。
5. Spot 内角：Inspector Inner/Outer（若有自定义 Light Editor）。

| 能力 | 说明 |
|------|------|
| Point 1/r²×range | 需 C# 上传 + HLSL 衰减 |
| Spot 锥（inner/outer） | directions + spotAngles |
| Other Lights 实时阴影 | 见 [point-and-spot-shadows.md](point-and-spot-shadows.md)；以 grep 为准，勿与直接光混谈 |
| 烘焙 falloff InverseSquared | Editor `lightsDelegate`（若设置；仅编辑器，运行时以管线上传为准） |

## 接线要点（概念）

| 端 | 内容 |
|----|------|
| C# | max other count；`SetupPointLight` / `SetupSpotLight`（或等价） |
| HLSL | `MAX_OTHER_LIGHT_COUNT`；Lighting 循环 |
| Lights-per-object | Asset 开关 → `PerObjectData.LightData\|LightIndices` + keyword + index 消毒 |
| Bake falloff | `FalloffType.InverseSquared` delegate（可选；**仅编辑器**统一场景灯光衰减类型，运行时仍以管线上传为准） |

两端 max **必须同值**（改前改后 grep）。

## Agent 规范

- 实时多光源：**C# 上传** + **HLSL 循环**；与 Directional 上限是两套常量。
- 勿把「未接实时阴影 / mask」说成 Point 完全无效（直接光可能已有）。
- 与 Baked Light：JSON/场景里的 Point/Spot 在 Mode=Realtime/Mixed 且可见时，是否进实时路径以 Lighting 为准。
- 实例偏暗：先关 Lights Per Object 再验。
- 不要整包搬 URP Forward+，除非用户要求。

## 技巧 / 排障

| 现象 | 查 |
|------|-----|
| 仍几乎只有平行光 | other light count？非 Directional 过滤？Lit LightMode？Linear？ |
| 超出 max 无效 | 两端 max 是否同值 |
| Spot 无锥形 | directions / spotAngles；inner/outer |
| 实例缺彩光 | 关 Lights Per Object；Play；材质 Enable GPU Instancing |
| Bake 过亮 | InverseSquared delegate 是否设置 |

对照：单 Point 贴地 → 拉远衰减 → Spot 对准 → 叠 Directional → 开关 Lights Per Object。

## 相关核对

```bash
rg -n "MaxOtherLightCount|MAX_OTHER_LIGHT_COUNT|OtherLight"
rg -n "SetupPointLight|SetupSpotLight|LIGHTS_PER_OBJECT|useLightsPerObject"
rg -n "FalloffType\.InverseSquared|lightsDelegate"
```

## 验收

- [ ] C# / HLSL max 同值；Point 衰减与 Spot 锥可见（若已接）
- [ ] Lights Per Object 可开关且行为可解释
- [ ] 实时阴影 / mask 口径与 grep 一致，未混为一谈
- [ ] 未把「烘焙 Point 看不见」单独当作实时未接证据（需先确认 Mode 与过滤逻辑）
