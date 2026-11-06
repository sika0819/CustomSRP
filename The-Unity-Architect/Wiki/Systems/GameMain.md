# 系统 — GameMain

**路径：** `Assets/GameMain/` · **程序集：** `Game` / `Game.Editor` / 测试  
**命名空间：** `Game`（硬规则）  
**详解：** [`Assets/GameMain/Docs/00-overview.md`](../../../Assets/GameMain/Docs/00-overview.md) · skill `game-framework` · rule `gamemain.mdc`

## 职责

全部玩法、UI、流程、服务、原生/Boluo API、存档、岛屿世界编排。渲染留在 CustomSRP；音频引擎留在 WanosAudio。

## 入口

- 场景：`Assets/GameMain/Scenes/Launch.unity`
- `partial class GameEntry` → `GameEntry.cs` + `Builtin` + `Custom`
- `[DefaultExecutionOrder(100)]` —— 晚于 UIComponent / ProcedureComponent 接线

## 模块地图（缩略）

| 区域 | 文档 |
|------|------|
| Boot / Procedure | `01-boot-procedure.md` |
| YooAsset | `02-yooasset.md` |
| 存档 | `03-persistence.md` |
| Services | `04-services.md` |
| 音频编排 | `05-audio.md` |
| UI | `06-ui.md` |
| 环境 | `07-environment.md` |
| 输入 / 相机 | `08-input-camera.md` |
| BoluoApi / Native | `09-boluoapi-native.md` |
| 会话生命周期 | `12-session-lifecycle.md` |
| Bank 下载 | `13-bank-download.md` |

## 热重载硬约束

开了 Disable Domain Reload。所有跨会话静态状态必须在 `GameEntry.Awake` 复位（BootLoading、YooResource、LocomotionInput、NativeBridge、BankUpgrade、BoluoApiEnvironment、WanosSoundSetup、MusicPlaylist、AudioInteractionBootstrap ……）。**新增静态单例 → 同步补这条清单。**

## 事件风格

GF：`*EventArgs : GameEventArgs`，ReferencePool + `GameEntry.Event.Fire`。另有少量普通 C# 事件（`OnInlandWaterReady`、目录就绪、Bank 已加载等）。
