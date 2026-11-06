# 运行时资源走 YooAsset

**背景：** 移动端要异步 Bundle、热更钩子，还要兼容 GF ResourceComponent。

**决策：** YooAsset 包 + `YooAssetResourceManager` 实现 GF `IResourceManager`。Editor 用 EditorResourceMode；真机用 Bundle。游戏资产禁止同步 `Resources.Load`（Yoo 自身配置除外）。

**理由：** 贴合 `ProcedureInitYooAsset` 与现有 Collector。代价：Disable Domain Reload 时 Awake 必须销毁包做会话复位。

**状态：** 已采纳
