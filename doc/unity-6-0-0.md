# Custom SRP 6.0.0（Unity 6.3 Native Render Passes）

## 本仓变更

| 项 | 状态 |
|----|------|
| `executionId = camera.GetEntityId()` | 已接 |
| `generateDebugData`（非 Preview / 非 RenderRequest） | 已接 |
| `LightDataGI.InitNoBake(light.GetEntityId())` | 已接 |
| `nativeRenderPassesEnabled` 保持默认开启 | 已接（去掉强制 `false`） |
| Final / PostFX `ImportBackbuffer(CameraTarget)` | 已接 |
| `ResetGraphAndLogException`（Pipeline.Render） | 已接 |
| `CameraSettings.renderingLayerMask` + `FormerlySerializedAs` | 已接 |

包版本：Burst ≥ 1.8.27、Mathematics 1.3.3、SRP Core 17.3.0（以 `Packages/manifest.json` 为准）。

## 验收

- [ ] Scene / Game 视图正常出图（Native RP 开启）
- [ ] Window → Analysis → Render Graph Viewer 能看到 pass；Final/PostFX 不被误剔
- [ ] Viewer 可见 pass merge（蓝条）
- [ ] 无 `executionName` / `GetInstanceID`（灯光 delegate）过时警告

## 相关代码

| 路径 | 职责 |
|------|------|
| `Runtime/CustomRenderPipeline.cs` | Native RP 默认；异常 ResetGraph |
| `Runtime/CameraRenderer.cs` | executionId / generateDebugData |
| `Runtime/Passes/FinalPass.cs` / `PostFXPass.cs` | ImportBackbuffer |
| `Runtime/PostFXStack.cs` / `CameraRendererCopier.cs` | DrawFinal / CopyToCameraTarget 接 TextureHandle |
| `Runtime/CameraSettings.cs` | `renderingLayerMask` |
