# Custom SRP 6.2.0（3D Color LUT + Compute）

## 本仓变更

| 项 | 状态 |
|----|------|
| `Shaders/ColorLUT.compute`（4 kernels） | 已接 |
| `PostFXSettings.colorLUTComputeShader` | 已接（各 PostFXSettings 资产已挂） |
| `ColorLUTPass` → `AddComputePass` + Tex3D UAV | 已接 |
| `ApplyLut3D` 采样；移除 2D LUT 生成 Pass | 已接 |
| Camera Debugger LUT 按 slice 展开 | 已接 |

## 验收

- [ ] Post FX / Tone Mapping 画面与升级前一致
- [ ] Show Color LUT：底部条带为分离 depth slices（无 slice 间插值）
- [ ] Frame Debugger 无法直接看 3D LUT（预期）；用 Show Color LUT 检查

## 相关代码

| 路径 | 职责 |
|------|------|
| `Shaders/ColorLUT.compute` | 填充 3D LUT |
| `Runtime/Passes/ColorLUTPass.cs` | Compute pass |
| `Shaders/PostFXStackPasses.hlsl` | `ApplyLut3D` |
| `Shaders/CameraDebuggerPasses.hlsl` | 3D LUT debug 条带 |
