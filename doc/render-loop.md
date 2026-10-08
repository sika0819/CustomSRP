# 渲染循环

每相机入口：`CustomRenderPipeline.Render` → `CameraRenderer.Render`。

## 固定顺序

以 [`CameraRenderer.cs`](../Assets/CustomSRP/Runtime/CameraRenderer.cs) 实际调用为准：

```
PrepareBuffer
PrepareForSceneWindow   # Editor：SceneView 发 Overlay 几何
Cull(shadowDistance)    # 必要时 Normalize 相机旋转；失败则 return
Lighting.Setup          # 先画 Shadows atlas，再写光 globals（须在相机 Setup 之前）
PostFXStack.Setup       # 按相机类型 / Scene Image Effects 决定是否活跃
Setup                   # SetupCameraProperties；中间缓冲（Post FX / copyColor / copyDepth）时分 Color+Depth attachment
DrawVisibleGeometry
  ├─ Opaque   (SRPDefaultUnlit + CustomLit)
  ├─ Skybox
  ├─ CopyAttachments  # 可选：拷贝 _CameraColorTexture / _CameraDepthTexture
  └─ Transparent
DrawUnsupportedShaders  # Editor only：Legacy → InternalErrorShader
DrawGizmosBeforeFX      # Editor：PreImageEffects（中间缓冲时先拷 depth）
PostFXStack.Render      # 活跃时：Bloom；否则若有中间缓冲则 DrawFinal 到相机
DrawGizmosAfterFX       # Editor：PostImageEffects
Cleanup                 # Lighting + 释放中间 RT / copy 纹理
Submit
```

上下文：`Cull` 成功后立刻 `_lighting.Setup(..., shadowSettings)`（内含阴影），再 `PostFXStack.Setup`，再 `Setup()`，再 `DrawVisibleGeometry(...)`。

## ShaderTag

| Tag | 状态 |
|-----|------|
| `SRPDefaultUnlit` | 已用；`CustomSRP/Unlit` Pass |
| `CustomLit` | 已用；`CustomSRP/Lit`、`TerrainLit`、`OilNPR` 等 Forward Pass |

Lit / TerrainLit 不得破坏现有 Unlit 与 `CustomRPTest` 约定。Terrain 不另开 Pass，见 [terrain.md](terrain.md)。

## Unity 6 API

绘制可见物体走 RendererList，不直接调旧式 `DrawRenderers`：

```csharp
var params = new RendererListParams(cullingResults, drawingSettings, filteringSettings);
var list = context.CreateRendererList(ref params);
buffer.DrawRendererList(list);
```

Skybox：`CreateSkyboxRendererList` + `DrawRendererList`。

## Clear Flags

`Setup` 按相机 `clearFlags` 决定是否清 Depth / Color（`flags <= Depth` / `flags <= Color`）。  
Post FX 活跃时：若 flags 严于 Color 则抬到 Color，并对中间 RT 强制清深度+颜色（避免未初始化内容）。
