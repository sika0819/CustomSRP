# Mega-Brain Wiki 索引

> 首次入库：2026-11-05 · Unity 6（`6000.x`）· Custom SRP + GameFramework  
> 诊断报告：[`../doctor-report.md`](../doctor-report.md) · [`../project-graph-report.md`](../project-graph-report.md) · [`../project-graph.json`](../project-graph.json)

## 分类

- [ADR](./ADR/) — 难回头的架构决策
- [Systems](./Systems/) — 运行时 / 管线模块
- [Features](./Features/) — 已批准的功能设计文档（Feature Pipeline 跑完后再往这放）
- [Lore](./Lore/) — 产品 / 世界观语境

## 速查地图

| 层 | 命名空间 / 程序集 | 目录 | Wiki |
|----|-------------------|------|------|
| 油画 NPR 渲染管线 | `CustomSRP` | `Assets/CustomSRP/` | [Systems/CustomSRP](./Systems/CustomSRP.md) |
| 游戏逻辑 | `Game` / `Game.asmdef` | `Assets/GameMain/` | [Systems/GameMain](./Systems/GameMain.md) |
| 空间音频引擎 | `WanosAudio` | `Assets/WanosAudio/` | [Systems/WanosAudio](./Systems/WanosAudio.md) |
| 启动与流程 | `Game` | `Procedure/` + `Boot/` | [Systems/BootAndProcedure](./Systems/BootAndProcedure.md) |
| UI Toolkit 界面 | `Game` | `UI/` | [Systems/UI](./Systems/UI.md) |
| 资源加载 | YooAsset | `YooAsset/` | [Systems/ResourcesYooAsset](./Systems/ResourcesYooAsset.md) |

## 入口场景

| 场景 | 路径 | 用途 |
|------|------|------|
| **Launch** | `Assets/GameMain/Scenes/Launch.unity` | 构建列表第 0 位 —— 真正的游戏入口 |
| Empty | `Assets/CustomSRP/Scenes/Empty.unity` | 油画 NPR 单物体验收 |
| MooreaTerrain | `Assets/CustomSRP/Scenes/MooreaTerrain.unity` | 全岛 NPR / 地形验收 |

## 关键包

| 包 | 作用 |
|----|------|
| `com.jiangyin.gameframework` | GF：Procedure / Entity / UI / Event / Resource |
| `com.tuyoogame.yooasset` | 异步资源包 |
| `com.cysharp.unitask` | 异步 |
| `com.unity.nuget.newtonsoft-json` | JSON |
| `com.unity.render-pipelines.core` | SRP Core（自建管线，**不是** URP） |
| `com.coplaydev.unity-mcp` | Editor MCP 桥 |

## ADR 列表

| # | 决策 |
|---|------|
| [0001](./ADR/0001-custom-srp-not-urp.md) | 自建 Custom SRP 油画 NPR，不用 URP/HDRP |
| [0002](./ADR/0002-gamemain-decoupled-from-srp.md) | GameMain ↔ SRP 只走接口 / 反射 |
| [0003](./ADR/0003-ui-toolkit-only.md) | 只用 UI Toolkit，不用 uGUI Canvas 做表单 |
| [0004](./ADR/0004-yooasset-resource-pipeline.md) | YooAsset + GF ResourceManager |
| [0005](./ADR/0005-entityid-not-instanceid.md) | Unity 6 用 `GetEntityId()`，禁止 `GetInstanceID()` |
| [0006](./ADR/0006-wanos-volume-rtpc-scale.md) | 音量 UI 0–1，引擎 RTPC 0–100 |

## 入库快照（2026-11-05）

### unity-doctor.js

- 扫描一等公民 **469** 个 `.cs`（`GameMain`、`CustomSRP`、`WanosAudio`、`Scripts`、`Editor`）。
- **14** 条发现 —— 全是 `UI-002` WARNING（Boot/UI 里用 `style.opacity = 0` 隐藏）。没有 CRITICAL 级别的 VFX/对象池问题。
- 这些 opacity 项当 **待复核**，不要当成自动 bug：不少界面是故意做淡出的。

### unity-project-graph.js（`--project-only --scene`）

| 指标 | 数量 |
|------|-----:|
| GUID 资产索引（Assets + Packages） | 3756 |
| Prefab / ScriptableObject / 场景 | 195 / 183 / 24 |
| 一等公民脚本 | 469 |
| 缺失脚本引用 | **0**（已清理 / 排除误报） |

已处理：

| 场景 | 处理 |
|------|------|
| `IslandNavBake.unity` | 已删除失效的 `NavMeshSurface`（旧 GUID，且工程未装 `com.unity.ai.navigation`）。根节点 `IslandNavBakeRoot` 与子层级保留。若以后要烘导航，再加 AI Navigation 包并重新挂组件。 |
| `CustomRPTest.unity` | **误报**：左上角 Overlay UI 的 uGUI GUID 与 Unity 内置 `com.unity.ugui` 一致；graph 此前未扫 BuiltInPackages。脚本已改为索引 Editor 内置包，不再报缺。 |

### 脚本密度（目录 Top）

`CustomSRP/Runtime` 80 · `GameMain/Services` 74 · `GameMain/Editor` 57 · `CustomSRP/Editor` 47 · `GameMain/Tests` 46 · `GameMain/UI` 40 · `WanosAudio/Runtime` 22 · `GameMain/Native` 21

## 仓库里已有的权威文档

Wiki 是 **AI 记忆索引**，不是第二份真相。细节优先看：

- 根目录：[`README.md`](../../README.md) · [`doc/`](../../doc/)
- 游戏侧：[`Assets/GameMain/Docs/00-overview.md`](../../Assets/GameMain/Docs/00-overview.md) 及 `01`–`13`
- Cursor skills：`custom-srp` · `game-framework` · `preset-bridge-ambience-stutter`
- Rules：`gamemain.mdc` · `unity6-entity-id.mdc` · `wanos-audio.mdc` · `the-unity-architect.mdc`

## 重新跑诊断

```bash
node The-Unity-Architect/execution/unity-doctor.js
node The-Unity-Architect/execution/unity-project-graph.js --project-only --scene
```
