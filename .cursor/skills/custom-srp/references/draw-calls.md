# Draw Calls

批处理 / Instancing / 透明 / Alpha Clip。布局与实例数以**当前工程**场景 / 示例脚本为准，勿写死。

主循环 / 队列号见主 Skill §3；接线状态见 §6（先 grep）。

## 怎么用

1. 确认已挂 Pipeline Asset；用当前场景或项目主题验证场（有则用，无则自建最小对照）。
2. **进 Play**（否则看不到 `Graphics.DrawMeshInstanced`）。
3. Frame Debugger：Opaque 2000 → AlphaTest 2450 → Skybox 2500 → Transparent 3000。
4. 对照 Asset：`Use SRP Batcher` / `Use GPU Instancing` / `Use Dynamic Batching`（字段名以工程为准）。

| 期望 | 能力 |
|------|------|
| 共享材质大簇合批 | SRP Batcher |
| 色各异的球 | MPB 打断 Batcher；可能走 Instancing（见下） |
| 半透 / Cutout | Transparent 3000 / AlphaTest 2450 |
| Play 后实例团 | `DrawMeshInstanced` |

## Agent 规范

- **默认勿给普通共享材质加 MPB**（打断 Batcher）。
- MPB ≠ 自动 Instancing：需材质 Enable GPU Instancing + Shader 支持 +（常规路径）Asset `Use GPU Instancing`。
- Asset `Use GPU Instancing` **只影响** `DrawRendererList`；**不影响** `DrawMeshInstanced`。
- `DrawMeshInstanced` 可见性：Play + 材质 Enable + Shader + **matrices 数组长度 / count = 实例数** + 视锥 / 层；**MPB 可选**，若用则其数组长度也需匹配（MPB 不是可见性开关）。
- Edit Mode 空物体属正常（`Update` 未跑），不是「丢了」。
- 开篇「管线能不能画 / Standard 粉红」用冒烟场景，不用本主题场。

### Asset 开关影响对象

| 字段 | 影响 |
|------|------|
| Use SRP Batcher | 常规 Renderer |
| Use GPU Instancing | 常规 Renderer；**不影响** `DrawMeshInstanced` |
| Use Dynamic Batching | 常规小网格；**不影响** `DrawMeshInstanced` |

GameObject 路径上 Unity **优先 SRP Batcher**。验常规 Instancing：关 Batcher + 开 Asset Instancing + 材质 Enable。

## 技巧 / 排障

| 条件 | 预期 |
|------|------|
| Batcher 开 | 共享材质大量合批 |
| Batcher 关 + Asset Instancing + 材质 Enable | MPB 物体可能 Instanced |
| 带 MPB | 不再进同一 Batcher 组 |
| Edit Mode `DrawMeshInstanced` | 不可见 |
| Play + 材质 Enable（与 Asset 无关） | 实例团可见 |

| 现象 | 查 |
|------|-----|
| 实例「丢了」 | Play？材质 Enable？Shader？视锥/层？**matrices** 数组长度 / count？**勿查** Asset Use GPU Instancing（只影响 `DrawRendererList`） |
| 合批差 | 谁挂了 MPB？Batcher 关了？variant 不同？ |
| Clip 无洞 | clipping keyword / Surface=Clip / Cutoff / alpha |
| Stats 负 batches saved | Batcher 开时常见；**不是错误**（SRP Batcher 统计方式）；看 Frame Debugger SRP Batch |

对照：Batcher 开 → 点 MPB 物体 → 关 Batcher 开 Instancing → 改 Clip Cutoff → 换实例材质。

透明预乘 / Fade 与 Lit 高光差异见 [directional-lights.md](directional-lights.md)。

## 相关核对

```bash
rg -n "DrawMeshInstanced|MaterialPropertyBlock|enableInstancing"
rg -n "useGPUInstancing|useSRPBatcher|useDynamicBatching"
```

Shader：`#pragma multi_compile_instancing` + `UNITY_INSTANCING_BUFFER`；Clip 用 feature keyword。队列 / Surface 预设以 Shader GUI 为准。

## 验收

- [ ] Play 后可见共享簇 + 透明/Clip +（若有）实例团
- [ ] Frame Debugger 队列号分档正确
- [ ] 关 Batcher 后常规 Instancing 行为符合上表
- [ ] 未把 Asset Instancing 当成 `DrawMeshInstanced` 开关
- [ ] 未默认给共享材质加 MPB
