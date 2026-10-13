# Custom SRP 6.1.0（Camera Target Texture + Color LUT Debug）

## 本仓变更

| 项 | 状态 |
|----|------|
| `CameraRendererTextures.cameraTarget` | 已接 |
| `SetupPass`：`targetTexture` 优先，否则 `CameraTarget` | 已接 |
| Final / PostFX / Gizmos 用 `textures.cameraTarget` | 已接 |
| Rendering Debugger → Show Color LUT | 已接 |
| `PostFXPass.Record` 返回 colorLUT；`DebugPass` 消费 | 已接 |
| Camera Debugger Pass 1：底部 LUT 条带 | 已接 |

## 验收

- [ ] Multiple Cameras 中 Render Texture 相机写入正确；选中该相机不再画到编辑器任意区域
- [ ] Window → Analysis → Rendering Debugger → Forward+ → Show Color LUT
- [ ] 有 Post FX 时底部显示 LUT 条（分辨率 16/32/64 高度不同）
- [ ] 无 Post FX 时打开 Show Color LUT 不画 LUT（resolution=0）

## 相关代码

| 路径 | 职责 |
|------|------|
| `Runtime/CameraRendererTextures.cs` / `SetupPass.cs` | cameraTarget 导入 |
| `Runtime/Passes/FinalPass.cs` / `PostFXPass.cs` / `GizmosPass.cs` | 使用 textures.cameraTarget |
| `Runtime/CameraDebugger.cs` / `Passes/DebugPass.cs` | Show Color LUT |
| `Shaders/CameraDebugger.shader` / `CameraDebuggerPasses.hlsl` | LUT 条带绘制 |
