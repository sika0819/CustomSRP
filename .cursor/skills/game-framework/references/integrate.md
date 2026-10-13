# 接入

只在用户明确要求把框架加进工程时执行。接入前先 grep `UnityGameFramework.Runtime.GameEntry`；已经存在就不要再加一份包或第二套入口。

不改 `Assets/CustomSRP` 下的渲染代码来“装上”框架。不把核心库源码复制进仓库。

## 包

`Packages/manifest.json` 增加：

```json
"com.jiangyin.gameframework": "https://github.com/EllanJiang/UnityGameFramework.git"
```

这是 Unity 集成包，内含 `Libraries/GameFramework.dll`。不要把 `EllanJiang/GameFramework` 再装成第二个包。

包声明的 Unity 版本是 `2017.1`（包版本 `2021.05.31`）。导入后看编译结果。

## 预制体与场景

1. 使用包里的 `GameFramework.prefab`，放进启动场景。
2. `UnityGameFramework.Runtime.GameEntry.Shutdown(ShutdownType.Restart)` 会加载 **Build Settings 里下标 0** 的场景（`GameFrameworkSceneId = 0`）。若使用重启，该场景必须是带此预制体的启动场景。不要为了这个去重排现有渲染验证场景，除非用户要求改构建顺序。
3. 编辑器勾选 `BaseComponent.EditorResourceMode`。框架在非编辑器播放时会把它强制关掉。玩家构建若还没有资源包，先不要关编辑器模式去换可更新模式。
4. `ProcedureComponent` 上登记流程类型并指定入口流程。`UIComponent` / `EntityComponent` / `SoundComponent` 上的组留空也算未配置：打开界面、显示实体、播声音之前，组必须已存在。

## 游戏目录

在 `Assets/GameMain`（或用户指定的目录）建独立程序集，命名空间不要用 `CustomSRP` 或 `UnityGameFramework.Runtime`。

三个入口文件，都是**游戏程序集**里的 `partial class`，不是框架那个静态类：

| 文件 | 职责 |
|------|------|
| `GameEntry.cs` | `MonoBehaviour.Start` 调用 `InitBuiltinComponents` 与 `InitCustomComponents` |
| `GameEntry.Builtin.cs` | 为预制体上已有的组件写静态属性，并用 `UnityGameFramework.Runtime.GameEntry.GetComponent<T>()` 赋值 |
| `GameEntry.Custom.cs` | 仅项目自己的 `GameFrameworkComponent`。没有就不添加属性 |

预制体上没有的组件不要缓存，`GetComponent` 会得到 null。属性按实际使用的模块逐个加（`Procedure`、`Event`、`Resource`、`Entity`、`UI` 等），不要一次生成 19 个空壳。

```csharp
namespace Game
{
    public partial class GameEntry : MonoBehaviour
    {
        void Start()
        {
            InitBuiltinComponents();
            InitCustomComponents();
        }
    }
}
```

```csharp
using UnityGameFramework.Runtime;

namespace Game
{
    public partial class GameEntry
    {
        public static EventComponent Event { get; private set; }
        public static UIComponent UI { get; private set; }

        static void InitBuiltinComponents()
        {
            Event = UnityGameFramework.Runtime.GameEntry.GetComponent<EventComponent>();
            UI = UnityGameFramework.Runtime.GameEntry.GetComponent<UIComponent>();
        }
    }
}
```

`InitCustomComponents` 在没有自定义组件时保持空方法。

把这个 `GameEntry` 挂到启动场景里框架预制体以外的物体上，或挂在预制体实例上。全工程只保留一个。

## 界面资源

每个界面一个预制体：

- `UIDocument`：`PanelSettings` + UXML
- 同物体上的 `UIFormLogic` 子类
- USS 负责样式

`OpenUIForm` 的资源名是该预制体在资源系统中的路径。Editor Resource 模式下，这个路径要能被编辑器资源辅助器按 Asset 路径加载。

多个界面可以共用 `PanelSettings` 的主题，但排序只写各自 `UIDocument.sortingOrder`。

## Unity 6 与 DLL

本工程编辑器是 `6000.6.4f1`。导入后若编译失败：

- 只改**本地包副本**里导致 Unity 6 编译失败的 API 调用。
- 不重写模块、不替换 Helper 的行为、不把修复扩散成玩法改动。
- `GameFramework.dll` 能加载就继续用包内 DLL。
- 仅当 DLL 无法加载或缺少必需类型时，用 [GameFramework](https://github.com/EllanJiang/GameFramework) 源码重编并替换 `Libraries/GameFramework.dll`。重编结果仍放在包的 `Libraries` 下，不放进 `Assets/CustomSRP`。

修完用编译结果确认，不要只改到能保存脚本为止。
