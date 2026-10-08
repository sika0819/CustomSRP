# Shadow Masks

Mixed=`Shadowmask` 下的烘焙产物与运行时续影。布局常与 Baked Light 共用数据，LightingSettings 切到 Shadowmask。数值以**当前工程**为准。

接线状态见主 Skill §6（先 grep mask 采样 / 管线是否读 `shadowmaskMode`）。

## 怎么用

1. 确认 Pipeline Asset；用当前场景或项目 Shadow Masks 验证内容（有则用）。
2. （可选）Generate Lighting → 可出 lightmap + **shadow mask** 图；运行时是否读取决于是否接线。
3. （可选）缩 Asset `maxDistance` 只验实时阴影截断（示例值 ≠ 项目默认）。
4. 改布局：编辑共用数据后重建（路径 / 菜单以工程为准）。

### vs Baked Light（概念）

| | BakedLight | ShadowMasks |
|--|------------|-------------|
| 布局 / 灯 | 常相同 | 常相同 |
| Mixed | IndirectOnly（常见） | **Shadowmask** |
| 可烘 mask 图 | 否 | **是** |
| 运行时采样 | 以源码为准 | 以源码为准 |

### `ShadowmaskMode`（写枚举名，禁止裸 0/1）

| 枚举 | 含义 | 常用称呼 |
|------|------|----------|
| `ShadowmaskMode.Shadowmask` | 静态投射体不进实时 shadow map | Shadowmask（非 Distance） |
| `ShadowmaskMode.DistanceShadowmask` | 距离内实时，之外用 mask | Distance Shadowmask |

官方 / Quality 显示多为 **Shadowmask** / **Distance Shadowmask**；勿用「Always」指代 `ShadowmaskMode.Shadowmask`。

读 **当前 Quality 等级**；管线是否读取以源码为准。

## Agent 规范

- 目标符号（`_SHADOW_MASK_*`、`MixBakedAndRealtimeShadows`、`SampleBakedShadows`、`occlusionMaskChannel` 等）可点名；**禁止**写成已实现步骤，除非 grep 命中。
- Bake 后仍无远处静态影 → 先确认是否接入 mask 采样，而非只查灯光。
- 改 Quality Distance / Shadowmask 无变化 → 常因管线未读 + 未接线。
- Subtractive Mixed：本系列通常不做。
- Point/Spot 实时口径同 [baked-light.md](baked-light.md) / [point-and-spot-lights.md](point-and-spot-lights.md)。
- `PerObjectData.ShadowMask` 已设 ≠ 已采样。

## 技巧 / 排障

| 现象 | 查 |
|------|-----|
| 与 BakedLight 看起来一样 | 若未采样则预期；差在 Mixed 与可烘 mask |
| Bake 后无远处静态影 | 无 mask 采样 |
| 改 Quality 无变化 | 管线未读 `shadowmaskMode` |
| Lit 全黑 | 无 Directional / 错 LightMode / 非 Linear |

对照：对比 Lighting Mixed → Bake 看出 mask 资源 → 缩 maxDistance 只见实时截断 → 改 Quality 看运行时是否有差。

## 相关核对

```bash
rg -n "SHADOW_MASK|SampleBakedShadows|MixBakedAndRealtimeShadows|occlusionMaskChannel"
rg -n "shadowmaskMode|ShadowmaskMode"
rg -n "PerObjectData\.ShadowMask|OcclusionProbe"
rg -n "MixedLightingMode\.Shadowmask"
```

未接线时业务源码常为 **0 命中**（或仅有 PerObjectData 标志无采样）。

## 验收

- [ ] Mixed=Shadowmask；可与 BakedLight 对照
- [ ] 未把 Bake 出 mask 图说成运行时已续影（除非已采样）
- [ ] `shadowmaskMode` 只用枚举名
- [ ] 目标符号未冒充已实现
