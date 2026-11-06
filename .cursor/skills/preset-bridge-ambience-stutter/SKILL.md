---
name: preset-bridge-ambience-stutter
description: >-
  过桥 / 岸边 / 切预设后环境音断续刺啦：根因是 Trigger（或距离轮询等价）边界抖动导致
  bank UnLoad↔Load↔Post，不是音量曲线。修法是引用计数 + Exit 宽限 + 近处几何 + 已播禁止再 Post
  + 共享 bank 禁止误卸。触达 AmbienceZoneService、WanosBankStore、Ocean/Grassland 停播卸包时使用。
---

# 过桥 / 岸边环境音断续（preset-bridge-ambience-stutter）

## 根因

边界物理抖：海浪等（如 Ocean_RD）是样条 Trigger，unloadwaittime=0。桥面 / 岸边 CharacterController 会连发 OnTriggerExit → OnTriggerEnter。

旧逻辑一 Exit 就卸：立刻 RequestUnload → UnLoad bank → 再 OnAllBankDecode → Post Play_*。日志里 UnLoad 与 Play 每 0.5–1.5s 成对出现 → 听感就是断续 / 刺啦。

引用计数没接上：注释写了重叠计数，Enter/Exit 实际没用；已在播时再 Enter 仍会重跑 Loading() 再 Post。

走路误卸加重：走路 SyncTriggerOverlapsAfterTeleport 在桥区误判区外就立刻 UnLoad，绕过 Exit 宽限。

共享 bank：GrassLand_Audio 与 GrassLand_S_Audio 共用 Boluodao_Grassland，一侧 Exit 的 Stop/Unload 会把另一侧还在播的同一 emitter 掐掉再 Play。

切预设后更明显：MoveTo 落在桥 / 岸边界附近，揭幕后一走就踩中上述边界抖动。

另：河岸 / 近岸海若 停播还卸引擎资源，走回再解码全局恢复，也会刺啦——所以后来改成「只停播、常保留 bank」。

## 下载完 bank 贴河/瀑/火仍无声

Critical 默认 `loadIntoEngine=false`：只发 `OnBankLocalReady`，不灌引擎。旧逻辑要求「先 Retain 失败进 pending」才重评；`PruneUnwantedPendingRetries` 同帧把 pending 清掉后，listener-still 会站着永远跳过 Evaluate。

修法（`AmbienceZoneService.HandleAmbienceBankReadySignal`）：
- 目录环境音 / 已有 source / pending 任一命中 → seed `_pendingRetryBanks` + `InvalidateListenerStill` + `EvaluateNow`
- `_forceEvalUntil`（≈ `ExitGraceSeconds`）内禁止 still 跳过，且强制窗内不 Prune pending
- 篝火另需 `IsVisuallyIgnited`；未点燃时 LocalReady 重评也不会 Post

贴河/瀑/火 `want=true` 时另走 `EnsureProximityBankReady`：
- 已落盘未灌引擎 → 立刻 `WanosBankStore.Load`（不靠 Bootstrap `loadIntoEngine`）
- 未落盘 → `UserBankDownloadFlow` `prioritize+preempt` + `loadIntoEngine=true`（无进度窗）
- 湖↔瀑互斥：`PreferFallWithinMeters` 须 ≥ 大瀑 Enter（80）；过小会在瀑可听圈内被湖掐成「走到瀑布没声」
- 桥上河声：River 增益近场 0dB 平台拉到 `NearSurfaceMeters`（12），且近场 Post 跳过 SoftEnter −36dB

## 火边听不见篝火（play=1 / gain=0 仍无声）

真机日志典型：`campfire_extra_* want=1 play=1 gain=0dB v=1`，同帧瀑布也 `gain=0dB`；`hours=12` 排除夜间。

根因不是「没 Post」，而是：
1. `WaterfallCampfireMuteMeters=0`（产品要同听）→ 火边瀑满增益盖住篝火
2. 篝火未钉正午 TOD（河瀑已钉）→ 夜间更易「play=1 听不见」
3. 双 `campfire_extra_*` 同 bank 叠 Post；`Campfire` 须进 exclusive shared
4. Critical bank 先 Post 后 loaded → 静音 voice 无 Stopped 回调，`KeepSourcePlaying` 不再 Post

修法：
- `ShouldPinDaytimeTimeOfDay` 含 `Campfire`
- `AmbienceWaterSilencePolicy.WaterDuckDbNearCampfire`：贴点燃火压河/瀑/湖增益（硬静音圈仍 0）
- `IsExclusiveSharedPlayBank` 含 `Campfire`
- `HandleAmbienceBankReadySignal` 后 `HealPlayingPointSourcesForBank`
- 篝火近场 0dB 平台拉到 12m

贴岸 / 沿河走路另一种刺啦：已播时每 tick 把 Ocean/River 发射器硬跟到最近点，近场空间化拉链。
修法见 `AmbienceOceanPosePolicy`：听者离当前声道落点不足 8m 不改 pose，够远才跳；增益仍用 shell 距离。

沿河「一会儿大一会儿小」：河探针离散样点无 sticky/线段投影时，水平距离在 Voronoi 边界阶跃，
增益曲线跟着泵。修法见 `WaterSurfaceProbe.TryNearestShell` / `ResolveShell`（对齐海岸
`TryNearestOceanShore`：sticky + 邻点 XZ 投影；邻点半径 24m 覆盖疏采样）。

## 怎么解决的

本工程**没有** Boluo `WanosBankLoader` Trigger；等价落在 `AmbienceZoneService` 距离轮询 + `WanosBankStore` Retain/Release：

| 手段 | 作用 | 本工程落点 |
|------|------|------------|
| Enter/Exit 引用计数 | 仅 0→1 才 Load，1→0 才考虑 Unload | `WanosBankStore.Retain` / `Release`；`Retain` 取消 `PendingUnloads`，仍 Loaded 则只 bump refs |
| Exit 后 ~2s 宽限 | 期间 Stay/Enter 取消 Unload，保持已播 | `AmbienceZoneCatalog.ExitGraceSeconds` + `KeepPlayingWithExitGrace` |
| 近处 ~12m 几何判断 | 物理已空但人仍贴桥/岸 → 再等一轮，禁止 UnLoad | `NearSurfaceMeters`；Ocean 进 `EffectiveExitRadius` |
| 已播禁止再 Load() / 再 Post | 打断重播拉锯 | `KeepSourcePlaying`：`Playing && VoiceIds>0` 只推 pose/gain |
| 走路只补漏播，禁止立刻 UnLoad | 修掉走路误卸 | 水类 `ShouldKeepBankOnWalkStop`；草地 unload delay ≥2s |
| 其它 Loader 仍占同一 bank → 禁止 Stop/引擎 UnLoad | 修共享草地包 | `AnyOtherSourceHoldsBank` before `Release` |
| 预设瞬移仍走强制对齐 / 立刻 UnLoad | 瞬移路径单独处理，不跟走路混 | `ClearAllPlaying(forceRelease)` + iris `ContentAudioDuck`；揭幕 `RetriggerContentAtListener` |

切预设窗口本身还有另一套策略（抑制新加载、按落点保留、揭幕 ForceNearby 对齐一次），避免转场同帧抢播；**边缘刺啦 / 过桥断续的直接修法是上面这套宽限 + 引用计数，不是改音量曲线。**

## 禁止

- 用 `ContentAudioDuck` / 增益 slew 当过桥断续的根治（duck 只服务虹膜 Peak 硬切遮罩）。
- 把 `GrasslandUnloadDelaySeconds` 设回 `0`。
- `Retain` 时不取消 `PendingUnloads`（会 UnLoad↔Load 成对刺啦）。
- 走路路径与预设 `forceReleaseBank` 混用同一套立刻 UnLoad。

## Boluo 对照

`/Users/sika/Desktop/code/BoluoProject/UnityProject/Assets/WanosAudio/RunTime/Bank/WanosBankLoader.TriggerOverlap.cs`
（`MinTriggerExitGraceSeconds`、`NearTriggerMeters`、`ForceNearby`）。
