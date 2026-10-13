# Shadow Masks 场景

路径：`Assets/CustomSRP/Scenes/ShadowMasks.unity`  
生成脚本：`Assets/CustomSRP/Editor/CreateShadowMasksScene.cs`（内部调用 `CreateBakedLightScene.CreateGiScene`）  
布局 / 灯参数据：共用 `Assets/CustomSRP/Editor/BakedLightSceneData/*.json`  
Lighting Settings：`Assets/CustomSRP/Settings/ShadowMasksLightingSettings.asset`（经 `TestSceneUtility.EnsureGiLightingSettings` → `MixedLightingMode.Shadowmask`）

**数字与灯 Bias 以 JSON / 生成脚本为准**；改前打开 `lights.json` / `objects.json`，勿写死后合计。

主循环顺序见 [渲染循环](render-loop.md)（**此处不复制顺序串**）。

## 当前状态

| 项 | 状态 |
|----|------|
| 场景布局（与 BakedLight 同数据） | 已建；菜单可重建 |
| Mixed | `Shadowmask`（`ShadowMasksLightingSettings`） |
| Editor Generate Lighting | 可烘焙 lightmap + **shadow mask** 图 |
| 运行时采样 shadow mask / lightmap / probes | **未接入** |
| 实时平行光 | 仅 Directional；两端 max=4（`Lighting.cs` / `Light.hlsl`） |
| Point / Spot 实时 | **不支持**（同 BakedLight） |
| Quality `shadowmaskMode` | 以 `ProjectSettings/QualitySettings.asset` **当前等级**为准；管线**未读** |

### `ShadowmaskMode`（枚举名，勿裸数字口述）

| 枚举名 | 含义（简述） | 常见别称 |
|--------|--------------|----------|
| `ShadowmaskMode.Shadowmask` | 静态投射体不进实时 shadow map | Always / Shadowmask |
| `ShadowmaskMode.DistanceShadowmask` | 阴影距离内实时，之外用 mask | Distance |

当前工程值：打开 QualitySettings，用 `m_CurrentQuality` 索引对应等级的 `name` + `shadowmaskMode`，再按上表解析。

## vs BakedLight

| | BakedLight | ShadowMasks |
|--|------------|-------------|
| 布局 / 灯 / 材质 | `BakedLightSceneData` | **相同** |
| Mixed | IndirectOnly | **Shadowmask** |
| LightingSettings asset | `BakedLightLightingSettings` | `ShadowMasksLightingSettings` |
| 可烘 shadow mask 图 | 否（Indirect） | **是** |
| 运行时采样 | 未接入 | 未接入（同一缺口） |

布局与 Contribute GI / probes 说明见 [BakedLight 场景](baked-light.md)。灯表 **以该文 / `lights.json` 为准**，本文不重复 Bias 表。

## 场景是干什么的

验证 Mixed=**Shadowmask** 下的烘焙产物与场景约定，验证同布局切换 Mixed=Shadowmask。

它**不是** Batcher / BRDF / CSM 专项场；也**不是**已接线的 Distance/Always 运行时混合影。

| 你期望看到 / 验证 | 对应能力 |
|-------------------|----------|
| 与 BakedLight 同几何 | 共用 JSON |
| Lighting 面板 Mixed = Shadowmask | `ShadowMasksLightingSettings` |
| Bake 后有 shadow mask 贴图 | Editor 烘焙 |
| 远处静态影 / Distance 过渡 | **当前不行**（缺采样；管线未读 Quality） |
| Point/Spot 影响画面 | **当前不行**（同 BakedLight） |

## 怎么打开 / 重建

1. 挂管线：菜单 **CustomSRP → Create & Assign Pipeline Asset**（已挂可跳过）。
2. 打开或重建：菜单 **CustomSRP → Create Shadow Masks Scene** → `Assets/CustomSRP/Scenes/ShadowMasks.unity`。
3. （可选）Window → Rendering → Lighting → **Generate Lighting**。可出 shadow mask；运行时 Lit **不会**读。
5. 改布局：编辑 `BakedLightSceneData/*.json` 后重新跑本菜单或 Baked Light 菜单。

## 待接清单（未实现）

**以下为目标符号，不是本仓现有类型/函数；预期业务源码 grep（`*.cs` / `*.hlsl` / `*.shader` / `*.compute`）为 0；未来若实现需同步更新本文与 Skill。**

| 符号（目标概念） | 标注 |
|------------------|------|
| `_SHADOW_MASK_DISTANCE` | 目标概念 / 本仓未实现 |
| `_SHADOW_MASK_ALWAYS` | 目标概念 / 本仓未实现 |
| `ShadowMask` | 目标概念 / 本仓未实现 |
| `SampleBakedShadows` | 目标概念 / 本仓未实现 |
| `unity_ProbesOcclusion` | 目标概念 / 本仓未实现 |
| `PerObjectData.ShadowMask` / `OcclusionProbe` | GeometryPass 已设；**采样未接** |
| `MixBakedAndRealtimeShadows` | 目标概念 / 本仓未实现 |
| `occlusionMaskChannel` | 目标概念 / 本仓未实现 |
| `CopyProbeOcclusionArrayFrom`（MeshBall） | 目标概念 / 本仓未实现 |

实现前排障**不要**把上表写成「已有能力」或展开接线步骤 / 通道公式。

**Subtractive** Mixed 模式：本系列不做。

## 排障

| 现象 | 查 |
|------|-----|
| 与 BakedLight 看起来一样 | 预期（未采样）；差在 Mixed 与可烘 mask 图 |
| Bake 后仍无远处静态影 / mask 续影 | 无 shadow mask 采样；当前缺口 |
| 改 Quality Distance / Shadowmask 无变化 | 管线未读 `shadowmaskMode`；且未接线 |
| Point/Spot 看不见 | 同 [BakedLight](baked-light.md)（本场 Mixed 灯用途 / Other max） |
| Lit 全黑 | 无可见 Directional / 非 `CustomLit` / 非 Linear |

## 建议对照实验

1. 与 BakedLight 对比 Lighting Settings：本场 Mixed = Shadowmask。  
2. Generate Lighting → 资源里可有 shadow mask 图；画面仍无 mask 续影。  
3. 临时缩 `maxDistance`→ 只见实时影截断。  
4. 改 Quality 的 Shadowmask / DistanceShadowmask → **当前无运行时差异**（管线未读）。
