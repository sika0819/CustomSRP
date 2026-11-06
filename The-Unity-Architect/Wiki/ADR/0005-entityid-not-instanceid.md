# Unity 6 用 EntityId，禁止 GetInstanceID()

**背景：** Unity 6000 把 `Object.GetInstanceID()` 标成 Obsolete（`CS0619`）。身份类型是 `EntityId`。

**决策：** 游戏 / SRP 代码一律 `GetEntityId()`，集合键用 `EntityId`。规则见 `.cursor/rules/unity6-entity-id.mdc`。Architect Kit 场景导出也写 EntityId 字符串。

**理由：** Unity 6 干净编译 + 稳定身份 API。代价：第三方包可能自带 Compat 包装 —— 不要在 `Assets/` 游戏代码里仿造。

**状态：** 已采纳
