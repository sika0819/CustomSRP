# 启动流程 / Procedure / 加载遮罩

模块文档索引见 [00-overview.md](00-overview.md)。

## 职责与边界

- `GameEntry` 是唯一入口 MonoBehaviour，负责：启动期静态复位、GF 组件初始化、自定义 Service 注册、UI Group 创建、移动端画质策略。
- `Procedure/*` 是 GameFramework 的流程状态机，负责**串行**完成：加载遮罩 → 资源系统 → 登录换票 → 进岛。
- `Boot/BootLoadingService` 是加载遮罩的静态门面，只驱动 `LoadingForm`，不碰渲染。
- `Procedure/SceneAssets.cs` 是 **YooAsset 运行时地址 + 编辑器资产路径** 的唯一真源（注意它在 `Procedure/` 目录下，但被 Runtime / Services / Editor 到处引用，namespace 是 `Game.SceneAssets`）。

## 关键文件

| 文件 | 行数 | 作用 |
|---|---|---|
| [GameEntry.cs](../GameEntry.cs) | 121 | Awake / Start / OnDestroy、画质策略、`EnsureUiGroups` |
| [GameEntry.Builtin.cs](../GameEntry.Builtin.cs) | 44 | 8 个 GF 内建组件静态属性 |
| [GameEntry.Custom.cs](../GameEntry.Custom.cs) | 61 | 5 个自定义 Service + `EnsureEntityGroups` |
| [Procedure/ProcedureLaunch.cs](../Procedure/ProcedureLaunch.cs) | 59 | 首帧打开 LoadingForm |
| [Procedure/ProcedureInitYooAsset.cs](../Procedure/ProcedureInitYooAsset.cs) | 136 | YooAsset 初始化 + 绑定 GF 资源管理器 |
| [Procedure/ProcedureGuestLogin.cs](../Procedure/ProcedureGuestLogin.cs) | 151 | 原生桥 init、读 boot.json、换票 |
| [Procedure/ProcedureIsland.cs](../Procedure/ProcedureIsland.cs) | 239 | 进岛状态机 |
| [Procedure/SceneAssets.cs](../Procedure/SceneAssets.cs) | 83 | 资源地址 / 路径常量 |
| [Boot/BootLoadingService.cs](../Boot/BootLoadingService.cs) | 266 | 加载遮罩门面 |

## 关键 API

### GameEntry

```csharp
// GameEntry.cs
[DefaultExecutionOrder(100)] public partial class GameEntry : MonoBehaviour
void Awake()      // 复位静态会话 → DeviceGraphicsPolicy.DetectAndApplyBoot()
void Start()      // InitBuiltinComponents → EditorResourceMode → InitCustomComponents → EnsureUiGroups
void OnDestroy()  // ResetSession → AudioInteractionBootstrap.Shutdown → WanosEngine.Unload → Clear*Components

// DeviceGraphicsPolicy.cs — Low/Mid/High/Gmj
// Detect：Gmj 渠道优先 → 桌面 High → ScoreAndroid / ScoreIos
// Boot：手机档 Very Low + DPI/lodBias + OIL_MOBILE_PERF（High 手机关 keyword）；桌面 High 跳过
// 明确不做 Screen.SetResolution
// 全链路文档：12-device-graphics-tiers.md
```

```csharp
// GameEntry.Custom.cs
static WeatherService Weather; static PresetService Presets; static AtmospherePropService AtmosphereProps;
static MusicPlaylist Music; static IslandWorldService World; static IslandGrassService Grass;

static void InitCustomComponents();  // Weather/Presets/AtmosphereProps/Music/World/Grass/DevicePresetShotHelper
static void InitWorldService();      // 热重载后重建 World/Grass（ProcedureIsland 会再调一次）
static void EnsureEntityGroups();    // Environment / FX
```

```csharp
// GameEntry.Builtin.cs
static BaseComponent Base; static ProcedureComponent Procedure; static EventComponent Event;
static ResourceComponent Resource; static UIComponent UI; static SceneComponent Scene;
static EntityComponent Entity; static SettingComponent Setting; static SoundComponent Sound;
```

### BootLoadingService

```csharp
public const float VisualPrepareMin = 0.70f;
public const float TerrainPrepareMax = 0.86f;
public const float CameraPrepareMax  = 0.93f;
public const float ItemsPrepareMax   = 1.0f;

static bool IsVisible { get; }
static bool HasForm   { get; }
static void ResetSession();
static void Bind(LoadingForm form);
static void Show(string tip = "正在进入世界...", float progress = 0.08f);
static void ReportProgress(float progress, string tip = null);          // 单调不回退
static void ReportPrepareProgress(float from, float to, int ready, int left, string tip);
static void FadeHide(float duration = 1f);
static void Hide();
```

### SceneAssets

```csharp
public const string IslandTerrainLocation = "IslandTerrain";
public const string IslandOceanLocation = "IslandOcean";
public const string IslandInlandWaterLocation = "IslandInlandWater";
public const string IslandNavMeshLocation = "IslandNavMesh";
public const string AllMapItemLocation = "AllMapItem";
public const string IslandSpawnPoseLocation = "IslandSpawnPose";
public const string IslandGrassBakeLocation = "Island_GrassBake";
public const string MapItemExtrasLocation = "MapItemExtras";
public const string MapItemLocation = "MapItem";
public const string CampfireNoiseFireLocation = "Campfire_NoiseFire";

static string DaoYu1Location(string parent, string prefabName);   // "parent_name"
static string DaoYu1EditorPath(string parent, string prefabName); // Assets/.../DaoYu1/parent/name.prefab
static string FxLocation(string prefabName);
static string FxEditorPath(string prefabName);
```

## 生命周期与调用链

### GameEntry.Awake 的复位顺序（热重载关键）

```
BootLoadingService.ResetSession()
YooResourceModule.ResetSession()      // 会 YooAssets.Destroy()
LocomotionInput.ClearAll()
NativeBridgeFacade.ResetSession()
BankUpgradeBootstrap.ResetSession()
BoluoApiEnvironment.ClearCached()
WanosSoundSetup.ResetSession()
MusicPlaylist.ResetSession()
AudioInteractionBootstrap.ResetSession()
DeviceGraphicsPolicy.DetectAndApplyBoot()
```

> `WanosSoundSetup.ConfigureHelpersBeforeSoundStart()` 必须在 `SoundComponent.Start` 之前执行，
> 它用反射改写 `SoundComponent` 私有的 `m_SoundHelperTypeName` / `m_SoundAgentHelperTypeName`。

### Procedure 链

```
ProcedureLaunch ──> ProcedureInitYooAsset ──> ProcedureGuestLogin ──> ProcedureIsland
```

流程类型名是**序列化进场景**的（`Editor/LaunchSceneSetup.cs` 写入 `GameFramework.prefab` 的
`m_AvailableProcedureTypeNames` / `m_EntranceProcedureTypeName`），不是代码里注册的。

### ProcedureIsland 内部状态机

`enum Phase { Services, World, Camera, Hud, Uncover, Done }`，由 `OnUpdate` → `TickPhase()` 推进。

| Phase | 做什么 | 进度 |
|---|---|---|
| Services | `EnsureUiGroups` / `EnsureEntityGroups` / `EnsureSoundGroups` / `InitWorldService` / `BoluoGameSave.Load` / `Weather.ApplyLocalClockIfNeeded` / `Music.ApplySavedVolumes` | 0.71 |
| World | `IslandWorldService.Load(report)` 协程 | 0.72 → 0.86 |
| Camera | 找/建 MainCamera、`IslandCameraSrpSetup.ApplyForIsland`、加 `IslandJoystickCamera`、`IslandPlayerPosSave.InitAndArm` | 0.93 |
| Hud | 打开 `IslandMainForm`，**10 s 超时** | 1.0 |
| Uncover | `UncoverAsync()`：开音频设备 → 重推音量 → 启动环境音 → 淡出遮罩 | — |
| Done | 每帧 `IslandPlayerPosSave.Tick()` | — |

### UncoverAsync 的音频顺序（不能改）

```
1. await BankUpgradeBootstrap.EnsureLocalBanksReadyAsync()   // Android jar 内 bank 先落盘
2. WanosEngine.EnablePlayback → StartEngine → Unmute          // 先开设备
3. GameEntry.Music?.ApplySavedVolumes()                       // 设备打开后必须重推一次 RTPC
4. AudioInteractionBootstrap.StartAfterUncover()              // 环境音 / 天气音 / 雨水面
5. BankUpgradeBootstrap.WaitCriticalThenCueAsync(45f)          // 关键 bank 就绪后起播 Music1_1
6. BootLoadingService.FadeHide(1f)
```

## 配置与资源依赖

- `Assets/GameMain/Scenes/Launch.unity`（唯一运行场景，Build Settings 第 0 位）
- `Assets/GameMain/UI/Forms/LoadingForm.prefab` / `IslandMainForm.prefab`
- `Assets/GameMain/Settings/Resources/{YooAssetSettings.asset, UpdateSetting.asset}`
- `StreamingAssets/Game/boot.json`（`packageName/version/playMode/yooFolder/allowGuestLogin`）

## 日志 tag

`[GameEntry]` `[InitYooAsset]` `[GuestLogin]` `[ProcedureIsland]` `[ProcedureGuestLogin]`

## 已知坑与排查手法

| 现象 | 原因 | 处理 |
|---|---|---|
| 卡在加载遮罩，Console 无日志 | `ProcedureIsland` 的 World 阶段发现 `YooResourceModule.IsInitialized == false`，直接 `break` 不推进阶段，每帧刷 `YooAsset not initialized — cannot load island world.` | 看 `[YooAsset]` 段，确认包初始化是否成功 |
| LoadingForm 8 s 没打开 | `[InitYooAsset] LoadingForm still missing after 8s` | 查 `GameEntry.UI.HasUIGroup("Loading")`；`EnsureUiGroups` 依赖 `UIComponent.Start` 先跑（GameEntry 延迟执行顺序 100 就是为此） |
| Editor 里一直用「加载失败」提示 | `Base.EditorResourceMode = true` 只在 `UNITY_EDITOR` 生效 | — |
| 第二次进游戏状态残留 | `Awake` 复位清单缺项 | 对照 [00-overview.md 第 4 节](00-overview.md#4-热重载复位清单disable-domain-reload) |
| 遮罩淡不掉 | `BootLoadingService._hiding` 卡 true | `Show()` 会主动清 `_hiding`；检查 `GameEntry.UI` 是否为空导致 `Hide()` 关不掉表单 |
| HUD 10 s 没出来 | `[ProcedureIsland] IslandMainForm timeout — uncover without HUD` | 查 `UiAssets.IslandMainForm` 资源地址与 `YooGF` 加载日志 |
| 遮罩进度条倒退 | `ReportProgress` 单调保护生效，会被**抬到**旧值而不是回退 | 检查是否有多处同时 `ReportProgress` |

> Editor 联调入口：菜单 **`GameMain/Boluo/编辑器登录`**（UITK EditorWindow，仅 Editor；**游客 / 手机** 二选一）。无已落盘会话时进 Play 自动弹出；游客与正式账号均复用 token，主动退出前无需重登。真机包不含此窗。环境/渠道见 `环境·渠道·版本`。
