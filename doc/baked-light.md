# Baked Light 场景

路径：`Assets/CustomSRP/Scenes/BakedLight.unity`  
生成脚本：`Assets/CustomSRP/Editor/CreateBakedLightScene.cs`  
布局 / 灯参数据：`Assets/CustomSRP/Editor/BakedLightSceneData/*.json`  
Lighting Settings：`Assets/CustomSRP/Settings/BakedLightLightingSettings.asset`（经 `TestSceneUtility.EnsureBakedIndirectLightingSettings`）

**数字与灯 Bias 以 JSON / 生成脚本为准**；改前打开 `lights.json` / `objects.json`，勿写死后合计。

主循环顺序见 [渲染循环](render-loop.md)（**此处不复制顺序串**）。

## 当前能力边界

| 能力 | 本仓状态 |
|------|----------|
| 场景布局（Contribute GI / probes / Mixed 灯） | 已建；菜单可重建 |
| 实时平行光 | Directional；两端 max 以 Lighting C# + `Light.hlsl` 为准 |
| Point / Spot 实时 | 管线 Other 光已接；本场 Mixed 灯主要作烘焙输入 |
| Light Probe SH | **已接**（`GI.hlsl` `SampleLightProbe`；非 lightmap 物体） |
| lightmap | `GI.hlsl` 有 `SampleLightMap`，但 Lit **缺** `#pragma multi_compile _ LIGHTMAP_ON` → 运行时不走 lightmap |
| Meta Pass | **未接** |
| Editor Generate Lighting | 可烘焙；静态 lightmap 需补 keyword / Meta 后才参与 Lit |

**「Bake 后静态间接光不变」口径：** 缺 `LIGHTMAP_ON` multi_compile + Meta；动态物体仍可走 Light Probe。

## 场景是干什么的

验证 Baked GI **布局与约定**（静态Contribute、动态 probes、Mixed 灯、自发光物体），以本仓 JSON / 生成脚本为准。

它**不是** Batcher 场（[DrawCalls](draw-calls.md)）、**不是** BRDF 网格场（[DirectionalLights](directional-lights.md)）、**不是** CSM 专项场（[DirectionalShadows](directional-shadows.md)）。

| 你期望看到 / 验证 | 对应能力 |
|-------------------|----------|
| 绿地面 + 开口结构 + 动态球 | 场景布局（JSON） |
| 两盏 Directional 叠光 | 实时 `Lighting.SetupLights` |
| Point/Spot 影响画面 | **当前不行**（烘焙输入；GI 未采样） |
| Bake 后间接光 / 探照明 | **当前不行**（缺采样） |

## 怎么打开 / 重建

1. 挂管线：菜单 **CustomSRP → Create & Assign Pipeline Asset**（已挂可跳过）。
2. 打开或重建：菜单 **CustomSRP → Create Baked Light Scene** → `Assets/CustomSRP/Scenes/BakedLight.unity`。
3. （可选）Window → Rendering → Lighting → **Generate Lighting**。Bake 结果在接入 GI 前**不会**改变 Lit 采样路径。
4. 改布局：编辑 `BakedLightSceneData/*.json` 后重新跑菜单；灯 Bias / intensity 以 `lights.json` 字段为准。

## 场景里有什么

分组以生成脚本与 JSON 为准（**不写死个数**）：

| 分组 | 内容 |
|------|------|
| Ground | Plane，Contribute GI，绿 base（MPB） |
| Varied Objects | 墙 / 顶 / 静态 Cube（Contribute GI）；动态 Sphere（Light Probes）；Emissive 小方块 |
| Light Probe Group Inside / Outside | 探针组（运行时尚未采样） |
| Lights | Mixed Directional + Point + Spot（见下表） |
| Mesh Ball | Lit Instanced；Play 才见实例 |

材质：`LitBakeOpaque` / `LitBakeSphere` / `LitBakeEmission`；复用 `LitInstanced`。颜色经 `PerObjectMaterialProperties`（含 `emissionColor` 字段；Lit 是否采样 emission 以 Shader 为准）。

## 灯参数（摘自 `lights.json`，写入时已核对）

Unity `type`：0=Spot，1=Directional，2=Point。`mapping` Mixed；`shadows` Soft。

| name | type | intensity | shadowBias | shadowNormalBias |
|------|------|-----------|------------|------------------|
| Directional Light | Directional (1) | 0.7 | 0 | 1 |
| Directional Light Secondary | Directional (1) | 0.3 | 0 | 1 |
| Point Light | Point (2) | 15 | 0.8 | 1 |
| Point Light (1) | Point (2) | 20 | 0.8 | 1 |
| Point Light (2) | Point (2) | 15 | 0.8 | 1 |
| Spot Light | Spot (0) | 10 | 0 | 1 |
| Spot Light (1) | Spot (0) | 10 | 0 | 1 |
| Spot Light (2) | Spot (0) | 10 | 0 | 1 |
| Spot Light (3) | Spot (0) | 6.78 | 0 | 1 |

若 JSON 已改，**以文件为准**，勿沿用上表记忆。

## Lighting Settings（场景）

经 `EnsureBakedIndirectLightingSettings`：Baked GI on、realtime GI off、Mixed = Indirect Only、resolution 20、Non-Directional、不压缩、Progressive GPU。细节以 `BakedLightLightingSettings.asset` 为准。

## 多光源说明

```text
visibleLights
  → Directional → _DirectionalLight* → Lit
  → Point / Spot → Other Lights（管线已接；本场 Mixed 灯主要为烘焙输入）
烘焙 lightmap → 需 Lit LIGHTMAP_ON + Meta（待补）
动态物体 → SampleLightProbe（已接）
```

- Contribute GI：静态物体占 lightmap（`SetContributeGI(..., true)`）。
- 动态球：`contributeGI=false`，走 Light Probe SH（`GI.hlsl`）。
- Mixed Directional：实时直射走本管线；静态间接需 `LIGHTMAP_ON`。

## 待接清单

| 项 | 用途 |
|----|------|
| Lit `#pragma multi_compile _ LIGHTMAP_ON` | 打开 lightmap 采样分支 |
| Meta Pass（`LightMode=Meta`） | 烘焙漫反射率 / emission |
| Lit Emission → Baked Emission（若缺） | 自发光进 bake |
| MeshBall `LightProbeUsage.CustomProvided`（若需） | 实例读 probes |

已有：`GI.hlsl`、`PerObjectData.Lightmaps|LightProbe|…`（GeometryPass）。缺 keyword / Meta 时勿声称静态 lightmap 已验收通过。

## 排障

| 现象 | 查 |
|------|-----|
| Point/Spot 几乎无感 | 本场 Mixed 灯强度/用途；Other max；是否被 Forward+ 剔除 |
| 像只有一盏平行光 | `lights.json` 两盏 Directional `intensity`（主 0.7 / 副 0.3） |
| Generate Lighting 后静态无间接 | Lit 缺 `LIGHTMAP_ON`；无 Meta |
| 绿地面无绿色间接 | 同上 |
| 动态球无探针贡献 | `SampleLightProbe`；物体非 Contribute GI；Lighting 已 Generate |
| Lit 全黑 | 无可见 Directional / 非 `CustomLit` / 非 Linear |

## 建议对照实验

1. 只留主 Directional → 开 Secondary 看叠光。  
2. 开关 Point：看 Other 光是否贡献（强度以 JSON 为准）。  
3. Generate Lighting → 静态 lightmap 仍可能不进 Lit（直到补 keyword / Meta）；动态球可验 Probe。  
4. 选中动态球看 Light Probe gizmo（Editor）。

## 相关场景

Shadowmask Mixed 变体（同布局、可烘 shadow mask 图、运行时仍未采样）见 [ShadowMasks 场景](shadow-masks.md)。
