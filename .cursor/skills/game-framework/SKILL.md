---
name: game-framework
description: >-
  在本 Unity 工程中用 EllanJiang GameFramework 与 UnityGameFramework
  （com.jiangyin.gameframework）写游戏逻辑：GameEntry、Procedure、Entity、
  Event、DataTable、Resource。界面只用 UI Toolkit（UIDocument、UXML、USS、
  VisualElement）。
  当用户提到 GameFramework、UnityGameFramework、GameEntry、Procedure、流程、
  Entity、实体、UIForm、界面、UI Toolkit、UIDocument、DataTable、数据表、
  事件、对象池、资源加载，或要在 Custom SRP 工程里加游戏模块时使用。
---

# Game Framework（本项目）

用 [GameFramework](https://github.com/EllanJiang/GameFramework) 核心库和 [UnityGameFramework](https://github.com/EllanJiang/UnityGameFramework) 写游戏逻辑。渲染仍由本工程 Custom SRP 负责。

**加载方式**：本文件是唯一入口。主题细节在 `references/`，按路由表按需 Read。

## 1. 两层仓库

| 层 | 仓库 / 包 | 内容 |
|----|-----------|------|
| 核心 | `EllanJiang/GameFramework` | 纯 C#，无 `UnityEngine`。19 个模块目录 + `Utility` |
| Unity | `com.jiangyin.gameframework`（`EllanJiang/UnityGameFramework`） | `UnityGameFramework.Runtime` 组件、`GameFramework` 预制体。核心以 `Libraries/GameFramework.dll` 随包提供 |

包的 `package.json`：`unity` 为 `2017.1`，`version` 为 `2021.05.31`。日常开发不单独克隆核心库。

`UnityGameFramework.Runtime.GameEntry` 是**静态类**，只有 `GetComponent<T>()` 和 `Shutdown`。游戏入口是另一个命名空间里的 `partial class GameEntry : MonoBehaviour`，在 `InitBuiltinComponents` 里转调静态 `GetComponent`。

## 2. 三处代码

| 位置 | 放什么 |
|------|--------|
| `Libraries/GameFramework.dll` | 核心实现。不把源码摊进 `Assets/CustomSRP` |
| `com.jiangyin.gameframework` | 框架组件与默认 Helper。玩法不改这里 |
| 游戏目录（建议 `Assets/GameMain`，独立程序集） | `ProcedureBase`、`EntityLogic`、`UIFormLogic`、事件、数据行、游戏侧 `GameEntry` |

游戏代码使用独立命名空间，不放进 `namespace CustomSRP`，也不放进 `Assets/CustomSRP`。

## 3. 未接入时

本仓库默认**没有**框架。回答或写代码前先 grep `UnityGameFramework.Runtime.GameEntry`。

未命中时：说明尚未接入，不要写 `GameEntry.Entity` / `OpenUIForm` 等会编译失败的调用，也不要假装预制体或流程已经存在。接入步骤只在 [references/integrate.md](references/integrate.md)，等用户明确要求再做。

## 4. 扩展点

玩法只从这些类型往外长：

| 需求 | 派生 |
|------|------|
| 游戏生命周期 | `GameFramework.Procedure.ProcedureBase` |
| 动态物体 | `UnityGameFramework.Runtime.EntityLogic` |
| 界面 | `UnityGameFramework.Runtime.UIFormLogic` |
| 事件 | `GameEventArgs` + `ReferencePool.Acquire` |
| 表行 | `DataRowBase`（`IDataRow`） |
| 网络包 | `Packet` |
| 项目组件快捷访问 | 游戏侧 `GameEntry` partial |

流程回调：`OnInit` / `OnEnter` / `OnUpdate` / `OnLeave` / `OnDestroy`。切换用 `ChangeState<T>(procedureOwner)`。

## 5. 界面（UI Toolkit）

框架 UI 模块只负责加载、开关、分组和深度通知。`DefaultUIFormHelper.InstantiateUIForm` 只做 `Instantiate`。`DefaultUIGroupHelper.SetDepth` 为空。官方文档里的 uGUI / NGUI 是可选视觉层，**本工程不用**。

游戏界面用 UI Toolkit：`UIDocument`、UXML/USS，或代码构建的 `VisualElement`。调试窗是 `DebuggerComponent` 上的同一套 UI Toolkit 界面。

- 表单预制体：`UIDocument` + `UIFormLogic` 子类。逻辑只查 `rootVisualElement`。
- 点击用 `RegisterCallback<ClickEvent>`，在 `OnClose` 里解除。
- 深度写在**该界面自己的** `UIDocument.sortingOrder`（`OnDepthChanged`）。多个界面不要改同一份 `PanelSettings.sortingOrder`。
- 禁止 `UnityEngine.UI`、Canvas、`GraphicRaycaster`、NGUI，以及用 `OnGUI` 做游戏界面或调试窗。
- 游戏界面不要写进 `DebuggerComponent`。

## 6. 与 Custom SRP

| 系统 | 归属 |
|------|------|
| 剔除、Pass、阴影、后处理 | `CustomRenderPipeline` / `CameraRenderer`（`namespace CustomSRP`） |
| 实体、场景物体、天空与地形的材质 | 本管线 Shader。不引入 URP / HDRP，不用框架替换管线 |
| 调试窗 | `DebuggerComponent`（UI Toolkit）。场景里有该组件才显示 |
| 声音 | `SoundComponent`（`AudioSource`），与 SRP 无关 |

实体和界面都是普通 GameObject，由当前管线绘制。

## 7. Agent 硬约束

- 资源只走异步 `ResourceComponent.LoadAsset`。编辑器日常保持 `BaseComponent.EditorResourceMode`（非编辑器会被框架强制关掉）。不主动改成可更新资源 / AssetBundle 模式。
- 事件：`ReferencePool.Acquire` 后 `Event.Fire`（或 `FireNow`）。事件池在处理完后 `Release`。调用方不要 `Release`，处理函数返回后不要再持有参数。
- 数据表：`DataTableComponent.CreateDataTable<T>()` 再 `ReadData`。没有 `DataTableComponent.LoadDataTable`。
- Config 是只读全局配置；Setting 是玩家键值。二者不混用。
- Unity 6（本工程 `6000.6.4f1`）上该 2021 包可能需要最小 API 修补：只修编译，不改模块行为。DLL 无法加载时才从核心库重编。
- 改绘制、Shader、阴影、后处理时继续遵循 `custom-srp` skill，不要把渲染逻辑搬进 Procedure。

## 8. 路由表

| 用户提到 | 读 |
|----------|-----|
| 19 个模块、该用哪一个、Config 与 Setting | [references/modules.md](references/modules.md) |
| 流程切换、事件、实体、界面、数据表、场景、声音 | [references/gameplay.md](references/gameplay.md) |
| 接入包、预制体、GameEntry、Editor Resource、Unity 6、重编 DLL | [references/integrate.md](references/integrate.md) |
