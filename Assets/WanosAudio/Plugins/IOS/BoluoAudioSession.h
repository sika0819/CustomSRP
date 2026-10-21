#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/// 将 AVAudioSession 锁为 Playback（不碰输出设备）。
__attribute__((visibility("default")))
void BoluoAudioSession_LockPlayback(void);

/// 进岛轻量恢复：锁回 Playback、setActive、取消静音。
/// 会话 Disarmed（IsLogoutSilenced=1）时整段 no-op；开闸仅 ArmSessionForIslandPlayback。
__attribute__((visibility("default")))
void BoluoAudioSession_RestorePlayback(void);

/// HUD 就绪后延迟恢复（0.3s，避开揭幕帧）；Disarm 后 generation 变化则丢弃。
__attribute__((visibility("default")))
void BoluoAudioSession_RestorePlaybackAfterHudReady(void);

/// 会话 Disarm（退登/注销/强制下线）：关闸 + 停播清空 + Ambient/deactivate。
/// 登录页禁声只靠本门闩；勿复用 userManuallyPaused。
__attribute__((visibility("default")))
void BoluoAudioSession_SilenceForLogout(void);

/// 会话是否 Disarmed（1=是）。中断/Resume 自动恢复须查询，避免登录页回声。
__attribute__((visibility("default")))
int32_t BoluoAudioSession_IsLogoutSilenced(void);

/// 会话 Arm（进岛首播前唯一开闸）：清门闩 + 重置播放意图 + LockPlayback +（若退登过）Rebuild。
__attribute__((visibility("default")))
void BoluoAudioSession_ArmSessionForIslandPlayback(void);

/// 兼容旧符号：不再清闸（no-op）。请改用 ArmSessionForIslandPlayback。
__attribute__((visibility("default")))
void BoluoAudioSession_PrepareAfterLogin(void);

/// 当前是否为健康的 Playback 类别。1=是，0=否。
__attribute__((visibility("default")))
int32_t BoluoAudioSession_IsHealthy(void);

/// 宿主场景前台态（由本模块 UIApplication 观察者维护；亦可外部写入兼容）。
/// 与 key window 无关；默认 0，DidBecomeActive 置 1。
/// 置 1 且非 logout-silenced 时顺带 setActive(YES)。
__attribute__((visibility("default")))
void BoluoAudioSession_SetHostForeground(int32_t active);

/// 宿主场景是否前台。1=是，0=否。供 Wanos C# 门控，替代 Application.isFocused。
__attribute__((visibility("default")))
int32_t BoluoAudioSession_IsHostForeground(void);

/// C# / 原生共用：追加一行到 Documents/boluo-audio-trail.log。
/// 每行带统一过滤字段 `[BoluoAudioDbg]` + 时间戳；msg 勿含换行。
__attribute__((visibility("default")))
void BoluoAudioSession_AppendTrail(const char *msg);

#ifdef __cplusplus
}
#endif
