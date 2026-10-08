# Baked Light

Contribute GI、Light Probe、Mixed 灯、自发光。布局 / 灯参以**当前工程**场景数据为准，勿写死后合计。

接线状态见主 Skill §6（先 grep：Meta / `LIGHTMAP_ON` / Light Probe / SpecCube）。

## 怎么用

1. 确认 Pipeline Asset；用当前场景或项目 Baked Light 验证内容（有则用）。
2. （可选）Lighting → Generate Lighting。Bake 产物在 **lightmap 未采样** 前不会走 Lit 的 lightmap 路径。
3. 改布局：编辑场景 / 生成数据后重建；Bias / intensity 以数据源字段为准，勿从 Skill 臆造。

| 概念 | 要点 |
|------|------|
| Contribute GI | 静态进 lightmap（需 Meta + `LIGHTMAP_ON` 采样才影响画面） |
| Light Probe | 动态球谐间接；需 `PerObjectData.LightProbe` + SH 采样 |
| Mixed 灯 | 烘焙间接 / mask；实时部分仍走可见光过滤 |
| Point/Spot | 是否进实时路径以 Lighting 过滤逻辑为准（见 [point-and-spot-lights.md](point-and-spot-lights.md)） |

## Agent 规范

- 回答前 grep：Meta / `LIGHTMAP_ON` / Light Probe / SpecCube。
- **禁止**把「查 Meta / lightmap」写成现成排障，除非源码已有；也禁止假装 lightmap 已接线。
- 「多光源没生效」：先分清 Directional 上限 vs Other Lights 是否上传 vs GI 未采样。
- Mixed = Indirect Only 常见于本主题；Shadowmask 变体 → [shadow-masks.md](shadow-masks.md)。
- 不串 DrawCalls / BRDF 网格 / CSM 专项验收。
- `PerObjectData.Lightmaps` 已设 ≠ lightmap 已采样；以 Shader multi_compile / 采样函数为准。

### 常见待接符号（未命中则勿当已有）

| 项 | 用途 |
|----|------|
| Lit `#pragma multi_compile _ LIGHTMAP_ON` | 启用 lightmap 分支 |
| Meta Pass（`LightMode=Meta`） | 烘焙漫反射率 / emission |
| `unity_LightmapST` 等 PerDraw | SRP Batcher 兼容 |
| 实例探针数组拷贝 | `DrawMeshInstanced` 读 probes（若需要） |

## 技巧 / 排障

| 现象 | 查 |
|------|-----|
| Point/Spot 看不见 | Lighting 是否跳过非 Directional；lightmap 是否采样 |
| 像只有一盏平行光 | 各 Directional intensity；类型过滤 |
| Generate Lighting 后无 lightmap 间接 | 无 `LIGHTMAP_ON` 采样路径 |
| 静态色渗不到动态物体 | Probe SH 是否接入；静态→动态靠 probe，不是靠未采样 lightmap |
| Lit 全黑 | 无可见 Directional / 错 LightMode / 非 Linear |

对照：只留主 Directional → 开副光叠色 → 开关 Point（看实时路径是否接）→ Bake 后确认 lightmap 是否进 Lit → 选中动态物体看 Probe gizmo。

## 相关核对

```bash
rg -n "LIGHTMAP_ON|LightMode.*Meta|SampleLightProbe|SampleLightMap|SampleLightmap"
rg -n "PerObjectData\.LightProbe|PerObjectData\.Lightmaps"
rg -n "LightType\.Directional|SetupPointLight|SetupSpotLight|continue"
```

`GI.hlsl` 里有 `LIGHTMAP_ON` 分支 ≠ Lit 已 `#pragma multi_compile`。Probe SH 与 lightmap 是两条路径。

## 验收

- [ ] 布局 / 灯参与场景数据一致
- [ ] 未把 Bake 成功说成 lightmap 已进 Lit（除非 grep 命中采样）
- [ ] Light Probe / SpecCube 口径与 grep 一致
- [ ] 未把 Shadowmask 场景需求塞进本场
