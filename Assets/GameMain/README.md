# GameMain

游戏侧程序集,独立 `namespace Game`、独立 `Game.asmdef`,与 `CustomSRP`(渲染管线)解耦。
依赖:`UnityGameFramework.Runtime`、`YooAsset`、`UniTask`、`Newtonsoft.Json`(见 `Game.asmdef`)。

框架使用约定见 [`.cursor/skills/game-framework/SKILL.md`](../../.cursor/skills/game-framework/SKILL.md);
本文件只描述本工程 GameMain **目录组织**与**启动接线**。

## 1. 入口

`Assets/GameMain/Scenes/Launch.unity` 是唯一运行场景,`ProjectSettings/EditorBuildSettings.asset` 第 0 位。
GameEntry 挂在 `Launch.unity` 内(由 `Editor/LaunchSceneSetup.cs` 部署),`[DefaultExecutionOrder(100)]`,晚于 `UIComponent.Start`(这样 `AddUIGroup` 才有 `InstanceRoot`),也晚于 `ProcedureComponent` 的 `EndOfFrame` 才进入首条 Procedure。

## 2. 目录约定

| 目录 | 放什么 |
|------|--------|
| `GameEntry.cs` / `GameEntry.Builtin.cs` / `GameEntry.Custom.cs` | 三个 `partial class GameEntry : MonoBehaviour`;**不要**新增 `GameEntry.****.cs` 之外的入口类 |
| `Boot/` | 启动期一次性的服务(BootLoadingService)。Awake 复位 |
| `Procedure/` | `ProcedureBase` 派生:`ProcedureLaunch` → `ProcedureInitYooAsset` → `ProcedureGuestLogin` → `ProcedureIsland` |
| `Entity/` | 实体 + 实体类辅助(`IslandJoystickCamera` 等)。`MonoBehaviour` 派生或 `static` 工具类 |
| `Environment/Runtime/` | 环境运行时(`EnvMapItemSpawner`/`EnvItemStreamerLite`/`FireController`/`CampfireWeatherGate` …)。美术资产(Models/Materials/Prefabs)与脚本分层 |
| `Input/` | 玩家输入(`LocomotionInput`/`IslandVirtualJoystick`/`IslandLocomotionInput`/`HudOverlayGate`)。热重载时 `LocomotionInput.ClearAll()` |
| `UI/` | 界面:`GameFormLogic : UIFormLogic` 基类 + `ScreenLayout` + `UiAssets` + `UiPreview` + `YooUIFormHelper`。运行时 Form 仅 7 个:`LoadingForm` / `IslandMainForm` / `WeatherForm` / `MusicForm` / `VolumeForm` / `PresetForm` / `TimerPauseForm`(下载类弹窗已移除) |
| `Services/` | 全局单例服务:`WeatherService` / `PresetService` / `AtmospherePropService` / `MusicService` / `IslandWorldService` / `GuestLoginClient` / `GuestSession`(薄包装 `BoluoSession`);以及 *EventArgs 事件类、`IAtmosphereVisualDriver` 接口、`StreamingAssetsUtil` |
| `Native/` | IMP 原生桥:`NativeBridgeTransport` / `NativeBridgeFacade` / `NativeSession` / Push / MsgType / SignKit 桥 / Overlay / Timer |
| `BoluoApi/` | 环境切换、device/user 换票、`upgrade/bank`、BankDownloader(落盘不灌 Wwise)、SignatureLib |
| `Utility/` | `Http.Retry` HTTP 重试 |
| `YooAsset/` | YooAsset 封装:`YooResourceModule`(会话复位)、`YooAssetResourceManager`(实现 GF `IResourceManager`)、`YooRemoteServices`、`YooAssetPlayModePrefs`、`UpdateSetting` |
| `Runtime/` | 极少:`ForceIncludeEngineTypes` 防止被 IL2CPP 裁掉的反射类型 |
| `Editor/` | 编辑器工具(`Game.Editor.asmdef`):`BoluoEnvTestWindow`(环境·渠道·版本)、GuestLogin 菜单、构建、YooAsset Collector 等。**运行时不参与** |
| `Scenes/` | `Launch.unity` / `UiPreview.unity` |
| `Settings/Resources/` | YooAsset 配置资产(`UpdateSetting.asset` / `YooAssetSettings.asset`)—— YooAsset 在运行时通过 `Resources` 模式读 |
| `Resources/`, `Scenes/`(除 Launch/UiPreview 外) | **空目录占位,不要塞东西**;运行时资源全部走 YooAsset 异步 |

## 3. 启动顺序(伪代码)

```
Awake()                           // DefaultExecutionOrder=100
  BootLoadingService.ResetSession()
  YooResourceModule.ResetSession()
  LocomotionInput.ClearAll()
  NativeBridgeFacade.ResetSession()
  BankUpgradeBootstrap.ResetSession()
  BoluoApiEnvironment.ClearCached()
  ApplyDeviceGraphicsPolicy()     // 非 Editor:Quality 2, res 0.7×, OIL_MOBILE_PERF

Start()
  InitBuiltinComponents()         // Base/Procedure/Event/Resource/UI/Scene/Entity/Setting
  if (Editor) Base.EditorResourceMode = true
  Base.FrameRate = 60
  InitCustomComponents()          // Weather/Presets/AtmosphereProps/Music/World + EnsureEntityGroups
  EnsureUiGroups()                // Loading/Main/Panel/Dialog
  if (!Editor) Screen.SetResolution(1280, 720, FullScreenWindow)

OnDestroy()
  → ClearCustomComponents → ClearBuiltinComponents
```

**热重载重要**:`Awake` 顶部 Reset **不能少**。项目开了 Disable Domain Reload,`RuntimeInitializeOnLoadMethod` 不会重跑,只有 `Awake` 每次都跑。

## 4. 命名 / 接线约束(硬规则,也是 `.cursor/rules/gamemain.mdc`)

- 所有 .cs 在 `namespace Game`;asmdef `rootNamespace=Game` 强制。
- 严禁放进 `namespace CustomSRP`(那是 SRP 渲染管线的命名空间)。
- UI 只用 UI Toolkit(`UnityEngine.UIElements`、`UIDocument`、`UXML`、`USS`、`VisualElement`)。
  - 严禁 `UnityEngine.UI` / `Canvas` / `GraphicRaycaster` / `OnGUI`。
  - 表单基类 `GameFormLogic : UIFormLogic`,只读 `Root`(已 `protected`)。
  - 深度走 `UIDocument.sortingOrder`,**不要**改 `PanelSettings.sortingOrder`。
- 资源全部 `ResourceComponent.LoadAsset(...)` 异步,Editor 模式 `Base.EditorResourceMode = true`。
  - **不要**用 `Resources.Load` 直接同步加载游戏资产(只允许 `YooAsset` 启动读 `Settings/Resources` 的 YooAsset 配置本身)。
- 事件 `*EventArgs : GameEventArgs`,`ReferencePool.Acquire` 后 `Fire`/`FireNow`,handler 返回后 `Release`;调用方不要 `Release`。
- Entity Group 当前只有 `Environment` / `FX`;新增 Group 在 `GameEntry.EnsureEntityGroups` 加。
- UI Group 当前 `Loading`/`Main`/`Panel`/`Dialog`,深度 0-3;新增在 `EnsureUiGroups` 加。
- 移动端 quality 等级 = 2(`Medium`),`OIL_MOBILE_PERF` keyword 必须开;非 Editor `Screen.SetResolution(1280,720)`。
- Unity 6 对象 ID 用 `GetEntityId()` / `EntityId`,**禁止** `GetInstanceID()`(详见 `.cursor/rules/unity6-entity-id.mdc`)。

## 5. Procedure 链

```
ProcedureLaunch            (Launch.unity 已加载,准备 YooAsset 会话)
  └─→ ProcedureInitYooAsset  (初始化 YooAsset + 读 BootManifest)
        └─→ ProcedureGuestLogin  (NativeBridgeFacade.Init → device/user 换票 → BankUpgradeBootstrap 后台 → 进岛)
              └─→ ProcedureIsland   (进岛; IslandWorldReady → Notify IslandReady)
```

Editor 联调:`GameMain/Boluo/环境·渠道·版本`;换票需开启 `Editor Guest Login/Try Remote TouristLogin`。

新增 Procedure:派生 `ProcedureBase`,在父 `OnUpdate` / 异步回调里 `ChangeState<T>(procedureOwner)`。

## 6. 路由表

| 想做什么 | 看哪 |
|----------|------|
| 加 UIForm | 派生 `Game.UI.GameFormLogic`,在 `UI/UiAssets.cs` 加常量;参考 `IslandBreathHint` |
| 加实体(树木/篝火/FX) | 派生 `EntityLogic`,在 `Entity/IslandSpawnPose` 注册,Group `Environment` 或 `FX` |
| 加环境运行时(地形 / 海面 / 河流) | `Environment/Runtime/`,参考 `EnvMapItemSpawner` |
| 加 Service | 参照 `MusicService`:`MonoBehaviour` + `Ensure()` 单例 + 在 `GameEntry.Custom.cs` 注册 + 事件用 `*EventArgs` |
| 加 YooAsset 资源 | `YooAsset/YooAssetCollectorSetup.cs` 走 `AddressByFolderAndFileName`,运行时 `ResourceComponent.LoadAsset` |
| Editor 工具 | `Editor/`(独立 `Game.Editor.asmdef`),菜单加进现有 `GameBuildWindow` |
| 改框架用法 | [`.cursor/skills/game-framework/SKILL.md`](../../.cursor/skills/game-framework/SKILL.md) + `references/` |
| 改渲染 | 别动这里,改 `CustomSRP/` |
