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
