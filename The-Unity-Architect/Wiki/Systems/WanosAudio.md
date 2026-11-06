# 系统 — WanosAudio + 游戏侧音频编排

**引擎：** `Assets/WanosAudio/`（`namespace WanosAudio`）  
**游戏编排：** `Assets/GameMain/Services/Audio/` + `MusicPlaylist` / `NowPlayingService`  
**详解：** [`05-audio.md`](../../../Assets/GameMain/Docs/05-audio.md) · rules `wanos-audio.mdc` · skill `preset-bridge-ambience-stutter` · ADR [0006](../ADR/0006-wanos-volume-rtpc-scale.md)

## 分层

| 层 | 负责 |
|----|------|
| WanosAudio | 原生插件、Bank 仓库、引擎 / Interaction 门面、平台音乐播放器 |
| GameMain Audio | 区域、天气桥、雨水面靠近、播放门控、静音恢复、闪避、歌单 / 正在播放 |

## 高回归话题

- 环境音 Bank 的 Load/Unload：**引用计数 + 退出宽限**（过桥 / 岸边刺啦的根因）。
- Uncover 门控 —— 音频设备未就绪不要 Post。
- 平台 Now Playing / 远程按键（`BoluoNowPlayingRemoteCommandPolicy`，Android/iOS 播放器）。
- 音量刻度不一致（UI 0–1 vs RTPC 0–100）。

## 相关服务

`AmbienceZoneService`、`WeatherAudioBridge`、`RainWaterProximityService`、`PlaybackGate`、`SilenceResumePolicy`、`MusicPlaylist`、`NowPlayingService`、`AudioInteractionBootstrap`、`AudioAppLifecycle`。
