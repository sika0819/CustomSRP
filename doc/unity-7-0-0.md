# Custom SRP 7.0.0（Unity 6.5+ Render Target Info / 去 LPPV）

对应升级：Unity 6.5+ Render Graph 要求 `ImportBackbuffer` 带 `RenderTargetInfo`；弃用 renderer list culling 与 LPPV。

本仓编辑器版本见 `ProjectSettings/ProjectVersion.txt`（当前 `6000.6.4f1`）。

## 本仓变更

| 项 | 状态 |
|----|------|
| `SetupPass`：`ImportBackbuffer(rt, RenderTargetInfo)` | 已接（`pixelWidth/Height`、`volumeDepth=1`、`msaaSamples=1`、framebuffer `R8G8B8A8_UNorm`） |
| 去掉 `RenderGraphParameters.rendererListCulling` | 已接 |
| `SkyboxPass` / `GizmosPass` 去掉 `AllowPassCulling(false)` | 已接 |
| `GeometryPass` 去掉 `LightProbeProxyVolume` / `OcclusionProbeProxyVolume` | 已接 |
| `MeshBall` / `GI.hlsl` / `UnityInput.hlsl` LPPV 代码 | 本仓此前已无 |
| `ShadowSettings` 去掉废弃 `cascadeBlend` / 旧 `FilterMode filter` | 已接（保留 `filterQuality` + `softCascadeBlend`） |

## 验收

- [ ] Play / Scene 可渲染；升级后若花屏可重启 Editor
- [ ] Skybox / Gizmos 仍可见（不再依赖 AllowPassCulling）
- [ ] MeshBall / Lit 探针路径正常（无 LPPV 警告）
- [ ] Asset Shadows：仅 `filterQuality` / `softCascadeBlend`，无 Deprecated 区

## 相关代码

| 路径 | 职责 |
|------|------|
| `Runtime/Passes/SetupPass.cs` | `RenderTargetInfo` + ImportBackbuffer |
| `Runtime/CameraRenderer.cs` | RenderGraphParameters |
| `Runtime/Passes/SkyboxPass.cs` / `GizmosPass.cs` | 去掉强制禁 cull |
| `Runtime/Passes/GeometryPass.cs` | PerObjectData 去 LPPV |
| `Runtime/ShadowSettings.cs` | 去掉废弃字段 |
