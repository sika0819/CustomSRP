# 创维闺蜜机（Gmj）帧率卡点分析

> 范围：`DeviceGraphicsClass.Gmj`（渠道 `BOLUOCWGMJ` / `originEnd=boluocwgmj`）站立态岛面。
> 约束（本次策略硬边界）：**不改 renderScale**、**不减流送半径**、**不减 LOD**（`lodBias` / `maximumLODLevel` / LODGroup 屏占比阈值保持现状）。
> 关联：分档全链路见 [12-device-graphics-tiers.md](../../GameMain/Docs/12-device-graphics-tiers.md)；CRP 移动地板见 [mobile-perf.md](mobile-perf.md)；流送见 `EnvStreamingConfig`；相机见 `IslandCameraSrpSetup`。

## 1. 结论（先看）

**有优化空间。** 闺蜜机帧率压力来自两层：

1. **稳态站立**：高物理分辨率 × Override scale + 全分辨率 Depth Copy + NearBase 密度（`OIL_MOBILE_PERF` 仍开；OilFilter 站立 UltraLite，跟 Settings）。
2. **冷启动首转**（「刚启动向右转特别卡」）：转头曾强制每帧平面镜 + 新视锥材质/阴影 PSO 首编 + Prefill 半径外后台 spawn 叠加。

Profile 站立/走/沉浸 **0.5 / 0.375 / 0.375**。三边界下的钳制见 §6。

## 2. 当前 Gmj 档位画像

| 项 | Gmj | 对比手机 Mid |
|----|-----|--------------|
| Detect | `BoluoDeviceInfo.IsSkyworthGmj()` 优先 | ScoreAndroid / ScoreIos |
| DPI | 1.0 | 0.85 |
| Stand / Walk / Immersive scale | **0.5 / 0.375 / 0.375** | 手机沉浸 0.25 |
| `OIL_MOBILE_PERF` | 开 | 开 |
| Quality `shadowDistance` | **40**（与手机 High 同） | 32 |
| CRP 实际阴影距离 | 移动端仍钳 **≤25 m**（`CameraRenderer`） | 同 |
| 流送 | NearBase 环 + `ApplyStreamingGmjDensity`（隔株/投射/spawn） | `ApplyStreamingMid`（更紧） |
| 云高环 | 关 | 关 |
| 相机 | `copyDepth=true`，`copyColor=false`，`farClip=750` | 同路径 |

入口：`DeviceGraphicsPolicy.GetProfile(Gmj)`、`IslandCameraSrpSetup`、`EnvStreamingConfig.ApplyStreamingNearBase`。

## 3. 已生效的移动端地板（Gmj 同样吃到）

Android 上 `Application.isMobilePlatform == true`，下列已在跑，**不是**「完全没优化」：

| 项 | 行为 | 代码 |
|----|------|------|
| 阴影 | `ClonedForMobile`：级联≤2、atlas≤1024；`maxDistance≤25` | `CameraRenderer` / `ShadowSettings` |
| Other 阴影 | 最多 8 盏 | `OtherShadows` |
| Bloom | ≤2 阶；走路 1 阶 | `BloomPass` |
| FXAA | 强制关 | `PostFXPass` |
| Forward+ tile | 128 px | `LightingPass` |
| ColorLUT | ≤24³ | `ColorLUTPass` |
| OilFilter | 站立 UltraLite（跟 Settings）；走路关 | `OilFilter` / `OilFilterPass` |
| 平面反射 | **走路 / 任意位移每帧**（防 screen-UV 整块抖）；Gmj **站立转头隔 2 帧**（相对上次镜子 ≥1.5° 强制补画）；`GmjMaxSize=64` | `OilPlanarReflection` |
| 草 | 手机约 16k/70 m；**Gmj 10k/50 m** | `OilTerrainGrassBootstrap` |
| Shader | `OIL_MOBILE_PERF`：少 tap、软雾、少 other light 循环等 | 各 Oil* Pass |

站立走 **Override**（`Profile.StandRenderScale`），不会触发 `Inherit` 时的 0.85 挪动；高物理分辨率下带宽仍紧。

## 4. 卡点假设（按预估影响排序）

### P0 — 冷启动首转（本轮主修）

**现象**：揭罩后立刻向右转特别卡；稳态站立相对好一些。

**根因组合**：

| 根因 | 说明 |
|------|------|
| 转头强制每帧 mirror | 旧规则 `turn > 0.05° → 永不节流`；4K 站立再跑一整次 Cull+Opaque+Skybox |
| 无 Shader/PSO 预热 | Prefill 按距离、不按朝向；右侧扇区首次进主相机/镜子视锥才 CreateGPUProgram |
| 后台 spawn 叠加 | Prefill 半径外 `StreamLoop` 仍按预算补装，与首转同帧抢 CPU |

**已落地**（§6）：转头隔 2 帧（+ stale≥1.5° 强制）、揭罩前 yaw 扫预热、Prefill 右前优先、揭罩后 4 s 压 spawn。

### P0b — 4K 站立填充 + Depth 带宽

**现象假设**：稳态 GPU 时间主要花在 Opaque/Transparent 每像素 × 缓冲面积，以及 Depth blit；站立 OilFilter UltraLite 半分辨率 QuadPick 另占带宽。

**不改三件硬边界的策略**：

| 策略 | 预期收益 | 风险 / 备注 |
|------|----------|-------------|
| 站立关 CopyDepth（若岸沫可接受退化） | 高：省一次全分辨率 depth store/blit | 岸沫质量下降；需美术验收 |
| CopyDepth 半分辨率（岸沫 shader 适配） | 中高 | 改 HLSL 采样与软粒子假设 |

### P1 — 阴影投射体过多（半径不动）

| 策略 | 预期收益 | 备注 |
|------|----------|------|
| Gmj `ShadowCastMaxDist`→18（Restore 14） | 已落地 | `ApplyStreamingGmjDensity` |
| FillBound stride 6、`MinDistance=140` | 已落地 | 半径不变，只隔株 |
| Gmj 级联 2→1 或 atlas 1024→512 | 中 | 近影锯齿；可只站立开 |

### P2 — 透明水面 / 天空 / 草

| 策略 | 预期收益 | 备注 |
|------|----------|------|
| 草 10k / 50 m | 已落地 | `OilTerrainGrassBootstrap` |
| 站立关平面镜只留解析天空反射 | 低～中 | 产品可接受时再开 |
| 地形 `heightmapPixelError` 略抬 | 低～中 | **不是** LODGroup |

### P3 — CPU：流送与合批

| 策略 | 预期收益 | 备注 |
|------|----------|------|
| spawn 10 / finalize 2.2 + 揭罩后 4 s 再压 | 已落地 | `BeginPostUncoverQuiet` |
| `EnvMeshColliderLod` 刷新 | 抑走动毛刺 | 右转基本无关 |

## 5. 明确不做（本次边界）

| 不做 | 原因 |
|------|------|
| 降低 stand/walk/immersive `renderScale` | 用户硬约束；清晰度是 Gmj 产品诉求 |
| 收紧 Ring0/1、TreeRing、KeepRadius 等流送半径 | 用户硬约束；会空林 / 穿帮 |
| 降 `lodBias`、抬 `maximumLODLevel`、改 LODGroup 屏占比 | 用户硬约束 |
| 把 Gmj 误当成手机 Mid 再压一档 scale | 等同改 renderScale |

## 6. 已落地钳制（仍守三边界）

| 项 | 行为 | 代码 |
|----|------|------|
| OilFilter | 站立 UltraLite（跟 Settings，无专档钳参）；走路关；soft `_OilBrushMobileScale=0.5` | `OilFilter` / `OilFilterPass` / `DeviceGraphicsPolicy` |
| CopyDepth | **保留**（岸沫） | `IslandCameraSrpSetup` |
| Bloom | 资产默认已关；Gmj 防御 `maxIterations≤1` | `BloomSettings.ClampForGmj` / `BloomPass` |
| 阴影投射 | `ShadowCastMaxDist=18` / `Restore=14` | `ApplyStreamingGmjDensity` |
| FillBound | stride=6、`MinDistance=140`（环半径仍 NearBase） | 同上 |
| spawn | 10 / 8 / finalize 2.2 ms；**揭罩后 4 s** 再压到约 35% / finalize 50% | `BeginPostUncoverQuiet` |
| 草 | `ApplyBudget(10000, 50f, bladeSegments: 2)` | `OilTerrainGrassBootstrap` |
| 平面反射 | **走路 / 位移每帧**；Gmj **站立转头隔 2 帧** + stale≥1.5° 强制；`GmjMaxSize=64` | `OilPlanarReflection` |
| OilNPR 硬影 | `OIL_NPR_HARD_DIRECTIONAL_SHADOW` → **1-tap** atlas（不对齐地形 5-tap） | `Shadows.hlsl` |
| 海面 mobile | 岸缘 depth 单采样；bump **单层**（桌面双层 + paint relief） | `OilOceanNPRPass.hlsl` |
| OilWarp | 仅沉浸 weight>0；站立/走 strength=0 → Pass 跳过、Oil→Final merge 保留 | `OilWarpDriver` / `OilWarpPass` / `PostFXPass` |
| ShoreWet | 已移除运行时扫水；岸边石用 `ShiTou_Shore` 材质 `_Wetness` | `EnvRockShoreWet` |
| Prefill 扇区 | 出生 yaw 前/右扇区略提前铺装（半径不变） | `EnvItemStreamerLite.PrefillAroundFocus` |
| 揭罩前预热 | 加载罩下扫 yaw ±85°，经 `ForceMirrorRedraw` 强制镜子重画 | `IslandStartupWarmup` / `ProcedureIsland.UncoverAsync` |

未做（后续可选）：半分辨率 CopyDepth、地形 `pixelError` 上抬、站立关平面镜。

验收：站立目标帧 P99；**未改路径成本 ≤ 线上**（硬影 1-tap、海面 mobile 单 bump/单 depth、非沉浸无 Warp）；**揭罩后立刻右转无长卡**；岸沫 / 水边 Z / 近树阴影可接受；转头时河面倒影滞后 ≤约 1.5°；走与沉浸不回归（走路镜可每帧、沉浸 Warp 可贵）。

## 7. 关键符号速查

| 主题 | 符号 |
|------|------|
| 分档 | `DeviceGraphicsPolicy` / `DeviceGraphicsClass.Gmj` |
| 相机 scale / depth | `IslandCameraSrpSetup` |
| 阴影钳制 | `CameraRenderer` + `ShadowSettings.ClonedForMobile` |
| OilFilter | `OilFilter.IsEnabled` |
| 流送 NearBase | `EnvStreamingConfig.ApplyStreamingNearBase` |
| Gmj 密度 | `EnvStreamingConfig.ApplyStreamingGmjDensity` |
| 投射距离 | `EnvStreamingConfig.ShadowCastMaxDist` |
| 草预算 | `OilTerrainGrassBootstrap.ApplyDeviceBudget` |
| 揭罩预热 | `IslandStartupWarmup` / `IslandJoystickCamera.PeekYaw` |
| 揭罩后 spawn 静默 | `EnvStreamingConfig.BeginPostUncoverQuiet` |
| 强制镜子重画 | `IslandLocomotionState.ForceMirrorRedraw` |
| 岛面灯/云/地形 | `IslandMobileTune` |

## 8. 与其它文档的关系

| 文档 | 关系 |
|------|------|
| [12-device-graphics-tiers.md](../../GameMain/Docs/12-device-graphics-tiers.md) | 分档 Detect / Profile 真源；Gmj 画像以该文 §4 / §7 为准 |
| [mobile-perf.md](mobile-perf.md) | 全移动端 CRP 地板与收益表 |
| 本文 | **Gmj 站立带宽 + 冷启动首转**卡点与 §6 专档钳制（不碰 renderScale / 流送半径 / LOD） |
