# 机型画质分档（DeviceGraphicsPolicy）

> 真源：`Assets/GameMain/Services/DeviceGraphicsPolicy.cs`。  
> 本文汇总 **Detect → Profile → Boot → 下游消费** 全链路；CRP 移动端地板见 [mobile-perf.md](../../CustomSRP/doc/mobile-perf.md)；Gmj 站立卡点见 [gmj-perf.md](../../CustomSRP/doc/gmj-perf.md)。

## 1. 职责与边界

| 做 | 不做 |
|----|------|
| 启动期 Detect 四档：`Low` / `Mid` / `High` / `Gmj` | `Screen.SetResolution`（用 DPI × renderScale） |
| 静态 `DeviceGraphicsProfile` 表（无 SO） | 用户运行时手动切档 UI（当前无） |
| Boot：Quality / DPI / `OIL_MOBILE_PERF` / `_OilBrushMobileScale` | 替代 CRP 内 `isMobilePlatform` 地板（阴影 atlas 等仍按平台钳） |
| 供相机 scale、流送、岛面调参、SRP 专档分支读取 | 把桌面 Editor 默认当成手机 Mid |

程序集：`Game`。CustomSRP 可直接 `using Game` 读 `DeviceGraphicsPolicy.Current`（已有引用）。

## 2. 关键类型与入口

| 符号 | 文件 | 作用 |
|------|------|------|
| `DeviceGraphicsClass` | `Services/DeviceGraphicsPolicy.cs` | 四档枚举 |
| `DeviceGraphicsProfile` | 同上 | 单档只读数值包 |
| `DeviceGraphicsPolicy` | 同上 | Detect / Profile / Boot |
| `IslandGraphicsTier` | `Services/IslandGraphicsTier.cs` | `IsLowEnd` → 仅 `Low`（预设虹膜过场） |
| `IslandMobileTune` | `Services/IslandMobileTune.cs` | 进岛后灯 / 云 / 地形（看 Profile 开关） |
| `EnvStreamingConfig.ApplyGraphicsTierDefaults` | `Environment/Runtime/EnvStreamingConfig.cs` | 流送环与密度 |
| `IslandCameraSrpSetup` | `Entity/IslandCameraSrpSetup.cs` | Stand/Walk/Immersive renderScale |
| `BoluoDeviceInfo.IsSkyworthGmj` | `BoluoApi/BoluoDeviceInfo.cs` | Gmj 渠道判定 |

启动调用（`GameEntry.Awake`）：

```text
…静态会话复位…
DeviceGraphicsPolicy.DetectAndApplyBoot()
```

日志 tag：`[DeviceGraphicsPolicy]`、`[IslandMobileTune]`、`[IslandCamera]`。

## 3. Detect 决策树

```text
#if UNITY_EDITOR
  EditorPrefs "Boluo.EditorTest.GraphicsTier" 非空 → 强制该档
       High 且非 MobilePlatform → IsDesktopHigh=true
  否则若 IsSkyworthGmj → Gmj
  否则 → High + IsDesktopHigh=true
#else
  IsSkyworthGmj → Gmj
  非 MobilePlatform → High + IsDesktopHigh=true
  IPhonePlayer → ScoreIos(ram, gpu, deviceModel)
  其它移动 → ScoreAndroid(ram, gpu, gfxMb, pixels)
#endif
```

**Gmj 判定**（优先于机型评分）：

- `channel == BOLUOCWGMJ` **或** `originEnd == boluocwgmj`
- Editor 可用 `GameMain/Boluo/环境·渠道·版本` 覆盖渠道 / originEnd

**Editor 强制档**：`EditorPrefs` key = `Boluo.EditorTest.GraphicsTier`，值为枚举名（如 `Low` / `Mid` / `Gmj`）。清 key 即恢复默认桌面 High。

## 4. Profile 策略表（与代码同步）

全档 Stand / Walk / Immersive 为 **0.5 / 0.375 / 0.375**。桌面 High 也写同组 scale，但 **不 Override 相机**（Inherit CRP Asset）。

| 字段 | Desktop High | Low | Mid | Phone High | Gmj |
|------|--------------|-----|-----|------------|-----|
| `DpiScale` | 1 | 0.7 | 0.85 | 1 | 1 |
| `LodBias` | 1 | 1 | 1 | 1 | 1 |
| `OilMobilePerf`（`OIL_MOBILE_PERF`） | off | on | on | **off** | on |
| Stand / Walk / Immersive scale | 0.5 / 0.375 / 0.375 | 同左 | 同左 | 同左 | 同左 |
| `ShadowDistance`（Quality） | 40 | 26 | 32 | 40 | 40 |
| `DisableCloudHighRing` | false | true | true | true | true |
| `ApplyMobileQualityBoot` | false | true | true | true | true |
| `ApplyStreamingTune` | false | true | true | true | true |
| `ApplyMobileIslandTune` | false | true | true | true | true |
| Boot `_OilBrushMobileScale` | 1（keyword 关） | 0 | 0.5 | 1（keyword 关） | 0.5 |

`IsDesktopHigh == true` 时走 Desktop 列（忽略枚举名上的 High 手机语义）。

## 5. Boot 套用（`ApplyBoot`）

仅当 `ApplyMobileQualityBoot`：

1. `QualitySettings.SetQualityLevel("Very Low")`
2. `targetFrameRate=60`，`vSyncCount=0`
3. `resolutionScalingFixedDPIFactor = DpiScale`，`lodBias`，`maximumLODLevel=0`
4. 开/关全局 keyword `OIL_MOBILE_PERF`
5. `Shader.SetGlobalFloat("_OilBrushMobileScale", …)`（见上表）

桌面 High：关 keyword、brushScale=1，打日志后返回。

## 6. 评分细则

### 6.1 Android（偏保守）

输入：`systemMemorySize`、`graphicsDeviceName`、`graphicsMemorySize`、屏像素。

**RAM 带**

| 带 | 条件 |
|----|------|
| StrongLow | ≤3072 MB |
| Low | ≤4096 |
| Mid | &lt;6144 |
| MidPlus | &lt;8192 |
| HighEligible | ≥8192 |

**GPU 带（名称解析）**

| 带 | 典型 |
|----|------|
| High | Adreno ≥700；Mali-G ≥710 / G78 / G77；Xclipse / Immortalis |
| Mid | Adreno ≥600；Mali-G ≥57 / G76 / G72 / G71 |
| Low | 更低 Adreno/Mali-T/PowerVR/未知 |

**合成**

1. RAM HighEligible **且** GPU High → High；否则 RAM≥Mid 或 GPU≥Mid → Mid；否则 Low  
2. StrongLow 或 GPU Low：禁止 High（压到 Mid）；StrongLow 再压到 Low  
3. VRAM≤512 或 像素≥1440×3200：High→Mid；Mid 且 RAM/GPU 为 Low → Low  

### 6.2 iOS

| Apple 代数 | 档 |
|------------|-----|
| ≤A11 | Low |
| A12–A14 | Mid（RAM&lt;2048 则 Low） |
| A15+ / M 系列 | High |

代数来自 GPU 名 `Apple Axx`，或 `deviceModel`（`iPhone10,x`→A11 … `iPhone16+`→A17）；解析失败：RAM≥4096→Mid，否则 Low（**不授予 High**）。

## 7. 下游消费一览

### 7.1 相机（`IslandCameraSrpSetup`）

| | Desktop High | 手机 / Gmj |
|--|--------------|------------|
| `renderScaleMode` | Inherit（0） | Override（2） |
| 初始 scale | CRP Asset | `Profile.StandRenderScale` |
| 走路 / 沉浸 / 弹窗 | — | Walk / Immersive（弹窗=沉浸） |
| 其它 | `copyDepth=true`，`copyColor=false`，`farClip=750`，关 FXAA | 同 |

走路切换会重建中间 RT（可接受）。

### 7.2 流送（`EnvStreamingConfig.ApplyGraphicsTierDefaults`）

`ApplyStreamingTune==false`（桌面 High）→ 不改 Base。

| 档 | 方法 | 要点 |
|----|------|------|
| Low | `ApplyStreamingLow` | 环最紧；FillBound stride=6；spawn 8 / finalize 2.5 ms |
| Mid | `ApplyStreamingMid` | 树环放宽；stride=4；spawn 12 |
| Phone High | `ApplyStreamingNearBase` | 接近 Editor Base；spawn 16 / finalize 3 ms |
| Gmj | NearBase + `ApplyStreamingGmjDensity` | **环半径不变**；stride=6、MinDistance=140；`ShadowCastMaxDist=18` / Restore=14；spawn 10 / finalize 2.2 ms |

别名：`ApplyMobileTierDefaults()` → 同上。

### 7.3 岛面（`IslandMobileTune.ApplyAfterTerrainSpawn`）

始终：关掉非岛根下的 Directional（Launch 残留灯）。

`ApplyMobileIslandTune` 时：

- Quality 阴影 HardOnly、`shadowDistance`←Profile、分辨率 Low；平行光 Hard，其它灯无影  
- `DisableCloudHighRing`：反射关 `OilCloudLayer.highRing`  
- 地形：`heightmapPixelError` 钳 ≤18、关 detail、缩树距、关 terrain shadow cast  
- 地形材质实例：`_ShadowLift=0.62`、开 `_RECEIVE_SHADOWS`

### 7.4 UI / 过场（`IslandGraphicsTier`）

仅 `Current == Low`：`IslandMainForm` 预设虹膜 **跳过 exit walk**、缩短 black hold。

### 7.5 CustomSRP 专档 / 平台地板

| 主题 | 行为 | 代码 |
|------|------|------|
| 移动地板 | `isMobilePlatform`：阴影 ClonedForMobile（级联≤2、atlas≤1024、距≤25）、Other 影≤8、Bloom≤2、FXAA 关、Forward+ tile 128、ColorLUT≤24、Inherit 时 renderScale 地板 0.85 | `CameraRenderer` 等，见 mobile-perf |
| OilFilter | 手机走路关；站立 UltraLite；跟 PostFX Settings（无分机型/材质专档） | `OilFilter` / `OilFilterPass` |
| Canvas | Low：grain/weave ×0.75 | `PostFXPass` |
| Bloom | Gmj：`ClampForGmj`（≤1 阶） | `BloomPass` / `PostFXSettings` |
| 草 | Android/iOS：16k/70 m；**Gmj 10k/50 m**；bladeSegments=2 | `OilTerrainGrassBootstrap` |
| 平面反射 | **走路 / 任意位移每帧**（防抖）；Gmj **站立转头隔 2 帧**（相对上次镜子 ≥1.5° 强制）；`GmjMaxSize=64` | `OilPlanarReflection` |
| Shader | `OIL_MOBILE_PERF` + `_OilBrushMobileScale` | 各 Oil* HLSL |
| Lit BRDF 油画 | 仅 Lit/TerrainLit：色阶 + SpecCube posterize；Oil NPR **不含** BRDF | `BRDF` / `Lighting` / `OilBrdfCommon` |

## 8. 调用时序

```text
GameEntry.Awake
  └─ DetectAndApplyBoot          // Quality / keyword / brush

ProcedureIsland → IslandWorldService.Load
  └─ EnvStreamingConfig.ApplyGraphicsTierDefaults   // 进岛流送前
  └─ 地形生成
  └─ IslandMobileTune.ApplyAfterTerrainSpawn
  └─ IslandCameraSrpSetup.ApplyForIsland            // Override scale
  └─ 草 Bootstrap.ApplyDeviceBudget

运行时
  └─ IslandJoystickCamera 按站立/走/沉浸切 Profile scale
  └─ CRP Pass 读 isMobilePlatform + Current（Gmj/Low 分支）
```

## 9. 与性能文档的分工

| 文档 | 内容 |
|------|------|
| **本文** | 分档定义、Detect、Profile、谁读档 |
| [mobile-perf.md](../../CustomSRP/doc/mobile-perf.md) | CRP 移动端已落地钳制与收益 |
| [gmj-perf.md](../../CustomSRP/doc/gmj-perf.md) | Gmj 站立带宽卡点与专档钳制（不改 scale/流送半径/LOD 的边界） |

改 Profile 数字时：**先改 `DeviceGraphicsPolicy.GetProfile`，再改本文 §4**；下游若写死旧常量（文档或注释）一并改。

## 10. 排查速查

| 症状 | 先看 |
|------|------|
| Editor 像手机画质 | 是否设了 `Boluo.EditorTest.GraphicsTier`；渠道是否被盖成 Gmj |
| 真机档不对 | Console `[DeviceGraphicsPolicy] Detect=` + ram/gpu/pixels |
| Gmj 没进档 | channel/originEnd 是否 `BOLUOCWGMJ` / `boluocwgmj` |
| 桌面却跑了流送收紧 | `IsDesktopHigh` 应为 true；查 override |
| 阴影 Quality 40 但 CRP 25 | 正常：Quality 写 Profile，CRP 移动另钳 ≤25 |
| 预设过场抖 / 黑屏长 | Low 才跳 walk、短 hold；确认是否误判 Low |
