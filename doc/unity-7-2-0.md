# Custom SRP 7.2（独立 Shadow Pass）

阴影外观不变：从 `LightingPass` 拆出 `DirectionalShadowsPass` / `OtherShadowsPass` / `ShadowsPass`。

## 本仓变更

| 项 | 状态 |
|----|------|
| `CameraRenderer` 持有 `Shadows`，`Setup(shadowSettings)` | 已接 |
| `LightingPass.Handles`；不再渲染 / 构建阴影 | 已接 |
| `ShadowsPass` + `DirectionalShadowsPass` + `OtherShadowsPass` | 已接 |
| `LightResources`：`lightHandles` + `shadowHandles` + `Use` | 已接 |
| `ShadowCastersCullingInfos` 传入 BuildRendererLists | 已接 |
| `UsesShadowMask` 属性（去掉 ref 参数） | 已接 |
| `Shadows` 仅关键词 / 距离淡出 / atlas 尺寸 | 已接 |

## Render Graph 顺序（阴影相关）

1. `LightingPass`（Reserve 阴影）  
2. `DirectionalShadowsPass`  
3. `OtherShadowsPass`  
4. `CullShadowCasters`  
5. `ShadowsPass`（全局关键词与淡出）

## 验收

- [ ] DirectionalShadows / PointAndSpotShadows 与升级前一致  
- [ ] Frame Debugger / Render Graph Viewer 可见三个阴影 Pass + Lighting  
- [ ] 无阴影灯时无报错  

## 相关代码

| 路径 | 职责 |
|------|------|
| `Runtime/Passes/DirectionalShadowsPass.cs` | 方向光阴影绘制 |
| `Runtime/Passes/OtherShadowsPass.cs` | Point/Spot 阴影绘制 |
| `Runtime/Passes/ShadowsPass.cs` | 编排 + 全局阴影状态 |
| `Runtime/Shadows.cs` | 捆绑 Directional/Other + 共用工具 |
| `Runtime/CameraRenderer.cs` | Setup + Record 接线 |
