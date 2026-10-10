# 玩法写法

游戏侧通过自己的 `GameEntry` 访问组件（见 [integrate.md](integrate.md)）。下面示例的命名空间用 `Game` 占位，以工程里实际游戏程序集为准。

组名、资源名、流程类型必须已经配在对应组件上。未接入框架时不要写这些调用。

## 流程

```csharp
using GameFramework.Procedure;
using ProcedureOwner = GameFramework.Fsm.IFsm<GameFramework.Procedure.IProcedureManager>;

namespace Game
{
    public class ProcedureMenu : ProcedureBase
    {
        bool _startRequested;

        protected override void OnUpdate(ProcedureOwner procedureOwner, float elapseSeconds, float realElapseSeconds)
        {
            base.OnUpdate(procedureOwner, elapseSeconds, realElapseSeconds);
            if (_startRequested)
                ChangeState<ProcedurePlay>(procedureOwner);
        }
    }
}
```

`ChangeState` 的目标必须是 `ProcedureComponent` 已注册的流程。流程间的少量参数用 `procedureOwner.SetData` / `GetData`（`Variable`），不要加静态全局字段传递。

## 事件

`Fire` 推迟到事件轮询；`FireNow` 立即分发。两者都会在处理函数返回后由事件池 `ReferencePool.Release`。

```csharp
using GameFramework;
using UnityGameFramework.Runtime;

public sealed class PlayStartedEventArgs : GameEventArgs
{
    public static readonly int EventId = typeof(PlayStartedEventArgs).GetHashCode();
    public override int Id => EventId;
    public int LevelId { get; private set; }

    public static PlayStartedEventArgs Create(int levelId)
    {
        PlayStartedEventArgs e = ReferencePool.Acquire<PlayStartedEventArgs>();
        e.LevelId = levelId;
        return e;
    }

    public override void Clear() => LevelId = 0;
}

// 发送
GameEntry.Event.Fire(this, PlayStartedEventArgs.Create(levelId));

// 订阅（OnEnter / OnShow / OnOpen 里订，对称位置退订）
GameEntry.Event.Subscribe(PlayStartedEventArgs.EventId, OnPlayStarted);
```

处理函数里只读字段。不要 `ReferencePool.Release`，不要把参数存进字段。

## 实体

`ShowEntity` 异步加载。逻辑在 `OnShow` 开始，`OnHide` 结束。隐藏默认回池，不要对实体 `Destroy`。

```csharp
GameEntry.Entity.ShowEntity<ItemLogic>(entityId, assetName, "Item", userData);
```

`entityId` 由游戏分配且当前唯一。实体组 `"Item"` 必须已在 `EntityComponent` 上建好。挂接用 `Entity.AttachEntity`，在 `OnAttachTo` / `OnAttached` 里摆位置。

`OnShow` / `OnHide` / `OnUpdate(float elapseSeconds, float realElapseSeconds)` 都要调 `base`。

## 界面（UI Toolkit）

`OpenUIForm` 返回序列号，加载是异步的。预制体上要有 `UIDocument`（指定 UXML 与 `PanelSettings`）和 `UIFormLogic` 子类。界面组名必须已在 `UIComponent` 上存在。

```csharp
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game
{
    public class MenuForm : UIFormLogic
    {
        Button _start;

        protected override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            _start = root.Q<Button>("start");
            _start.RegisterCallback<ClickEvent>(OnStart);
        }

        protected override void OnClose(bool isShutdown, object userData)
        {
            _start?.UnregisterCallback<ClickEvent>(OnStart);
            _start = null;
            base.OnClose(isShutdown, userData);
        }

        protected override void OnDepthChanged(int uiGroupDepth, int depthInUIGroup)
        {
            base.OnDepthChanged(uiGroupDepth, depthInUIGroup);
            GetComponent<UIDocument>().sortingOrder = uiGroupDepth * 100 + depthInUIGroup;
        }

        void OnStart(ClickEvent evt) { /* 切流程或 Fire 事件 */ }
    }
}
```

```csharp
int serialId = GameEntry.UI.OpenUIForm(menuFormAssetName, "Default", userData);
GameEntry.UI.CloseUIForm(serialId);
```

- 控件查询用 `Q<T>(name)`，名字与 UXML 里的 `name` 一致。
- 样式放 USS。运行时结构变化可以用 `VisualElement` 代码补，仍然不要引入 `UnityEngine.UI`。
- 每个界面用自己的 `UIDocument.sortingOrder`。不要在运行时改共享 `PanelSettings` 的排序。
- `OnPause` / `OnResume` / `OnCover` / `OnReveal` 用来停逻辑；不要在被遮住时继续当可点界面。

## 数据表

行类型覆盖与资源格式对应的 `ParseDataRow`（文本用 `string` 重载，二进制用 `byte[]` 重载）。`Id` 必须唯一。

```csharp
IDataTable<DRItem> table = GameEntry.DataTable.CreateDataTable<DRItem>();
table.ReadData(dataTableAssetName);
DRItem row = table.GetDataRow(id);
```

`ReadData` 异步。读行放在 `ReadDataSuccess` 或框架抛出的加载成功事件之后。同一 `T` 已创建过则用 `GetDataTable<T>()`，不要重复 `CreateDataTable`。

## 场景与声音

```csharp
GameEntry.Scene.LoadScene(sceneAssetName);
GameEntry.Scene.UnloadScene(sceneAssetName);

int soundId = GameEntry.Sound.PlaySound(soundAssetName, "Music");
```

场景和声音都是异步的。成功、失败听对应的 `*SuccessEventArgs` / `*FailureEventArgs`，不要在调用的下一行就使用加载结果。

`PlaySound` 可传入 `PlaySoundParams`、`Entity` 或世界坐标。声音组要在 `SoundComponent` 上先建好。
