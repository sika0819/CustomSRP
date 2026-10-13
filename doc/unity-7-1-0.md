# Custom SRP 7.1.0（拆分 Directional / Other Shadows）

功能不变：把阴影实现从单一 `Shadows` 拆成专类，为后续独立 Shadow Pass 铺路。

## 本仓变更

| 项 | 状态 |
|----|------|
| `OtherShadows` + `Handles` / `OtherShadowData` | 已接 |
| `DirectionalShadows` + `Handles` / `DirectionalShadowCascade` | 已接 |
| `Shadows` 改为转发 + 共用 `ConvertToAtlasMatrix` / `SetTileViewport` | 已接 |
| `ShadowResources` → `Shadows.Handles` | 已接 |
| `LightingPass`：`BuildRendererLists` + `GetHandles` 分离 | 已接 |
| `GeometryPass`：`shadowHandles.Use(builder)` | 已接 |
| Reserve 写入 shadow mask channel / `_SHADOW_MASK_*` keywords | 已接（采样仍待接） |

## 验收

- [ ] DirectionalShadows / PointAndSpotShadows 画面与升级前一致
- [ ] Spot / Point Hard 影、CSM、PCF 仍正常
- [ ] 无阴影灯时无报错（default shadow texture + 空 buffer）

## 相关代码

| 路径 | 职责 |
|------|------|
| `Runtime/DirectionalShadows.cs` | 方向光 atlas / cascade |
| `Runtime/OtherShadows.cs` | Point/Spot atlas |
| `Runtime/Shadows.cs` | 编排、关键词、距离淡出、atlas 尺寸 |
| `Runtime/LightResources.cs` | `shadowHandles` |
| `Runtime/Passes/LightingPass.cs` / `GeometryPass.cs` | 接线 |
