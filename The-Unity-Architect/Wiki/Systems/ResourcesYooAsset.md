# 系统 — YooAsset 资源

**路径：** `Assets/GameMain/YooAsset/`  
**详解：** [`02-yooasset.md`](../../../Assets/GameMain/Docs/02-yooasset.md) · ADR [0004](../ADR/0004-yooasset-resource-pipeline.md)

## 职责

- 在 `ProcedureInitYooAsset` 里初始化包。
- 经 `YooAssetResourceManager` 绑定 GF `IResourceManager`。
- Editor 走 `Base.EditorResourceMode = true`；真机走 Bundle。
- 运行时游戏资源：**异步** `ResourceComponent.LoadAsset` —— 不要 `Resources.Load`（YooAsset 自己在 `Settings/Resources` 读配置除外）。

## 会话

Disable Domain Reload 下，`GameEntry.Awake` 必须调 `YooResourceModule.ResetSession()`（销毁包实例）。
