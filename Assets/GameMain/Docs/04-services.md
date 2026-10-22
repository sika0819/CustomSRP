# 核心服务（天气 / 预设 / 氛围 / 世界 / 草地 / 移动端调优）

模块文档索引见 [00-overview.md](00-overview.md)。

## 职责与边界

`Services/` 下是**全局单例服务**，模式统一：`MonoBehaviour` + `Ensure()` 静态入口 + `DontDestroyOnLoad`。
服务只管状态和事件，**不画画**：视觉通过 `IAtmosphereVisualDriver` 推给 SRP。

| 服务 | 文件 | 职责 |
|---|---|---|
| `WeatherService` | [Services/WeatherService.cs](../Services/WeatherService.cs) | 时刻 / 天气 / 雾 / look 状态 |
| `PresetService` | [Services/PresetService.cs](../Services/PresetService.cs) | 预设列表与切换（虹膜过场状态机） |
| `AtmospherePropService` + `AtmospherePropRegistry` | [Services/AtmospherePropService.cs](../Services/AtmospherePropService.cs) | 昼夜 FX 道具、篝火天气门控 |
| `IslandWorldService` | [Services/IslandWorldService.cs](../Services/IslandWorldService.cs) | 地形 / 海面 / 内陆水 / MapItem / NavMesh / 草地装配 |
| `IslandGrassService` | [Services/IslandGrassService.cs](../Services/IslandGrassService.cs) | 草地 sidecar 载入 → 反射交给 SRP |
| `IslandMobileTune` | [Services/IslandMobileTune.cs](../Services/IslandMobileTune.cs) | 地形生成后的移动端灯光 / 云 / 地形 LOD 调优 |
| `NowPlayingService` | [Services/NowPlayingService.cs](../Services/NowPlayingService.cs) | 系统媒体卡片（负一屏）；顺带推后台传输计划 |
| `BackgroundTransportSync` | [Services/BackgroundTransportSync.cs](../Services/BackgroundTransportSync.cs) | Android 后台传输计划推送 / 回前台对账（详见 [05-audio](05-audio.md#后台传输计划android-负一屏)） |
| `GuestLoginClient` / `GuestSession` | [Services/GuestLoginClient.cs](../Services/GuestLoginClient.cs) | 游客登录 |
| `DevicePresetShotHelper` | [Services/DevicePresetShotHelper.cs](../Services/DevicePresetShotHelper.cs) | 真机截图桥（adb 写命令文件） |
| `StreamingAssetsUtil` | [Services/StreamingAssetsUtil.cs](../Services/StreamingAssetsUtil.cs) | Android 可用的 StreamingAssets 读取 |

## 关键 API

### WeatherService

```csharp
static WeatherService Ensure();

void ApplyLocalClockIfNeeded();                              // 墙上时钟 → 时期锚点（06/12/18/00）
void SetTimeOfDay(float hours, bool fireEvent = true);       // 立即设置，不插值
void SetPeriod(IslandTimePeriod period, bool animate);       // animate = Boluo IETimeLerp，2s SmoothStep
void BeginPanelTransitionLock();                             // 面板手势锁 2s
void ClearPanelTransitionLock();
void SetWeather(IslandWeatherKind kind, int level, bool fireEvent = true);
void SetFogDensity(float density);                           // 绝对 ExpSquared 密度
void SetLookKind(IslandLookKind kind);
void SetLightColorOverride(Color? color);
void PushVisuals();                                         // 世界就绪后重推
static float WeatherFogScale(IslandWeatherKind kind, int level);
static IslandTimePeriod ResolvePeriod(float hours);
static float PeriodToDialDegrees(IslandTimePeriod period);
```

天气类型：`IslandWeatherKind { Clear, Cloud, Rain, Foggy, Wind }`；时期：`IslandTimePeriod`；
look：`IslandLookKind`。

### PresetService

```csharp
static PresetService Ensure();
static int UiPresetCount = 10;               // Boluo schemeInfos[0..9]
IslandPresetAsset[] Presets { get; }
void ReloadFromResources();
void SetPresets(IslandPresetAsset[] presets);
int GetUiCount();
IslandPresetAsset GetById(int id);
IslandPresetAsset GetByIndex(int index);
void RequestTransition(int id);              // SchemeState=1，触发 PresetTransitionRequestEventArgs
void MarkPlaying();                          // SchemeState=2
void AbortTransition();                      // 中断时必须调用，否则卡在“切换中”
void Apply(int id, bool teleportPlayer = true);
void ApplyIndex(int index, bool teleportPlayer = true);
static string ResolvePresetMusicBank(IslandPresetAsset preset);
```

`Apply` 的执行顺序（跨服务，顺序敏感）：

```
1. GameEntry.Music?.ForceRetargetAtLanding(resolvedMusicBank)   // BGM 切到预设的 bank
2. AudioInteractionBootstrap.ClearContent3D()                    // 掐掉所有环境/天气/雨水音
3. CurrentId = id
4. Weather.SetLookKind / SetTimeOfDay / ClearPanelTransitionLock / SetWeather / SetFogDensity / SetLightColorOverride
5. 传送：SchemeState==1 → IslandJoystickCamera.ApplyPresetForReveal(preset)
         否则          → IslandJoystickCamera.ApplyPreset(preset)
6. SchemeState != 1 → AudioInteractionBootstrap.RetriggerContentAtListener()
7. 触发 PresetChangedEventArgs
```

`IslandPresetAsset` 字段：`Id, DisplayName, Cover, TargetPos, TargetRotateY, CameraOffset,
CameraToTargetDis, PlayerHeight, TimeHours, WeatherKind, WeatherLevel, LookKind, FogDensity,
LightColorOverride, BankNames`。

### AtmospherePropService / Registry

```csharp
static AtmospherePropService Ensure();
void Refresh();   // 依据 雨雾/大雨/昼夜 切 FX_Day / FX_Night / 篝火

// Registry（静态）
enum AtmospherePropKind { DayRoot, NightRoot, SunBeam, Butterflies, Fireflies, Campfire }
static void Register(AtmospherePropKind kind, Transform t);
static void RegisterByName(Transform t);   // 按名字推断：FX_Day / FX_Night / FX_SunBeam / FX_Fireflies / Huodui
static void Unregister(Transform t);
static void Clear();
static void SetActive(AtmospherePropKind kind, bool active);
```

### IslandWorldService

```csharp
static IslandWorldService Ensure();
static IslandWorldService Instance { get; }
static bool InlandWaterReady { get; }
static event Action<bool> OnInlandWaterReady;

IEnumerator Load(System.Action<float,string> reportProgress);
// 顺序：出生位姿 → 地形 → 海面 → IslandMobileTune.ApplyAfterTerrainSpawn
//      → 内陆水 → MapItem → NavMesh → 草地 → 触发 IslandWorldReadyEventArgs
void Release();
```

### IslandGrassService（反射桥）

```csharp
static IslandGrassService Ensure();
static IslandGrassService Active { get; }
IEnumerator Load();
void Release();

// 反射目标（Type.GetType("CustomSRP.OilTerrainGrassBootstrap, Assembly-CSharp")）
//   EnsureConfigured(byte[]) / TryLoadBake() / ApplyBudget(count, dist, lod)
```

### IslandMobileTune

```csharp
static void ApplyAfterTerrainSpawn(GameObject terrainInstance);
// DisableLaunchDirectionalLights / TuneLightsForMobileOil / TuneCloudsForMobile
// TuneTerrainForMobile / TuneTerrainShadowReceive
```

## 事件

见 [00-overview.md 第 5 节](00-overview.md#5-事件总表)。本模块相关：
`WeatherChangedEventArgs`、`TimeOfDayChangedEventArgs`、`IslandWorldReadyEventArgs`、
`PresetChangedEventArgs`、`PresetTransitionRequestEventArgs`、
`CampfireSuppressChangedEventArgs`、`CampfireVisualIgnitedChangedEventArgs`。

## 配置与资源依赖

| 资源 | 地址 |
|---|---|
| 地形 / 海面 / 内陆水 | `IslandTerrain` / `IslandOcean` / `IslandInlandWater` |
| NavMesh | `IslandNavMesh` |
| 出生位姿 | `IslandSpawnPose` |
| 草地 sidecar | `Island_GrassBake`（编辑器 `Assets/AssetRaw/Configs/Island_GrassBake.bytes`，也可在 `StreamingAssets/Island_GrassBake.bytes`） |
| MapItem | `MapItem` + `AllMapItem` + `MapItemExtras` |
| 预设 | `Assets/GameMain/Environment/IslandPresets`（采集组 `IslandPresets`）、封面 `Resources/PresetCovers/zhanweitu{1..10}.png` |

## 日志 tag

`[IslandWorld]` `[IslandNavMesh]` `[IslandGrass]` `[IslandSpawnPose]` `[IslandMobileTune]` `[DevicePresetShot]`

## 已知坑与排查手法

| 现象 | 原因 | 处理 |
|---|---|---|
| 天气改了但画面不变 | `WeatherService` 的 driver 缓存 2s，新挂载的 `OilAtmosphereDriver` 收不到推送 | 世界加载完成后 `WeatherService.PushVisuals()` 会补推；其它时机需手动调 |
| 真机已是「雨」仍无雨丝 | Runtime `Shader.Find` / Yoo 双份 Settings / 缺 `streakMaterial` | 见 [11-rain-device-visual.md](11-rain-device-visual.md)；须重打 Yoo + 真机包 |
| 真机完全没云 | `TuneCloudsForMobile`（`#if !UNITY_EDITOR`）Disable `OilCloudLayer` | **有意为之**，不是丢资源；与雨无关 |
| 预设切换后一直「切换中」 | `SchemeState` 卡在 1：虹膜流程没调 `MarkPlaying()` | 中断路径必须调 `AbortTransition()` |
| 切预设后环境音没了 | `Apply` 里 `ClearContent3D()` 掐音，只有 `SchemeState != 1` 才 `RetriggerContentAtListener()` | 检查状态机分支 |
| 切换预设雾浓度被覆盖 | 任何 `SetWeather` 都会重乘 `WeatherFogScale`，覆盖预设里的绝对 `FogDensity` | 先设 weather 再设 fog |
| 篝火一直不亮 | `CampfireWeatherGate.SuppressIgnite` 默认 **true**，靠 `AtmospherePropService.Refresh()` 放开 | 看天气等级（大雨会压制） |
| 草地不出现 | 反射的 SRP 类型名变了，`GetMethod` 返回 null | 日志 `[IslandGrass] bootstrap type missing` |
| 移动端阴影过重 | `TuneLightsForMobileOil` 只在 `#if !UNITY_EDITOR` 生效 | Editor 与真机表现不同是预期 |
| 地形材质内存涨 | `TuneTerrainShadowReceive` 实例化材质且不销毁 | 每次重进岛多一份实例 |
| 游客 id 每次都变 | `GuestSession.ApplyLocalEditorGuest` 用 `string.GetHashCode()`，不保证跨进程稳定 | 仅 Editor 兜底路径 |
| 天气插值中途读到的还是旧值 | `SetPeriod(animate:true)` 立刻触发事件，`Hours` 已是新值但画面在 lerp | 读 `Hours` 与看天空可能不同步 |
