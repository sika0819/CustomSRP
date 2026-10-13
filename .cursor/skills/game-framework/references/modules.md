# 模块

核心类型在 `GameFramework.*`（DLL）。Unity 入口是同名 `*Component`（`UnityGameFramework.Runtime`）。游戏侧 `GameEntry` 只缓存预制体上实际挂着的组件。

`ReferencePool`、`Variable`、`Utility` 是支撑类型，不是第 20 个模块。

## 对照

| 模块 | 核心 | 组件 | 何时用 | 扩展 |
|------|------|------|--------|------|
| Config | `GameFramework.Config` | `ConfigComponent` | 只读全局配置（初始音量、常量表） | `ReadData` 后 `GetBool` / `GetInt` / `GetFloat` / `GetString` |
| Data Node | `GameFramework.DataNode` | `DataNodeComponent` | 运行时树状临时数据 | `GetOrAddNode` / `SetData` / `GetData` |
| Data Table | `GameFramework.DataTable` | `DataTableComponent` | 行式表数据 | `DataRowBase`；`CreateDataTable<T>()` 后 `ReadData` |
| Debugger | `GameFramework.Debugger` | `DebuggerComponent` | 运行时 UI Toolkit 调试窗 | 游戏界面不要挂到这个组件上 |
| Download | `GameFramework.Download` | `DownloadComponent` | 带断点续传的文件下载；资源更新会用它 | 未做热更时不要先接 |
| Entity | `GameFramework.Entity` | `EntityComponent` | 动态创建、显示、隐藏、挂接的物体 | `EntityLogic` |
| Event | `GameFramework.Event` | `EventComponent` | 模块之间解耦 | `GameEventArgs` |
| File System | `GameFramework.FileSystem` | `FileSystemComponent` | 把散文件打成虚拟磁盘，供资源局部加载 | 随资源模式启用，不单独先上 |
| FSM | `GameFramework.Fsm` | `FsmComponent` | 单个对象或子系统的状态机 | `FsmState<T>`。整局流程用 Procedure，不要另起一套 |
| Localization | `GameFramework.Localization` | `LocalizationComponent` | 文本或多语言资源 | 字典 `ReadData`；界面文案从这里取，不把语言写死在 UXML |
| Network | `GameFramework.Network` | `NetworkComponent` | TCP 长连接（IPv4/IPv6） | 派生 `Packet` |
| Object Pool | `GameFramework.ObjectPool` | `ObjectPoolComponent` | 业务自己的缓存池。实体和界面框架已内部使用 | `CreateSingleSpawnObjectPool` / `CreateMultiSpawnObjectPool` |
| Procedure | `GameFramework.Procedure` | `ProcedureComponent` | 整局生命周期 | `ProcedureBase` |
| Resource | `GameFramework.Resource` | `ResourceComponent` | 资源、数据表、实体、界面、场景的异步加载 | 编辑器保持 Editor Resource 模式 |
| Scene | `GameFramework.Scene` | `SceneComponent` | 多场景加载与卸载 | `LoadScene` / `UnloadScene`。完成与失败看对应事件 |
| Setting | `GameFramework.Setting` | `SettingComponent` | 玩家键值（进度、音量偏好） | `Get*` / `Set*` / `Save`。可走 PlayerPrefs 或磁盘 |
| Sound | `GameFramework.Sound` | `SoundComponent` | 按声音组播放，可绑实体或世界坐标 | `PlaySound` + `PlaySoundParams` |
| UI | `GameFramework.UI` | `UIComponent` | 界面的加载、开关、暂停与分组 | `UIFormLogic`。视觉层用 UI Toolkit |
| Web Request | `GameFramework.WebRequest` | `WebRequestComponent` | 短连接 GET/POST | 与 Network 的长连接分开 |

## Config 与 Setting

- **Config**：启动时加载，运行时只读。改设计数据改资源，不在运行时 `Set`。
- **Setting**：玩家本机状态。写入后要 `Save`。不要把配表行塞进 Setting，也不要把存档写进 Config。

## UI 视觉层

`UIComponent` 不创建 Canvas。表单资源是带 `UIDocument` 的预制体。深度在 `UIFormLogic.OnDepthChanged` 里写到该文档的 `sortingOrder`。详见 [gameplay.md](gameplay.md)。
