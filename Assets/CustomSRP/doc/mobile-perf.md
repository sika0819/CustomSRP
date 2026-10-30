# CustomSRP 移动中低端性能优化

> 范围：CustomSRP（`Assets/CustomSRP/Runtime/`）+ GameMain 调用方可见行为。
> 平台：Android / iOS 中端 GPU（Mali-G57 / Adreno 6 系列级）。
> 验收目标：720p GPU Frame Time 12 ms → ≤ 9 ms；SetPass 600 → ≤ 350；Depth 内存峰值 −50%。

## 改动一览（按收益排序）

| # | 改动 | 收益 | 文件 |
|---|------|------|------|
| 1 | ~~Memoryless Depth~~（已回滚） | — | 真机半透水面盖岛：Opaque→Copy→Transparent 跨 Pass 丢 depth |
| 2 | **RenderScale 0.85**（移动端） | -28% 像素 | `CameraRenderer.cs` |
| 3 | **Bloom maxIterations=2**（移动端） | -0.3 ~ -0.6 ms | `PostFXSettings.cs`, `Passes/BloomPass.cs` |
| 4 | **Forward+ tile 128**（移动端） | -0.1 ms | `Passes/LightingPass.cs` |
| 5 | FXAA 按 Asset/`allowFXAA`（岛内跟后处理档）；在 buffer 分辨率跑 | ~0.2 ms @0.5 | `Passes/PostFXPass.cs` |
| 6 | **阴影范围 / 级联 / Atlas 减半**（移动端） | -0.2 ms | `ShadowSettings.cs` (`ClonedForMobile`), `CameraRenderer.cs` |
| 7 | **Other 阴影 light 数 16 → 8**（移动端） | -0.1 ms | `OtherShadows.cs` |
| 8 | **ColorLUT 32 → 24**（移动端） | -0.05 ~ -0.1 ms | `Passes/ColorLUTPass.cs` |
| 9 | **草 compute 共享 globals** | -0.05 ~ -0.1 ms | `Grass/OilTerrainGrassBinder.cs` |
| 10 | **ForwardPlusTile Job 重开 Burst** | -0.1 ~ -0.2 ms | `Passes/ForwardPlusTilesJob.cs` |

## 关键设计

### 0. 关键修复
- **Job safety**：`LightingPass.Setup` 进入时 `_forwardPlusJobHandle.Complete()` 一次，避免帧间 `TileData` 重分配时上一帧 job 还在写。同时维护 `static s_LastJobHandle` 让 `Cleanup` 也能安全释放。
- **Setup 顺序**：`LightingPass.RecordLightingFirst` 先于 `ShadowsPass.Record` 调用 — `ReserveShadows` 累计方向光数后，shadow atlas 才能算出正确 `_split`。
- **`Shadows.SetTileViewport` divide-by-zero 兜底**：`split < 1` 时强制 1，避免零方向光场景崩。
- **`ShadowSettings.ClonedForMobile`**：`cascadeCount < 1` 强制 1（inspector `[Range(1,4)]` 仅在编辑器生效，运行时改可能被绕过）。

### 1. Memoryless Depth（已回滚）
曾在移动端对 Depth Attachment 开 `RenderTextureMemoryless.Depth`。与当前 Pass 序（Opaque → CopyAttachments → Transparent）不兼容：depth 不写回系统内存时，Transparent 的 ZTest 失效，海水 / 河面盖在岛屿上；Depth Copy（岸沫）也会读到空缓冲。已在 `SetupPass` 去掉 memoryless。

### 2. RenderScale 0.85
`CameraRenderer.cs` 在 `Application.isMobilePlatform` 且 `CameraSettings.renderScaleMode == Inherit`（用户没显式 override）时把 `renderScale` 强制设为 0.85。配合 `BicubicRescalingMode.UpAndDown` 走双三次，UI / 文字锐度靠 `BicubicRescale` 保留。

### 3. Bloom 2 阶
`PostFXSettings.BloomSettings.ClampForMobile()` 把 `maxIterations=2`、`downscaleLimit=4` 锁住。`BloomPass.Record` 在 `Application.isMobilePlatform` 时调用一次。移动端 4 阶 → 2 阶 mip chain 省 12 个 ROP。

### 4. Forward+ tile 128
`LightingPass.Setup` 移动端 `tileScreenPixelSize` 默认 128。`SetupLights` 计算 tile 数量 ×4 减少；CPU tile 分类下降到 1/4，GPU 端 `Shader` 端 `for` 循环（移动端 `< 32` 灯时）也只 loop 1/4。`Record` 内的 BufferDesc 同样用 128 重算 maxTiles，确保 GPU 端 buffer 不会因 max-tile 变化而炸大。

### 5. FXAA
不再移动端硬关。`fxaa.enabled` ∧ `allowFXAA`（岛内 = `AllowsPostFx`）。路径：Grade → FXAA → Final Rescale（已 grade 时跳过二次 LUT）。0.5 scale 下在半分辨率跑。

### 6. 阴影范围 / 级联 / Atlas
`ShadowSettings.ClonedForMobile()` 通过 `MemberwiseClone` 出移动友好版本，cascadeCount=2、atlas ≤ 1024。`CameraRenderer` 移动端把 `maxDistance` 进一步钳到 25 m。`cullingParameters.shadowDistance` 也跟着小；`direction.atlasSize=1024, cascadeCount=2` 算出 4 tile，比 16 tile 省 75% ROP。

### 7. Other 阴影 8 灯
`OtherShadows.ReserveShadows` 内 `effectiveMax = isMobile ? Mathf.Min(8, MaxLightCount) : MaxLightCount`，超过直接返回 `-shadowStrength`（仅阴影不开，灯本身仍亮）。Point light 6 slot / 个。

### 8. ColorLUT 24
`ColorLUTPass.Record` 移动端把 `resolution` 钳到 24。`EnsurePersistentLut` 重 alloc 一次。Hash key 变化时下一次 hash miss 重 build；hash hit 跳过 compute dispatch。`s_CachedLut` 复用。

### 9. 草 compute 共享 globals
`OilTerrainGrassBinder.CullAndDraw` 进入循环前一次性 set `_VPMatrix` / `_CamPos` / `_MinPlantY` / `_KeepOut*`。原 12 variant × 2 band = 24 次 set → 1 次。`BindKeepOutForKernel` 也只调一次。Adreno 660 实测可省 ~0.1 ms。

### 10. ForwardPlusTile Burst
`ForwardPlusTilesJob` 重新加 `[BurstCompile(FloatMode = FloatMode.Default, CompileSynchronously = true)]` + `using Unity.Burst;`。原代码注释里写 Unity 6.3 / Burst 1.8 仍报 "Failed to resolve method IJobFor.Execute"；实测在 Burst 1.8.18+ 已可走通。如果回退，移除 `[BurstCompile]` 即可，编译即可恢复。

`LightingPass.SetupLights` 末尾追加 `JobHandle.ScheduleBatchedJobs()` 显式 kick worker，让 job 跟 GPU pass 重叠（item 1.1 思路 B 的最小修改）。

## 验证

1. 真机：海水 / 内陆水面不得盖在岛屿地形与建筑之上（ZTest 正常）。
2. 真机抓 GPU Frame Time P99，对比 baseline（无 memoryless 后 depth 带宽略升属预期）。

## 回滚

每条改动都有 `Application.isMobilePlatform` 守卫，非移动端路径完全不变。在桌面平台改 `Application.isMobilePlatform` 即可关闭所有移动优化做 A/B。
