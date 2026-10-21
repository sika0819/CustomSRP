# WanosAudio（WIAEngine 封装）

`namespace WanosAudio`，程序集 `WanosAudio`（`Runtime/WanosAudio.asmdef`）。
GameMain 侧通过 `WanosEngine` 静态门面调用（`Game.asmdef` 已引用 `WanosAudio`）。

硬规则见 [`.cursor/rules/wanos-audio.mdc`](../../.cursor/rules/wanos-audio.mdc)。

## 结构

| 目录 | 内容 |
|------|------|
| `Runtime/Native/` | `WanosNative.cs`：P/Invoke 裸通道（WIAEngine 动态库），`WanosNativeTypes.cs`：`WanosResult` 等 |
| `Runtime/Engine/` | `WanosEngine.cs`：生命周期门面（Load → EnablePlayback → Unload）、音量 / 全局 RTPC 写入 |
| `Runtime/Interaction/`、`Runtime/Bank/` | `WanosInteraction`（GameObject / 3D 音源）、`WanosBankStore`（bank 加载） |
| `Runtime/Platform/` | 平台胶水：Android 桥（`BoluoAndroidEngineNative`、前台服务、iOS） |
| `Plugins/Android/` | Android 原生 Java：`WanosMusicPlayerNative`（AudioFocus、前台播放服务、通知栏） |
| `Editor/` | `WanosAudioEditorLifecycle`：退出 Play Mode / 退出 Editor 时强制 `Unload` |

## 音量：唯一的两个 RTPC

| 用途 | RTPC 名（常量） | 引擎量程 | UI 量纲 | legacy 来源 |
|------|-----------------|----------|---------|-------------|
| 背景音乐 | `WanosEngine.MusicVolumeParam` = `Music_Volume` | 0–100 | 0–1 | `GameData.bgmVolume`（默认 80） |
| 音效 | `WanosEngine.FxVolumeParam` = `Fx_Volume` | 0–100 | 0–1 | `GameData.wFX` |

写音量只有一条路：

```csharp
WanosEngine.SetVolumeParameter(WanosEngine.MusicVolumeParam, uiValue01); // 内部 Clamp01 → ×100
```

非音量 RTPC（天气等）走 `WanosEngine.SetGlobalParameter(name, value)`，量纲自行定义（天气是 0/30/60/90）。

### 两次真实踩坑

**1. 名字写错 → 引擎静默丢弃。**
写成了 `SFX_Volume`，正确是 `Fx_Volume`。引擎对不认识的 RTPC 名既不报错也不打日志，
表现为「UI 音量条拖了没反应 / 音量恒为默认值」，极难查。
现在 `WanosEngine.SetVolumeParameter` 有名字白名单，`WanosNative.SetParameter` 有兜底报错。

**2. 量纲搞错 → 有播放但听不到。**
引擎 RTPC 量程是 0–100，UI 是 0–1。把 0.8 直接透传过去 = 0.8% 音量，
声音小到听不见，但「在播」的状态一切正常。
现在 `WanosNative.SetParameter` 会在收到 0–1 量纲的音量值时直接 `LogError`。

## 起播路径：Unity 直发 PostEvent 的 Android 补丁

`MusicPlaylist.PlayTrack` → `GameEntry.Sound.PlaySound` → Unity 侧直接 `PostEvent`。
这条路径**不经过** Android 原生的 `nativePlayTrackAt`，所以原生那道门禁不会自动执行：

- 原生 `nativePlayTrackAt` 会做：抢 AudioFocus → unmute → 起前台播放服务。
- Unity 直发不做以上任何一步 → 真机上「有 PostEvent、有播放状态，但没声」。

所以 Android 上 Unity 起播后必须补：

```csharp
WanosEngine.Unmute("music-play");
PushVolumesToEngine();                                      // PlaySound 期间 agent 可能盖过 RTPC
BoluoAndroidEngineNative.EnsureAudibleForUnityPlayback();   // AudioFocus + unmute + 前台服务
```

对应 Java 实现：`WanosMusicPlayerNative.ensureAudibleForUnityPlayback()`。

## 排查「有播放但听不到」

| 现象 | 检查点 |
|------|--------|
| 日志里没有 `SetVolumeParameter` | 音量压根没推；查 `MusicPlaylist.PushVolumesToEngine` / `WanosSoundAgentHelper.ApplyVolumeToEngine` |
| `SetVolumeParameter ... = 0.8` | 量纲错（0–1 直传）；应看到 `= 80.0 (ui=0.800)` |
| 值是 80 但音量没变化 | RTPC 名写错（如 `SFX_Volume`），引擎静默丢弃 |
| 值对、`PostEvent` 有日志、真机没声 | Android AudioFocus / mute 影子态；补 `EnsureAudibleForUnityPlayback()` |
| `PlaybackState != Started` | `WanosEngine.IsLoaded` / `IsPlaybackEnabled`；`EnablePlayback` 之前 `PostEvent` 会被拦（仅控制类事件例外） |
| Editor 里正常、真机不行 | AudioFocus / 前台服务是 Android 特有，Editor 模拟不出来，必须真机验证 |

## 静音与生命周期

- `WanosEngine.KeepSilent(reason)`：`Pause_ALL` + 引擎 mute（不是停止）。
- `WanosEngine.Unmute(reason)`：解除引擎 mute。移动端引擎默认 mute 态。
- `WanosEngine.RestoreForeground(reason)`：解 mute + `ReapplyVolumes`（回前台必走；RTPC 可能被重置）。
- `Load` → `EnablePlayback` → `Unload`；`EnablePlayback` 之后业务 `PostEvent` 才放行。
- Editor 退出 Play Mode / 退出 Editor：`WanosAudioEditorLifecycle` 会强制 `Unload`。

## 后台传输（Android）

Unity 进后台会被 pause，负一屏控播必须由原生直接驱动引擎。

| 层 | 文件 | 职责 |
|---|---|---|
| GameMain | `BackgroundTransportSync` | 前台推计划 / 回前台对账 / 退岛清计划 |
| WanosAudio C# | `AndroidBackgroundTransport` | JNI：`SetPlan` / `ClearPlan` / `GetStateJson` |
| Java | `WanosMusicPlayerNative` | `handleTransportInBackground` / `handleTransportSkipInBackground` |

完整数据流与硬规则见 [`Assets/GameMain/Docs/05-audio.md`](../GameMain/Docs/05-audio.md#后台传输计划android-负一屏)。
