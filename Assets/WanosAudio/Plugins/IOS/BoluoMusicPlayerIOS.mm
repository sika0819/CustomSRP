#import <Foundation/Foundation.h>
#import <AVFoundation/AVFoundation.h>
#import <UIKit/UIKit.h>
#import <MediaPlayer/MediaPlayer.h>
#import <QuartzCore/QuartzCore.h>

#ifdef DEBUG
#define BoluoAudioLog(...) NSLog(__VA_ARGS__)
#else
#define BoluoAudioLog(...) do {} while(0)
#endif

/// 播放器会话阶段（轴 1）：Stopped / Arming / AwaitingFirstPlay / Engaged。
typedef NS_ENUM(NSInteger, BoluoPlayerSessionPhase) {
    BoluoPlayerSessionPhaseStopped = 0,
    BoluoPlayerSessionPhaseArming = 1,
    BoluoPlayerSessionPhaseAwaitingFirstPlay = 2,
    BoluoPlayerSessionPhaseEngaged = 3,
};

/// 恢复阶段（轴 2）：替代 interruption / competing / duck 等多布尔组合。
typedef NS_ENUM(NSInteger, BoluoPlayerRecoveryPhase) {
    BoluoPlayerRecoveryPhaseNone = 0,
    BoluoPlayerRecoveryPhaseDucked = 1,
    BoluoPlayerRecoveryPhaseInterruptionActive = 2,
    BoluoPlayerRecoveryPhaseRecoveringDevice = 3,
    BoluoPlayerRecoveryPhaseAwaitingCompetingStop = 4,
};

typedef NS_ENUM(NSInteger, BoluoUnmuteVerdict) {
    BoluoUnmuteVerdictAllow = 0,
    BoluoUnmuteVerdictBlockedDisarmed = 1,
    BoluoUnmuteVerdictBlockedUserPaused = 2,
    BoluoUnmuteVerdictDeferDeviceBusy = 3,
    BoluoUnmuteVerdictDeferRecovery = 4,
};

/// C# / 原生统一 unmute 原因（PR2 RequestUnmute）。
typedef NS_ENUM(NSInteger, BoluoUnmuteReason) {
    BoluoUnmuteReasonFirstPlay = 0,
    BoluoUnmuteReasonForegroundResume = 1,
    BoluoUnmuteReasonExternalResume = 2,
};

static NSString *BoluoPlayerSessionPhaseName(BoluoPlayerSessionPhase phase) {
    switch (phase) {
        case BoluoPlayerSessionPhaseStopped: return @"Stopped";
        case BoluoPlayerSessionPhaseArming: return @"Arming";
        case BoluoPlayerSessionPhaseAwaitingFirstPlay: return @"AwaitingFirstPlay";
        case BoluoPlayerSessionPhaseEngaged: return @"Engaged";
        default: return [NSString stringWithFormat:@"Unknown(%ld)", (long)phase];
    }
}

static NSString *BoluoPlayerRecoveryPhaseName(BoluoPlayerRecoveryPhase phase) {
    switch (phase) {
        case BoluoPlayerRecoveryPhaseNone: return @"None";
        case BoluoPlayerRecoveryPhaseDucked: return @"Ducked";
        case BoluoPlayerRecoveryPhaseInterruptionActive: return @"InterruptionActive";
        case BoluoPlayerRecoveryPhaseRecoveringDevice: return @"RecoveringDevice";
        case BoluoPlayerRecoveryPhaseAwaitingCompetingStop: return @"AwaitingCompetingStop";
        default: return [NSString stringWithFormat:@"Unknown(%ld)", (long)phase];
    }
}

static NSString *BoluoUnmuteVerdictName(BoluoUnmuteVerdict v) {
    switch (v) {
        case BoluoUnmuteVerdictAllow: return @"Allow";
        case BoluoUnmuteVerdictBlockedDisarmed: return @"BlockedDisarmed";
        case BoluoUnmuteVerdictBlockedUserPaused: return @"BlockedUserPaused";
        case BoluoUnmuteVerdictDeferDeviceBusy: return @"DeferDeviceBusy";
        case BoluoUnmuteVerdictDeferRecovery: return @"DeferRecovery";
        default: return [NSString stringWithFormat:@"Unknown(%ld)", (long)v];
    }
}

/// 与 BoluoAudioSession 同文件；统一过滤字段 `[BoluoAudioDbg]`。
static void BoluoMusicPlayer_Trail(NSString *fmt, ...) {
    va_list args;
    va_start(args, fmt);
    NSString *msg = [[NSString alloc] initWithFormat:fmt arguments:args];
    va_end(args);
    BoluoAudioLog(@"[BoluoAudioDbg] %@", msg);
    NSString *docs = NSSearchPathForDirectoriesInDomains(
        NSDocumentDirectory, NSUserDomainMask, YES).firstObject;
    if (docs.length == 0) {
        return;
    }
    NSString *path = [docs stringByAppendingPathComponent:@"boluo-audio-trail.log"];
    NSDateFormatter *df = [[NSDateFormatter alloc] init];
    df.locale = [NSLocale localeWithLocaleIdentifier:@"en_US_POSIX"];
    df.dateFormat = @"HH:mm:ss.SSS";
    NSString *line = [NSString stringWithFormat:@"%@ [BoluoAudioDbg] %@\n",
                      [df stringFromDate:[NSDate date]], msg];
    NSFileHandle *fh = [NSFileHandle fileHandleForWritingAtPath:path];
    if (fh == nil) {
        [line writeToFile:path atomically:YES encoding:NSUTF8StringEncoding error:nil];
        return;
    }
    [fh seekToEndOfFile];
    NSData *data = [line dataUsingEncoding:NSUTF8StringEncoding];
    if (data != nil) {
        [fh writeData:data];
    }
    [fh closeFile];
}

/// RD 软恢复：抖动窗与 settle（秒）
static const NSTimeInterval kBoluoRDFlapWindowSeconds = 90.0;
static const NSTimeInterval kBoluoRDSettleDelayFirstSeconds = 0.6;
static const NSTimeInterval kBoluoRDSettleDelayFlapSeconds = 1.2;
static const NSTimeInterval kBoluoRDSoftEscalateDelaySeconds = 0.4;
static const NSTimeInterval kBoluoDuckEscalateDelaySeconds = 2.5;
#ifdef __cplusplus
extern "C" {
#endif

typedef int Result;

Result ResetPlaybackDevice(void);
Result SetPlaybackDevice(const char* playbackInfoJson);
void SetMute(bool mute);
int GetPlaybackState(void);
int32_t BoluoAudioSession_IsLogoutSilenced(void);
int32_t BoluoAudioSession_AllowUnmuteNow(void);
int32_t BoluoAudioSession_IsStartupAudibleCommitted(void);
void BoluoAudioSession_NoteStartupAudibleCommitted(void);
/// Wanos 引擎对象级增益（与 Unity WIAEngine.cs 的 DllImport 指向同一 C 符号）。
/// 护栏静音/恢复爬坡在原生同步调用，一个渲染周期内生效，不依赖 Unity SendMessage 异步。
void SetGameObjAttenuationGain(unsigned long long obj_id, float gain);
void setGameObjAttenuationGainEnable(unsigned long long obj_id, bool enable);

void WanosMusicPlayer_PostGlobalPauseMusic(void);
void WanosMusicPlayer_PostGlobalResumeMusic(void);
/// 当前音乐播放对象 id（WanosMusicPlayer.mm 于 NativeConfigure 缓存；0=尚未配置过播放）。
unsigned long long WanosMusicPlayer_GetConfiguredObjectId(void);

/// 前向声明：实现在 @implementation 之后，供 BoluoSetMuteWithMusicEventsNative 异步回调使用。
static void BoluoLogAudioResumeSnapshot(NSString *tag);
/// 原生音频关键路径墙钟 ≥100ms 时抛通知给壳进程 → BLS（wanos_audio_jank）。
static void BoluoNativeAudioJank_Report(NSString *phase, CFTimeInterval durationMs, NSString *detail);
/// 供顶层 C 函数查询（BoluoSetMute 定义在 @interface 之前）。
static int BoluoAudio_IsPlaybackDeviceRecoveryInFlight(void);
/// ownerEpoch：本次 unmute 决策的 Focus Epoch；窗口内出现更新决策则整链作废（防 unmute 压过更新的 mute）。
static void BoluoTryDelayedUnmute(NSInteger ownerEpoch);

/// 静音/恢复错序与 Post 事件；Unity DllImport 与本文件内 ObjC 共用此唯一实现，避免与 C# 重复调用。
static const NSTimeInterval kBoluoMuteStaggerDelaySeconds = 0.08;
/// 护栏静音：系统触发的 SetMute 前，先经引擎 C API 同步把音乐对象增益压到近静音，
/// 短缓冲后再真正 SetMute。取代旧 Unity soft-silence(0.2s)——SendMessage 异步偶发不生效
/// 仍电流声，且 0.2s 窗口内音乐可闻（微信语音「不静音」）。
/// 对齐 C# BoluoAudioFocusLossBranchPolicy.NativeGuardedMuteDelaySeconds / SoftSilenceDuckFactor。
static const NSTimeInterval kBoluoGuardedMuteDelaySeconds = 0.08;
static const float kBoluoGuardedMuteGainFactor = 0.0f;
/// unmute 爬坡：SetMute(false) 前增益压到 0.05，之后 0.4 → 1.0 两步回满，
/// 消除「静音→满音量」阶跃爆音（回前台恢复偶现电流声）。
static const NSTimeInterval kBoluoUnmuteRampStepSeconds = 0.05;
static const float kBoluoUnmuteRampMidGainFactor = 0.25f;
static const float kBoluoUnmuteRampHighGainFactor = 0.55f;
/// ResignActive 兜底复查延迟：微信发语音偶现收不到 SilenceSecondary BEGIN / Began
/// （或记账漂移）→ 音乐不静音、负一屏被对方接管却仍出声。
static const NSTimeInterval kBoluoResignActiveSecondaryBackstopDelaySeconds = 1.0;
/// transient duck 增益（对齐 C# BoluoAudioDuckController.DefaultDuckFactor；iOS 焦点增益已原生化）。
static const float kBoluoTransientDuckGainFactor = 0.4f;

/// 是否曾对引擎真正执行过 SetMute(true)。
/// 冷启动记账 isMuted=YES 只是桥接初始值，引擎未必硬静音；若对已在播流做
/// gain→0 + SetMute(false) 爬坡，会先闷一下再爬满 = 启动电流音。
static BOOL sBoluoEngineHardMuted = NO;
/// 护栏已 dip、尚未落地 SetMute(true) 的窗口。unmute NOOP 若在此窗内把增益拉回 1.0，
/// 护栏仍会落地硬 mute → 满能量电流音（微信发语音 / 快切）。
static BOOL sBoluoGuardedMuteInFlight = NO;
/// delayed unmute 真正 SetMute(false) 后同步桥接 isMuted（定义在 @implementation 之后）。
static void BoluoNotifyDelayedUnmuteLanded(void);

#pragma mark - Focus Epoch（统一决策世代）

/// 统一焦点决策世代：任何「音频效果决策」落地（真实 mute/unmute、护栏静音、unmute 爬坡、
/// transient duck 开始、回前台清理）都自增一次，作废此前调度的全部一次性延迟效果回调：
///   护栏 mute 落地、爬坡步进、mute/unmute 0.08s 错峰事件、ResignActive 兜底复查、
///   RD settle 恢复 block、中断恢复激活 block、RD 软恢复升级检查、duck escalate、
///   setAudioMute unmute 设备工作完成后的 resume 尾段。
/// 取代旧 sBoluoSystemMuteGuardGeneration（只挡护栏链）与 sBoluoResignActiveBackstopToken
///（局部变体）——不再逐处发明局部 token。
/// 【不纳入 epoch 的定时器】（对齐 C# BoluoAudioFocusLossBranchPolicy epoch 不变量单测）：
///   - 看门狗轮询：_pollWaitForCompetingAudioToStop / _pollResumeWhenDeviceIdleAfterSystemMute
///     —— 每次执行都重读当前条件、自带 cancelPrevious + 计数生命周期；被 mute 类决策杀掉
///     反而造成「对方停了也永不恢复」类回归。
///   - exclusive reclaim 重试：入口自检 userPaused/interruptionActive/竞争条件。
///   - 纯 UI/诊断：NowPlaying 补写、_debugDumpSystemNowPlayingInfo。
///   - 设备层：蓝牙 route fallback Reset、session-work 队列内部工作。
static NSInteger sBoluoFocusEpoch = 0;

static NSInteger BoluoFocusEpochCurrent(void) {
    return sBoluoFocusEpoch;
}

/// 决策落地时调用；返回新世代值供本决策随后调度的回调持有。仅主线程调用。
static NSInteger BoluoFocusEpochBump(NSString *reason) {
    NSInteger next = ++sBoluoFocusEpoch;
    BoluoAudioLog(@"[BoluoAudioEpoch] bump→%ld reason=%@", (long)next, reason ?: @"?");
    return next;
}

#pragma mark - 唯一增益写入者（iOS）

/// 上一次经写入者写过衰减的对象 id；切歌/重建 player 后旧对象可能仍停在衰减增益。
static unsigned long long sBoluoGainWriterLastObjId = 0;

/// 唯一增益写入者：transient duck(0.4) / 护栏静音(0) / unmute 爬坡 / 还原(1.0) 全部经此函数
/// 写引擎对象增益，保证「先降增益再 mute / 先 unmute 再回增益」顺序不变量只有一个执行入口。
/// 写新对象前自动把旧对象还原（取代 C# BoluoAudioDuckController._duckedObjId 记账；iOS 侧
/// C# 不再写焦点增益，OnAudioFocusDuck/OnAudioFocusUnduck 消息已退役）。仅主线程调用。
static void BoluoApplyMusicGain(float gain, bool enable, NSString *reason) {
    unsigned long long objId = WanosMusicPlayer_GetConfiguredObjectId();
    if (sBoluoGainWriterLastObjId != 0 && sBoluoGainWriterLastObjId != objId) {
        SetGameObjAttenuationGain(sBoluoGainWriterLastObjId, 1.0f);
        setGameObjAttenuationGainEnable(sBoluoGainWriterLastObjId, false);
    }
    if (objId == 0) {
        // 尚未配置过音乐播放：无对象可写（调用方自行退化，如护栏退化为纯延迟 mute）
        sBoluoGainWriterLastObjId = 0;
        return;
    }
    if (enable) {
        setGameObjAttenuationGainEnable(objId, true);
        SetGameObjAttenuationGain(objId, gain);
        sBoluoGainWriterLastObjId = objId;
    } else {
        SetGameObjAttenuationGain(objId, gain);
        setGameObjAttenuationGainEnable(objId, false);
        sBoluoGainWriterLastObjId = 0;
    }
    BoluoAudioLog(@"[BoluoAudioDuck] gain-writer objId=%llu gain=%.2f enable=%d reason=%@",
          objId, gain, enable ? 1 : 0, reason ?: @"?");
}

/// AppWasSuspended：系统挂起打断，不是竞争音源。Began 须整段 no-op（对齐
/// BoluoAudioFocusLossBranchPolicy.DecideInterruptionBeganBranch → None）。
static BOOL BoluoNotificationIsAppWasSuspended(NSNotification *notification) {
    if (@available(iOS 14.5, *)) {
        NSNumber *reasonValue = notification.userInfo[AVAudioSessionInterruptionReasonKey];
        if (reasonValue != nil
            && (AVAudioSessionInterruptionReason)[reasonValue unsignedIntegerValue]
                == AVAudioSessionInterruptionReasonAppWasSuspended) {
            return YES;
        }
    }
    return NO;
}

/// 通知栈内同步压增益：赶在 MiniAudio <c>ma_device_stop</c> 之前。
/// 禁止在此调用 SetMute / Reset / setActive（与 MiniAudio 同栈 stop 会死锁）。
static void BoluoSyncPreDipForSystemInterruption(NSString *reason) {
    BoluoApplyMusicGain(kBoluoGuardedMuteGainFactor, true, reason ?: @"pre_dip");
}

/// dylib 加载即注册，早于引擎 device start 时的 MiniAudio observer，保证 pre-dip 先执行。
__attribute__((constructor))
static void BoluoInstallEarlyInterruptionPreDipObserver(void) {
    NSNotificationCenter *nc = [NSNotificationCenter defaultCenter];
    [nc addObserverForName:AVAudioSessionInterruptionNotification
                    object:nil
                     queue:nil
                usingBlock:^(NSNotification *note) {
        NSNumber *typeValue = note.userInfo[AVAudioSessionInterruptionTypeKey];
        if ((AVAudioSessionInterruptionType)[typeValue unsignedIntegerValue]
            != AVAudioSessionInterruptionTypeBegan) {
            return;
        }
        if (BoluoNotificationIsAppWasSuspended(note)) {
            return;
        }
        BoluoSyncPreDipForSystemInterruption(@"interruption_began_pre_dip");
    }];
    [nc addObserverForName:AVAudioSessionSilenceSecondaryAudioHintNotification
                    object:nil
                     queue:nil
                usingBlock:^(NSNotification *note) {
        NSNumber *typeValue = note.userInfo[AVAudioSessionSilenceSecondaryAudioHintTypeKey];
        if ((AVAudioSessionSilenceSecondaryAudioHintType)[typeValue unsignedIntegerValue]
            != AVAudioSessionSilenceSecondaryAudioHintTypeBegin) {
            return;
        }
        BoluoSyncPreDipForSystemInterruption(@"silence_secondary_pre_dip");
    }];
}

/// transient duck escalate：对方 otherAudio 常晚到；一次 skip 后须重试（对齐 C# TransientDuckEscalateMaxAttempts）。
static const NSTimeInterval kBoluoTransientDuckEscalateDelaySeconds = 2.5;
static const NSInteger kBoluoTransientDuckEscalateMaxAttempts = 8;
/// QA 手动注入用间隔；默认不自动开启，见 BoluoMusicPlayerIOS_ForceBluetoothRdStutterRepro。
static const NSTimeInterval kBoluoForceRdStutterIntervalSeconds = 3.0;

#pragma mark - 焦点单一状态枚举（架构治理 Phase 2）

/// 对齐 C# BoluoAudioFocusLossBranchPolicy.BoluoAudioFocusState（数值一致；
/// 转移表以 C# DecideNextFocusState 纯函数为规范源，EditMode 单测锁定）。
/// 决策点统一经 noteFocusState:reason: 落状态：单一命名状态成为日志/BLS/对账的真值来源；
/// pendingResume/awaiting/defer 等旧 BOOL 的删除按真机矩阵验证分批推进（见 policy 注释）。
typedef NS_ENUM(NSInteger, BoluoAudioFocusStateValue) {
    BoluoAudioFocusStateNormal = 0,
    BoluoAudioFocusStateTransientDuck = 1,
    BoluoAudioFocusStateGuardedMuting = 2,
    BoluoAudioFocusStateCompetingWait = 3,
    BoluoAudioFocusStateRdRecovering = 4,
    BoluoAudioFocusStateUserPaused = 5,
};

static NSString *BoluoFocusStateName(BoluoAudioFocusStateValue state) {
    switch (state) {
        case BoluoAudioFocusStateNormal: return @"Normal";
        case BoluoAudioFocusStateTransientDuck: return @"TransientDuck";
        case BoluoAudioFocusStateGuardedMuting: return @"GuardedMuting";
        case BoluoAudioFocusStateCompetingWait: return @"CompetingWait";
        case BoluoAudioFocusStateRdRecovering: return @"RdRecovering";
        case BoluoAudioFocusStateUserPaused: return @"UserPaused";
    }
    return [NSString stringWithFormat:@"Unknown(%ld)", (long)state];
}

/// 该状态下引擎必须静默（对齐 C# StateRequiresEngineSilence；对账自愈方向判定）。
static BOOL BoluoFocusStateRequiresEngineSilence(BoluoAudioFocusStateValue state) {
    return state == BoluoAudioFocusStateGuardedMuting
        || state == BoluoAudioFocusStateCompetingWait
        || state == BoluoAudioFocusStateRdRecovering
        || state == BoluoAudioFocusStateUserPaused;
}

/// 状态转移后对账延迟（秒）；对齐 C# FocusTruthAuditDelaySeconds。
static const NSTimeInterval kBoluoFocusTruthAuditDelaySeconds = 0.25;

static NSString *BoluoPlaybackStateName(int state) {
    switch (state) {
        case 0: return @"Uninitialized";
        case 1: return @"Stopped";
        case 2: return @"Started";
        case 3: return @"Starting";
        case 4: return @"Stopping";
        default: return [NSString stringWithFormat:@"Unknown(%d)", state];
    }
}

static void BoluoTryDelayedUnmute(NSInteger ownerEpoch) {
    // 统一 epoch：DEFER 等待期间出现更新的决策（如护栏静音）→ 本次 unmute 整链作废，
    // 由新 owner 收敛（修复「延迟 unmute 压过更新的 mute」错序）。
    if (ownerEpoch != BoluoFocusEpochCurrent()) {
        BoluoAudioLog(@"[BoluoAudioResume] BoluoSetMute unmute SKIP (superseded epoch=%ld now=%ld)",
              (long)ownerEpoch, (long)BoluoFocusEpochCurrent());
        return;
    }
    if (BoluoAudioSession_IsLogoutSilenced() != 0) {
        BoluoAudioLog(@"[BoluoAudioResume] BoluoSetMute unmute BLOCKED logoutSilenced");
        BoluoMusicPlayer_Trail(@"TryDelayedUnmute BLOCKED logoutSilenced");
        return;
    }
    // 设备队列 Reset/Set 进行中时禁止主线程 SetMute(false)，否则与 MiniAudio 争锁卡死。
    if (BoluoAudio_IsPlaybackDeviceRecoveryInFlight()) {
        BoluoAudioLog(@"[BoluoAudioResume] BoluoSetMute unmute DEFER (playbackDeviceRecoveryInFlight)");
        BoluoMusicPlayer_Trail(@"TryDelayedUnmute DEFER inFlight — reschedule");
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(kBoluoMuteStaggerDelaySeconds * NSEC_PER_SEC)),
                       dispatch_get_main_queue(), ^{
            BoluoTryDelayedUnmute(ownerEpoch);
        });
        return;
    }
    // unmute 爬坡护栏：先把音乐对象增益压到 0 再 SetMute(false)，随后 ~0.15s 内三步回满。
    unsigned long long guardObjId = WanosMusicPlayer_GetConfiguredObjectId();
    if (guardObjId != 0) {
        BoluoApplyMusicGain(kBoluoGuardedMuteGainFactor, true, @"unmute_ramp_dip");
    }
    CFTimeInterval t0 = CACurrentMediaTime();
    SetMute(false);
    BoluoNativeAudioJank_Report(@"native_set_mute", (CACurrentMediaTime() - t0) * 1000.0, @"unmute");
    sBoluoEngineHardMuted = NO;
    // SetMute(false) 之后再 Resume_Music，避免仍硬 mute 时事件抢恢复造成听感毛刺
    WanosMusicPlayer_PostGlobalResumeMusic();
    BoluoNotifyDelayedUnmuteLanded();
    int stateDone = GetPlaybackState();
    BoluoAudioLog(@"[BoluoAudioResume] BoluoSetMute DONE(mute=NO) SetMute(false) playbackState=%@ rampObjId=%llu",
          BoluoPlaybackStateName(stateDone), guardObjId);
    BoluoLogAudioResumeSnapshot(@"BoluoSetMute-unmute-done");
    if (guardObjId != 0) {
        NSInteger gen = BoluoFocusEpochBump(@"unmute_ramp");
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(kBoluoUnmuteRampStepSeconds * NSEC_PER_SEC)),
                       dispatch_get_main_queue(), ^{
            if (gen != BoluoFocusEpochCurrent()) return;
            BoluoApplyMusicGain(kBoluoUnmuteRampMidGainFactor, true, @"unmute_ramp_mid");
        });
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(kBoluoUnmuteRampStepSeconds * 2.0 * NSEC_PER_SEC)),
                       dispatch_get_main_queue(), ^{
            if (gen != BoluoFocusEpochCurrent()) return;
            BoluoApplyMusicGain(kBoluoUnmuteRampHighGainFactor, true, @"unmute_ramp_high");
        });
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(kBoluoUnmuteRampStepSeconds * 3.0 * NSEC_PER_SEC)),
                       dispatch_get_main_queue(), ^{
            if (gen != BoluoFocusEpochCurrent()) return;
            BoluoApplyMusicGain(1.0f, false, @"unmute_ramp_done");
        });
    }
}
void BoluoSetMuteWithMusicEventsNative(bool mute) {
    int stateEnter = GetPlaybackState();
    BoluoAudioLog(@"[BoluoAudioResume] BoluoSetMute ENTER mute=%s playbackState=%@ hardMuted=%d",
          mute ? "YES" : "NO", BoluoPlaybackStateName(stateEnter), sBoluoEngineHardMuted ? 1 : 0);

    // 从未硬 mute：禁止 dip/爬坡（冷启动记账 isMuted=YES）。
    // 【关键】禁止 bump Focus Epoch：否则会作废窗口内正在进行的 guarded mute。
    // 【关键】护栏 inFlight 时禁止把增益拉回 1.0：否则 80ms 后满能量硬 SetMute = 发语音电流音。
    if (!mute && !sBoluoEngineHardMuted) {
        if (sBoluoGuardedMuteInFlight) {
            BoluoAudioLog(@"[BoluoAudioResume] BoluoSetMute unmute NOOP (never hard-muted, keep dipped while guard in-flight, no epoch bump)");
        } else {
            BoluoApplyMusicGain(1.0f, false, @"unmute_skip_ramp_never_hard_muted");
            BoluoAudioLog(@"[BoluoAudioResume] BoluoSetMute unmute NOOP (never hard-muted, skip dip/ramp, no epoch bump)");
        }
        BoluoLogAudioResumeSnapshot(@"BoluoSetMute-unmute-noop");
        return;
    }

    NSInteger epoch = BoluoFocusEpochBump(mute ? @"native_mute_events" : @"native_unmute_events");
    if (mute) {
        CFTimeInterval t0 = CACurrentMediaTime();
        SetMute(true);
        sBoluoEngineHardMuted = YES;
        BoluoNativeAudioJank_Report(@"native_set_mute", (CACurrentMediaTime() - t0) * 1000.0, @"mute");
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(kBoluoMuteStaggerDelaySeconds * NSEC_PER_SEC)),
                       dispatch_get_main_queue(), ^{
            if (epoch != BoluoFocusEpochCurrent()) {
                BoluoAudioLog(@"[BoluoAudioResume] BoluoSetMute mute-events SKIP (superseded)");
                return;
            }
            WanosMusicPlayer_PostGlobalPauseMusic();
            int stateDone = GetPlaybackState();
            BoluoAudioLog(@"[BoluoAudioResume] BoluoSetMute DONE(mute=YES) PostPauseMusic playbackState=%@",
                  BoluoPlaybackStateName(stateDone));
            BoluoMusicPlayer_Trail(@"SetMuteEvents DONE mute=1 st=%@", BoluoPlaybackStateName(stateDone));
            BoluoLogAudioResumeSnapshot(@"BoluoSetMute-mute-done");
        });
        return;
    }

    if (BoluoAudioSession_IsLogoutSilenced() != 0) {
        BoluoAudioLog(@"[BoluoAudioResume] BoluoSetMute unmute ENTER BLOCKED logoutSilenced");
        BoluoMusicPlayer_Trail(@"SetMuteEvents unmute BLOCKED logoutSilenced");
        return;
    }

    // 已硬 mute：先延迟再 unmute；Resume_Music 放到 SetMute(false) 之后，避免仍 mute 时抢恢复事件。
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(kBoluoMuteStaggerDelaySeconds * NSEC_PER_SEC)),
                   dispatch_get_main_queue(), ^{
        BoluoTryDelayedUnmute(epoch);
    });
}

#ifdef __cplusplus
}
#endif

static const char * const kBoluoDefaultDeviceJson = "{\"play_layout\":0,\"device_id\":\"Default\"}";

/// 蓝牙断开/重连等路由切换期间，AVAudioSession 的 setActive/setCategory 与 Wanos 的
/// ResetPlaybackDevice/SetPlaybackDevice 都是同步阻塞的 CoreAudio 调用（可达 1 秒级）。
/// 此前它们全部跑在主队列上，Unity 主线程在帧间处理 runloop 时被卡住，
/// Profiler 表现为 WaitForTargetFPS 单帧 1000ms+ 的尖刺（用户反馈的蓝牙重连大卡顿）。
/// 统一挪到本串行队列执行：不阻塞渲染，且会话激活与设备重建之间保持严格先后顺序。
/// 约束：状态字段（isMuted 等）、UIApplication、NowPlaying、UnitySendMessage 仍须在主线程。
static dispatch_queue_t BoluoAudioWorkQueue(void) {
    static dispatch_queue_t queue;
    static dispatch_once_t onceToken;
    dispatch_once(&onceToken, ^{
        dispatch_queue_attr_t attr =
            dispatch_queue_attr_make_with_qos_class(DISPATCH_QUEUE_SERIAL, QOS_CLASS_USER_INITIATED, 0);
        queue = dispatch_queue_create("com.boluo.audio.session-work", attr);
    });
    return queue;
}

/// BoluoAudioResume 诊断专用：绝不与 session-work（Reset/setActive）混用，避免日志查询引擎状态时争锁。
static dispatch_queue_t BoluoAudioResumeLogQueue(void) {
    static dispatch_queue_t queue;
    static dispatch_once_t onceToken;
    dispatch_once(&onceToken, ^{
        dispatch_queue_attr_t attr =
            dispatch_queue_attr_make_with_qos_class(DISPATCH_QUEUE_SERIAL, QOS_CLASS_UTILITY, 0);
        queue = dispatch_queue_create("com.boluo.audio.resume-log", attr);
    });
    return queue;
}

extern void UnitySendMessage(const char* obj, const char* method, const char* msg);

@interface BoluoMusicPlayerIOSBridge : NSObject

@property (nonatomic, assign) NSInteger sessionCategory;
@property (nonatomic, assign) NSUInteger sessionOptions;
@property (nonatomic, assign) BOOL autoActivateSession;
@property (nonatomic, assign) BOOL notifyOthersOnDeactivate;
@property (nonatomic, strong) NSString *unityGameObjectName;
@property (nonatomic, strong) NSString *nowPlayingTitle;
@property (nonatomic, strong) NSString *nowPlayingArtist;
@property (nonatomic, strong) NSString *nowPlayingAlbum;
@property (nonatomic, strong) NSString *nowPlayingArtworkPath;
/// 封面图缓存：仅当路径改变时才重新从磁盘加载，避免每次 updateNowPlayingInfoCenter 都做 I/O
@property (nonatomic, strong) UIImage *cachedArtworkImage;
@property (nonatomic, copy)   NSString *cachedArtworkImagePath;
@property (nonatomic, assign) BOOL isMuted;
@property (nonatomic, assign) BOOL isPlaying;
/// 进岛占位：引擎还 mute 时也让负一屏显示播放中。切走且仍 mute 时不占卡片。
@property (nonatomic, assign) BOOL nowPlayingAdvertisedPlaying;
@property (nonatomic, assign) BOOL userManuallyPaused;
@property (nonatomic, assign) BOOL remoteCommandsConfigured;
/// transient duck escalate 已尝试次数（otherAudio 晚到时重试，防永久停在 0.4）。
@property (nonatomic, assign) NSInteger transientDuckEscalateAttemptCount;
/// 本轮 duck 决策的 Focus Epoch；escalate 触发时若已有更新决策则作废（统一 epoch，见文件头）。
@property (nonatomic, assign) NSInteger transientDuckEscalateEpoch;
@property (nonatomic, assign) BOOL isRestoringFromInterruption;
/// 中断期间设备已脏（MiniAudio 可能已 ma_device_stop），恢复须 Reset+Set。
/// 注意：不等于「Began 已执行过 ResetPlaybackDevice」（Began 一律不再 Reset）。
@property (nonatomic, strong) dispatch_block_t pendingActivationBlock;
@property (nonatomic, strong) dispatch_block_t pendingPauseBlock;
@property (nonatomic, strong) dispatch_block_t pendingResumeBlock;
@property (nonatomic, assign) NSTimeInterval nextRecoveryDelay;

// NowPlaying 时间基准：系统 UI 在一些平台上会依赖 ElapsedPlaybackTime 做收敛判断。
// 如果每次都写 0，有概率在系统二次同步时把按钮状态“回弹”。
@property (nonatomic, assign) NSTimeInterval nowPlayingElapsedSeconds;
@property (nonatomic, assign) CFTimeInterval nowPlayingLastTick;

// 电话/高优先级中断结束后：先用 Mix 抢回 session + NowPlaying，再延迟尝试切回独占（options 回到 sessionOptions）
@property (nonatomic, assign) NSInteger exclusiveReclaimRetryCount;
@property (nonatomic, assign) BOOL isInitialized; // 标记是否已初始化（登录后）

/// 远程命令防抖：待执行的 mute 状态（NSNumber bool），nil 表示无待执行
/// 窗口对齐 BoluoNowPlayingRemoteCommandPolicy.PlayPauseCoalesceMs
@property (nonatomic, strong) NSNumber *pendingRemoteMuteState;
/// 远程命令防抖定时器
@property (nonatomic, strong) NSTimer *pendingRemoteMuteTimer;

/// 电话/高优先级中断恢复时 attemptSessionActivation 允许 MixWithOthers；从其它音乐 App 回前台为 NO
@property (nonatomic, assign) BOOL resumeActivationUsesMixWithOthers;
/// 等待竞争音源停止的轮询计数（0.5s × 180 ≈ 90s 看门狗）
@property (nonatomic, assign) NSInteger competingAudioPollCount;
/// 后台竞争音源静默确认：连续 N 次（0.5s/次）isOtherAudioPlaying==NO 才判定对方真停了
/// （被杀死/退出/长暂停）。瞬时间隙（切歌、视频切换）达不到阈值，不会触发后台抢占。
@property (nonatomic, assign) NSInteger backgroundSilenceConfirmStreak;
/// 静默确认通过，放行本轮后台恢复链（restoreExclusiveForResume / _attemptExclusiveReclaim
/// 的后台 gate 检查此标志）；恢复完成、回前台或对方重新出声时清除。
@property (nonatomic, assign) BOOL backgroundResumeArmed;
/// 本轮后台静默确认所需连续次数。初始 60（30s）：用户在对方 App 里选内容/暂停看评论的
/// 静默间隙常达十几秒，3s 级窗口会误判「对方已退出」而抢占（灵动岛卡片+独占会话顶掉对方）。
/// 若自动恢复后短时间内又被竞争音源打断（明显误判），翻倍退避（封顶 600=5min）；回前台重置。
@property (nonatomic, assign) NSInteger backgroundSilenceConfirmPollsRequired;
/// 最近一次后台自动恢复（armed 放行）的时间戳（epoch 秒），用于识别「恢复后立刻又被打断」
@property (nonatomic, assign) NSTimeInterval lastBackgroundAutoResumeEpoch;
/// 蓝牙断开后路由抖动期间，合并多次 Reset/SetPlaybackDevice，避免与 MiniAudio 主线程 ma_device_stop 死锁
@property (nonatomic, assign) BOOL bluetoothRouteFallbackResetPending;
/// RouteDisconnected 自调度恢复（可取消），频繁断连时合并为最后一次
@property (nonatomic, strong) dispatch_block_t pendingRouteDisconnectedRecoveryBlock;
/// 串行音频队列上 Reset/SetPlaybackDevice 进行中；Unity 回前台 DoResume 须避让以免与原生恢复/SetMute 争锁卡死
@property (nonatomic, assign) BOOL playbackDeviceRecoveryInFlight;
/// 系统 mute 后等待 inFlight 结束再恢复的轮询次数（防 Reset 挂死导致永久无声）
@property (nonatomic, assign) NSInteger deviceIdleResumePollCount;
/// 短暂可 duck（导航/短信）：Began 未 mute，由 Unity 降音量；Ended / escalate 时 unduck（recoveryPhase==Ducked）
@property (nonatomic, strong) dispatch_block_t pendingDuckEscalateBlock;

/// 显式状态机（轴 1 / 轴 2）。写走转移函数 / note*。
@property (nonatomic, assign) BoluoPlayerSessionPhase sessionPhase;
@property (nonatomic, assign) BoluoPlayerRecoveryPhase recoveryPhase;
/// 恢复完成后是否自动出声。
@property (nonatomic, assign) BOOL wantsResumeAfterRecovery;
/// 已调度 delayed unmute、引擎仍硬 mute：记账 isMuted 保持 YES 直至 SetMute(false) 落地。
@property (nonatomic, assign) BOOL pendingUnmuteEngine;
/// 恢复时是否需 Reset+Set（设备脏）。
@property (nonatomic, assign) BOOL deviceDirty;

/// RD 软恢复抖动窗：重复 RouteDisconnected 时优先 unmute-only，少 SetPlaybackDevice
@property (nonatomic, assign) CFTimeInterval routeDisconnectFlapWindowStart;
@property (nonatomic, assign) NSInteger routeDisconnectFlapCount;
@property (nonatomic, assign) BOOL preferSoftRouteDisconnectedResume;
@property (nonatomic, strong) dispatch_block_t pendingSoftEscalateBlock;
/// 近期曾有外接输出（蓝牙等）；外接→内建短窗内才允许「扬声器兜底」当 RD（防微信误判）
@property (nonatomic, assign) BOOL recentlyHadExternalAudioRoute;
@property (nonatomic, assign) NSTimeInterval lastExternalToBuiltInRouteEpoch;
/// 本轮竞争曾见 otherAudioPlaying=YES（杀 QQ 音乐后 secondarySilence 可能卡住）
@property (nonatomic, assign) BOOL competingEpisodeSawOtherAudioPlaying;
/// 本轮是否曾见 secondarySilenceHint（微信语音输入/ASR）。
/// 与 sawOtherAudio 组合：ASR-only → 禁止后台自动续播，等回菠萝前台。
@property (nonatomic, assign) BOOL competingEpisodeSawSecondarySilence;
/// 最近一次 Interruption Began 时间；微信语音识别刚启动时 secondarySilence 可能晚于 Began 数百毫秒，
/// 此窗口内禁止静默确认/独占抢回，否则对方录音/ASR 一上来就被掐。
@property (nonatomic, assign) NSTimeInterval lastInterruptionBeganEpoch;
/// 上次 RD 恢复成功（软或硬）时间；抖动窗内后续 RD 优先软恢复。
@property (nonatomic, assign) NSTimeInterval lastRouteDisconnectedResumeEpoch;
/// 最近一次进入 RD 自恢复分支的时间（用于窗内计数，含尚未恢复成功的连发）。
@property (nonatomic, assign) NSTimeInterval lastRouteDisconnectedInterruptionEpoch;
/// RD 恢复已调度、尚未记成功；硬/软路径 unmute 成功后清掉并刷新 resume epoch。
@property (nonatomic, assign) BOOL awaitingRouteDisconnectedResumeCompletion;
/// QA：强制禁用软恢复，每次模拟 RD 都走硬 SetPlaybackDevice（听感一卡一卡）。
@property (nonatomic, assign) BOOL forceHardRdResumeForRepro;
@property (nonatomic, strong) dispatch_source_t forceRdStutterTimer;
@property (nonatomic, assign) BOOL forceRdStutterActive;

/// 单一命名焦点状态（Phase 2）：决策点经 noteFocusState:reason: 落状态，
/// 作为日志/BLS/真值对账的判定输入。旧 BOOL 暂保留并行，按矩阵验证分批删除。
@property (nonatomic, assign) BoluoAudioFocusStateValue focusState;
/// 待执行真值对账的原因（合并去重：连续转移只保留最后一次的 reason）。
@property (nonatomic, copy) NSString *pendingFocusTruthAuditReason;

/// 幂等记忆：上一次成功 configureAudioSession 应用的 category/options。
/// 进岛/登录完成时 C# 的 BoluoMusicPlayerIOS.Start 协程会重复调 ApplyAudioSession
/// （Init → configureAudioSession → setCategory + setActive:YES），即使 category 完全没变。
/// AVAudioSession 在 active 状态下被再次 setActive:YES 会让 CoreAudio 重新协商输出 IO，
/// 听感上就是一声"声卡切换"级别的几十毫秒断流（用户反馈的「进岛咯噔一下」）。
/// 命中幂等 → 整个 configureAudioSession 直接 noop，避免无谓的 IO 重启。
@property (nonatomic, assign) BOOL sessionConfiguredOnce;
@property (nonatomic, copy) NSString *lastAppliedCategory;
@property (nonatomic, assign) AVAudioSessionCategoryOptions lastAppliedOptions;

+ (instancetype)sharedInstance;
- (void)configureAudioSession;
- (void)setupRemoteCommandsIfNeeded;
- (void)removeRemoteCommands;
- (void)updateNowPlayingInfoCenter;
- (NSString *)resolvedNowPlayingArtworkPath;
- (void)applyNowPlayingInfoWithTitle:(NSString *)title artist:(NSString *)artist album:(NSString *)album artwork:(NSString *)artworkPath;
- (void)setAudioMute:(BOOL)mute isUserAction:(BOOL)isUserAction;
- (void)setAudioMute:(BOOL)mute isUserAction:(BOOL)isUserAction resetDevice:(BOOL)resetDevice;
- (void)handleAudioInterruption:(NSNotification *)notification;
- (BOOL)isUserPaused;
- (void)syncExternalPlaybackState:(BOOL)isPlaying;
- (void)startAudioInterruptionObserver;
- (void)stopAudioInterruptionObserver;
- (void)handleRouteChange:(NSNotification *)notification;
- (BOOL)isBluetoothPortType:(NSString *)portType;
- (BOOL)routeDescriptionHasBluetoothOutput:(AVAudioSessionRouteDescription *)route;
- (void)resetPlaybackDeviceForRouteFallback;
- (void)scheduleBluetoothDisconnectRouteRecovery;
- (void)_runBluetoothDisconnectRouteRecovery;
- (BOOL)isRouteDisconnectedInterruption:(NSNotification *)notification;
- (void)noteRouteDisconnectedForSoftResumePolicy;
- (NSTimeInterval)routeDisconnectedSettleDelaySeconds;
- (BOOL)currentRouteHasValidOutputForResume;
- (BOOL)shouldAttemptSoftRouteDisconnectedResume;
- (void)startForceBluetoothRdStutterReproWithInterval:(NSTimeInterval)intervalSeconds;
- (void)stopForceBluetoothRdStutterRepro;
- (void)_fireForceBluetoothRdStutterTick;
- (void)performSoftRouteDisconnectedResume;
- (void)_escalateSoftRouteDisconnectedResumeIfNeeded;
- (void)markRouteDisconnectedResumeSucceeded;
- (void)scheduleRouteDisconnectedRecovery;
- (BOOL)shouldUseTransientDuckForInterruption:(NSNotification *)notification;
- (void)beginAudioFocusDuck;
- (void)endAudioFocusDuck;
- (void)scheduleDuckEscalateCheck;
- (void)notifyUnityAudioFocusDuck:(BOOL)duck factor:(float)factor;
- (void)notifyUnityCompetingOrSystemMuted:(BOOL)competingLong;
- (void)performDeviceResetAndResume;
- (void)performSetPlaybackDeviceRetryWithCount:(NSInteger)retryCount;
- (void)finishResumeAfterDeviceReady;
- (void)resumeIfStillMutedAfterDeviceWork:(NSString *)reason;
- (void)applyFirstPlayUnmuteForced;
- (void)scheduleResumeWhenDeviceIdleAfterSystemMute;
- (void)_pollResumeWhenDeviceIdleAfterSystemMute;
- (BOOL)currentRouteLooksLikeBuiltInFallback;
- (BOOL)routeDescriptionHasExternalOutput:(AVAudioSessionRouteDescription *)route;
- (void)updateExternalRouteMemoryFromPrevious:(AVAudioSessionRouteDescription *)previous
                                      current:(AVAudioSessionRouteDescription *)current;
- (BOOL)shouldDeferForCompetingOrSecondarySilence;
- (BOOL)shouldDeferForCompetingOrSecondarySilenceIgnoringStuckSecondaryWhenActive:(BOOL)ignoreStuckWhenActive;
- (BOOL)shouldKeepWaitingForCompetingAudio;
- (BOOL)shouldUseRouteDisconnectedSelfResume:(BOOL)isRouteDisconnected;
/// 非 RD / 非电话竞争 / 无 secondarySilence 时优先 duck（约 0.4），避免短信/短提示 mute 卡顿。
- (BOOL)shouldPreferTransientDuckForInterruption:(NSNotification *)notification
                              isRouteDisconnected:(BOOL)isRouteDisconnected;
- (void)cancelTransientDuckEscalate;
- (void)escalateTransientDuckToMuteIfNeeded;
/// 后台被微信录音/语音识别等抢焦点时：异步 setActive:NO + NotifyOthers，真正让出麦克风。
- (BOOL)shouldYieldSessionForCompetingAudioNow;
- (void)yieldAudioSessionForCompetingAudioWithReason:(NSString *)reason;
/// 系统触发静音的原生护栏：同步降增益 → 短缓冲 → SetMute；completion 于 mute 落地后执行。
- (void)guardedSystemMuteWithReason:(NSString *)reason completion:(void (^)(void))afterMute;
/// SilenceSecondary BEGIN / ResignActive 兜底共用的竞争静音 episode（记账+护栏 mute+yield+UI）。
- (void)beginSecondarySilenceCompetingEpisodeFromSignal:(NSString *)signal;
/// Phase 2/3：记录单一焦点状态转移（日志+BLS），并调度一次记账-引擎真值对账。
- (void)noteFocusState:(BoluoAudioFocusStateValue)state reason:(NSString *)reason;
/// 状态转移后 0.25s 对账（看门狗类：不纳入 epoch，触发时重读全部现场，幂等；连发合并）。
- (void)scheduleFocusTruthAuditWithReason:(NSString *)reason;
- (void)scheduleFocusTruthAuditWithDelay:(NSTimeInterval)delay reason:(NSString *)reason;
- (void)_runFocusTruthAudit;
- (void)restoreExclusiveAudioSession;
- (BOOL)restoreExclusiveAudioSessionForResume:(BOOL)forceInterruptOthers;
- (BOOL)unmuteEngineIfExclusiveAvailableWithReason:(NSString *)reason;
- (void)attemptSessionActivationWithRetry:(void (^)(BOOL success))completion;
- (void)performSessionActivationWithRetryCount:(NSInteger)retryCount allowMix:(BOOL)allowMix completion:(void (^)(BOOL success))completion;
- (void)scheduleInterruptionRecovery;
- (void)cancelPendingInterruptionRecovery;
- (void)enqueueInterruptionRecoveryWithDelay:(NSTimeInterval)delay weakSelf:(__weak BoluoMusicPlayerIOSBridge *)weakSelf;
- (void)_deactivateSessionIfBackgroundUserPaused;
- (void)_reassertNowPlayingAndCommandsWithReason:(NSString *)reason;
- (void)_scheduleExclusiveReclaimAfterInterruption;
- (void)_attemptExclusiveReclaim;
- (BOOL)shouldAttemptAutoResumeAfterCompetingAudio;
- (BOOL)shouldDeferUnityForegroundResume;
- (BoluoUnmuteVerdict)unmuteVerdictForReason:(NSString *)reason;
- (void)transitionToSessionPhase:(BoluoPlayerSessionPhase)phase reason:(NSString *)reason;
- (void)transitionToRecoveryPhase:(BoluoPlayerRecoveryPhase)phase reason:(NSString *)reason;
- (void)noteInterruptionBegan:(NSString *)reason;
- (void)noteInterruptionEnded:(NSString *)reason;
- (void)noteCompetingAudioDetected:(NSString *)reason;
- (void)noteCompetingAudioCleared:(NSString *)reason;
- (void)noteDeviceRecoveryStarted:(NSString *)reason;
- (void)noteDeviceRecoveryFinished:(NSString *)reason;
- (void)noteDuckBegan:(NSString *)reason;
- (void)noteDuckEnded:(NSString *)reason;
- (void)noteFirstPlayRequested:(NSString *)reason;
- (void)noteFirstPlayConsumed:(NSString *)reason;
- (void)noteSessionArming:(NSString *)reason;
- (void)noteSessionArmDrainComplete:(NSString *)reason;
- (void)noteSessionStopped:(NSString *)reason;
- (void)tryResumeWhenCompetingAudioStopped;
- (void)deferTryResumeWhenCompetingAudioStopped:(NSString *)reason;
- (void)_deferredTryResumeWhenCompetingAudioStopped;
- (void)_scheduleWaitForCompetingAudioToStop;
- (void)_pollWaitForCompetingAudioToStop;
- (void)_penalizeBackgroundSilenceConfirmIfMisjudged;
- (void)handleSilenceSecondaryAudioHint:(NSNotification *)notification;
// 公共方法：暂停播放（供定时器等外部调用）
// reason: 暂停原因，用于日志记录（如 "Timer"）
- (void)pausePlaybackWithReason:(NSString *)reason isUserAction:(BOOL)isUserAction;
/// 防抖定时器到时：应用 pendingRemoteMuteState 并通知 Unity
- (void)_flushPendingRemoteMute;
/// 负一屏切歌接受后作废尚未 flush 的 play/pause（BoluoNowPlayingRemoteCommandPolicy）
- (void)cancelPendingRemoteMuteForSkip;
/// 真机诊断：统一 tag [BoluoAudioResume]，Xcode 控制台可过滤
- (void)logAudioResumeSnapshot:(NSString *)tag;
/// 声音焦点被抢占（Interruption Began）专用 BLS：level=warn，event=boluo_audio_focus_loss。
- (void)reportAudioFocusLossToBLSWithTag:(NSString *)tag
                                  detail:(NSString *)detail
                              durationMs:(CFTimeInterval)durationMs;
- (void)handleAppDidBecomeActive:(NSNotification *)notification;
- (void)handleAppWillResignActive:(NSNotification *)notification;
/// 回前台清除 transient duck（含 escalate）；Ended 丢失时 Unity 侧可能粘在 0.4×
- (void)clearTransientDuckOnForegroundReturn;
/// 从其它 App 抢音频后回前台（shouldResume=NO 时系统不会自动恢复）
- (void)tryResumePlaybackAfterReturningToForeground;
/// 回前台时无条件把控制中心/锁屏/负一屏的 Now Playing 卡片与远程控制抢回波萝名下
- (void)reassertNowPlayingOnForegroundIfNeeded;

@end

static void BoluoNotifyDelayedUnmuteLanded(void) {
    BoluoMusicPlayerIOSBridge *bridge = [BoluoMusicPlayerIOSBridge sharedInstance];
    if (!bridge) {
        return;
    }
    if (bridge.userManuallyPaused) {
        bridge.pendingUnmuteEngine = NO;
        BoluoAudioLog(@"[BoluoAudioResume] delayed unmute landed SKIP accounting (userPaused)");
        return;
    }
    BOOL wasPending = bridge.pendingUnmuteEngine;
    bridge.pendingUnmuteEngine = NO;
    bridge.isMuted = NO;
    bridge.isPlaying = YES;
    [bridge updateNowPlayingInfoCenter];
    if (wasPending) {
        NSString *go = bridge.unityGameObjectName ?: @"BoluoMusicPlayerIOS";
        UnitySendMessage([go UTF8String], "OnNowPlayingPlay", "");
    }
    BoluoAudioLog(@"[BoluoAudioResume] delayed unmute landed accounting isMuted=NO wasPending=%d",
          wasPending ? 1 : 0);
}

@implementation BoluoMusicPlayerIOSBridge

+ (instancetype)sharedInstance {
    static BoluoMusicPlayerIOSBridge *instance = nil;
    static dispatch_once_t onceToken;
    dispatch_once(&onceToken, ^{
        instance = [[BoluoMusicPlayerIOSBridge alloc] init];
        instance.sessionCategory = 4; // Playback
        instance.sessionOptions = 0;
        instance.autoActivateSession = YES;
        instance.notifyOthersOnDeactivate = YES;
        instance.unityGameObjectName = @"BoluoMusicPlayerIOS";
        instance.nowPlayingTitle = @"直播";
        instance.nowPlayingArtist = @"";
        instance.nowPlayingAlbum = @"";
        instance.nowPlayingArtworkPath = nil;
        // 关键修复：启动时初始状态设为未播放，避免启动时的路由变化事件错误地设置 userManuallyPaused
        // 当 Unity 层调用 SetMute(false) 或 syncExternalPlaybackState(true) 时，会正确更新这些状态
        instance.isMuted = YES;
        instance.isPlaying = NO;
        instance.userManuallyPaused = NO;
        instance.remoteCommandsConfigured = NO;
        instance.transientDuckEscalateAttemptCount = 0;
        instance.pendingActivationBlock = nil;
        instance.pendingRouteDisconnectedRecoveryBlock = nil;
        instance.nextRecoveryDelay = 0.3;
        instance.nowPlayingElapsedSeconds = 0;
        instance.nowPlayingLastTick = 0;
        instance.exclusiveReclaimRetryCount = 0;
        instance.isInitialized = NO; // 默认未初始化，等待登录
        instance.pendingRemoteMuteState = nil;
        instance.pendingRemoteMuteTimer = nil;
        instance.resumeActivationUsesMixWithOthers = NO;
        instance.competingAudioPollCount = 0;
        instance.backgroundSilenceConfirmStreak = 0;
        instance.backgroundResumeArmed = NO;
        instance.backgroundSilenceConfirmPollsRequired = 60;
        instance.lastBackgroundAutoResumeEpoch = 0;
        instance.recentlyHadExternalAudioRoute = NO;
        instance.lastExternalToBuiltInRouteEpoch = 0;
        instance.competingEpisodeSawOtherAudioPlaying = NO;
        instance.competingEpisodeSawSecondarySilence = NO;
        instance.lastInterruptionBeganEpoch = 0;
        instance.lastRouteDisconnectedResumeEpoch = 0;
        instance.lastRouteDisconnectedInterruptionEpoch = 0;
        instance.routeDisconnectFlapCount = 0;
        instance.preferSoftRouteDisconnectedResume = NO;
        instance.awaitingRouteDisconnectedResumeCompletion = NO;
        instance.forceHardRdResumeForRepro = NO;
        instance.forceRdStutterTimer = nil;
        instance.forceRdStutterActive = NO;
        instance.sessionConfiguredOnce = NO;
        instance.lastAppliedCategory = nil;
        instance.lastAppliedOptions = 0;
        instance.sessionPhase = BoluoPlayerSessionPhaseStopped;
        instance.recoveryPhase = BoluoPlayerRecoveryPhaseNone;
        instance.wantsResumeAfterRecovery = NO;
        instance.pendingUnmuteEngine = NO;
        instance.deviceDirty = NO;
        instance.recentlyHadExternalAudioRoute = NO;
        instance.lastExternalToBuiltInRouteEpoch = 0;
        instance.competingEpisodeSawOtherAudioPlaying = NO;
        [instance startAudioInterruptionObserver];
        // 冷启动若已连蓝牙耳机，先记一笔，避免首轮 RouteDisconnected reason 缺失时漏恢复
        [instance updateExternalRouteMemoryFromPrevious:nil
                                                current:[AVAudioSession sharedInstance].currentRoute];
    });
    return instance;
}

- (void)transitionToSessionPhase:(BoluoPlayerSessionPhase)phase reason:(NSString *)reason {
    BoluoPlayerSessionPhase old = self.sessionPhase;
    if (old == phase) {
        return;
    }
    // 非法转移只 WARN：Stopped→Engaged 跳过 Arming 等仍允许（兼容旧路径），但落盘便于排查。
    BOOL suspicious = NO;
    if (old == BoluoPlayerSessionPhaseStopped && phase == BoluoPlayerSessionPhaseEngaged) {
        suspicious = YES;
    }
    if (old == BoluoPlayerSessionPhaseStopped && phase == BoluoPlayerSessionPhaseAwaitingFirstPlay) {
        suspicious = YES;
    }
    self.sessionPhase = phase;
    BoluoMusicPlayer_Trail(@"sessionPhase %@ → %@ (%@)%@",
                           BoluoPlayerSessionPhaseName(old),
                           BoluoPlayerSessionPhaseName(phase),
                           reason ?: @"?",
                           suspicious ? @" WARN-suspicious" : @"");
}

- (void)transitionToRecoveryPhase:(BoluoPlayerRecoveryPhase)phase reason:(NSString *)reason {
    BoluoPlayerRecoveryPhase old = self.recoveryPhase;
    if (old == phase) {
        return;
    }
    self.recoveryPhase = phase;
    BoluoMusicPlayer_Trail(@"recoveryPhase %@ → %@ (%@)",
                           BoluoPlayerRecoveryPhaseName(old),
                           BoluoPlayerRecoveryPhaseName(phase),
                           reason ?: @"?");
}

- (void)noteSessionArming:(NSString *)reason {
    self.wantsResumeAfterRecovery = NO;
    self.deviceDirty = NO;
    [self transitionToRecoveryPhase:BoluoPlayerRecoveryPhaseNone reason:reason ?: @"sessionArm"];
    [self transitionToSessionPhase:BoluoPlayerSessionPhaseArming reason:reason ?: @"sessionArm"];
}

- (void)noteSessionArmDrainComplete:(NSString *)reason {
    // drain 完成：离开 Arming。尚未 MarkAwaiting 时落回 Stopped（已开闸、待首播请求）。
    if (self.sessionPhase == BoluoPlayerSessionPhaseArming) {
        [self transitionToSessionPhase:BoluoPlayerSessionPhaseStopped
                                reason:reason ?: @"armDrainComplete"];
    }
}

- (void)noteSessionStopped:(NSString *)reason {
    self.wantsResumeAfterRecovery = NO;
    self.deviceDirty = NO;
    [self transitionToRecoveryPhase:BoluoPlayerRecoveryPhaseNone reason:reason ?: @"sessionStopped"];
    [self transitionToSessionPhase:BoluoPlayerSessionPhaseStopped reason:reason ?: @"sessionStopped"];
}

- (void)noteFirstPlayRequested:(NSString *)reason {
    if (self.sessionPhase == BoluoPlayerSessionPhaseEngaged) {
        return;
    }
    [self transitionToSessionPhase:BoluoPlayerSessionPhaseAwaitingFirstPlay
                            reason:reason ?: @"firstPlayRequested"];
}

- (void)noteFirstPlayConsumed:(NSString *)reason {
    [self transitionToRecoveryPhase:BoluoPlayerRecoveryPhaseNone reason:reason ?: @"firstPlayConsumed"];
    self.wantsResumeAfterRecovery = NO;
    [self transitionToSessionPhase:BoluoPlayerSessionPhaseEngaged
                            reason:reason ?: @"firstPlayConsumed"];
}

- (void)noteInterruptionBegan:(NSString *)reason {
    self.deviceDirty = YES; // Began 不 Reset，但设备视为脏，Ended 须恢复
    [self transitionToRecoveryPhase:BoluoPlayerRecoveryPhaseInterruptionActive
                             reason:reason ?: @"interruptionBegan"];
}

- (void)noteInterruptionEnded:(NSString *)reason {
    if (self.recoveryPhase == BoluoPlayerRecoveryPhaseInterruptionActive
        || self.recoveryPhase == BoluoPlayerRecoveryPhaseDucked) {
        // Ended 后进入设备恢复或竞音等待由调用方再 note；此处先落到 None。
        // 多数路径会立刻 noteDeviceRecoveryStarted / noteCompetingAudioDetected。
        [self transitionToRecoveryPhase:BoluoPlayerRecoveryPhaseNone
                                 reason:reason ?: @"interruptionEnded"];
    }
}

- (void)noteCompetingAudioDetected:(NSString *)reason {
    [self transitionToRecoveryPhase:BoluoPlayerRecoveryPhaseAwaitingCompetingStop
                             reason:reason ?: @"competingDetected"];
}

- (void)noteCompetingAudioCleared:(NSString *)reason {
    if (self.recoveryPhase == BoluoPlayerRecoveryPhaseAwaitingCompetingStop) {
        [self transitionToRecoveryPhase:BoluoPlayerRecoveryPhaseNone
                                 reason:reason ?: @"competingCleared"];
    }
}

- (void)noteDeviceRecoveryStarted:(NSString *)reason {
    [self transitionToRecoveryPhase:BoluoPlayerRecoveryPhaseRecoveringDevice
                             reason:reason ?: @"deviceRecoveryStarted"];
}

- (void)noteDeviceRecoveryFinished:(NSString *)reason {
    if (self.recoveryPhase == BoluoPlayerRecoveryPhaseRecoveringDevice) {
        [self transitionToRecoveryPhase:BoluoPlayerRecoveryPhaseNone
                                 reason:reason ?: @"deviceRecoveryFinished"];
    }
    self.deviceDirty = NO;
}

- (void)noteDuckBegan:(NSString *)reason {
    [self transitionToRecoveryPhase:BoluoPlayerRecoveryPhaseDucked reason:reason ?: @"duckBegan"];
}

- (void)noteDuckEnded:(NSString *)reason {
    if (self.recoveryPhase == BoluoPlayerRecoveryPhaseDucked) {
        [self transitionToRecoveryPhase:BoluoPlayerRecoveryPhaseNone reason:reason ?: @"duckEnded"];
    }
}

- (BoluoUnmuteVerdict)unmuteVerdictForReason:(NSString *)reason {
    BoluoUnmuteVerdict v = BoluoUnmuteVerdictAllow;
    const char *block = "allow";
    // inFlight 优先于 userPaused：Unity DoResume 在设备重建窗口必须 defer（即便短暂 userPaused）。
    if (self.playbackDeviceRecoveryInFlight) {
        v = BoluoUnmuteVerdictDeferDeviceBusy;
        block = "inFlight";
    } else if (BoluoAudioSession_IsLogoutSilenced() != 0) {
        v = BoluoUnmuteVerdictBlockedDisarmed;
        block = "logoutSilenced";
    } else if (self.userManuallyPaused) {
        v = BoluoUnmuteVerdictBlockedUserPaused;
        block = "userPaused";
    } else if (self.recoveryPhase == BoluoPlayerRecoveryPhaseInterruptionActive
        || self.recoveryPhase == BoluoPlayerRecoveryPhaseRecoveringDevice
        || self.recoveryPhase == BoluoPlayerRecoveryPhaseAwaitingCompetingStop
        || self.pendingActivationBlock != nil) {
        v = BoluoUnmuteVerdictDeferRecovery;
        block = "recoveryPhase";
    }
    BoluoMusicPlayer_Trail(
        @"unmuteVerdict reason=%@ → %@ via=%s sess=%@ rec=%@ muted=%d playing=%d "
         "userPaused=%d inFlight=%d silenced=%d otherAudio=%d cat=%@",
        reason ?: @"?",
        BoluoUnmuteVerdictName(v),
        block,
        BoluoPlayerSessionPhaseName(self.sessionPhase),
        BoluoPlayerRecoveryPhaseName(self.recoveryPhase),
        (int)self.isMuted,
        (int)self.isPlaying,
        (int)self.userManuallyPaused,
        (int)self.playbackDeviceRecoveryInFlight,
        (int)BoluoAudioSession_IsLogoutSilenced(),
        (int)[[AVAudioSession sharedInstance] isOtherAudioPlaying],
        [AVAudioSession sharedInstance].category ?: @"?");
    return v;
}

- (AVAudioSessionCategory)categoryFromValue:(NSInteger)value {
    // Boluo 仅使用 Playback。Unity C# 传入 4；勿将 1(None) 映射为系统当前 category（启动时常为 Ambient）。
    if (value != 4) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] categoryFromValue(%ld) remapped to Playback", (long)value);
    }
    return AVAudioSessionCategoryPlayback;
}

- (AVAudioSessionCategoryOptions)optionsFromBits:(NSUInteger)bits {
    AVAudioSessionCategoryOptions options = 0;
    if (bits & 0x01) options |= AVAudioSessionCategoryOptionMixWithOthers;
    if (bits & 0x02) options |= AVAudioSessionCategoryOptionDuckOthers;
    if (bits & 0x04) options |= AVAudioSessionCategoryOptionAllowBluetooth;
    if (bits & 0x08) options |= AVAudioSessionCategoryOptionDefaultToSpeaker;
    if (bits & 0x10) options |= AVAudioSessionCategoryOptionInterruptSpokenAudioAndMixWithOthers;
    if (bits & 0x20) options |= AVAudioSessionCategoryOptionAllowBluetoothA2DP;
    if (bits & 0x40) options |= AVAudioSessionCategoryOptionAllowAirPlay;
    return options;
}

- (void)configureAudioSession {
    AVAudioSession *session = [AVAudioSession sharedInstance];
    AVAudioSessionCategory category = [self categoryFromValue:self.sessionCategory];
    AVAudioSessionCategoryOptions options = [self optionsFromBits:self.sessionOptions];

    // 幂等性 guard：会话已是目标 category/options 且我们之前成功配过一次 → 整段 noop。
    // 这是消除"进岛咯噔一下"的关键之一。BoluoMusicPlayerIOS.Start 协程在登录完成时被
    // InitializeAfterLogin 触发，会连续多次 ApplyAudioSession。每次 setCategory+setActive:YES
    // 都让 CoreAudio 重启输出 IO，听感就是一声「声卡切换」。命中幂等就跳过。
    if (self.sessionConfiguredOnce &&
        self.autoActivateSession &&
        self.lastAppliedCategory != nil &&
        [self.lastAppliedCategory isEqualToString:category] &&
        self.lastAppliedOptions == options &&
        [session.category isEqualToString:category]) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] configureAudioSession NOOP (idempotent) category=%@ opts=0x%lx",
              category, (unsigned long)options);
        if (self.isInitialized) {
            // NowPlaying / RemoteCommands 在首次已建立；这里不重做，因为不会触发 CoreAudio 抖动，
            // 但若调用方期望刷新，可走专门的 SetNowPlayingInfo 路径而不是 Init。
        }
        return;
    }

    // 是否激活会话的决策依赖 UIApplication 状态，必须在主线程读取；阻塞调用本身放队列
    BOOL shouldActivate = NO;
    if (self.autoActivateSession) {
        // Activate session usually uses options 0. NotifyOthersOnDeactivation is for deactivation.
        // 关键修复：即使用户手动暂停，如果应用在前台（或启动中），也应该激活音频会话
        // 这对于 SoloAmbient 类别尤其重要，否则即使在前台也可能没有声音
        // userManuallyPaused 应该只影响播放状态（静音），而不应该阻止音频会话的激活
        UIApplicationState appState = [UIApplication sharedApplication].applicationState;
        // 检查应用是否在前台（Active）或启动中（Inactive），但不包括后台（Background）
        BOOL isForeground = (appState != UIApplicationStateBackground);
        
        if (self.userManuallyPaused && !isForeground) {
            // 仅在后台且用户手动暂停时跳过激活（避免后台自动恢复）
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] User manually paused in background, skipping session activation");
        } else {
            // 前台时（包括启动中）即使用户暂停也要激活会话（确保 SoloAmbient 类别正常工作）
            if (self.userManuallyPaused && isForeground) {
                BoluoAudioLog(@"[BoluoMusicPlayerIOS] User manually paused but app is foreground (state=%ld) - activating session anyway (required for SoloAmbient)", (long)appState);
            }
            shouldActivate = YES;
        }
    } else {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] Auto activate disabled, skip setActive:YES");
    }

    // 提前记录幂等参数（乐观）：C# 启动链会连续多次调 ApplyAudioSession，
    // 若等队列完成才记录，间隙里的重复调用会再次入队 setCategory/setActive，
    // 造成背靠背两次 CoreAudio IO 重启（进岛咯噔）。setCategory(Playback) 由
    // BoluoHybridSchemeBridge 的拦截器兜底保证成功，乐观记录是安全的。
    self.sessionConfiguredOnce = YES;
    self.lastAppliedCategory = category;
    self.lastAppliedOptions = options;

    // setCategory/setActive 为阻塞调用（路由切换期间可达秒级），放串行音频队列执行；
    // 与随后的 SetPlaybackDevice（同队列）保持先 category/activate 后建设备的顺序。
    dispatch_async(BoluoAudioWorkQueue(), ^{
        CFTimeInterval t0 = CACurrentMediaTime();
        NSError *error = nil;
        BOOL success = NO;
        if ([session respondsToSelector:@selector(setCategory:mode:options:error:)]) {
            success = [session setCategory:category
                                      mode:AVAudioSessionModeDefault
                                   options:options
                                     error:&error];
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] Using setCategory:mode:options:error:");
        } else {
            success = [session setCategory:category
                               withOptions:options
                                     error:&error];
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] Using legacy setCategory:withOptions:error:");
        }
        
        if (!success || error) {
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] Failed to set category/options: %@", error ? error.localizedDescription : @"Unknown error");
            BoluoNativeAudioJank_Report(@"native_configure_session",
                                       (CACurrentMediaTime() - t0) * 1000.0,
                                       @"setCategory_fail");
            return;
        }
        
        if (shouldActivate) {
            success = [session setActive:YES error:&error];
            if (!success || error) {
                BoluoAudioLog(@"[BoluoMusicPlayerIOS] Failed to activate audio session: %@", error ? error.localizedDescription : @"Unknown error");
            } else {
                BoluoAudioLog(@"[BoluoMusicPlayerIOS] Audio session activated");
            }
        }

        BoluoNativeAudioJank_Report(@"native_configure_session",
                                   (CACurrentMediaTime() - t0) * 1000.0,
                                   shouldActivate ? @"setCategory+setActive" : @"setCategory");

        BoluoAudioLog(@"[BoluoMusicPlayerIOS] Final Session -> Category: %@, Options: 0x%lx, SampleRate: %.0f, IOBuffer: %.4f, Outputs: %lu, Active: %@",
              session.category,
              (unsigned long)session.categoryOptions,
              session.sampleRate,
              session.IOBufferDuration,
              (unsigned long)session.currentRoute.outputs.count,
              session.isOtherAudioPlaying ? @"OtherAudioPlaying" : @"OK");

        dispatch_async(dispatch_get_main_queue(), ^{
            // 关键修改：只有在已初始化（登录后）时才设置远程命令和更新NowPlaying
            if (self.isInitialized)
            {
                [self setupRemoteCommandsIfNeeded];
                [self updateNowPlayingInfoCenter];
            }
            else
            {
                BoluoAudioLog(@"[BoluoMusicPlayerIOS] configureAudioSession - Not initialized yet (waiting for login), skipping remote commands and NowPlaying");
            }
        });
    });
}

- (void)setupRemoteCommandsIfNeeded {
    // 关键修改：如果未初始化（未登录），不设置远程命令
    if (!self.isInitialized)
    {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] setupRemoteCommandsIfNeeded - Not initialized yet (waiting for login), skipping");
        return;
    }
    
    if (self.remoteCommandsConfigured) {
        return;
    }

    MPRemoteCommandCenter *commandCenter = [MPRemoteCommandCenter sharedCommandCenter];
    commandCenter.playCommand.enabled = YES;
    commandCenter.pauseCommand.enabled = YES;
    commandCenter.togglePlayPauseCommand.enabled = YES;

    __weak __typeof(self) weakSelf = self;
    // 对齐 BoluoNowPlayingRemoteCommandPolicy.PlayPauseCoalesceMs：连点只落地最终 mute 意图
    static const NSTimeInterval kCoalesceInterval = 0.25;

    [commandCenter.playCommand addTargetWithHandler:^MPRemoteCommandHandlerStatus(MPRemoteCommandEvent * _Nonnull event) {
        BoluoMusicPlayerIOSBridge *self = weakSelf;
        if (!self) return MPRemoteCommandHandlerStatusSuccess;
        self.pendingRemoteMuteState = @NO;  // Play = unmute
        [self.pendingRemoteMuteTimer invalidate];
        self.pendingRemoteMuteTimer = [NSTimer scheduledTimerWithTimeInterval:kCoalesceInterval target:self selector:@selector(_flushPendingRemoteMute) userInfo:nil repeats:NO];
        [[NSRunLoop mainRunLoop] addTimer:self.pendingRemoteMuteTimer forMode:NSRunLoopCommonModes];
        return MPRemoteCommandHandlerStatusSuccess;
    }];

    [commandCenter.pauseCommand addTargetWithHandler:^MPRemoteCommandHandlerStatus(MPRemoteCommandEvent * _Nonnull event) {
        BoluoMusicPlayerIOSBridge *self = weakSelf;
        if (!self) return MPRemoteCommandHandlerStatusSuccess;
        // 已是用户暂停态则立即补 NowPlaying，不再走防抖（避免重复触发）。
        // 不能只看 isMuted：冷启动后桥接层 isMuted 停留在初始 YES 而引擎可能已自动首播，
        // 若据此早退，负一屏点暂停会被当成「已暂停」吞掉，音乐照播（点击暂停不生效）。
        if (self.isMuted && self.userManuallyPaused) {
            [self updateNowPlayingInfoCenter];
            [self _deactivateSessionIfBackgroundUserPaused];
            return MPRemoteCommandHandlerStatusSuccess;
        }
        self.pendingRemoteMuteState = @YES;  // Pause = mute
        [self.pendingRemoteMuteTimer invalidate];
        self.pendingRemoteMuteTimer = [NSTimer scheduledTimerWithTimeInterval:kCoalesceInterval target:self selector:@selector(_flushPendingRemoteMute) userInfo:nil repeats:NO];
        [[NSRunLoop mainRunLoop] addTimer:self.pendingRemoteMuteTimer forMode:NSRunLoopCommonModes];
        return MPRemoteCommandHandlerStatusSuccess;
    }];

    [commandCenter.togglePlayPauseCommand addTargetWithHandler:^MPRemoteCommandHandlerStatus(MPRemoteCommandEvent * _Nonnull event) {
        BoluoMusicPlayerIOSBridge *self = weakSelf;
        if (!self) return MPRemoteCommandHandlerStatusSuccess;
        // 冷启动桥接状态未同步时（isMuted 停留在初始 YES 而引擎实际在播），
        // 按「非用户暂停 + 引擎 Started」视为正在播放，toggle 应执行暂停而非再次播放
        BOOL effectivelyPlaying = !self.isMuted
            || (!self.userManuallyPaused && GetPlaybackState() == 2 /* Started */);
        BOOL newMute = effectivelyPlaying;
        self.pendingRemoteMuteState = @(newMute);
        [self.pendingRemoteMuteTimer invalidate];
        self.pendingRemoteMuteTimer = [NSTimer scheduledTimerWithTimeInterval:kCoalesceInterval target:self selector:@selector(_flushPendingRemoteMute) userInfo:nil repeats:NO];
        [[NSRunLoop mainRunLoop] addTimer:self.pendingRemoteMuteTimer forMode:NSRunLoopCommonModes];
        return MPRemoteCommandHandlerStatusSuccess;
    }];

    self.remoteCommandsConfigured = YES;
    BoluoAudioLog(@"[BoluoMusicPlayerIOS] Remote command center configured");

    // 维持 remote control events 管线（公开 API），不影响按钮变灰（按钮变灰来自禁用 command）
    [[UIApplication sharedApplication] beginReceivingRemoteControlEvents];

    [self startAudioInterruptionObserver];
}

- (void)removeRemoteCommands {
    if (![NSThread isMainThread]) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self removeRemoteCommands];
        });
        return;
    }

    if (!self.remoteCommandsConfigured) {
        return;
    }

    MPRemoteCommandCenter *commandCenter = [MPRemoteCommandCenter sharedCommandCenter];
    
    // 移除所有target handlers
    [commandCenter.playCommand removeTarget:nil];
    [commandCenter.pauseCommand removeTarget:nil];
    [commandCenter.togglePlayPauseCommand removeTarget:nil];
    
    // 禁用所有命令
    commandCenter.playCommand.enabled = NO;
    commandCenter.pauseCommand.enabled = NO;
    commandCenter.togglePlayPauseCommand.enabled = NO;
    
    // 停止接收远程控制事件
    [[UIApplication sharedApplication] endReceivingRemoteControlEvents];
    
    // 清空NowPlayingInfo
    MPNowPlayingInfoCenter *nowPlayingCenter = [MPNowPlayingInfoCenter defaultCenter];
    nowPlayingCenter.nowPlayingInfo = nil;
    
    // 取消防抖定时器，防止残留回调
    [self.pendingRemoteMuteTimer invalidate];
    self.pendingRemoteMuteTimer = nil;
    self.pendingRemoteMuteState = nil;
    
    self.remoteCommandsConfigured = NO;
    BoluoAudioLog(@"[BoluoMusicPlayerIOS] Remote commands removed and disabled");
}

- (void)cancelPendingRemoteMuteForSkip {
    if (![NSThread isMainThread]) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self cancelPendingRemoteMuteForSkip];
        });
        return;
    }
    // BoluoNowPlayingRemoteCommandPolicy.ShouldCancelPendingPlayPauseOnSkip
    [self.pendingRemoteMuteTimer invalidate];
    self.pendingRemoteMuteTimer = nil;
    self.pendingRemoteMuteState = nil;
}

- (void)_flushPendingRemoteMute {
    [self.pendingRemoteMuteTimer invalidate];
    self.pendingRemoteMuteTimer = nil;
    NSNumber *pending = self.pendingRemoteMuteState;
    self.pendingRemoteMuteState = nil;
    if (pending == nil) return;
    BOOL mute = [pending boolValue];
    // 对齐 BoluoNowPlayingRemoteCommandPolicy.ShouldSkipFlushBecauseAlreadyConsistent：
    // 已在播放时再点播放勿碰设备；暂停须同时 userManuallyPaused（冷启动 isMuted 初值 YES 不能早退）。
    BOOL alreadyConsistent = mute ? (self.isMuted && self.userManuallyPaused) : !self.isMuted;
    if (alreadyConsistent) {
        [self updateNowPlayingInfoCenter];  // 仅补 UI，不碰设备
        return;
    }
    [self setAudioMute:mute isUserAction:YES];
    NSString *go = self.unityGameObjectName ?: @"BoluoMusicPlayerIOS";
    UnitySendMessage([go UTF8String], mute ? "OnNowPlayingPause" : "OnNowPlayingPlay", "");
}

- (void)_reassertNowPlayingAndCommandsWithReason:(NSString *)reason {
    if (![NSThread isMainThread]) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self _reassertNowPlayingAndCommandsWithReason:reason];
        });
        return;
    }

    // 有些中断（电话）会让系统把 NowPlaying 卡片切到其它源，哪怕声音恢复了。
    // 这里显式把 remote commands 重新 enable，并刷新一次 NowPlayingInfo，尽量抢回控制中心显示。
    MPRemoteCommandCenter *commandCenter = [MPRemoteCommandCenter sharedCommandCenter];
    commandCenter.playCommand.enabled = YES;
    commandCenter.pauseCommand.enabled = YES;
    commandCenter.togglePlayPauseCommand.enabled = YES;

    [self setupRemoteCommandsIfNeeded];
    [self updateNowPlayingInfoCenter];
    BoluoAudioLog(@"[BoluoMusicPlayerIOS] Reassert NowPlaying/Commands (%@) done (isMuted=%@, userPaused=%@)",
          reason ?: @"unknown",
          self.isMuted ? @"YES" : @"NO",
          self.userManuallyPaused ? @"YES" : @"NO");
}

- (void)_scheduleExclusiveReclaimAfterInterruption {
    // 仅在“应当在播”的情况下尝试回到独占；用户手动暂停则不抢占
    if (self.userManuallyPaused) {
        return;
    }
    self.exclusiveReclaimRetryCount = 0;
    [NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(_attemptExclusiveReclaim) object:nil];
    // 电话挂断后的 teardown 窗口里直接独占会被系统拒绝，稍后再尝试
    [self performSelector:@selector(_attemptExclusiveReclaim) withObject:nil afterDelay:0.8];
}

- (void)_attemptExclusiveReclaim {
    if (![NSThread isMainThread]) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self _attemptExclusiveReclaim];
        });
        return;
    }

    if (self.userManuallyPaused) {
        return;
    }
    // 电话中断进行中仍勿抢占；RecoveringDevice 不再长期挡住 reclaim
    if (self.recoveryPhase == BoluoPlayerRecoveryPhaseInterruptionActive) {
        return;
    }
    // 后台默认不抢独占:reclaim 成功路径会顺带 unmute 复播(deferEngineUnmuteUntilExclusiveReclaim),
    // 在后台执行等于趁竞争音源瞬时停顿偷回焦点、打断用户正在用的程序。
    // 例外:backgroundResumeArmed=YES(静默确认通过,对方 App 已被杀/退出)时放行,
    // 否则对方被杀后菠萝只能干等回前台才恢复(用户明确要求后台自动续播)。
    if ([UIApplication sharedApplication].applicationState == UIApplicationStateBackground
        && !self.backgroundResumeArmed) {
        BoluoAudioLog(@"[BoluoAudioResume] Exclusive reclaim skipped: app in background");
        return;
    }

    AVAudioSession *session = [AVAudioSession sharedInstance];
    // 回前台：卡住 secondarySilence 不挡 reclaim（与 unmute / keepWaiting 对齐）
    BOOL ignoreStuckSecondary =
        [UIApplication sharedApplication].applicationState == UIApplicationStateActive;
    if ([self shouldDeferForCompetingOrSecondarySilenceIgnoringStuckSecondaryWhenActive:ignoreStuckSecondary]) {
        self.exclusiveReclaimRetryCount += 1;
        BoluoAudioLog(@"[BoluoAudioResume] Exclusive reclaim waiting otherAudio=%@ secondarySilence=%@ try=%ld",
              [session isOtherAudioPlaying] ? @"YES" : @"NO",
              session.secondaryAudioShouldBeSilencedHint ? @"YES" : @"NO",
              (long)self.exclusiveReclaimRetryCount);
        if (self.exclusiveReclaimRetryCount < 12) {
            [self performSelector:@selector(_attemptExclusiveReclaim) withObject:nil afterDelay:0.5];
        } else {
            // 快速 reclaim 耗尽后其它音源可能尚未暂停；转入慢速轮询，避免只能等回前台才恢复
            [self _scheduleWaitForCompetingAudioToStop];
        }
        return;
    }

    AVAudioSessionCategory category = [self categoryFromValue:self.sessionCategory];
    AVAudioSessionCategoryOptions options = [self optionsFromBits:self.sessionOptions];
    options &= ~AVAudioSessionCategoryOptionMixWithOthers;

    // setCategory/setActive 阻塞调用走串行音频队列，成功/重试处理回主线程
    dispatch_async(BoluoAudioWorkQueue(), ^{
        // 竞态兜底：入队后对方可能刚开始播放/录音，独占 reclaim 此刻会顶停对方。
        // Active 时忽略卡住 secondarySilence（与主线程 reclaim 门闩对齐）。
        BOOL ignoreStuckOnQueue =
            [UIApplication sharedApplication].applicationState == UIApplicationStateActive;
        if ([self shouldDeferForCompetingOrSecondarySilenceIgnoringStuckSecondaryWhenActive:ignoreStuckOnQueue]) {
            BoluoAudioLog(@"[BoluoAudioResume] Exclusive reclaim ABORT on queue: competing/secondary silence");
            dispatch_async(dispatch_get_main_queue(), ^{
                [self _scheduleWaitForCompetingAudioToStop];
            });
            return;
        }
        NSError *error = nil;
        [session setCategory:category mode:AVAudioSessionModeDefault options:options error:&error];
        if (error) {
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] Exclusive reclaim setCategory failed: %@", error.localizedDescription);
        }

        NSError *actErr = nil;
        BOOL ok = [session setActive:YES error:&actErr];
        dispatch_async(dispatch_get_main_queue(), ^{
            if (ok && !actErr) {
                BoluoAudioLog(@"[BoluoMusicPlayerIOS] Exclusive reclaim succeeded (options=0x%lx)", (unsigned long)options);
                self.exclusiveReclaimRetryCount = 0;
                [self _reassertNowPlayingAndCommandsWithReason:@"ExclusiveReclaimOK"];
                if (self.recoveryPhase == BoluoPlayerRecoveryPhaseAwaitingCompetingStop && self.isMuted) {
                    [self unmuteEngineIfExclusiveAvailableWithReason:@"ExclusiveReclaimOK"];
                }
                return;
            }

            BoluoAudioLog(@"[BoluoMusicPlayerIOS] Exclusive reclaim activation failed (try=%ld): %@",
                  (long)self.exclusiveReclaimRetryCount,
                  actErr ? actErr.localizedDescription : @"unknown");

            self.exclusiveReclaimRetryCount += 1;
            if (self.exclusiveReclaimRetryCount < 12) {
                [self performSelector:@selector(_attemptExclusiveReclaim) withObject:nil afterDelay:0.6];
            }
        });
    });
}

- (BOOL)shouldAttemptAutoResumeAfterCompetingAudio {
    if (!self.isInitialized) {
        return NO;
    }
    BoluoUnmuteVerdict v = [self unmuteVerdictForReason:@"shouldAttemptAutoResume"];
    if (v == BoluoUnmuteVerdictBlockedDisarmed || v == BoluoUnmuteVerdictBlockedUserPaused) {
        return NO;
    }
    // 有恢复意图：wantsResume / deviceDirty / recovery 非 None
    return self.wantsResumeAfterRecovery
        || self.deviceDirty
        || self.recoveryPhase != BoluoPlayerRecoveryPhaseNone;
}

/// Unity OnApplicationFocus/DoResume 与原生竞争音源恢复、中断恢复共用设备队列；并发 Reset + 主线程 SetMute 可触发 8BADF00D。
- (BOOL)shouldDeferUnityForegroundResume {
    // inFlight 优先：即使 isInitialized 短暂为 NO，也必须挡住 Unmute（退登后再进 Rebuild 窗口）。
    if (self.playbackDeviceRecoveryInFlight) {
        return YES;
    }
    BoluoUnmuteVerdict v = [self unmuteVerdictForReason:@"shouldDeferForeground"];
    if (v == BoluoUnmuteVerdictDeferRecovery) {
        return YES;
    }
    if (!self.isInitialized || self.userManuallyPaused) {
        return NO;
    }
    return [self shouldAttemptAutoResumeAfterCompetingAudio];
}

- (void)deferTryResumeWhenCompetingAudioStopped:(NSString *)reason {
    if (![self shouldAttemptAutoResumeAfterCompetingAudio]) {
        return;
    }
    BoluoAudioLog(@"[BoluoAudioResume] defer tryResume (%@)", reason ?: @"?");
    // 禁止在 AVAudioSession 通知栈内同步 tryResume：会触发 setActive/ResetPlaybackDevice/ma_device_stop，
    // 与 OutputCoreAudio 路由 observer 重入，主线程可阻塞 >10s 触发 8BADF00D（见 2026-07-10 104523 crash）。
    [NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(_deferredTryResumeWhenCompetingAudioStopped) object:nil];
    [self performSelector:@selector(_deferredTryResumeWhenCompetingAudioStopped) withObject:nil afterDelay:0];
}

- (void)_deferredTryResumeWhenCompetingAudioStopped {
    [self tryResumeWhenCompetingAudioStopped];
}

- (void)tryResumeWhenCompetingAudioStopped {
    if (![NSThread isMainThread]) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self tryResumeWhenCompetingAudioStopped];
        });
        return;
    }

    if (![self shouldAttemptAutoResumeAfterCompetingAudio]) {
        BoluoAudioLog(@"[BoluoAudioResume] Competing audio stopped but nothing to resume");
        self.backgroundSilenceConfirmStreak = 0;
        self.backgroundResumeArmed = NO;
        return;
    }

    AVAudioSession *session = [AVAudioSession sharedInstance];
    if ([self shouldKeepWaitingForCompetingAudio]) {
        BoluoAudioLog(@"[BoluoAudioResume] Competing/secondary silence still active, keep waiting (otherAudio=%@ secondarySilence=%@ sawOtherAudio=%@ sawSecondary=%@)",
              [session isOtherAudioPlaying] ? @"YES" : @"NO",
              session.secondaryAudioShouldBeSilencedHint ? @"YES" : @"NO",
              self.competingEpisodeSawOtherAudioPlaying ? @"YES" : @"NO",
              self.competingEpisodeSawSecondarySilence ? @"YES" : @"NO");
        [self noteCompetingAudioDetected:@"competingStillPlaying"];
        [self _penalizeBackgroundSilenceConfirmIfMisjudged];
        self.backgroundSilenceConfirmStreak = 0;
        self.backgroundResumeArmed = NO;
        [self _scheduleExclusiveReclaimAfterInterruption];
        [self _scheduleWaitForCompetingAudioToStop];
        return;
    }

    // 微信语音（ASR-only）：发完后仍在后台 → 保持暂停，等回菠萝前台再续。
    // 对齐 BoluoAudioFocusLossBranchPolicy.ShouldAllowBackgroundAutoResumeAfterCompetingAudioStopped。
    if (![self shouldAllowBackgroundAutoResumeAfterCompetingAudioStopped]) {
        BoluoAudioLog(@"[BoluoAudioResume] ASR-only episode — defer resume until foreground (sawSecondary=%@ sawOtherAudio=%@)",
              self.competingEpisodeSawSecondarySilence ? @"YES" : @"NO",
              self.competingEpisodeSawOtherAudioPlaying ? @"YES" : @"NO");
        self.backgroundSilenceConfirmStreak = 0;
        self.backgroundResumeArmed = NO;
        self.wantsResumeAfterRecovery = YES;
        [self noteCompetingAudioDetected:@"ASR-only-defer-foreground"];
        [NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(_pollWaitForCompetingAudioToStop) object:nil];
        self.competingAudioPollCount = 0;
        return;
    }

    // 竞争音源停了但菠萝在后台:先做「静默确认」再恢复,不趁瞬时间隙抢占。
    // - 误伤场景一(切歌间隙):对方切视频/切歌的 1-2s 停顿,0.5s 轮询立刻独占复播顶掉对方。
    // - 误伤场景二(灵动岛抢占,2026-07-20 用户反馈):用户在对方 App 里选内容/暂停看评论,
    //   静默常达十几秒,窗口太短会误判「对方已退出」→ 菠萝恢复+独占,对方再播时被顶,
    //   来回拉锯,体感就是「菠萝的灵动岛抢占其他程序」。
    // - 需求(上轮):对方 App 被杀死/退出后,菠萝应在后台自动恢复,不能死等回前台;
    //   对方被杀时系统常不发 InterruptionEnded,只能靠轮询判定。
    // 平衡:连续 backgroundSilenceConfirmPollsRequired 次(0.5s/次,默认 60 次=30s)无其它
    // 音频才放行;期间对方一出声即重置。若恢复后短窗内又被打断(误判对方还活着),
    // 确认窗口翻倍退避(见 _penalizeBackgroundSilenceConfirmIfMisjudged),防反复拉锯。
    // 真中断恢复(电话/Siri/闹钟,InterruptionEnded shouldResume=YES)走
    // handleAudioInterruption → scheduleInterruptionRecovery,不经本路径,不受影响。
    if ([UIApplication sharedApplication].applicationState == UIApplicationStateBackground
        && !self.backgroundResumeArmed) {
        NSInteger requiredPolls = MAX(self.backgroundSilenceConfirmPollsRequired, 1);
        self.backgroundSilenceConfirmStreak += 1;
        if (self.backgroundSilenceConfirmStreak < requiredPolls) {
            // 每 10 次(5s)打一条,避免长窗口刷屏
            if (self.backgroundSilenceConfirmStreak % 10 == 1) {
                BoluoAudioLog(@"[BoluoAudioResume] Competing audio stopped in BACKGROUND — confirming silence %ld/%ld",
                      (long)self.backgroundSilenceConfirmStreak, (long)requiredPolls);
            }
            [NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(_pollWaitForCompetingAudioToStop) object:nil];
            [self performSelector:@selector(_pollWaitForCompetingAudioToStop) withObject:nil afterDelay:0.5];
            return;
        }
        self.backgroundResumeArmed = YES;
        self.lastBackgroundAutoResumeEpoch = [[NSDate date] timeIntervalSince1970];
        BoluoAudioLog(@"[BoluoAudioResume] Background silence confirmed (%.1fs) — competing app gone, resuming",
              requiredPolls * 0.5);
        [self logAudioResumeSnapshot:@"competing-stopped-background-confirmed"];
    }
    self.backgroundSilenceConfirmStreak = 0;

    BoluoAudioLog(@"[BoluoAudioResume] Competing audio gone — resuming Boluo playback");

    [NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(_pollWaitForCompetingAudioToStop) object:nil];
    self.competingAudioPollCount = 0;
    [self noteInterruptionEnded:@"competingResume"];
    self.resumeActivationUsesMixWithOthers = NO;
    [self noteCompetingAudioCleared:@"competingResume"];

    int state = GetPlaybackState();
    BOOL engineDown = (state == 0 || state == 1);
    BOOL needsDeviceRecovery = engineDown || self.deviceDirty;
    self.resumeActivationUsesMixWithOthers = needsDeviceRecovery;

    [self attemptSessionActivationWithRetry:^(BOOL success) {
        if (!success) {
            BoluoAudioLog(@"[BoluoAudioResume] Competing audio resume: session activation failed");
            self.wantsResumeAfterRecovery = YES;
            [self _scheduleWaitForCompetingAudioToStop];
            return;
        }
        [self restoreExclusiveAudioSessionForResume:YES];
        [self _reassertNowPlayingAndCommandsWithReason:@"CompetingAudioStopped"];
        [self _scheduleExclusiveReclaimAfterInterruption];

        if (needsDeviceRecovery) {
            [self scheduleInterruptionRecovery];
        } else if (self.isMuted) {
            [self finishResumeAfterDeviceReady];
        } else {
            self.wantsResumeAfterRecovery = NO;
            self.deviceDirty = NO;
            // 本就未静音，无需 unmute；后台放行标志随本轮结束复位
            self.backgroundResumeArmed = NO;
            self.competingEpisodeSawOtherAudioPlaying = NO;
            self.competingEpisodeSawSecondarySilence = NO;
        }
    }];
}

/// 对齐 C# ShouldAllowBackgroundAutoResumeAfterCompetingAudioStopped：
/// ASR-only（曾见 secondarySilence、从未见 otherAudio）后台禁止自动续播。
- (BOOL)shouldAllowBackgroundAutoResumeAfterCompetingAudioStopped {
    if ([UIApplication sharedApplication].applicationState != UIApplicationStateBackground) {
        return YES;
    }
    if (self.competingEpisodeSawSecondarySilence && !self.competingEpisodeSawOtherAudioPlaying) {
        return NO;
    }
    return YES;
}

/// 后台自动恢复后短窗内又被竞争音源打断 → 上次「对方已退出」是误判（对方 App 还活着，
/// 只是长静默）。把静默确认窗口翻倍（封顶 5min），防止「菠萝恢复→对方再播→又被顶」拉锯。
/// 回前台（handleAppDidBecomeActive）重置为默认 30s。
- (void)_penalizeBackgroundSilenceConfirmIfMisjudged {
    if (self.lastBackgroundAutoResumeEpoch <= 0) {
        return;
    }
    NSTimeInterval elapsed = [[NSDate date] timeIntervalSince1970] - self.lastBackgroundAutoResumeEpoch;
    self.lastBackgroundAutoResumeEpoch = 0;
    if (elapsed >= 0 && elapsed < 30.0) {
        NSInteger doubled = MAX(self.backgroundSilenceConfirmPollsRequired, 60) * 2;
        self.backgroundSilenceConfirmPollsRequired = MIN(doubled, 600);
        BoluoAudioLog(@"[BoluoAudioResume] Background resume misjudged (re-interrupted after %.1fs) — confirm window now %.0fs",
              elapsed, self.backgroundSilenceConfirmPollsRequired * 0.5);
    }
}

- (void)_scheduleWaitForCompetingAudioToStop {
    if (![self shouldAttemptAutoResumeAfterCompetingAudio]) {
        return;
    }
    [NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(_pollWaitForCompetingAudioToStop) object:nil];
    self.competingAudioPollCount = 0;
    [self performSelector:@selector(_pollWaitForCompetingAudioToStop) withObject:nil afterDelay:0.5];
}

- (void)_pollWaitForCompetingAudioToStop {
    if (![self shouldAttemptAutoResumeAfterCompetingAudio]) {
        self.competingAudioPollCount = 0;
        self.backgroundSilenceConfirmStreak = 0;
        self.backgroundResumeArmed = NO;
        return;
    }

    if (![self shouldKeepWaitingForCompetingAudio]) {
        [self tryResumeWhenCompetingAudioStopped];
        return;
    }

    // 对方重新出声或系统要求次要静音：静默确认作废，重新走完整确认窗口
    self.backgroundSilenceConfirmStreak = 0;
    self.backgroundResumeArmed = NO;
    self.competingAudioPollCount += 1;
    // 看门狗 30 分钟：对方被杀死/退出常无任何系统通知（InterruptionEnded 不保证），只能靠
    // 本轮询发现静默后自动续播。旧值 90s 覆盖不了「刷半小时视频再杀掉」的真实使用；
    // 轮询体仅一次 BOOL 查询，且进程被系统 suspend 时 performSelector 自动挂起，无耗电顾虑。
    if (self.competingAudioPollCount < 3600) {
        [self performSelector:@selector(_pollWaitForCompetingAudioToStop) withObject:nil afterDelay:0.5];
    } else {
        BoluoAudioLog(@"[BoluoAudioResume] Competing audio poll watchdog expired (30min)");
        self.competingAudioPollCount = 0;
    }
}

- (void)handleSilenceSecondaryAudioHint:(NSNotification *)notification {
    if (![NSThread isMainThread]) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self handleSilenceSecondaryAudioHint:notification];
        });
        return;
    }

    NSNumber *typeValue = notification.userInfo[AVAudioSessionSilenceSecondaryAudioHintTypeKey];
    if (typeValue == nil) {
        return;
    }

    AVAudioSessionSilenceSecondaryAudioHintType type =
        (AVAudioSessionSilenceSecondaryAudioHintType)[typeValue unsignedIntegerValue];
    if (type == AVAudioSessionSilenceSecondaryAudioHintTypeBegin) {
        // 若误走了 RD 快速自恢复，立刻取消，避免与微信录音抢会话
        if (self.pendingRouteDisconnectedRecoveryBlock) {
            dispatch_block_cancel(self.pendingRouteDisconnectedRecoveryBlock);
            self.pendingRouteDisconnectedRecoveryBlock = nil;
            BoluoAudioLog(@"[BoluoAudioResume] SilenceSecondary BEGIN — cancel pending RD self-resume");
        }
        // 刚做完后台自动恢复又被压制 → 静默窗口误判，退避加倍
        [self _penalizeBackgroundSilenceConfirmIfMisjudged];
        // 若误走了 RD 快速自恢复，立刻取消，避免约 1s 后独占抢会话顶掉微信录音
        if (self.pendingRouteDisconnectedRecoveryBlock) {
            dispatch_block_cancel(self.pendingRouteDisconnectedRecoveryBlock);
            self.pendingRouteDisconnectedRecoveryBlock = nil;
            BoluoAudioLog(@"[BoluoAudioResume] SilenceSecondary BEGIN — cancelled pending RD self-resume");
        }
        [NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(_pollResumeWhenDeviceIdleAfterSystemMute) object:nil];
        self.deviceIdleResumePollCount = 0;
        BOOL engineStartedSafe = NO;
        if (!BoluoAudio_IsPlaybackDeviceRecoveryInFlight()) {
            int engineStateEarly = GetPlaybackState();
            engineStartedSafe = (engineStateEarly == 2 /*Started*/ || engineStateEarly == 3 /*Starting*/)
                && !sBoluoEngineHardMuted;
        }
        // 启动期 isInitialized 仍可能为 NO，但引擎已经在响：切到抖音 / QQ 必须停。
        if (!self.userManuallyPaused && (self.isInitialized || engineStartedSafe)) {
            // Phase 3：wasAudible = 记账 || 引擎真值（记账漂移时「负一屏没了还在播」仍要静音；
            // 对齐 WasAudibleForSecondarySilenceBegin）。设备恢复 inFlight 时不读
            // GetPlaybackState（与 Reset 争锁），引擎真值按 NO 计。硬 mute 后也不算还在响。
            BOOL wasAudible = self.isPlaying || !self.isMuted || engineStartedSafe;
            BoluoAudioLog(@"[BoluoAudioResume] SilenceSecondaryAudioHint BEGIN wasAudible=%@ engineStartedSafe=%@",
                  wasAudible ? @"YES" : @"NO", engineStartedSafe ? @"YES" : @"NO");
            if ([[AVAudioSession sharedInstance] isOtherAudioPlaying]) {
                self.competingEpisodeSawOtherAudioPlaying = YES;
            }
            // SilenceSecondary BEGIN 即 ASR/录音/部分语音播放抢麦信号
            self.competingEpisodeSawSecondarySilence = YES;
            if (wasAudible) {
                self.wantsResumeAfterRecovery = YES;
                [self noteCompetingAudioDetected:@"silenceSecondaryBegin"];
                // 满音量硬 SetMute 会呲啦 → 统一走原生护栏（同步降增益 → 50ms → mute → yield）。
                [self beginSecondarySilenceCompetingEpisodeFromSignal:@"SilenceSecondaryBegin"];
            }
        }
        return;
    }
    if (type == AVAudioSessionSilenceSecondaryAudioHintTypeEnd) {
        BoluoAudioLog(@"[BoluoAudioResume] SilenceSecondaryAudioHint END");
        [self deferTryResumeWhenCompetingAudioStopped:@"SilenceSecondaryAudioHintEnd"];
    }
}

- (void)startAudioInterruptionObserver {
    [[NSNotificationCenter defaultCenter] addObserver:self
                                             selector:@selector(handleAudioInterruption:)
                                                 name:AVAudioSessionInterruptionNotification
                                               object:[AVAudioSession sharedInstance]];

    [[NSNotificationCenter defaultCenter] addObserver:self
                                             selector:@selector(handleRouteChange:)
                                                 name:AVAudioSessionRouteChangeNotification
                                               object:[AVAudioSession sharedInstance]];

    [[NSNotificationCenter defaultCenter] addObserver:self
                                             selector:@selector(handleSilenceSecondaryAudioHint:)
                                                 name:AVAudioSessionSilenceSecondaryAudioHintNotification
                                               object:[AVAudioSession sharedInstance]];

    [[NSNotificationCenter defaultCenter] addObserver:self
                                             selector:@selector(handleAppDidBecomeActive:)
                                                 name:UIApplicationDidBecomeActiveNotification
                                               object:nil];
    [[NSNotificationCenter defaultCenter] addObserver:self
                                             selector:@selector(handleAppWillResignActive:)
                                                 name:UIApplicationWillResignActiveNotification
                                               object:nil];
    [[NSNotificationCenter defaultCenter] addObserver:self
                                             selector:@selector(handleFocusNotifySettingChanged:)
                                                 name:@"BoluoAudioFocusNotifyChanged"
                                               object:nil];
    [self pushFocusNotifySettingFromUserDefaults];
}

- (void)pushFocusNotifySettingFromUserDefaults {
    NSUserDefaults *defs = [NSUserDefaults standardUserDefaults];
    BOOL enabled = YES;
    if ([defs objectForKey:@"boluo_focus_notify_enabled"] != nil) {
        enabled = [defs boolForKey:@"boluo_focus_notify_enabled"];
    }
    NSString *payload = [NSString stringWithFormat:@"focusNotify=%d", enabled ? 1 : 0];
    NSString *go = self.unityGameObjectName ?: @"BoluoMusicPlayerIOS";
    UnitySendMessage([go UTF8String], "SetUserAudioSettings", [payload UTF8String]);
}

- (void)handleFocusNotifySettingChanged:(NSNotification *)notification {
    NSNumber *enabledNum = notification.userInfo[@"enabled"];
    BOOL enabled = enabledNum != nil ? enabledNum.boolValue : YES;
    NSString *payload = [NSString stringWithFormat:@"focusNotify=%d", enabled ? 1 : 0];
    NSString *go = self.unityGameObjectName ?: @"BoluoMusicPlayerIOS";
    UnitySendMessage([go UTF8String], "SetUserAudioSettings", [payload UTF8String]);
}

- (NSString *)appStateString:(UIApplicationState)state {
    switch (state) {
        case UIApplicationStateActive: return @"Active";
        case UIApplicationStateInactive: return @"Inactive";
        case UIApplicationStateBackground: return @"Background";
        default: return @"Unknown";
    }
}

- (void)logAudioResumeSnapshot:(NSString *)tag {
    // 禁止在 AVAudioSession 通知栈 / 主线程音频热路径上同步 GetPlaybackState / 拼 route：
    // GetPlaybackState 可能与 session-work 上 Reset 争锁 → 卡死。
    // 一律丢到独立 resume-log 队列；BLS 经 Notification 异步上报（Swift 侧限流）。
    NSString *tagCopy = [tag copy] ?: @"?";
    BOOL isMuted = self.isMuted;
    BOOL isPlaying = self.isPlaying;
    BOOL userPaused = self.userManuallyPaused;
    BOOL interruption = (self.recoveryPhase == BoluoPlayerRecoveryPhaseInterruptionActive);
    BOOL pendingResume = self.wantsResumeAfterRecovery;
    BOOL restoring = (self.recoveryPhase == BoluoPlayerRecoveryPhaseRecoveringDevice);
    BOOL deviceReset = self.deviceDirty;
    BOOL inFlight = self.playbackDeviceRecoveryInFlight;
    UIApplicationState uiState = [UIApplication sharedApplication].applicationState;

    dispatch_async(BoluoAudioResumeLogQueue(), ^{
        // inFlight 时跳过引擎状态查询，避免与 Reset 争锁。
        int playbackState = -2; // -2 = skipped while inFlight
        if (!inFlight) {
            playbackState = GetPlaybackState();
        }

        AVAudioSession *session = [AVAudioSession sharedInstance];
        NSMutableString *routeSummary = [NSMutableString string];
        // Unity/IL2CPP 默认关闭 ObjC 异常（-fno-objc-exceptions），不能用 @try/@catch。
        for (AVAudioSessionPortDescription *output in session.currentRoute.outputs) {
            [routeSummary appendFormat:@"%@(%@);", output.portType ?: @"?", output.portName ?: @"?"];
        }
        if (routeSummary.length == 0) {
            [routeSummary appendString:@"none"];
        }

        NSString *engineName = (playbackState == -2)
            ? @"SkippedInFlight"
            : BoluoPlaybackStateName(playbackState);

        NSString *line = [NSString stringWithFormat:
            @"[BoluoAudioResume] SNAPSHOT tag=%@ | UI=%@ | engine=%@ | native isMuted=%@ isPlaying=%@ userPaused=%@ interruption=%@ pendingResume=%@ restoring=%@ deviceReset=%@ inFlight=%@ | session category=%@ options=0x%lx otherAudio=%@ secondarySilenceHint=%@ sampleRate=%.0f | route=%@",
            tagCopy,
            [self appStateString:uiState],
            engineName,
            isMuted ? @"YES" : @"NO",
            isPlaying ? @"YES" : @"NO",
            userPaused ? @"YES" : @"NO",
            interruption ? @"YES" : @"NO",
            pendingResume ? @"YES" : @"NO",
            restoring ? @"YES" : @"NO",
            deviceReset ? @"YES" : @"NO",
            inFlight ? @"YES" : @"NO",
            session.category ?: @"?",
            (unsigned long)session.categoryOptions,
            [session isOtherAudioPlaying] ? @"YES" : @"NO",
            session.secondaryAudioShouldBeSilencedHint ? @"YES" : @"NO",
            session.sampleRate,
            routeSummary];

        // 控制台：DEBUG 走 BoluoAudioLog；Release 仍打一条精简 NSLog 便于真机 Console 过滤。
        BoluoAudioLog(@"%@", line);
#if !DEBUG
        NSLog(@"%@", line);
#endif

        // BLS：不在本队列做网络；抛通知给壳进程 Swift（限流在 Observer）。
        NSDictionary *info = @{
            @"tag": tagCopy,
            @"message": line,
            @"level": @"info",
        };
        dispatch_async(dispatch_get_main_queue(), ^{
            [[NSNotificationCenter defaultCenter] postNotificationName:@"BoluoAudioResumeBLSLogNotification"
                                                                object:nil
                                                              userInfo:info];
        });
    });
}

/// 焦点抢占专用上云：异步拼快照，绝不在 AVAudioSession 通知栈做网络 / GetPlaybackState。
- (void)reportAudioFocusLossToBLSWithTag:(NSString *)tag
                                  detail:(NSString *)detail
                              durationMs:(CFTimeInterval)durationMs {
    NSString *tagCopy = [tag copy] ?: @"audio_focus_loss";
    NSString *detailCopy = detail.length > 0 ? [detail copy] : @"";
    CFTimeInterval durationCopy = durationMs;
    BOOL isMuted = self.isMuted;
    BOOL isPlaying = self.isPlaying;
    BOOL userPaused = self.userManuallyPaused;
    BOOL pendingResume = self.wantsResumeAfterRecovery;
    BOOL inFlight = self.playbackDeviceRecoveryInFlight;
    UIApplicationState uiState = [UIApplication sharedApplication].applicationState;

    dispatch_async(BoluoAudioResumeLogQueue(), ^{
        AVAudioSession *session = [AVAudioSession sharedInstance];
        NSString *line = [NSString stringWithFormat:
            @"[BoluoAudioFocus] LOSS tag=%@ durationMs=%.1f | UI=%@ isMuted=%@ isPlaying=%@ userPaused=%@ pendingResume=%@ inFlight=%@ otherAudio=%@ secondarySilence=%@ | %@",
            tagCopy,
            durationCopy,
            [self appStateString:uiState],
            isMuted ? @"YES" : @"NO",
            isPlaying ? @"YES" : @"NO",
            userPaused ? @"YES" : @"NO",
            pendingResume ? @"YES" : @"NO",
            inFlight ? @"YES" : @"NO",
            [session isOtherAudioPlaying] ? @"YES" : @"NO",
            session.secondaryAudioShouldBeSilencedHint ? @"YES" : @"NO",
            detailCopy];
        BoluoAudioLog(@"%@", line);
#if !DEBUG
        NSLog(@"%@", line);
#endif
        NSMutableDictionary *info = [@{
            @"tag": tagCopy,
            @"message": line,
            @"level": @"warn",
            @"event": @"boluo_audio_focus_loss",
            @"durationMs": @(durationCopy),
        } mutableCopy];
        if (detailCopy.length > 0) {
            info[@"detail"] = detailCopy;
        }
        dispatch_async(dispatch_get_main_queue(), ^{
            [[NSNotificationCenter defaultCenter] postNotificationName:@"BoluoAudioResumeBLSLogNotification"
                                                                object:nil
                                                              userInfo:info];
        });
    });

    // 处理路径本身 ≥100ms 时同步走 jank 通道，便于与 wanos_audio_jank 交叉过滤。
    if (durationMs >= 100.0) {
        BoluoNativeAudioJank_Report(tagCopy, durationMs, detailCopy);
    }
}

- (void)tryResumePlaybackAfterReturningToForeground {
    if (BoluoAudioSession_IsLogoutSilenced() != 0) {
        BoluoAudioLog(@"[BoluoAudioResume] Foreground resume BLOCKED logoutSilenced");
        return;
    }
    BoluoAudioLog(@"[BoluoAudioResume] Foreground resume requested");
    [self tryResumeWhenCompetingAudioStopped];
}

- (void)handleAppDidBecomeActive:(NSNotification *)notification {
    BoluoAudioLog(@"[BoluoAudioResume] UIApplicationDidBecomeActive");
    [self logAudioResumeSnapshot:@"AppDidBecomeActive"];
    // 已回前台=新决策：作废后台世代全部陈旧回调（ResignActive 兜底、残留护栏 mute 落地等）。
    // 下拉控制中心/横幅等短暂失焦同样经此路径清兜底。后续增益/静音由本方法内的
    // clearTransientDuckOnForegroundReturn + tryResume 链收敛。
    BoluoFocusEpochBump(@"app_did_become_active");
    // 回前台走正常恢复链，后台静默确认状态作废，退避窗口恢复默认（新一轮后台会话重新起算）
    self.backgroundSilenceConfirmStreak = 0;
    self.backgroundResumeArmed = NO;
    self.backgroundSilenceConfirmPollsRequired = 60;
    self.lastBackgroundAutoResumeEpoch = 0;
    // 回前台且当前已是内建路由：清掉「外接→内建」记忆，避免后续微信录音误走 RD 快速链
    if (![self routeDescriptionHasExternalOutput:[AVAudioSession sharedInstance].currentRoute]) {
        self.recentlyHadExternalAudioRoute = NO;
        self.lastExternalToBuiltInRouteEpoch = 0;
    }
    // 回前台时若对方已不在播，勿让上一轮「见过 otherAudio / secondarySilence」永久粘住。
    // Active 且无 otherAudio：即便 secondarySilence hint 卡住也清 episode（微信常不发 END），
    // 否则 tryResume / unmute 会被卡住 hint 永久 defer → 回岛无声。
    {
        AVAudioSession *sess = [AVAudioSession sharedInstance];
        BOOL otherAudio = [sess isOtherAudioPlaying];
        BOOL secondary = sess.secondaryAudioShouldBeSilencedHint;
        if (!otherAudio && (!secondary
                            || [UIApplication sharedApplication].applicationState == UIApplicationStateActive)) {
            self.competingEpisodeSawOtherAudioPlaying = NO;
            self.competingEpisodeSawSecondarySilence = NO;
        }
    }
    // 切微信等常走 transient_duck 且没有可靠 InterruptionEnded → Unity 增益粘在 0.4；
    // 回前台必须清 duck，否则回岛切预设听感一直偏小。
    [self clearTransientDuckOnForegroundReturn];

    // QQ 音乐等：回前台时 otherAudio 常仍为 YES；旧逻辑 defer → 焦点留在 QQ。
    // 用户主动切回菠萝 → 强制独占抢回（可顶停对方），再 tryResume unmute。
    // 注意（2026-07-23 复发根因）：不能再用 !interruptionActive 当「非电话中断」判据——
    // QQ 等音乐 App 接管后系统不会发 InterruptionEnded，回前台时 interruptionActive 仍挂着，
    // 旧门条件把强抢路径全部挡死（焦点留在 QQ + 负一屏卡片不回）。电话/闹钟等真中断的保护
    // 交给系统仲裁：真中断里独占 setActive 必因 '!pri' 失败，强抢不会生效（见 restoreExclusive）。
    BOOL hasResumeIntent = [self shouldAttemptAutoResumeAfterCompetingAudio]
        || self.isMuted
        || (self.isInitialized && self.nowPlayingTitle != nil && !self.userManuallyPaused);
    // 不看 InterruptionActive：切到 QQ / 抖音后系统经常不发 InterruptionEnded，
    // 回前台这面旗还挂着，强抢和负一屏都会被挡死。电话中断由 setActive 失败兜住。
    BOOL forceReclaim = !self.userManuallyPaused
        && self.isInitialized
        && hasResumeIntent;
    if (forceReclaim) {
        BoluoAudioLog(@"[BoluoAudioResume] AppDidBecomeActive force exclusive reclaim (otherAudio=%@)",
              [[AVAudioSession sharedInstance] isOtherAudioPlaying] ? @"YES" : @"NO");
        [self restoreExclusiveAudioSessionForResume:YES];
    }

    [self tryResumePlaybackAfterReturningToForeground];
    // 关键修复：切到其它音频 App 后，系统会把 Now Playing 归属让给对方；回到波萝时即便引擎仍在
    // 出声（场景音效/音乐已由中断恢复路径拉起），tryResume 会因「nothing to resume」直接早退，
    // 于是我们再也不会重新 push nowPlayingInfo，负一屏（控制中心/锁屏/Today）的波萝卡片就此消失。
    // 在混合壳里 Unity 的 OnApplicationFocus 依赖 Unity 窗口成为 key，并不一定触发，无法兜底。
    // 因此这里在 OS 级别的 becomeActive 上无条件抢回卡片，不依赖是否需要「恢复播放」。
    [self reassertNowPlayingOnForegroundIfNeeded];
}

/// 回前台清除 transient duck（含 escalate 定时器）；Unity Unduck 幂等。
- (void)clearTransientDuckOnForegroundReturn {
    if (self.pendingDuckEscalateBlock) {
        dispatch_block_cancel(self.pendingDuckEscalateBlock);
        self.pendingDuckEscalateBlock = nil;
    }
    BOOL wasDucked = (self.recoveryPhase == BoluoPlayerRecoveryPhaseDucked);
    if (wasDucked) {
        [self noteDuckEnded:@"foregroundClear"];
    }
    // 即 wasDucked=NO 也通知 Unity：Ended 丢失时 C# 侧 s_ducked 仍可能为 YES。
    NSString *go = self.unityGameObjectName ?: @"BoluoMusicPlayerIOS";
    UnitySendMessage([go UTF8String], "OnAudioFocusUnduck", "");
    if (wasDucked) {
        BoluoAudioLog(@"[BoluoAudioDbg] evt=audio-duck-clear reason=AppDidBecomeActive");
/// 回前台清除 transient duck（含 escalate 定时器）；增益还原走唯一写入者（幂等）。
    [self cancelTransientDuckEscalate];
    // 仍静音时禁止拉回 1.0：QQ 切回时 force reclaim + unmute 窗口若满增益，
    // SetMute(false)/设备重建会偶现短促电流声，并污染随后微信发语音的 mute 听感。
    // 保持护栏近静音，等 unmute 爬坡再回满。
    if (self.isMuted || self.userManuallyPaused) {
        unsigned long long objId = WanosMusicPlayer_GetConfiguredObjectId();
        if (objId != 0) {
            BoluoApplyMusicGain(kBoluoGuardedMuteGainFactor, true, @"foreground_clear_keep_muted");
        }
    } else {
        // 未静音：Ended 丢失/爬坡被作废时引擎可能仍停在衰减增益 → 无条件还原。
        BoluoApplyMusicGain(1.0f, false, @"foreground_clear");
    }
        NSLog(@"[BoluoAudioDbg] evt=audio-duck-clear reason=AppDidBecomeActive");
        [self reportAudioFocusLossToBLSWithTag:@"audio_duck_end"
                                        detail:@"foreground_clear"
                                    durationMs:0];
        if (!self.isMuted && !self.userManuallyPaused) {
            [self noteFocusState:BoluoAudioFocusStateNormal reason:@"foreground_clear_duck"];
        }
    }
    BoluoAudioLog(@"[BoluoAudioDuck] foreground clear unduck wasNativeDucked=%@",
          wasDucked ? @"YES" : @"NO");
}

- (void)reassertNowPlayingOnForegroundIfNeeded {
    if (![NSThread isMainThread]) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self reassertNowPlayingOnForegroundIfNeeded];
        });
        return;
    }

    // 未登录/未初始化不接管 Now Playing。
    if (!self.isInitialized) {
        return;
    }
    // 后台被拉起（非用户主动回前台）时不抢占，避免在后台意外占用音频焦点。
    if ([UIApplication sharedApplication].applicationState == UIApplicationStateBackground) {
        return;
    }
    // 上层策略（如禁用后台播放 backgroundPlayDisable）会显式清空 Now Playing 并移除远程命令，
    // 此时缓存的 nowPlayingTitle 被置为 nil。尊重该「主动清空」意图，不要在回前台时又把卡片补回来。
    if (self.nowPlayingTitle == nil) {
        BoluoAudioLog(@"[BoluoAudioResume] reassertNowPlaying skip: nowPlayingTitle cleared (policy)");
        return;
    }

    // 人已经回到波萝（Active）：负一屏必须出现，不能因为对方还在播就跳过。
    // 还在 Inactive / 后台时才避让，避免下拉控制中心就把卡片抢走。
    BOOL active = [UIApplication sharedApplication].applicationState == UIApplicationStateActive;
    if (!active
        && [self shouldDeferForCompetingOrSecondarySilenceIgnoringStuckSecondaryWhenActive:YES]) {
        BoluoAudioLog(@"[BoluoAudioResume] reassertNowPlaying skip: competing/secondary silence");
        [self logAudioResumeSnapshot:@"reassertNowPlaying-skip-otherAudio"];
        return;
    }

    // 启动占位还没真正出声：回前台先按播放中出卡，不等 Unity 揭罩。
    if (active && !self.userManuallyPaused
        && BoluoAudioSession_IsStartupAudibleCommitted() == 0) {
        self.nowPlayingAdvertisedPlaying = YES;
    }
    // 若其它音源已停，顺带把独占 Playback 会话与音频焦点抢回来（isOtherAudioPlaying 时内部会跳过，不强抢）。
    [self restoreExclusiveAudioSessionForResume:NO];
    // 重新启用并注册远程命令 + 按真实 isMuted 状态刷新 nowPlayingInfo（播放=Rate1/暂停=Rate0）。
    [self _reassertNowPlayingAndCommandsWithReason:@"AppDidBecomeActive"];
    BoluoAudioLog(@"[BoluoAudioResume] reassertNowPlaying on foreground done (isMuted=%@ userPaused=%@)",
          self.isMuted ? @"YES" : @"NO",
          self.userManuallyPaused ? @"YES" : @"NO");
}

- (void)handleAppWillResignActive:(NSNotification *)notification {
    BoluoAudioLog(@"[BoluoAudioResume] UIApplicationWillResignActive");
    [self logAudioResumeSnapshot:@"AppWillResignActive"];
    // 启动期还没在前台稳定出过声：一切走就停。回前台由 becomeActive / Unity 再拉起。
    // 已出过声的后台续播不走这里，仍交给中断与真值对账。
    if (BoluoAudioSession_IsStartupAudibleCommitted() == 0) {
        // 占位卡片还没真正出声：一切走就收成暂停，别占着抖音 / QQ 的负一屏。
        self.nowPlayingAdvertisedPlaying = NO;
        [self updateNowPlayingInfoCenter];
    }
    if (BoluoAudioSession_IsStartupAudibleCommitted() == 0 && !self.userManuallyPaused) {
        BOOL engineHot = NO;
        if (!BoluoAudio_IsPlaybackDeviceRecoveryInFlight() && !sBoluoEngineHardMuted) {
            int st = GetPlaybackState();
            engineHot = (st == 2 || st == 3);
        }
        if (engineHot || self.isPlaying || !self.isMuted) {
            self.wantsResumeAfterRecovery = YES;
            BoluoAudioLog(@"[BoluoAudioResume] startup resign — mute so foreground app can play");
            [self guardedSystemMuteWithReason:@"startup_resign" completion:nil];
        }
    }
    // 兜底：微信发语音偶现收不到 SilenceSecondary BEGIN / Interruption Began（或记账漂移）
    // → 音乐不静音、负一屏被对方接管却仍出声。Phase 3 起本兜底不再是独立逻辑，
    // 只是真值对账的一个调用点：离开前台 1s 后跑一次对账，自愈条件（非 Active + hint +
    // 非用户暂停 + 记账/引擎显示在播且状态并非静默 episode）由 _runFocusTruthAudit 统一判定。
    // 窗口内若已有 owner 收敛（护栏静音落地 → CompetingWait + muted），对账自然判定无需补静音。
    [self scheduleFocusTruthAuditWithDelay:kBoluoResignActiveSecondaryBackstopDelaySeconds
                                    reason:@"resign_active_backstop"];
}

- (void)stopAudioInterruptionObserver {
    [[NSNotificationCenter defaultCenter] removeObserver:self
                                                    name:AVAudioSessionInterruptionNotification
                                                  object:[AVAudioSession sharedInstance]];

    [[NSNotificationCenter defaultCenter] removeObserver:self
                                                    name:AVAudioSessionRouteChangeNotification
                                                  object:[AVAudioSession sharedInstance]];

    [[NSNotificationCenter defaultCenter] removeObserver:self
                                                    name:AVAudioSessionSilenceSecondaryAudioHintNotification
                                                  object:[AVAudioSession sharedInstance]];
}

// REMOVED handleRemotePause and handleRemotePlay to match WanosAudioSession.mm structure exactly.
// Logic is now inlined in setupRemoteCommandsIfNeeded blocks.

- (void)handleRouteChange:(NSNotification *)notification {
    if (![NSThread isMainThread]) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self handleRouteChange:notification];
        });
        return;
    }

    NSNumber *reasonNumber = notification.userInfo[AVAudioSessionRouteChangeReasonKey];
    AVAudioSessionRouteChangeReason reason = (AVAudioSessionRouteChangeReason)[reasonNumber unsignedIntegerValue];

    NSString *reasonString = @"Unknown";
    switch (reason) {
        case AVAudioSessionRouteChangeReasonNewDeviceAvailable: reasonString = @"NewDeviceAvailable"; break;
        case AVAudioSessionRouteChangeReasonOldDeviceUnavailable: reasonString = @"OldDeviceUnavailable"; break;
        case AVAudioSessionRouteChangeReasonCategoryChange: reasonString = @"CategoryChange"; break;
        case AVAudioSessionRouteChangeReasonOverride: reasonString = @"Override"; break;
        case AVAudioSessionRouteChangeReasonWakeFromSleep: reasonString = @"WakeFromSleep"; break;
        case AVAudioSessionRouteChangeReasonNoSuitableRouteForCategory: reasonString = @"NoSuitableRoute"; break;
        case AVAudioSessionRouteChangeReasonRouteConfigurationChange: reasonString = @"RouteConfigurationChange"; break;
        default: reasonString = @"Unknown"; break;
    }

    // 检查当前路由的输出设备状态（用于处理单个 AirPods 摘下等情况）
    AVAudioSession *session = [AVAudioSession sharedInstance];
    AVAudioSessionRouteDescription *currentRoute = session.currentRoute;
    NSArray<AVAudioSessionPortDescription *> *outputs = currentRoute.outputs;
    
    BOOL hasValidOutput = NO;
    NSMutableString *outputInfo = [NSMutableString string];
    if (outputs.count > 0) {
        // 检查是否有有效的输出设备（排除空字符串端口类型）
        for (AVAudioSessionPortDescription *output in outputs) {
            NSString *portType = output.portType;
            NSString *portName = output.portName;
            NSString *uid = output.UID;
            [outputInfo appendFormat:@"[type=%@, name=%@, uid=%@] ", portType ?: @"nil", portName ?: @"nil", uid ?: @"nil"];
            
            // 如果端口类型不为空，则认为有有效输出
            // 注意：单个 AirPods 摘下时，可能仍然有输出设备（另一个耳机），所以不能简单地认为无输出
            if (portType != nil && portType.length > 0) {
                // 排除一些明显无效的端口类型
                if (![portType isEqualToString:@"Unknown"] && ![portType isEqualToString:@"None"]) {
                    hasValidOutput = YES;
                }
            }
        }
    }
    
    BoluoAudioLog(@"[BoluoMusicPlayerIOS] Audio route changed - reason=%@, outputCount=%lu, hasValidOutput=%@, outputs=%@, isMuted=%@, userPaused=%@",
          reasonString,
          (unsigned long)outputs.count,
          hasValidOutput ? @"YES" : @"NO",
          outputInfo.length > 0 ? outputInfo : @"none",
          self.isMuted ? @"YES" : @"NO",
          self.userManuallyPaused ? @"YES" : @"NO");

    // 关键修复：处理单个 AirPods 摘下、蓝牙断连等情况
    // 1. OldDeviceUnavailable：设备断开；若系统已切到扬声器/听筒等有效输出则继续播放
    //    蓝牙断连时路由可能短暂为空，需延迟再判定
    // 2. RouteConfigurationChange：路由配置改变，需要检查具体情况
    //    - 如果输出设备数量减少到0，应该暂停
    //    - 如果仍有输出设备，可能是单个耳机摘下，但另一个还在，不应该暂停
    // 3. NoSuitableRouteForCategory：无合适路由 - 必须暂停
    // 4. CategoryChange：类别改变（通常是启动时音频会话初始化），不应该暂停
    AVAudioSessionRouteDescription *previousRoute = notification.userInfo[AVAudioSessionRouteChangePreviousRouteKey];
    [self updateExternalRouteMemoryFromPrevious:previousRoute current:currentRoute];
    BOOL previousWasBluetooth = [self routeDescriptionHasBluetoothOutput:previousRoute];
    [self updateExternalRouteMemoryFromPrevious:previousRoute current:currentRoute];
    BOOL shouldPause = NO;
    if (reason == AVAudioSessionRouteChangeReasonOldDeviceUnavailable) {
        if ((outputs.count == 0 || !hasValidOutput) && !self.isMuted && self.isPlaying) {
            if (previousWasBluetooth) {
                BoluoAudioLog(@"[BoluoMusicPlayerIOS] OldDeviceUnavailable after Bluetooth disconnect with no immediate fallback - scheduling route recovery");
                [self scheduleBluetoothDisconnectRouteRecovery];
            } else {
                shouldPause = YES;
                BoluoAudioLog(@"[BoluoMusicPlayerIOS] OldDeviceUnavailable with no fallback output - pausing playback");
            }
        } else if (hasValidOutput && !self.isMuted && self.isPlaying) {
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] OldDeviceUnavailable but fallback output available - continuing playback");
            // 蓝牙 → 扬声器：禁止立刻 Reset/Set。常见时序是 routeChange 后紧跟 RouteDisconnected 中断；
            // 若此处先占 playbackDeviceRecoveryInFlight 并在 session-work 上 Reset，会：
            // 1) 中断 mute 时日志已是 inFlight=YES；
            // 2) Reset 可能在路由抖动时挂住串行队列；
            // 3) 后续 RD 恢复的 SetPlaybackDevice 排在后面永远跑不到 → 永久无声。
            // 交给 interruption 恢复链（Began 标脏后只 Set）。
            if (previousWasBluetooth) {
                BoluoAudioLog(@"[BoluoAudioResume] skip routeFallback Reset for Bluetooth→built-in; await interruption recovery");
            } else {
                [self resetPlaybackDeviceForRouteFallback];
            }
        } else {
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] OldDeviceUnavailable - no pause needed (outputCount=%lu, isMuted=%@, isPlaying=%@)",
                  (unsigned long)outputs.count, self.isMuted ? @"YES" : @"NO", self.isPlaying ? @"YES" : @"NO");
        }
    } else if (reason == AVAudioSessionRouteChangeReasonRouteConfigurationChange) {
        // 路由配置改变：只有当完全没有输出设备时才暂停
        // 单个耳机摘下时，如果另一个耳机还在，输出设备仍然存在，不应该暂停
        // 关键修复：如果当前没有在播放（isMuted=YES 或 isPlaying=NO），可能是启动阶段，不应该暂停
        if ((outputs.count == 0 || !hasValidOutput) && !self.isMuted && self.isPlaying) {
            shouldPause = YES;
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] RouteConfigurationChange with no valid output (outputCount=%lu) - pausing playback", (unsigned long)outputs.count);
        } else {
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] RouteConfigurationChange but still has valid output or not playing (outputCount=%lu, isMuted=%@, isPlaying=%@) - allowing playback", 
                  (unsigned long)outputs.count, self.isMuted ? @"YES" : @"NO", self.isPlaying ? @"YES" : @"NO");
        }
    } else if (reason == AVAudioSessionRouteChangeReasonNoSuitableRouteForCategory) {
        // 关键修复：只有在实际播放时才暂停，启动时可能暂时没有合适路由但不应暂停
        if (!self.isMuted && self.isPlaying) {
            shouldPause = YES;
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] NoSuitableRouteForCategory - pausing playback");
        } else {
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] NoSuitableRouteForCategory but not playing (isMuted=%@, isPlaying=%@) - skipping pause", 
                  self.isMuted ? @"YES" : @"NO", self.isPlaying ? @"YES" : @"NO");
        }
    } else if (reason == AVAudioSessionRouteChangeReasonCategoryChange) {
        // 关键修复：CategoryChange 通常是启动时音频会话初始化，不应该暂停
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] CategoryChange detected (likely startup) - skipping pause");
    }
    
    if (shouldPause) {
        // 将其视为用户操作的暂停，防止任何自动恢复路径把它拉回播放
        if (!self.isMuted) {
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] Device unavailable detected - pausing playback");
            [self setAudioMute:YES isUserAction:YES];
        } else {
            // 已经 muted 也补一次 NowPlaying 刷新，保证 UI 收敛
            [self updateNowPlayingInfoCenter];
        }
    }

    // 通知 Unity（保持和 WanosAudioSession 的回调命名一致）
    // 传递更详细的信息：reason + 是否有有效输出
    NSString *message = [NSString stringWithFormat:@"%@|%@", reasonString, hasValidOutput ? @"YES" : @"NO"];
    NSString *go = self.unityGameObjectName ?: @"BoluoMusicPlayerIOS";
    UnitySendMessage([go UTF8String], "OnAudioRouteChanged", [message UTF8String]);
}

- (BOOL)isBluetoothPortType:(NSString *)portType {
    if (portType.length == 0) {
        return NO;
    }
    return [portType isEqualToString:AVAudioSessionPortBluetoothA2DP] ||
           [portType isEqualToString:AVAudioSessionPortBluetoothLE] ||
           [portType isEqualToString:AVAudioSessionPortBluetoothHFP];
}

- (BOOL)routeDescriptionHasBluetoothOutput:(AVAudioSessionRouteDescription *)route {
    if (!route) {
        return NO;
    }
    for (AVAudioSessionPortDescription *output in route.outputs) {
        if ([self isBluetoothPortType:output.portType]) {
            return YES;
        }
    }
    return NO;
}

/// 非内建扬声器/听筒的输出（蓝牙/有线/USB/AirPlay/车载等）——真·设备断开才会从这类端口掉到内建。
- (BOOL)routeDescriptionHasExternalOutput:(AVAudioSessionRouteDescription *)route {
    if (!route) {
        return NO;
    }
    for (AVAudioSessionPortDescription *output in route.outputs) {
        NSString *portType = output.portType;
        if (portType.length == 0) {
            continue;
        }
        if ([portType isEqualToString:AVAudioSessionPortBuiltInSpeaker] ||
            [portType isEqualToString:AVAudioSessionPortBuiltInReceiver]) {
            continue;
        }
        return YES;
    }
    return NO;
}

- (void)updateExternalRouteMemoryFromPrevious:(AVAudioSessionRouteDescription *)previous
                                      current:(AVAudioSessionRouteDescription *)current {
    BOOL currentExternal = [self routeDescriptionHasExternalOutput:current];
    BOOL previousExternal = [self routeDescriptionHasExternalOutput:previous];
    if (currentExternal) {
        self.recentlyHadExternalAudioRoute = YES;
        return;
    }
    // 外接 → 内建：短窗内允许 RD reason 缺失时的内建兜底（真蓝牙断开常见）
    if (previousExternal) {
        self.recentlyHadExternalAudioRoute = YES;
        self.lastExternalToBuiltInRouteEpoch = [[NSDate date] timeIntervalSince1970];
        BoluoAudioLog(@"[BoluoAudioResume] external→built-in route remembered for RD eligibility");
        return;
    }
    // 本就只在扬声器/听筒：清除，避免微信录音等打断误走 RD
    if (current != nil && current.outputs.count > 0) {
        self.recentlyHadExternalAudioRoute = NO;
        self.lastExternalToBuiltInRouteEpoch = 0;
    }
}

/// 对方在播，或系统要求次要音源静音（微信语音输入等录音场景 isOtherAudioPlaying 常为 NO）。
/// 例外（须放行，否则负一屏杀 QQ 音乐后永不恢复 / 回前台无声）：
/// - backgroundResumeArmed：后台静默确认已通过；
/// - competingEpisodeSawOtherAudioPlaying：本轮曾见对方真播音且现已无人出声（hint 常卡住不发 END）；
/// - App Active：回菠萝前台时微信常不发 SilenceSecondary END，卡住 hint 不得永久挡 unmute。
- (BOOL)shouldDeferForCompetingOrSecondarySilence {
    return [self shouldDeferForCompetingOrSecondarySilenceIgnoringStuckSecondaryWhenActive:NO];
}

- (BOOL)shouldDeferForCompetingOrSecondarySilenceIgnoringStuckSecondaryWhenActive:(BOOL)ignoreStuckWhenActive {
    AVAudioSession *session = [AVAudioSession sharedInstance];
    BOOL isActive = [UIApplication sharedApplication].applicationState == UIApplicationStateActive;
    // 用户已回前台且非电话中断：抢回焦点（顶停 QQ），不再因 otherAudio / 卡住 hint defer。
    // ignoreStuckWhenActive 用于回前台恢复链；Began/duck 等仍走默认 shouldDefer（传 NO）。
    if (ignoreStuckWhenActive && isActive && self.recoveryPhase != BoluoPlayerRecoveryPhaseInterruptionActive) {
        return NO;
    }
    if ([session isOtherAudioPlaying]) {
        self.competingEpisodeSawOtherAudioPlaying = YES;
        return YES;
    }
    if (session.secondaryAudioShouldBeSilencedHint) {
        self.competingEpisodeSawSecondarySilence = YES;
        if (self.backgroundResumeArmed || self.competingEpisodeSawOtherAudioPlaying) {
            BoluoAudioLog(@"[BoluoAudioResume] ignore stuck secondarySilence (armed=%@ sawOtherAudio=%@)",
                  self.backgroundResumeArmed ? @"YES" : @"NO",
                  self.competingEpisodeSawOtherAudioPlaying ? @"YES" : @"NO");
            return NO;
        }
        if (ignoreStuckWhenActive && isActive) {
            BoluoAudioLog(@"[BoluoAudioResume] ignore stuck secondarySilence (app Active — foreground resume)");
            return NO;
        }
        return YES;
    }
    return NO;
}

/// 导航/短信等短暂介入：前台 + 无 otherAudio、无 secondarySilence、非 RD → duck；电话/微信/QQ 仍 mute。
/// 后台一律禁止 duck：切 QQ 等 Began 常早于 otherAudio，duck 增益骤降即「卡一下」
/// （线上 D89E1DC4410E：transient_duck → 2.5s escalate；对齐 BoluoAudioFocusLossBranchPolicy）。
/// 误判风险：电话刚开始可能短暂符合条件 → 2.5s 后 escalate 到 mute（见 escalateTransientDuckToMuteIfNeeded）。
- (BOOL)shouldPreferTransientDuckForInterruption:(NSNotification *)notification
                              isRouteDisconnected:(BOOL)isRouteDisconnected {
    // Inactive/Background 都视为已切走：WillResignActive 后 Began 时 state 可能仍是 Inactive
    UIApplicationState uiState = [UIApplication sharedApplication].applicationState;
    if (uiState != UIApplicationStateActive) {
        return NO;
    }
    if (isRouteDisconnected || self.userManuallyPaused) {
        return NO;
    }
    if ([self shouldDeferForCompetingOrSecondarySilence]) {
        return NO;
    }
    if (BoluoNotificationIsAppWasSuspended(notification)) {
        return NO;
    }
    if (@available(iOS 14.5, *)) {
        NSNumber *reasonValue = notification.userInfo[AVAudioSessionInterruptionReasonKey];
        if (reasonValue != nil) {
            AVAudioSessionInterruptionReason reason =
                (AVAudioSessionInterruptionReason)[reasonValue unsignedIntegerValue];
            if (reason == AVAudioSessionInterruptionReasonRouteDisconnected) {
                return NO;
            }
        }
    }
    // 中断前在播（或未 mute）才值得 duck；已停/已 mute 走既有 mute 链即可
    return (self.isPlaying || !self.isMuted);
}

- (void)cancelTransientDuckEscalate {
    [NSObject cancelPreviousPerformRequestsWithTarget:self
                                             selector:@selector(escalateTransientDuckToMuteIfNeeded)
                                               object:nil];
    self.transientDuckEscalateAttemptCount = 0;
}

#pragma mark - Focus State（单一状态 + 记账-引擎真值对账，架构治理 Phase 2/3）

/// 记录一次焦点状态转移。只做三件事：落状态、打日志/BLS、调度对账。
/// 刻意不 bump epoch：状态记录不是效果决策（mute/unmute/duck/护栏各自已 bump；
/// 若在效果调度之后再 bump 会误伤刚调度的合法回调）。
- (void)noteFocusState:(BoluoAudioFocusStateValue)state reason:(NSString *)reason {
    if (![NSThread isMainThread]) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self noteFocusState:state reason:reason];
        });
        return;
    }
    BoluoAudioFocusStateValue previous = self.focusState;
    if (previous == state) {
        // 同状态重申（episode 内多信号常见，如 Began competing 后又收 SilenceSecondary BEGIN）：
        // 不重复打转移日志，但刷新对账（现场可能已变化）。
        [self scheduleFocusTruthAuditWithReason:[NSString stringWithFormat:@"reaffirm_%@", reason ?: @"?"]];
        return;
    }
    self.focusState = state;
    NSLog(@"[BoluoAudioDbg] evt=focus-state from=%@ to=%@ reason=%@",
          BoluoFocusStateName(previous), BoluoFocusStateName(state), reason ?: @"?");
    [self reportAudioFocusLossToBLSWithTag:@"audio_focus_state"
                                    detail:[NSString stringWithFormat:@"%@->%@:%@",
                                            BoluoFocusStateName(previous),
                                            BoluoFocusStateName(state),
                                            reason ?: @"?"]
                                durationMs:0];
    [self scheduleFocusTruthAuditWithReason:reason];
}

- (void)scheduleFocusTruthAuditWithReason:(NSString *)reason {
    [self scheduleFocusTruthAuditWithDelay:kBoluoFocusTruthAuditDelaySeconds reason:reason];
}

/// 看门狗类回调：不纳入 Focus Epoch（触发时重读全部现场，幂等），连发只保留最后一次。
- (void)scheduleFocusTruthAuditWithDelay:(NSTimeInterval)delay reason:(NSString *)reason {
    if (![NSThread isMainThread]) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self scheduleFocusTruthAuditWithDelay:delay reason:reason];
        });
        return;
    }
    self.pendingFocusTruthAuditReason = reason ?: @"?";
    [NSObject cancelPreviousPerformRequestsWithTarget:self
                                             selector:@selector(_runFocusTruthAudit)
                                               object:nil];
    [self performSelector:@selector(_runFocusTruthAudit) withObject:nil afterDelay:delay];
}

/// 记账(isPlaying/isMuted/userManuallyPaused) vs 引擎真值(GetPlaybackState) vs 系统现场
/// (hint/前后台) 对账。ResignActive 兜底自 Phase 3 起只是本对账的一个调用点（1s 延迟），
/// 不再是独立逻辑。自愈方向对齐 C# ShouldBeginCompetingEpisodeOnTruthAudit；
/// 其余分歧只上报不自动恢复（防误抢占，见 ShouldOnlyReportNonSilenceDivergenceOnTruthAudit）。
- (void)_runFocusTruthAudit {
    NSString *reason = self.pendingFocusTruthAuditReason ?: @"?";
    self.pendingFocusTruthAuditReason = nil;
    if (!self.isInitialized) {
        // 观察者已在，但进岛 Arm 还没到。人已经切走就再等一轮，别把这次对账丢掉。
        static int sAuditBeforeInitRetries = 0;
        if (sAuditBeforeInitRetries < 8
            && [UIApplication sharedApplication].applicationState != UIApplicationStateActive) {
            sAuditBeforeInitRetries += 1;
            [self scheduleFocusTruthAuditWithDelay:0.5 reason:@"resign_before_init"];
        }
        return;
    }
    // 设备恢复窗口：状态由恢复流程收敛，且 GetPlaybackState 会与 Reset 争锁 → 顺延一轮
    if (BoluoAudio_IsPlaybackDeviceRecoveryInFlight()) {
        NSString *deferred = [reason hasSuffix:@"#deferred"]
            ? reason
            : [reason stringByAppendingString:@"#deferred"];
        [self scheduleFocusTruthAuditWithReason:deferred];
        return;
    }
    AVAudioSession *auditSession = [AVAudioSession sharedInstance];
    BOOL notActive = [UIApplication sharedApplication].applicationState != UIApplicationStateActive;
    BOOL hint = auditSession.secondaryAudioShouldBeSilencedHint;
    BOOL otherAudio = [auditSession isOtherAudioPlaying];
    BOOL flagsAudible = self.isPlaying || !self.isMuted;
    int engineState = GetPlaybackState();
    BOOL engineStarted = (engineState == 2 /*Started*/ || engineState == 3 /*Starting*/);
    // 启动期记账 isMuted=YES 但未硬 mute：引擎 Started 就是还在响。
    BOOL engineAudible = engineStarted && !sBoluoEngineHardMuted;
    BoluoAudioFocusStateValue state = self.focusState;
    BOOL requiresSilence = BoluoFocusStateRequiresEngineSilence(state);

    // 自愈方向：离开前台后对方在播（hint 或 otherAudio），引擎却还在响 → 补静音。
    // 启动期尚未在前台出过声：只要引擎还热就停，不等 otherAudio 翻成 YES。
    // 静默 episode 内已经硬 mute 的不进这里（engineAudible 为 NO）。
    BOOL startupOpen = BoluoAudioSession_IsStartupAudibleCommitted() == 0;
    BOOL foreign = hint || otherAudio;
    BOOL shouldHeal = notActive && !self.userManuallyPaused
        && ((foreign && (flagsAudible || engineAudible || (engineStarted && !requiresSilence && hint)))
            || (startupOpen && engineAudible));
    if (shouldHeal) {
        self.competingEpisodeSawSecondarySilence = YES;
        NSLog(@"[BoluoAudioDbg] evt=truth-audit-heal reason=%@ state=%@ flagsAudible=%d engine=%@",
              reason, BoluoFocusStateName(state), flagsAudible ? 1 : 0,
              BoluoPlaybackStateName(engineState));
        [self reportAudioFocusLossToBLSWithTag:@"audio_truth_heal"
                                        detail:[NSString stringWithFormat:@"%@:%@:flags=%d:engine=%d",
                                                reason, BoluoFocusStateName(state),
                                                flagsAudible ? 1 : 0, engineState]
                                    durationMs:0];
        [self beginSecondarySilenceCompetingEpisodeFromSignal:
                  [NSString stringWithFormat:@"TruthAudit_%@", reason]];
        return;
    }
    // 只上报不自愈的分歧（恢复方向自动化需矩阵验证后再放开，防误恢复顶掉对方音频）：
    //  a) 静默态却记账可闻（无 hint，如 QQ 音乐竞争窗内漂移）
    //  b) Normal 却引擎仍 muted（卡在静音）
    //  c) UserPaused 态与 userManuallyPaused 旗标互相矛盾
    BOOL divergence = (requiresSilence && flagsAudible && !self.userManuallyPaused)
        || (state == BoluoAudioFocusStateNormal && self.isMuted && !self.userManuallyPaused)
        || (state == BoluoAudioFocusStateUserPaused && !self.userManuallyPaused)
        || (state != BoluoAudioFocusStateUserPaused && self.userManuallyPaused);
    if (divergence) {
        NSLog(@"[BoluoAudioDbg] evt=truth-audit-divergence reason=%@ state=%@ muted=%d playing=%d userPaused=%d engine=%@ hint=%d notActive=%d",
              reason, BoluoFocusStateName(state), self.isMuted ? 1 : 0, self.isPlaying ? 1 : 0,
              self.userManuallyPaused ? 1 : 0, BoluoPlaybackStateName(engineState),
              hint ? 1 : 0, notActive ? 1 : 0);
        [self reportAudioFocusLossToBLSWithTag:@"audio_truth_divergence"
                                        detail:[NSString stringWithFormat:@"%@:%@:muted=%d:playing=%d:userPaused=%d:engine=%d",
                                                reason, BoluoFocusStateName(state),
                                                self.isMuted ? 1 : 0, self.isPlaying ? 1 : 0,
                                                self.userManuallyPaused ? 1 : 0, engineState]
                                    durationMs:0];
        return;
    }
    BoluoAudioLog(@"[BoluoAudioTruth] audit ok reason=%@ state=%@ engine=%@",
          reason, BoluoFocusStateName(state), BoluoPlaybackStateName(engineState));
}

#pragma mark - Guarded System Mute（原生同步降增益护栏）

/// 系统触发的静音统一护栏：先经引擎 C API 同步把音乐对象增益压到近静音（一个渲染周期内
/// 生效，不依赖 Unity SendMessage 异步），kBoluoGuardedMuteDelaySeconds 后再真正 SetMute。
/// 同时消除两类互斥老毛病：
///  1) 满音量硬 SetMute → 呲啦/电流声（微信发语音、切 QQ 偶现）；
///  2) 旧 Unity soft-silence(0.2s)：SendMessage 异步偶发不生效仍电流声，且窗口内音乐可闻。
/// completion 在 mute 已落地后执行（yield / UI 提示等尾动作放这里）；窗口内出现新的
/// mute/unmute/护栏（世代变化）则本次护栏作废、completion 不执行，由新 owner 收敛。
- (void)guardedSystemMuteWithReason:(NSString *)reason completion:(void (^)(void))afterMute {
    if (self.userManuallyPaused) {
        return;
    }
    // 仅「引擎已硬 mute」才可跳过护栏。冷启动/SyncExternal 可能把 isMuted 记成 YES
    // 但从未 SetMute(true)（hardMuted=NO）——此时若早退，微信发语音会漏掉 soft-silence。
    if (self.isMuted && sBoluoEngineHardMuted) {
        // 已静音：直接执行尾动作（yield / UI 仍需要）。
        [self noteFocusState:BoluoAudioFocusStateCompetingWait
                      reason:[NSString stringWithFormat:@"guard_already_muted_%@", reason ?: @"?"]];
        if (afterMute) afterMute();
        return;
    }
    if (self.isMuted && !sBoluoEngineHardMuted) {
        BoluoAudioLog(@"[BoluoAudioDuck] guarded mute continue (accounting muted but never hard-muted) reason=%@",
              reason ?: @"?");
    }
    self.pendingUnmuteEngine = NO;
    sBoluoGuardedMuteInFlight = YES;
    unsigned long long objId = WanosMusicPlayer_GetConfiguredObjectId();
    if (objId != 0) {
        BoluoApplyMusicGain(kBoluoGuardedMuteGainFactor, true, @"guarded_mute_dip");
    }
    // objId==0（尚未配置过音乐播放）：无护栏可用，退化为纯延迟 mute（不劣于旧行为）
    [self noteFocusState:BoluoAudioFocusStateGuardedMuting
                  reason:[NSString stringWithFormat:@"guard_begin_%@", reason ?: @"?"]];
    NSInteger gen = BoluoFocusEpochBump([NSString stringWithFormat:@"guarded_mute_%@", reason ?: @"?"]);
    BoluoAudioLog(@"[BoluoAudioDuck] guarded mute begin reason=%@ objId=%llu gen=%ld hardMuted=%d",
          reason, objId, (long)gen, sBoluoEngineHardMuted ? 1 : 0);
    [self reportAudioFocusLossToBLSWithTag:@"audio_duck_begin"
                                    detail:[NSString stringWithFormat:@"native_guard_%@", reason]
                                durationMs:0];
    __weak __typeof(self) weakSelf = self;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(kBoluoGuardedMuteDelaySeconds * NSEC_PER_SEC)),
                   dispatch_get_main_queue(), ^{
        __strong __typeof(weakSelf) strongSelf = weakSelf;
        if (!strongSelf) return;
        if (gen != BoluoFocusEpochCurrent() || strongSelf.userManuallyPaused) {
            sBoluoGuardedMuteInFlight = NO;
            BoluoAudioLog(@"[BoluoAudioDuck] guarded mute superseded reason=%@ gen=%ld now=%ld",
                  reason, (long)gen, (long)BoluoFocusEpochCurrent());
            return;
        }
        if (!strongSelf.isMuted || !sBoluoEngineHardMuted) {
            [strongSelf setAudioMute:YES isUserAction:NO resetDevice:NO];
            // setAudioMute 路径不一定走 BoluoSetMuteWithMusicEventsNative；显式钉硬 mute
            sBoluoEngineHardMuted = YES;
        }
        sBoluoGuardedMuteInFlight = NO;
        // mute 落地后保持增益 0（勿立刻还原 1.0），由 unmute 爬坡回满。
        BoluoAudioLog(@"[BoluoAudioDuck] guarded mute done reason=%@", reason);
        [strongSelf noteFocusState:BoluoAudioFocusStateCompetingWait
                            reason:[NSString stringWithFormat:@"guard_landed_%@", reason ?: @"?"]];
        if (afterMute) afterMute();
    });
}

/// SilenceSecondary BEGIN（或 ResignActive 兜底）确认的「竞争静音 episode」统一入口：
/// 同步记账 → 护栏静音 → yield 会话 + 岛内 UI 提示。signal 仅用于日志区分触发源。
- (void)beginSecondarySilenceCompetingEpisodeFromSignal:(NSString *)signal {
    self.wantsResumeAfterRecovery = YES;
    [self noteCompetingAudioDetected:signal ?: @"secondarySilence"];
    [self endAudioFocusDuck];
    [self cancelTransientDuckEscalate];
    [self _scheduleWaitForCompetingAudioToStop];
    __weak __typeof(self) weakSelf = self;
    [self guardedSystemMuteWithReason:signal completion:^{
        __strong __typeof(weakSelf) strongSelf = weakSelf;
        if (!strongSelf) return;
        // 仅 mute 不够：独占 Playback 会话仍占着，微信语音识别拿不到麦
        [strongSelf yieldAudioSessionForCompetingAudioWithReason:@"SilenceSecondaryBegin"];
        NSString *goSec = strongSelf.unityGameObjectName ?: @"BoluoMusicPlayerIOS";
        UnitySendMessage([goSec UTF8String], "OnCompetingAudioMuted", "");
    }];
}

- (void)escalateTransientDuckToMuteIfNeeded {
    // 统一 epoch：本 duck 决策之后出现过更新决策（护栏静音/用户操作/回前台清理/新 duck）
    // → escalate 已被接管，作废。保留下方 BOOL 自检做双保险。
    if (self.transientDuckEscalateEpoch != BoluoFocusEpochCurrent()) {
        BoluoAudioLog(@"[BoluoAudioDuck] escalate superseded (epoch=%ld now=%ld)",
              (long)self.transientDuckEscalateEpoch, (long)BoluoFocusEpochCurrent());
        self.transientDuckEscalateAttemptCount = 0;
        return;
    }
    if (!(self.recoveryPhase == BoluoPlayerRecoveryPhaseDucked)) {
        return;
    }
    if (self.userManuallyPaused) {
        self.transientDuckEscalateAttemptCount = 0;
        [self endAudioFocusDuck];
        return;
    }
    // 仍在中断且对方已出声 / secondarySilence → 升级 mute；否则重试（微信语音 otherAudio 常晚到）
    if (![self shouldDeferForCompetingOrSecondarySilence]) {
        self.transientDuckEscalateAttemptCount += 1;
        if (self.transientDuckEscalateAttemptCount < kBoluoTransientDuckEscalateMaxAttempts) {
            BoluoAudioLog(@"[BoluoAudioDuck] escalate deferred (no competing yet) retry=%ld/%ld",
                  (long)self.transientDuckEscalateAttemptCount,
                  (long)kBoluoTransientDuckEscalateMaxAttempts);
            [self performSelector:@selector(escalateTransientDuckToMuteIfNeeded)
                       withObject:nil
                       afterDelay:kBoluoTransientDuckEscalateDelaySeconds];
        } else {
            BoluoAudioLog(@"[BoluoAudioDuck] escalate give up after %ld tries (still no competing)",
                  (long)self.transientDuckEscalateAttemptCount);
        }
        return;
    }
    BoluoAudioLog(@"[BoluoAudioDuck] escalate duck → mute (competing/secondarySilence)");
    self.transientDuckEscalateAttemptCount = 0;
    self.deviceDirty = YES;
    self.wantsResumeAfterRecovery = YES;
    [self noteCompetingAudioDetected:@"DuckEscalateCompeting"];
    [self _scheduleWaitForCompetingAudioToStop];
    __weak __typeof(self) weakSelf = self;
    // 护栏 mute 落地后由护栏自身还原增益（唯一写入者内的顺序不变量：先静音再回增益）；
    // 旧 OnAudioFocusUnduck 消息退役（C# 不再写 iOS 焦点增益）。
    [self guardedSystemMuteWithReason:@"DuckEscalateCompeting" completion:^{
        __strong __typeof(weakSelf) strongSelf = weakSelf;
        if (!strongSelf) return;
        [strongSelf endAudioFocusDuck];
        [strongSelf yieldAudioSessionForCompetingAudioWithReason:@"DuckEscalateCompeting"];
        [strongSelf notifyUnityCompetingOrSystemMuted:YES];
        NSString *go = strongSelf.unityGameObjectName ?: @"BoluoMusicPlayerIOS";
        UnitySendMessage([go UTF8String], "OnCompetingAudioMuted", "");
    }];
    [self reportAudioFocusLossToBLSWithTag:@"audio_duck_escalate"
                                    detail:@"duck_to_mute"
                                durationMs:0];
}

/// 竞争音源轮询/静默确认是否应继续等待（与独占激活 defer 略有不同）。
/// 曾见过对方真播音后 otherAudio 已停、仅 secondarySilence 残留 → 不阻塞静默确认（杀音乐 App）。
/// 从未见过 otherAudio（微信录音）→ 后台继续等 END；**前台 Active 不得空等**（hint 卡住 → 无声）。
- (BOOL)shouldKeepWaitingForCompetingAudio {
    AVAudioSession *session = [AVAudioSession sharedInstance];
    BOOL isActive = [UIApplication sharedApplication].applicationState == UIApplicationStateActive;
    // 回前台：立即放行抢回（顶停 QQ），不得空等对方停播
    if (isActive && self.recoveryPhase != BoluoPlayerRecoveryPhaseInterruptionActive) {
        return NO;
    }
    if ([session isOtherAudioPlaying]) {
        self.competingEpisodeSawOtherAudioPlaying = YES;
        return YES;
    }
    if (session.secondaryAudioShouldBeSilencedHint) {
        self.competingEpisodeSawSecondarySilence = YES;
        if (self.competingEpisodeSawOtherAudioPlaying || self.backgroundResumeArmed) {
            BoluoAudioLog(@"[BoluoAudioResume] secondarySilence after otherAudio stopped — allow silence confirm / resume");
            return NO;
        }
        return YES;
    }
    // 微信语音识别：Began 后 secondarySilenceHint 常晚几百毫秒才拉起；窗口内勿当「对方已退出」。
    // 仅后台 grace；回前台立即放行，避免空等 3s 体感无声。
    if (!isActive && self.lastInterruptionBeganEpoch > 0 && !self.backgroundResumeArmed) {
        NSTimeInterval age = [[NSDate date] timeIntervalSince1970] - self.lastInterruptionBeganEpoch;
        if (age >= 0 && age < 3.0) {
            BoluoAudioLog(@"[BoluoAudioResume] keep waiting: interruption grace age=%.2fs (await secondarySilence)", age);
            return YES;
        }
    }
    return NO;
}

/// 是否允许 Interruption Began 后走 RouteDisconnected 快速自恢复（约 1s 独占抢会话）。
/// 微信语音输入/识别等：route 本就扬声器 + secondarySilenceHint → 必须 NO，否则掐断对方录音。
/// 显式 RD reason 也不能绕过 secondarySilence / otherAudio（曾误把录音中断标成 RD）。
- (BOOL)shouldUseRouteDisconnectedSelfResume:(BOOL)isRouteDisconnected {
    AVAudioSession *session = [AVAudioSession sharedInstance];
    if (session.secondaryAudioShouldBeSilencedHint) {
        BoluoAudioLog(@"[BoluoAudioResume] RD self-resume denied: secondarySilenceHint=YES");
        return NO;
    }
    if ([session isOtherAudioPlaying]) {
        BoluoAudioLog(@"[BoluoAudioResume] RD self-resume denied: otherAudioPlaying=YES");
        return NO;
    }

    NSTimeInterval externalAge = -1;
    if (self.recentlyHadExternalAudioRoute && self.lastExternalToBuiltInRouteEpoch > 0) {
        externalAge = [[NSDate date] timeIntervalSince1970] - self.lastExternalToBuiltInRouteEpoch;
    }
    BOOL recentExternalToBuiltIn = (externalAge >= 0 && externalAge <= 5.0);

    if (isRouteDisconnected) {
        // 后台且无近期外接→内建：更像其它 App 抢会话被系统误标 RD，勿 0.6s 独占抢回（掐微信 ASR）
        if ([UIApplication sharedApplication].applicationState == UIApplicationStateBackground
            && !recentExternalToBuiltIn) {
            BoluoAudioLog(@"[BoluoAudioResume] RD self-resume denied: background without recent external→built-in");
            return NO;
        }
        BoluoAudioLog(@"[BoluoAudioResume] RD self-resume allowed: explicit RouteDisconnected reason");
        return YES;
    }

    if (!recentExternalToBuiltIn) {
        BoluoAudioLog(@"[BoluoAudioResume] RD self-resume denied: no recent external→built-in transition");
        return NO;
    }
    if (![self currentRouteLooksLikeBuiltInFallback]) {
        return NO;
    }
    BoluoAudioLog(@"[BoluoAudioResume] RD self-resume allowed: external→built-in fallback age=%.2fs", externalAge);
    return YES;
}

/// 是否应对当前竞争主动 yield（对齐 BoluoAudioFocusLossBranchPolicy.ShouldYieldSessionForCompetingAudio）。
/// 仅 secondarySilence（微信 ASR/录音）必须让麦；切 QQ 等仅 otherAudio 时只 mute+wait，
/// 禁止 NotifyOthers（对方暂停又续播 + Interruption 风暴）。
- (BOOL)shouldYieldSessionForCompetingAudioNow {
    if (self.userManuallyPaused) {
        return NO;
    }
    if ([UIApplication sharedApplication].applicationState != UIApplicationStateBackground) {
        return NO;
    }
    return [AVAudioSession sharedInstance].secondaryAudioShouldBeSilencedHint;
}

/// 后台竞争音源（微信语音识别等）时主动 deactivate，让出麦克风。
/// 必须离开 AVAudioSession 通知栈再调（走 session-work 队列），避免与 MiniAudio 同栈死锁。
- (void)yieldAudioSessionForCompetingAudioWithReason:(NSString *)reason {
    if (self.userManuallyPaused) {
        return;
    }
    if ([UIApplication sharedApplication].applicationState != UIApplicationStateBackground) {
        return;
    }
    // 真·蓝牙 RD 快速恢复在途时勿 yield，否则刚让出会话又立刻独占抢回
    if (self.pendingRouteDisconnectedRecoveryBlock != nil) {
        return;
    }

    // SilenceSecondaryBegin 本身即 ASR 抢麦信号，允许无 hint 残留时仍让出。
    // 其它入口（Began / soft-silence / escalate）必须 secondarySilence，避免打乱 QQ。
    BOOL fromSilenceSecondary = [reason isEqualToString:@"SilenceSecondaryBegin"];
    if (!fromSilenceSecondary && ![self shouldYieldSessionForCompetingAudioNow]) {
        BoluoAudioLog(@"[BoluoAudioResume] yield SKIP (no secondarySilence, music-switch safe) reason=%@",
              reason ?: @"?");
        return;
    }

    BoluoAudioLog(@"[BoluoAudioResume] yield session for competing audio (%@)", reason ?: @"?");
    dispatch_async(BoluoAudioWorkQueue(), ^{
        CFTimeInterval t0 = CACurrentMediaTime();
        NSError *error = nil;
        BOOL ok = [[AVAudioSession sharedInstance]
                   setActive:NO
                   withOptions:AVAudioSessionSetActiveOptionNotifyOthersOnDeactivation
                   error:&error];
        CFTimeInterval ms = (CACurrentMediaTime() - t0) * 1000.0;
        BoluoAudioLog(@"[BoluoAudioResume] yield setActive:NO notifyOthers=%@ err=%@ durationMs=%.1f",
              ok ? @"OK" : @"FAIL",
              error ? error.localizedDescription : @"nil",
              ms);
        NSString *detail = [NSString stringWithFormat:@"reason=%@ ok=%@ err=%@",
                            reason ?: @"?",
                            ok ? @"YES" : @"NO",
                            error.localizedDescription ?: @"nil"];
        // reportAudioFocusLoss 内部 ≥100ms 会顺带抛 wanos_audio_jank，勿重复报。
        dispatch_async(dispatch_get_main_queue(), ^{
            [[BoluoMusicPlayerIOSBridge sharedInstance]
             reportAudioFocusLossToBLSWithTag:@"audio_focus_yield_session"
                                       detail:detail
                                   durationMs:ms];
        });
    });
}

- (void)resetPlaybackDeviceForRouteFallback {
    if (self.userManuallyPaused || self.isMuted || !self.isPlaying) {
        return;
    }
    // 中断恢复链已在处理设备重建时，勿再叠一层 Reset（与 MiniAudio 主线程 stop 争锁）
    if (self.recoveryPhase == BoluoPlayerRecoveryPhaseInterruptionActive
        || self.recoveryPhase == BoluoPlayerRecoveryPhaseRecoveringDevice
        || self.deviceDirty) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] resetPlaybackDeviceForRouteFallback SKIP (interruption recovery in flight)");
        return;
    }
    // 走动时蓝牙路由会连发 OldDeviceUnavailable；合并为一次延迟 Reset，避免队列堆积阻塞
    if (self.bluetoothRouteFallbackResetPending) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] resetPlaybackDeviceForRouteFallback coalesced");
        return;
    }
    self.bluetoothRouteFallbackResetPending = YES;
    __weak __typeof(self) weakSelf = self;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.4 * NSEC_PER_SEC)), dispatch_get_main_queue(), ^{
        __strong __typeof(weakSelf) strongSelf = weakSelf;
        if (!strongSelf) {
            return;
        }
        strongSelf.bluetoothRouteFallbackResetPending = NO;
        if (strongSelf.userManuallyPaused || strongSelf.isMuted || !strongSelf.isPlaying) {
            return;
        }
        if (strongSelf.recoveryPhase == BoluoPlayerRecoveryPhaseInterruptionActive
            || strongSelf.recoveryPhase == BoluoPlayerRecoveryPhaseRecoveringDevice) {
            return;
        }
        if (strongSelf.playbackDeviceRecoveryInFlight) {
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] resetPlaybackDeviceForRouteFallback SKIP (already inFlight)");
            return;
        }
        // 蓝牙断开切扬声器：设备重建等待新路由就绪，属阻塞调用，走串行音频队列。
        // 必须标 inFlight，否则 Unity OnApplicationFocus 无法 defer，主线程 SetMute 与 Reset 争锁 → 8BADF00D
        // （BoluoAudioDeviceRecoveryPolicy.MustMarkPlaybackDeviceRecoveryInFlight BluetoothRouteFallback）。
        strongSelf.playbackDeviceRecoveryInFlight = YES;
        dispatch_async(BoluoAudioWorkQueue(), ^{
            Result resetResult = ResetPlaybackDevice();
            Result setResult = SetPlaybackDevice(kBoluoDefaultDeviceJson);
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] resetPlaybackDeviceForRouteFallback Reset=%d Set=%d",
                  resetResult, setResult);
            dispatch_async(dispatch_get_main_queue(), ^{
                strongSelf.playbackDeviceRecoveryInFlight = NO;
                [strongSelf updateNowPlayingInfoCenter];
                [strongSelf resumeIfStillMutedAfterDeviceWork:@"routeFallback-done"];
            });
        });
    });
}

- (void)scheduleBluetoothDisconnectRouteRecovery {
    // 取消上一次未执行的恢复，避免走动时多次 schedule 叠加
    [NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(_runBluetoothDisconnectRouteRecovery) object:nil];
    [self performSelector:@selector(_runBluetoothDisconnectRouteRecovery) withObject:nil afterDelay:0.45];
}

- (void)_runBluetoothDisconnectRouteRecovery {
    if (self.userManuallyPaused || self.isMuted || !self.isPlaying) {
        return;
    }
    // RouteDisconnected 中断恢复已接管时，交给 scheduleRouteDisconnectedRecovery，勿再 Reset/Pause
    if (self.recoveryPhase == BoluoPlayerRecoveryPhaseInterruptionActive
        || self.recoveryPhase == BoluoPlayerRecoveryPhaseRecoveringDevice
        || self.wantsResumeAfterRecovery) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] Bluetooth disconnect route recovery deferred to interruption path");
        return;
    }

    AVAudioSessionRouteDescription *currentRoute = [AVAudioSession sharedInstance].currentRoute;
    NSArray<AVAudioSessionPortDescription *> *outputs = currentRoute.outputs;
    BOOL hasValidOutput = NO;
    for (AVAudioSessionPortDescription *output in outputs) {
        NSString *portType = output.portType;
        if (portType.length > 0 &&
            ![portType isEqualToString:@"Unknown"] &&
            ![portType isEqualToString:@"None"]) {
            hasValidOutput = YES;
            break;
        }
    }

    if (hasValidOutput) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] Bluetooth disconnect route recovered - continuing playback");
        [self resetPlaybackDeviceForRouteFallback];
        NSString *go = self.unityGameObjectName ?: @"BoluoMusicPlayerIOS";
        UnitySendMessage([go UTF8String], "OnAudioRouteChanged", "NewDeviceAvailable|YES");
        return;
    }

    BoluoAudioLog(@"[BoluoMusicPlayerIOS] Bluetooth disconnect route recovery failed - pausing playback");
    [self setAudioMute:YES isUserAction:YES];
}

- (void)logPlaybackStateWithPrefix:(NSString *)prefix {
    int playbackState = GetPlaybackState();
    NSString *stateDesc = @"Unknown";
    switch (playbackState) {
        case 0: stateDesc = @"Uninitialized"; break;
        case 1: stateDesc = @"Stopped"; break;
        case 2: stateDesc = @"Started"; break;
        case 3: stateDesc = @"Starting"; break;
        case 4: stateDesc = @"Stopping"; break;
        default: break;
    }
    BoluoAudioLog(@"[BoluoMusicPlayerIOS] %@ - PlaybackState: %d (%@)", prefix, playbackState, stateDesc);
}

- (void)applyNowPlayingInfoWithTitle:(NSString *)title artist:(NSString *)artist album:(NSString *)album artwork:(NSString *)artworkPath {
    // 关键修复：如果所有参数都为空或nil，清空NowPlayingInfo并移除远程控制命令
    BOOL allEmpty = (!title || title.length == 0) && 
                    (!artist || artist.length == 0) && 
                    (!album || album.length == 0) && 
                    (!artworkPath || artworkPath.length == 0);
    
    if (allEmpty) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] applyNowPlayingInfoWithTitle - All parameters empty, clearing NowPlayingInfo");
        // 移除远程控制命令
        [self removeRemoteCommands];
        // 清空NowPlayingInfo
        MPNowPlayingInfoCenter *center = [MPNowPlayingInfoCenter defaultCenter];
        center.nowPlayingInfo = nil;
        // 清空缓存
        self.nowPlayingTitle = nil;
        self.nowPlayingArtist = nil;
        self.nowPlayingAlbum = nil;
        self.nowPlayingArtworkPath = nil;
        self.cachedArtworkImage = nil;
        self.cachedArtworkImagePath = nil;
        return;
    }
    
    self.nowPlayingTitle = title ?: @"直播";
    self.nowPlayingArtist = artist ?: @"";
    self.nowPlayingAlbum = album ?: @"";
    self.nowPlayingArtworkPath = artworkPath ?: nil;
    [self setupRemoteCommandsIfNeeded];
    [self updateNowPlayingInfoCenter];
}

/// hybrid 宿主 BoluoSwiftUIApp 只打包 Data/Raw；封面应在 Data/Raw/WanosAudio/Artwork/boluo.png。
- (NSString *)resolvedNowPlayingArtworkPath {
    NSFileManager *fm = [NSFileManager defaultManager];
    if (self.nowPlayingArtworkPath.length > 0 && [fm fileExistsAtPath:self.nowPlayingArtworkPath]) {
        return self.nowPlayingArtworkPath;
    }
    if (self.nowPlayingArtworkPath.length > 0) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] artwork path not found, trying bundle fallback: %@", self.nowPlayingArtworkPath);
    }
    NSString *streamingArtwork = [[[[[[NSBundle mainBundle] bundlePath]
        stringByAppendingPathComponent:@"Data"]
        stringByAppendingPathComponent:@"Raw"]
        stringByAppendingPathComponent:@"WanosAudio"]
        stringByAppendingPathComponent:@"Artwork"]
        stringByAppendingPathComponent:@"boluo.png"];
    if ([fm fileExistsAtPath:streamingArtwork]) {
        return streamingArtwork;
    }
    BoluoAudioLog(@"[BoluoMusicPlayerIOS] artwork fallback missing at %@", streamingArtwork);
    return nil;
}

- (void)updateNowPlayingInfoCenter {
    // 关键修改：如果未初始化（未登录），不更新NowPlaying信息
    if (!self.isInitialized)
    {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] updateNowPlayingInfoCenter - Not initialized yet (waiting for login), clearing NowPlaying");
        MPNowPlayingInfoCenter *center = [MPNowPlayingInfoCenter defaultCenter];
        center.nowPlayingInfo = nil;
        return;
    }
    
    // iOS 对 NowPlaying/RemoteCommand 的 UI 收敛非常"挑时机"，并且很多 API 要求主线程更新才稳定。
    // 如果不是主线程，切到主线程再执行，避免系统偶发忽略/覆盖我们的暂停态。
    if (![NSThread isMainThread]) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self updateNowPlayingInfoCenter];
        });
        return;
    }

    NSMutableDictionary *info = [NSMutableDictionary dictionary];

    info[MPMediaItemPropertyTitle] = self.nowPlayingTitle ?: @"直播";
    info[MPMediaItemPropertyArtist] = self.nowPlayingArtist ?: @"";
    info[MPMediaItemPropertyAlbumTitle] = self.nowPlayingAlbum ?: @"";
    // 进岛占位：人还在前台时，引擎 mute 也按播放中出卡。后台且仍 mute 则不占负一屏。
    BOOL inBackground = [UIApplication sharedApplication].applicationState == UIApplicationStateBackground;
    BOOL showPlaying = !self.userManuallyPaused && !self.isMuted;
    if (!showPlaying && self.nowPlayingAdvertisedPlaying && !inBackground) {
        showPlaying = YES;
    }
    // PlaybackRate: 0=暂停，1=播放
    info[MPNowPlayingInfoPropertyPlaybackRate] = @(showPlaying ? 1.0 : 0.0);
    // DefaultPlaybackRate: 提供稳定默认速率（公开字段，不需要 entitlement）
    info[MPNowPlayingInfoPropertyDefaultPlaybackRate] = @(1.0);

    // ElapsedPlaybackTime：播放时推进，暂停时冻结。
    // 一些系统/平台在控制中心按钮收敛时会参考 ElapsedPlaybackTime，如果永远写 0，可能被系统二次同步覆盖。
    CFTimeInterval now = CACurrentMediaTime();
    if (showPlaying) {
        if (self.nowPlayingLastTick > 0) {
            CFTimeInterval delta = now - self.nowPlayingLastTick;
            if (delta > 0 && delta < 10) {
                self.nowPlayingElapsedSeconds += delta;
            }
        }
        self.nowPlayingLastTick = now;
    } else {
        self.nowPlayingLastTick = 0;
    }
    info[MPNowPlayingInfoPropertyElapsedPlaybackTime] = @(self.nowPlayingElapsedSeconds);

    // 直播：不显示进度条/总时长，同时标记 IsLiveStream 提高系统识别一致性
    info[MPNowPlayingInfoPropertyIsLiveStream] = @(YES);
    info[MPMediaItemPropertyPlaybackDuration] = @(0.0);

    NSString *resolvedArtworkPath = [self resolvedNowPlayingArtworkPath];
    if (resolvedArtworkPath.length > 0) {
        // 仅路径变化时才从磁盘重新加载，避免每次刷新 NowPlaying 都触发文件 I/O
        if (![resolvedArtworkPath isEqualToString:self.cachedArtworkImagePath]) {
            self.cachedArtworkImage = [UIImage imageWithContentsOfFile:resolvedArtworkPath];
            self.cachedArtworkImagePath = resolvedArtworkPath;
            if (!self.cachedArtworkImage) {
                BoluoAudioLog(@"[BoluoMusicPlayerIOS] Failed to load artwork at path: %@", resolvedArtworkPath);
            }
        }
        if (self.cachedArtworkImage) {
            UIImage *image = self.cachedArtworkImage;
            MPMediaItemArtwork *artwork = [[MPMediaItemArtwork alloc] initWithBoundsSize:image.size requestHandler:^UIImage * _Nonnull(CGSize size) {
                if (size.width <= 0 || size.height <= 0) return image;
                if (size.width >= image.size.width && size.height >= image.size.height) return image;
                UIGraphicsBeginImageContextWithOptions(size, YES, 1.0);
                [image drawInRect:CGRectMake(0, 0, size.width, size.height)];
                UIImage *scaled = UIGraphicsGetImageFromCurrentImageContext();
                UIGraphicsEndImageContext();
                return scaled ?: image;
            }];
            info[MPMediaItemPropertyArtwork] = artwork;
        }
    }

    MPNowPlayingInfoCenter *center = [MPNowPlayingInfoCenter defaultCenter];
    center.nowPlayingInfo = info;
    // iOS 13+ 公开属性。只写 PlaybackRate 时，直播卡片（IsLiveStream）的播放/暂停按钮经常不换图标。
    if (@available(iOS 13.0, *)) {
        center.playbackState = showPlaying
            ? MPNowPlayingPlaybackStatePlaying
            : MPNowPlayingPlaybackStatePaused;
    }

    // 与参考实现对齐：不要在这里动态禁用 play/pause。
    // 控制中心/锁屏的主按钮在不同系统版本可能绑定到 pauseCommand / togglePlayPauseCommand，
    // 动态禁用会导致按钮“变灰不可点”。
    
    // DEBUG LOGGING
    BoluoAudioLog(@"[BoluoMusicPlayerIOS] Updated NowPlayingInfo: Title=%@, Rate=%.1f, Elapsed=%.2f, IsLive=%@, AudioSessionCategory=%@, Options=0x%lx",
          info[MPMediaItemPropertyTitle],
          [info[MPNowPlayingInfoPropertyPlaybackRate] floatValue],
          [info[MPNowPlayingInfoPropertyElapsedPlaybackTime] doubleValue],
          [info[MPNowPlayingInfoPropertyIsLiveStream] boolValue] ? @"YES" : @"NO",
          [AVAudioSession sharedInstance].category,
          (unsigned long)[AVAudioSession sharedInstance].categoryOptions);
}

// ----------------------------------------------------------------
// CORE LOGIC ALIGNED WITH WanosAudioSession.mm
// ----------------------------------------------------------------

- (void)setAudioMute:(BOOL)mute isUserAction:(BOOL)isUserAction {
    [self setAudioMute:mute isUserAction:isUserAction resetDevice:YES];
}

- (void)setAudioMute:(BOOL)mute isUserAction:(BOOL)isUserAction resetDevice:(BOOL)resetDevice {
    BoluoAudioLog(@"[BoluoMusicPlayerIOS] ===== setAudioMute request: mute=%@, userAction=%@, resetDevice=%@ =====",
          mute ? @"YES" : @"NO",
          isUserAction ? @"YES" : @"NO",
          resetDevice ? @"YES" : @"NO");
    [self logAudioResumeSnapshot:[NSString stringWithFormat:@"setAudioMute-enter-mute=%@-user=%@",
                                  mute ? @"YES" : @"NO", isUserAction ? @"YES" : @"NO"]];

    // Disarmed 时禁止解 mute：否则会先把 isMuted 钉成 NO，再走异步 Reset，安全网永久跳过。
    if (!mute && BoluoAudioSession_IsLogoutSilenced() != 0) {
        BoluoAudioLog(@"[BoluoAudioResume] setAudioMute unmute BLOCKED logoutSilenced");
        BoluoMusicPlayer_Trail(@"setAudioMute unmute BLOCKED logoutSilenced");
        self.isMuted = YES;
        self.isPlaying = NO;
        [self updateNowPlayingInfoCenter];
        return;
    }
    
    // 1. Logic aligned with WanosAudioSession.mm
    
    if (isUserAction) {
        self.userManuallyPaused = mute;
        // REMOVED: UnitySendMessage here caused double-notifications (once in Handler, once here).
        // WanosAudioSession.mm does NOT send message here.
        // NSString *gameObjectName = self.unityGameObjectName ?: @"BoluoMusicPlayerIOS";
        // UnitySendMessage([gameObjectName UTF8String], mute ? "OnNowPlayingPause" : "OnNowPlayingPlay", "");
    } else {
        // System/Unity call
        if (self.userManuallyPaused && !mute) {
             BoluoAudioLog(@"[BoluoAudioResume] setAudioMute BLOCKED: userManuallyPaused=YES, system unmute ignored");
             [self logAudioResumeSnapshot:@"setAudioMute-blocked-userPaused"];
             return;
        }
        if (!mute && !self.userManuallyPaused) {
             self.userManuallyPaused = NO;
        }
    }
    
    // 2. Cancel delayed tasks
    [NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(performDelayedPauseTask) object:nil];
    [NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(performDelayedResumeTask) object:nil];
    [NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(_deactivateSessionIfBackgroundUserPaused) object:nil];
    
    // FIRE FIX 3: 仅用户主动 Pause 才终止中断恢复。
    // 系统 mute（Interruption Began / RouteDisconnected）若也 cancel，会清掉
    // wantsResumeAfterRecovery 并取消已调度的 RD block → 蓝牙断开后永不恢复。
    if (mute && isUserAction) {
        [self cancelPendingInterruptionRecovery];
        [NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(_pollResumeWhenDeviceIdleAfterSystemMute) object:nil];
        self.deviceIdleResumePollCount = 0;
    }
    
    // 3. Update internal state
    // 真实 mute/unmute 决策落地：作废窗口内所有陈旧回调（guarded mute / unmute 爬坡 / 兜底复查等）。
    // decisionEpoch 供 unmute 路径的设备工作完成回调核对：设备工作期间出现更新决策则放弃 resume 尾段。
    NSInteger decisionEpoch = BoluoFocusEpochBump([NSString stringWithFormat:@"set_%s_%s",
                                                   mute ? "mute" : "unmute",
                                                   isUserAction ? "user" : "system"]);
    (void)decisionEpoch;
    BOOL wasMuted = self.isMuted;
    self.isMuted = mute;
    self.isPlaying = !mute;

    // Phase 2 状态落点（漏斗）：用户操作直接定 UserPaused/Normal；系统 unmute 落地即回 Normal。
    // 系统 mute 不在此落状态：episode 语义由调用方语境决定（护栏完成→CompetingWait，RD→RdRecovering）。
    if (isUserAction) {
        [self noteFocusState:(mute ? BoluoAudioFocusStateUserPaused : BoluoAudioFocusStateNormal)
                      reason:mute ? @"user_pause" : @"user_play"];
    } else if (!mute) {
        [self noteFocusState:BoluoAudioFocusStateNormal reason:@"system_unmute"];
    }
    
    UIApplicationState appState = [UIApplication sharedApplication].applicationState;
    BOOL isBackground = (appState == UIApplicationStateBackground);
    BOOL shouldBackgroundDeactivate = (mute && isUserAction && isBackground);

    if (mute) {
        // Mute (Pause) Path
        // Wanos: SetMute(YES) -> 0.1s -> ResetPlaybackDevice
        // 中断 Began 必须传 resetDevice=NO：MiniAudio 正于主线程通知栈内 ma_device_stop，
        // 再排队 DelayedPauseReset → session-work 上 Reset 会与之死锁（8BADF00D / C7087C4A）。
        BoluoSetMuteWithMusicEventsNative(true);
        if (resetDevice) {
            [self performSelector:@selector(performDelayedPauseTask) withObject:nil afterDelay:0];
        }

        // 关键：如果用户是在控制中心暂停且 App 在后台，系统会在短时间后用“真实状态”回填 UI。
        // 对我们这种“静音/重置设备”式暂停，系统可能仍认为 session 正在播放（即使无声），从而 UI 回弹到暂停符号。
        // 这里用一个更强的信号：后台用户暂停时，主动 deactivate audio session，让系统明确收敛到 Paused。
        // 关键：后台用户 Pause 需要强收敛，尽量“立即”触发 deactivate，减少每次 Pause 都出现的那一下回弹
        if (shouldBackgroundDeactivate) {
           // [self _deactivateSessionIfBackgroundUserPaused];
        }
    } else if (!wasMuted) {
        // 已在播：只保证引擎解 mute，禁止 Reset（否则拆掉 FirstPlay / Bank Play 实例 → 无声）。
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] setAudioMute unmute soft-path (alreadyAudible, skip Reset)");
        BoluoMusicPlayer_Trail(@"setAudioMute unmute soft-path skipReset");
        BoluoSetMuteWithMusicEventsNative(false);
    } else {
        // Unmute (Play) Path — 从静音恢复：Reset+Set 处理路由脏设备
        // setActive:YES 与 Reset/SetPlaybackDevice 都是阻塞调用（蓝牙路由重建时可达秒级），
        // 挪到串行音频队列顺序执行：激活会话 → 重置设备 → 重建设备 → 回主线程取消静音。
        self.playbackDeviceRecoveryInFlight = YES;
        __weak __typeof(self) weakSelf = self;
        dispatch_async(BoluoAudioWorkQueue(), ^{
            // 如果之前后台用户暂停导致 session 被 deactivate，这里需要先激活 session，避免系统状态不同步
            NSError *actErr = nil;
            [[AVAudioSession sharedInstance] setActive:YES error:&actErr];
            if (actErr) {
                BoluoAudioLog(@"[BoluoMusicPlayerIOS] Warning: setActive:YES before play failed: %@", actErr.localizedDescription);
            }

            BoluoAudioLog(@"[BoluoMusicPlayerIOS] Resetting playback device before resume (handling route changes)");
            Result resetResult = ResetPlaybackDevice();
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] ResetPlaybackDevice result: %d", resetResult);

            Result setResult = SetPlaybackDevice(kBoluoDefaultDeviceJson);
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] SetPlaybackDevice result: %d", setResult);

            dispatch_async(dispatch_get_main_queue(), ^{
                __strong __typeof(weakSelf) strongSelf = weakSelf;
                if (strongSelf) {
                    // inFlight 必须无条件清（否则 TryDelayedUnmute 永远 DEFER / 看门狗兜底才解）
                    strongSelf.playbackDeviceRecoveryInFlight = NO;
                    // 设备工作期间（可达秒级）出现更新决策（如微信 SilenceSecondary 护栏静音）：
                    // 本次 unmute 的 resume 尾段作废，由新 owner 收敛，防止陈旧 SetMute(false) 压过新 mute。
                    if (decisionEpoch != BoluoFocusEpochCurrent()) {
                        BoluoAudioLog(@"[BoluoAudioResume] setAudioMute unmute tail SKIP (superseded epoch=%ld now=%ld)",
                              (long)decisionEpoch, (long)BoluoFocusEpochCurrent());
                        return;
                    }
                    [strongSelf performDelayedResumeTask];
                    [strongSelf resumeIfStillMutedAfterDeviceWork:@"setAudioMute-unmute-deviceReady"];
                }
            });
        });
    }
    
    // 4. Update UI immediately (Wanos line 714)
    // CRITICAL: We only update UI here because Wanos does it here.
    // BUT, Wanos updates with Rate=0.0 when muting.
    // If you see Updated NowPlayingInfo, that is expected Wanos behavior.
    [self updateNowPlayingInfoCenter];

    // 系统中断静音（其它游戏/电话等）：负一屏已 Rate=0，但勿走 OnNowPlayingPause（会 PauseAll 阻断自动恢复）。
    // 只通知 Unity 显示岛屿 yinyuePause / 隐藏 yinyuedise；自动 unmute 成功时仍走 OnNowPlayingPlay 收起。
    if (mute && !isUserAction) {
        NSString *go = self.unityGameObjectName ?: @"BoluoMusicPlayerIOS";
        UnitySendMessage([go UTF8String], "OnSystemAudioMuted", "");
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] system mute → OnSystemAudioMuted (pause UI only)");
    }

    // iOS 控制中心在用户点击 Pause 后，系统可能会进行一次“状态收敛/二次同步”，偶发覆盖我们刚写入的 PlaybackRate。
    // 由于在某些平台上无法使用 MPNowPlayingInfoCenter.playbackState（需要私有 entitlement），
    // 这里用一个轻量的“延迟再写一次 NowPlayingInfo”来对抗覆盖，确保按钮最终停在正确状态。
    [NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(updateNowPlayingInfoCenter) object:nil];
    // 对后台用户 Pause：deactivate 内部会补写一次 NowPlayingInfo，这里不再额外补写，避免引入二次 UI 刷新导致抖动
    if (!(shouldBackgroundDeactivate)) {
        [self performSelector:@selector(updateNowPlayingInfoCenter) withObject:nil afterDelay:0.25];
    }

    // 诊断：Pause/Play 后读取系统当前 nowPlayingInfo，判断是否被系统/其他路径改写（用于定位回弹源头）
    [self performSelector:@selector(_debugDumpSystemNowPlayingInfo) withObject:nil afterDelay:0.7];
}

// 后台用户暂停时，deactivate session 促使控制中心稳定收敛到 Paused（避免 0.5s 回弹）
- (void)_deactivateSessionIfBackgroundUserPaused {
    // 只在“用户意图暂停”且当前仍 muted 的情况下执行
    if (!self.userManuallyPaused || !self.isMuted) {
        return;
    }

    // 仅在后台时执行；前台暂停不应强制 deactivate（会导致路由/焦点波动）
    if ([UIApplication sharedApplication].applicationState != UIApplicationStateBackground) {
        return;
    }

    if (![NSThread isMainThread]) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self _deactivateSessionIfBackgroundUserPaused];
        });
        return;
    }

    NSError *error = nil;
    AVAudioSessionSetActiveOptions options = 0;
    if (self.notifyOthersOnDeactivate) {
        options |= AVAudioSessionSetActiveOptionNotifyOthersOnDeactivation;
    }
    BOOL ok = [[AVAudioSession sharedInstance] setActive:NO withOptions:options error:&error];
    BoluoAudioLog(@"[BoluoMusicPlayerIOS] Background user pause -> setActive:NO (%@) notifyOthers=%@ err=%@",
          ok ? @"OK" : @"FAIL",
          self.notifyOthersOnDeactivate ? @"YES" : @"NO",
          error ? error.localizedDescription : @"nil");

    // deactivate 完成后再补写一次 NowPlaying，减少系统回填造成的“回弹”
    [self updateNowPlayingInfoCenter];
}

// 仅用于定位控制中心“回弹”来源：读取系统 nowPlayingInfo 里的关键字段
- (void)_debugDumpSystemNowPlayingInfo {
    if (![NSThread isMainThread]) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self _debugDumpSystemNowPlayingInfo];
        });
        return;
    }
    NSDictionary *sysInfo = [MPNowPlayingInfoCenter defaultCenter].nowPlayingInfo;
    NSNumber *rate = sysInfo[MPNowPlayingInfoPropertyPlaybackRate];
    NSNumber *elapsed = sysInfo[MPNowPlayingInfoPropertyElapsedPlaybackTime];
    NSNumber *isLive = sysInfo[MPNowPlayingInfoPropertyIsLiveStream];
    NSString *title = sysInfo[MPMediaItemPropertyTitle];
    BoluoAudioLog(@"[BoluoMusicPlayerIOS] DEBUG SystemNowPlayingInfo (after delay): Title=%@, Rate=%@, Elapsed=%@, IsLive=%@ | internal isMuted=%@ userPaused=%@",
          title,
          rate,
          elapsed,
          isLive,
          self.isMuted ? @"YES" : @"NO",
          self.userManuallyPaused ? @"YES" : @"NO");
}

- (void)performDelayedPauseTask {
    if (!self.isMuted) return;
    
    // Wanos: Only ResetPlaybackDevice（阻塞调用，走串行音频队列避免卡渲染）
    // 标 inFlight，避免回前台与 Pause Reset 并发 SetMute（见 BoluoAudioDeviceRecoveryPolicyTests）。
    self.playbackDeviceRecoveryInFlight = YES;
    __weak __typeof(self) weakSelf = self;
    dispatch_async(BoluoAudioWorkQueue(), ^{
        Result resetResult = ResetPlaybackDevice();
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] performDelayedPauseTask -> ResetPlaybackDevice result: %d", resetResult);
        dispatch_async(dispatch_get_main_queue(), ^{
            __strong __typeof(weakSelf) strongSelf = weakSelf;
            if (strongSelf) {
                strongSelf.playbackDeviceRecoveryInFlight = NO;
                [strongSelf resumeIfStillMutedAfterDeviceWork:@"delayedPauseReset-done"];
            }
        });
    });
    
    // NO UI UPDATE HERE. Also removed logPlaybackStateWithPrefix to match Wanos verbosity.
}

- (void)performDelayedResumeTask {
    // setAudioMute:NO 会先钉 isMuted=NO 再异步设备；若期间已 Disarm，此处必须重钉 mute，
    // 否则 BoluoSetMute 因 silenced no-op，书签却停在「已解 mute」→ 重登安全网跳过。
    if (BoluoAudioSession_IsLogoutSilenced() != 0) {
        BoluoAudioLog(@"[BoluoAudioResume] performDelayedResumeTask BLOCKED logoutSilenced — re-nail mute");
        BoluoMusicPlayer_Trail(@"delayedResume BLOCKED logoutSilenced re-nail");
        self.isMuted = YES;
        self.isPlaying = NO;
        [self updateNowPlayingInfoCenter];
        return;
    }
    if (self.isMuted) return;
    
    // Wanos: Only SetMute(false)
    BoluoSetMuteWithMusicEventsNative(false);
    BoluoAudioLog(@"[BoluoMusicPlayerIOS] performDelayedResumeTask -> BoluoSetMuteWithMusicEventsNative(false)");
    
    // NO UI UPDATE HERE. Also removed logPlaybackStateWithPrefix to match Wanos verbosity.
}

- (void)dummyMethodToReplaceOldResumeBlock {
    // Placeholder
}

- (void)performDeviceResetAndResume {
    // CRITICAL: Gate check for user pause.
    // Even if called by interruption recovery, we must stop here if the user paused.
    if (self.userManuallyPaused) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] performDeviceResetAndResume ABORTED - User Manually Paused");
        return;
    }

    // Began 已跳过 Reset（防与 MiniAudio 主线程 ma_device_stop 死锁），此处只重绑输出即可。
    // deviceDirty=YES 表示设备已脏/已被 MiniAudio stop，不是「缺一次 Reset」。
    // 若此处再 Reset，会拆掉仍存活的 Wanos 场景音效实例 → 恢复后「音效没了」、只剩音乐事件。
    // 与旧 RouteDisconnected 恢复一致：脏则跳过 Reset，只 SetPlaybackDevice（见 MustResetOnInterruptionRecovery）。
    BOOL needReset = !self.deviceDirty;
    self.deviceDirty = YES;
    self.playbackDeviceRecoveryInFlight = YES;
    dispatch_async(BoluoAudioWorkQueue(), ^{
        Result resetResult = 0;
        if (needReset) {
            resetResult = ResetPlaybackDevice();
        }
        Result setResult = SetPlaybackDevice(kBoluoDefaultDeviceJson);
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] performDeviceResetAndResume -> Reset result: %d (didReset=%@), SetPlaybackDevice result: %d",
              resetResult, needReset ? @"YES" : @"NO", setResult);

        dispatch_async(dispatch_get_main_queue(), ^{
            // 先清 inFlight，再 unmute：否则 BoluoTryDelayedUnmute 会因 inFlight 一直 DEFER。
            self.playbackDeviceRecoveryInFlight = NO;
            if (setResult != 0) {
                BoluoAudioLog(@"[BoluoMusicPlayerIOS] Warning: SetPlaybackDevice failed (%d), entering retry loop...", setResult);
                [self performSetPlaybackDeviceRetryWithCount:0];
            } else {
                [self finishResumeAfterDeviceReady];
            }
        });
    });
}

- (void)performSetPlaybackDeviceRetryWithCount:(NSInteger)retryCount {
    if (retryCount >= 5) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] SetPlaybackDevice retries exhausted. Resume failed.");
        self.playbackDeviceRecoveryInFlight = NO;
        [self resumeIfStillMutedAfterDeviceWork:@"SetPlaybackDevice-retries-exhausted"];
        return;
    }
    
    self.playbackDeviceRecoveryInFlight = YES;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.1 * NSEC_PER_SEC)), BoluoAudioWorkQueue(), ^{
        Result retrySet = SetPlaybackDevice(kBoluoDefaultDeviceJson);
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] Retry SetPlaybackDevice (attempt %ld) result: %d", (long)(retryCount + 1), retrySet);
        
        dispatch_async(dispatch_get_main_queue(), ^{
            self.playbackDeviceRecoveryInFlight = NO;
            if (retrySet != 0) {
                [self performSetPlaybackDeviceRetryWithCount:retryCount + 1];
            } else {
                [self finishResumeAfterDeviceReady];
            }
        });
    });
}

- (BOOL)unmuteEngineIfExclusiveAvailableWithReason:(NSString *)reason {
    BoluoUnmuteVerdict v = [self unmuteVerdictForReason:reason ?: @"unmuteExclusive"];
    if (v == BoluoUnmuteVerdictBlockedDisarmed) {
        BoluoAudioLog(@"[BoluoAudioResume] unmute BLOCKED logoutSilenced (%@)", reason ?: @"?");
        return NO;
    }
    if (v == BoluoUnmuteVerdictBlockedUserPaused) {
        return NO;
    }
    // 退登后 Arm 若触发 Rebuild（sNeedsDeviceRebuildAfterLogout），重进窗口内 inFlight / recovery
    // 常为真；此前未认 Defer*，会在 Reset/SetPlaybackDevice 进行中就抢先 unmute 并把 isMuted
    // 错误钉成 NO —— 与 MiniAudio 争锁、且让后续安全网(resumeIfStillMutedAfterDeviceWork/RequestUnmute)
    // 误判「已完成」而不再补播，表现为退登重登后无声。inFlight 清空后由该安全网统一补一次 unmute。
    if (v == BoluoUnmuteVerdictDeferDeviceBusy || v == BoluoUnmuteVerdictDeferRecovery) {
        BoluoAudioLog(@"[BoluoAudioResume] unmute DEFER %@ (%@)",
              BoluoUnmuteVerdictName(v), reason ?: @"?");
        BoluoMusicPlayer_Trail(@"unmuteExclusive DEFER %@ (%@)",
                               BoluoUnmuteVerdictName(v), reason ?: @"?");
        return NO;
    }
    // 已在播且未静音：跳过 BoluoSetMute(false)（内部会 Post Resume_Music + 延迟 SetMute），
    // 避免进岛窗口与 SyncExternal 叠峰造成可闻卡顿。
    if (!self.isMuted && self.isPlaying) {
        BoluoAudioLog(@"[BoluoAudioResume] unmute skip already unmuted (%@)", reason ?: @"?");
        return YES;
    }
    AVAudioSession *session = [AVAudioSession sharedInstance];
    // 回前台恢复：忽略卡住 secondarySilence（微信常不发 END），仅 otherAudio 仍挡。
    BOOL ignoreStuckSecondary =
        [UIApplication sharedApplication].applicationState == UIApplicationStateActive;
    if ([self shouldDeferForCompetingOrSecondarySilenceIgnoringStuckSecondaryWhenActive:ignoreStuckSecondary]) {
        [self noteCompetingAudioDetected:reason ?: @"unmute-otherAudio"];
        // 恢复链走到 unmute 前对方又出声/系统要求次要静音：后台静默确认作废（且计入误判退避），
        // 否则 armed 残留会让下一次 0.5s 静默直接放行，形成抢占拉锯。
        // secondarySilenceHint：微信语音输入等录音场景 isOtherAudioPlaying 常为 NO，必须一并 defer。
        [self _penalizeBackgroundSilenceConfirmIfMisjudged];
        self.backgroundSilenceConfirmStreak = 0;
        self.backgroundResumeArmed = NO;
        BoluoFocusEpochBump(@"unmute_deferred_competing");
        BoluoAudioLog(@"[BoluoAudioResume] unmute deferred (%@) otherAudio=%@ secondarySilence=%@",
              reason ?: @"?",
              [session isOtherAudioPlaying] ? @"YES" : @"NO",
              session.secondaryAudioShouldBeSilencedHint ? @"YES" : @"NO");
        // 注意：这里绝不能再 restoreExclusiveAudioSessionForResume:YES——自己不出声却强行
        // 独占激活会话，会把正在播的对方顶停（用户反馈「别的程序播一下就暂停，菠萝也没播」）。
        // 会话抢回统一交给等待链：对方停止后再恢复。
        [self _scheduleWaitForCompetingAudioToStop];
        [self updateNowPlayingInfoCenter];
        return NO;
    }
    [self noteCompetingAudioCleared:reason ?: @"unmute-exclusiveOK"];
    // 本轮后台恢复已完成，一次性放行标志复位
    self.backgroundResumeArmed = NO;
    self.competingEpisodeSawOtherAudioPlaying = NO;
    self.competingEpisodeSawSecondarySilence = NO;
    BoluoSetMuteWithMusicEventsNative(false);
    self.isMuted = NO;
    self.isPlaying = YES;
    [self noteFocusState:BoluoAudioFocusStateNormal
                  reason:[NSString stringWithFormat:@"unmute_exclusive_%@", reason ?: @"?"]];
    // 本轮中断的设备恢复已完成；不清会导致之后每次回前台 shouldAttemptAutoResume 误判，
    // 反复走 Reset/SetPlaybackDevice 产生无谓的音频断流（咯噔）。
    self.deviceDirty = NO;
    if (self.recoveryPhase == BoluoPlayerRecoveryPhaseRecoveringDevice) {
        [self noteDeviceRecoveryFinished:reason ?: @"unmute-exclusiveOK"];
    }
    [self updateNowPlayingInfoCenter];
    [self logPlaybackStateWithPrefix:@"Play"];
    BoluoAudioLog(@"[BoluoAudioResume] unmute OK (%@)", reason ?: @"?");
    // 程序化 Exclusive unmute 只同步岛内暂停 UI；必须传 "ui"。
    // 若传空串，C# OnNowPlayingPlay 会 RequestResumeAll → ForceUnmute → 再进本路径，
    // 形成 unmute 风暴（电流音 + 假 isPlaying）。控制中心/切曲仍走 "" / remote 全量 Resume。
    NSString *go = self.unityGameObjectName ?: @"BoluoMusicPlayerIOS";
    UnitySendMessage([go UTF8String], "OnNowPlayingPlay", "ui");
    return YES;
}

- (void)finishResumeAfterDeviceReady {
    // CRITICAL FIX: Respect User Intent.
    // If user manually paused, DO NOT automatically unmute or clear the pause flag.
    // This method is often called by system recovery paths (interruption ended, session activation).
    [self logAudioResumeSnapshot:@"finishResumeAfterDeviceReady-enter"];

    // 本轮设备恢复已结束：必须先清 inFlight，再走 SetMute(false)；
    // 否则 BoluoTryDelayedUnmute 见 inFlight=YES 会无限 DEFER，表现为蓝牙断开后无声。
    self.playbackDeviceRecoveryInFlight = NO;

    BoluoUnmuteVerdict v = [self unmuteVerdictForReason:@"finishResumeAfterDeviceReady"];
    if (v == BoluoUnmuteVerdictBlockedDisarmed) {
        BoluoAudioLog(@"[BoluoAudioResume] finishResumeAfterDeviceReady BLOCKED logoutSilenced");
        BoluoSetMuteWithMusicEventsNative(true);
        self.isMuted = YES;
        self.isPlaying = NO;
        self.userManuallyPaused = YES;
        self.wantsResumeAfterRecovery = NO;
        [self updateNowPlayingInfoCenter];
        return;
    }

    if (v == BoluoUnmuteVerdictBlockedUserPaused) {
        BoluoAudioLog(@"[BoluoAudioResume] finishResumeAfterDeviceReady BLOCKED userManuallyPaused=YES");
        BoluoSetMuteWithMusicEventsNative(true);
        self.isMuted = YES;
        self.isPlaying = NO;
        [self updateNowPlayingInfoCenter];
        return;
    }

    [self restoreExclusiveAudioSessionForResume:YES];
    if (self.recoveryPhase == BoluoPlayerRecoveryPhaseAwaitingCompetingStop) {
        BoluoAudioLog(@"[BoluoAudioResume] finishResumeAfterDeviceReady defer unmute (await exclusive reclaim)");
        [self _scheduleExclusiveReclaimAfterInterruption];
        [self _scheduleWaitForCompetingAudioToStop];
        [self updateNowPlayingInfoCenter];
        [self logAudioResumeSnapshot:@"finishResumeAfterDeviceReady-deferred"];
        return;
    }

    if (![self unmuteEngineIfExclusiveAvailableWithReason:@"finishResumeAfterDeviceReady"]) {
        [self logAudioResumeSnapshot:@"finishResumeAfterDeviceReady-deferred"];
        return;
    }
    if (self.awaitingRouteDisconnectedResumeCompletion) {
        [self markRouteDisconnectedResumeSucceeded];
        BoluoAudioLog(@"[BoluoAudioResume] RD hard-resume marked OK after finishResume");
    }
    [self logAudioResumeSnapshot:@"finishResumeAfterDeviceReady-done"];
}

/// 进岛首播强制解 mute 的共享实现；供 C# 首播路径与 Rebuild 完成安全网复用，
/// 两条路径行为须一致（都不走 isOtherAudioPlaying 推迟，那条会把 isMuted 钉回 YES）。
- (void)applyFirstPlayUnmuteForced {
    self.userManuallyPaused = NO;
    [self noteCompetingAudioCleared:@"firstPlayUnmute"];
    [self noteFirstPlayConsumed:@"firstPlayUnmute"];
    self.isMuted = NO;
    self.isPlaying = YES;
    SetMute(false);
    int st = GetPlaybackState();
    BoluoAudioLog(@"[BoluoMusicPlayerIOS] applyFirstPlayUnmuteForced OK");
    BoluoMusicPlayer_Trail(
        @"applyFirstPlayUnmuteForced OK st=%@ silenced=%d inFlight=%d cat=%@ otherAudio=%d",
        BoluoPlaybackStateName(st),
        (int)BoluoAudioSession_IsLogoutSilenced(),
        (int)self.playbackDeviceRecoveryInFlight,
        [AVAudioSession sharedInstance].category ?: @"?",
        (int)[[AVAudioSession sharedInstance] isOtherAudioPlaying]);
    [self updateNowPlayingInfoCenter];
}

/// 设备队列工作结束后的兜底：仍静音且有恢复意图时补一次 finishResume。
/// 覆盖「系统 mute 清掉 pending / RD 被取消 / inFlight 期间 unmute DEFER」等漏网路径。
/// 首播：sessionPhase==AwaitingFirstPlay 时强制 unmute。
/// Engaged 且书签已 unmuted：Reset/Rebuild 刚拆实例时仍须补 unmute（勿因 muted=0 跳过）。
- (void)resumeIfStillMutedAfterDeviceWork:(NSString *)reason {
    BoluoUnmuteVerdict v = [self unmuteVerdictForReason:reason ?: @"safety-net"];
    if (v == BoluoUnmuteVerdictBlockedDisarmed) {
        BoluoAudioLog(@"[BoluoAudioResume] safety-net BLOCKED logoutSilenced (%@)", reason ?: @"?");
        BoluoMusicPlayer_Trail(@"safety-net SKIP blockedDisarmed (%@)", reason ?: @"?");
        return;
    }
    if (self.playbackDeviceRecoveryInFlight) {
        BoluoMusicPlayer_Trail(@"safety-net SKIP inFlight (%@)", reason ?: @"?");
        return;
    }
    if (v == BoluoUnmuteVerdictBlockedUserPaused) {
        BoluoMusicPlayer_Trail(@"safety-net SKIP userPaused (%@)", reason ?: @"?");
        return;
    }

    BOOL awaitingFirst = (self.sessionPhase == BoluoPlayerSessionPhaseAwaitingFirstPlay);
    if (awaitingFirst) {
        // 不认 isMuted：可能被抢先钉成 NO，设备工作后又哑了，仍须强制补播。
        BoluoAudioLog(@"[BoluoAudioResume] safety-net first-play unmute after device work (%@)", reason ?: @"?");
        BoluoMusicPlayer_Trail(@"safety-net firstPlay unmute muted=%d (%@)",
                               (int)self.isMuted, reason ?: @"?");
        [self applyFirstPlayUnmuteForced];
        return;
    }

    // 退登重登：FirstPlay 已 Engaged+isMuted=NO，随后迟到的 Reset/Rebuild 拆掉声源；
    // 旧逻辑见 muted=0 直接 SKIP → 书签「可听」实际无声。此处补 SetMute + Resume_Music。
    if (self.sessionPhase == BoluoPlayerSessionPhaseEngaged
        && !self.isMuted
        && !self.userManuallyPaused) {
        int st = GetPlaybackState();
        BoluoAudioLog(@"[BoluoAudioResume] safety-net Engaged re-unmute after device work (%@) eng=%@",
              reason ?: @"?", BoluoPlaybackStateName(st));
        BoluoMusicPlayer_Trail(@"safety-net Engaged reUnmute st=%@ (%@)",
                               BoluoPlaybackStateName(st), reason ?: @"?");
        self.isPlaying = YES;
        BoluoSetMuteWithMusicEventsNative(false);
        [self updateNowPlayingInfoCenter];
        return;
    }

    if (!self.isMuted) {
        BoluoMusicPlayer_Trail(
            @"safety-net SKIP muted=0 phase=%@ verdict=%@ (%@)",
            BoluoPlayerSessionPhaseName(self.sessionPhase),
            BoluoUnmuteVerdictName(v),
            reason ?: @"?");
        return;
    }
    BOOL wantsResume = self.wantsResumeAfterRecovery
        || self.deviceDirty
        || self.recoveryPhase == BoluoPlayerRecoveryPhaseInterruptionActive
        || self.recoveryPhase == BoluoPlayerRecoveryPhaseAwaitingCompetingStop;
    if (!wantsResume) {
        BoluoMusicPlayer_Trail(@"safety-net SKIP noResumeIntent (%@)", reason ?: @"?");
        return;
    }
    BoluoAudioLog(@"[BoluoAudioResume] safety-net resume after device work (%@) pending=%@ deviceDirty=%@ interruption=%@",
          reason ?: @"?",
          self.wantsResumeAfterRecovery ? @"YES" : @"NO",
          self.deviceDirty ? @"YES" : @"NO",
          (self.recoveryPhase == BoluoPlayerRecoveryPhaseInterruptionActive) ? @"YES" : @"NO");
    BoluoMusicPlayer_Trail(@"safety-net finishResume (%@)", reason ?: @"?");
    self.wantsResumeAfterRecovery = YES;
    [self finishResumeAfterDeviceReady];
}

- (void)restoreExclusiveAudioSession {
    // CRITICAL CHANGE: Always activate session on foreground/restore, even if paused.
    // This ensures the App has the "Audio Focus" and Lock Screen controls remain visible/active.
    // WanosAudioSession.mm 'handleApplicationDidBecomeActive' unconditionally calls 'applySession'.
    
    // GUARD: Do not run if app is in background (Control Center / Lock Screen case)
    // Matches WanosAudioSession.mm 'handleApplicationDidBecomeActive' background check.
    if ([UIApplication sharedApplication].applicationState == UIApplicationStateBackground) {
        BoluoAudioLog(@"[BoluoAudioResume] restoreExclusiveAudioSession SKIP: App is Background");
        [self logAudioResumeSnapshot:@"restoreExclusive-skip-background"];
        return;
    }
    
    // 关键修复：即使用户手动暂停，如果应用在前台（或启动中），也应该激活音频会话
    // 这对于 SoloAmbient 类别尤其重要，否则即使在前台也可能没有声音
    // userManuallyPaused 应该只影响播放状态（静音），而不应该阻止音频会话的激活
    UIApplicationState appState = [UIApplication sharedApplication].applicationState;
    // 检查应用是否在前台（Active）或启动中（Inactive），但不包括后台（Background）
    BOOL isForeground = (appState != UIApplicationStateBackground);
    
    if (self.userManuallyPaused && !isForeground) {
        // 仅在后台且用户手动暂停时跳过激活（避免后台自动恢复）
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] restoreExclusiveAudioSession BLOCKED - User Manually Paused in Background (Preventing Resume Intent)");
        return;
    }
    
    if (self.userManuallyPaused && isForeground) {
        // 前台时（包括启动中）即使用户暂停也要激活会话（确保 SoloAmbient 类别正常工作）
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] restoreExclusiveAudioSession - User paused but app is foreground (state=%ld), activating session anyway (required for SoloAmbient)", (long)appState);
    }

    // Ensure run on main thread
    if (![NSThread isMainThread]) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self restoreExclusiveAudioSession];
        });
        return;
    }

    [self restoreExclusiveAudioSessionForResume:NO];
}

- (BOOL)restoreExclusiveAudioSessionForResume:(BOOL)forceInterruptOthers {
    if (![NSThread isMainThread]) {
        // 禁止 dispatch_sync(main)：session-work 调用时主线程若在等该队列会 GCD 死锁。
        dispatch_async(dispatch_get_main_queue(), ^{
            [self restoreExclusiveAudioSessionForResume:forceInterruptOthers];
        });
        return NO;
    }

    // 后台默认不抢会话；backgroundResumeArmed=YES 表示静默确认已通过（对方 App 被杀/退出），
    // 放行本轮后台恢复（白噪音在后台自动续播的核心路径）。
    if ([UIApplication sharedApplication].applicationState == UIApplicationStateBackground
        && !self.backgroundResumeArmed) {
        BoluoAudioLog(@"[BoluoAudioResume] restoreExclusiveForResume SKIP: background force=%@",
              forceInterruptOthers ? @"YES" : @"NO");
        return NO;
    }

    if (self.userManuallyPaused) {
        UIApplicationState appState = [UIApplication sharedApplication].applicationState;
        if (appState == UIApplicationStateBackground) {
            return NO;
        }
    }

    AVAudioSession *session = [AVAudioSession sharedInstance];
    // 对方正在出声或系统要求次要静音时：默认不独占。
    // 例外：forceInterruptOthers + Active → 用户切回菠萝须抢回（顶停 QQ）。
    // 不看 interruptionActive：QQ 接管不发 Ended，该 flag 回前台仍挂着（2026-07-23 复发根因）；
    // 电话/闹钟等真中断由系统仲裁——下方 setActive 会因 '!pri' 失败，抢不走对方。
    BOOL isActive = [UIApplication sharedApplication].applicationState == UIApplicationStateActive;
    // 前台强抢不看 InterruptionActive。QQ / 抖音不发 Ended 时这面旗会一直挂着，负一屏回不来。
    BOOL allowForceOverCompeting = forceInterruptOthers && isActive;
    if ([self shouldDeferForCompetingOrSecondarySilence] && !allowForceOverCompeting) {
        BoluoAudioLog(@"[BoluoAudioResume] restoreExclusiveForResume SKIP: competing/secondary silence force=%@",
              forceInterruptOthers ? @"YES" : @"NO");
        [self logAudioResumeSnapshot:@"restoreExclusive-skip-otherAudio"];
        return NO;
    }
    if (allowForceOverCompeting && [session isOtherAudioPlaying]) {
        BoluoAudioLog(@"[BoluoAudioResume] restoreExclusiveForResume FORCE over otherAudio (foreground reclaim)");
    }

    AVAudioSessionCategory category = [self categoryFromValue:self.sessionCategory];
    AVAudioSessionCategoryOptions options = [self optionsFromBits:self.sessionOptions];
    options &= ~AVAudioSessionCategoryOptionMixWithOthers;
    if (category == AVAudioSessionCategoryPlayback) {
        options &= ~AVAudioSessionCategoryOptionDefaultToSpeaker;
    }

    // 幂等 guard（对齐 BoluoAudioFocusLossBranchPolicy.ShouldNoopRestoreExclusiveWhenEngineAlreadyAudible）：
    // 引擎已 Started、未 mute、category/options 达标、无 otherAudio、无恢复 in-flight 时，
    // **无视**粘住的 pendingResume / deviceReset / interruptionActive（AppWasSuspended 误标脏后的 sticky），
    // 直接 NOOP 并清 sticky——否则每次回前台 setCategory+setActive 重启 IO = 「咯噔」。
    // force 也走该 guard：DidBecomeActive 每次都 force，已 audible 独占时无可抢回之物。
    // 对方仍在出声 / 仍 mute / 恢复 in-flight 时绝不 NOOP。
    BOOL categoryOptionsMatch =
        [session.category isEqualToString:category] && session.categoryOptions == options;
    BOOL engineStarted = (GetPlaybackState() == 2 /* Started */);
    BOOL otherAudioPlaying = [session isOtherAudioPlaying];
    BOOL allowNoop =
        engineStarted
        && !self.isMuted
        && categoryOptionsMatch
        && !otherAudioPlaying
        && !self.isRestoringFromInterruption;
    if (allowNoop) {
        // 旧 sticky 布尔已迁到 recoveryPhase / wantsResume / deviceDirty；NOOP 时清掉以免下次误判。
        BOOL hadSticky = self.wantsResumeAfterRecovery
            || self.deviceDirty
            || self.recoveryPhase != BoluoPlayerRecoveryPhaseNone;
        self.wantsResumeAfterRecovery = NO;
        self.deviceDirty = NO;
        if (self.recoveryPhase != BoluoPlayerRecoveryPhaseNone) {
            [self transitionToRecoveryPhase:BoluoPlayerRecoveryPhaseNone reason:@"restoreExclusiveNoop"];
        }
        [self updateNowPlayingInfoCenter];
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] restoreExclusiveForResume NOOP (idempotent) category=%@ opts=0x%lx engine=Started stickyCleared=%@",
              category, (unsigned long)options, hadSticky ? @"YES" : @"NO");
        return YES;
    }

    // setCategory/setActive 为阻塞调用（蓝牙重连时可达秒级），放串行音频队列；
    // 各调用方均不消费返回值，这里在完成预检并成功入队后即返回 YES。
    dispatch_async(BoluoAudioWorkQueue(), ^{
        // 竞态兜底：主线程预检到真正执行之间对方可能已开始播放/录音。
        // force+Active 抢回路径允许顶停 QQ；非 force 仍 ABORT。
        // 同样不看 interruptionActive（QQ 无 Ended），电话保护靠 setActive '!pri' 失败。
        BOOL allowForceOnQueue = forceInterruptOthers
            && [UIApplication sharedApplication].applicationState == UIApplicationStateActive
            && self.recoveryPhase != BoluoPlayerRecoveryPhaseInterruptionActive;
        if ([self shouldDeferForCompetingOrSecondarySilence] && !allowForceOnQueue) {
            BoluoAudioLog(@"[BoluoAudioResume] restoreExclusiveForResume ABORT on queue: competing/secondary silence");
            return;
        }
        NSError *error = nil;
        [session setCategory:category mode:AVAudioSessionModeDefault options:options error:&error];
        if (error) {
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] restoreExclusiveForResume setCategory failed: %@", error.localizedDescription);
        }

        NSError *actErr = nil;
        BOOL activated = [session setActive:YES error:&actErr];
        dispatch_async(dispatch_get_main_queue(), ^{
            if (!activated || actErr) {
                // 电话/闹钟等真中断中强抢会走到这里（'!pri' 优先级不足）：保持 defer，
                // interruptionActive 不动，等真正的 InterruptionEnded 走标准恢复链。
                BoluoAudioLog(@"[BoluoAudioResume] restoreExclusiveForResume setActive failed force=%@ err=%@",
                      forceInterruptOthers ? @"YES" : @"NO",
                      actErr ? actErr.localizedDescription : @"unknown");
                [self logAudioResumeSnapshot:@"restoreExclusive-activate-failed"];
                return;
            }

            // 强抢成功 + 中断标志仍挂着：说明对方是 QQ 等音乐 App 的陈旧中断（不会有 Ended），
            // 系统已把独占会话判给菠萝（真电话中断到不了这里）。必须清掉陈旧 interruptionActive，
            // 否则 keepWaiting / reassertNowPlaying / unmute 全被它挡死——表现即
            // 「从 QQ 切回菠萝不恢复焦点 + 负一屏卡片消失」。清完立刻重挂卡片并驱动恢复链。
            if (forceInterruptOthers && self.recoveryPhase == BoluoPlayerRecoveryPhaseInterruptionActive) {
                [self transitionToRecoveryPhase:BoluoPlayerRecoveryPhaseNone reason:@"ForcedForegroundReclaim"];
                BoluoAudioLog(@"[BoluoAudioResume] forced reclaim OK — clear stale InterruptionActive (music-app takeover, no Ended)");
                [self logAudioResumeSnapshot:@"forced-reclaim-clear-stale-interruption"];
                [self _reassertNowPlayingAndCommandsWithReason:@"ForcedForegroundReclaim"];
                [self deferTryResumeWhenCompetingAudioStopped:@"ForcedForegroundReclaim"];
            }

            [self updateNowPlayingInfoCenter];
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] restoreExclusiveForResume OK force=%@ otherAudio=%@ muted=%@",
                  forceInterruptOthers ? @"YES" : @"NO",
                  [session isOtherAudioPlaying] ? @"YES" : @"NO",
                  self.isMuted ? @"YES" : @"NO");
            [self logAudioResumeSnapshot:@"restoreExclusive-done"];
        });
    });
    return YES;
}

- (void)attemptSessionActivationWithRetry:(void (^)(BOOL success))completion {
    [self performSessionActivationWithRetryCount:0
                                        allowMix:self.resumeActivationUsesMixWithOthers
                                      completion:completion];
}

- (void)performSessionActivationWithRetryCount:(NSInteger)retryCount
                                      allowMix:(BOOL)allowMix
                                    completion:(void (^)(BOOL success))completion {
    if (![NSThread isMainThread]) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self performSessionActivationWithRetryCount:retryCount allowMix:allowMix completion:completion];
        });
        return;
    }
    
    // CRITICAL GUARD: Abort activation if user paused.
    // We should not be activating the session if the user intent is Pause.
    if (self.userManuallyPaused) {
         BoluoAudioLog(@"[BoluoMusicPlayerIOS] Activation ABORTED - User Manually Paused");
         if (completion) completion(NO);
         return;
    }
    
    AVAudioSession *session = [AVAudioSession sharedInstance];

    // 独占激活(非 Mix)前必须确认无人出声、且系统未要求次要静音：
    // 对方在播时 setActive 独占会直接把对方顶停；微信语音输入等录音场景
    // isOtherAudioPlaying 常为 NO，但 secondaryAudioShouldBeSilencedHint=YES，同样不可抢。
    // 后台即使 allowMix 也不可抢：Mix setActive 仍会干扰微信语音识别会话。
    // 前台电话恢复（allowMix=YES）不受影响。
    if ([self shouldDeferForCompetingOrSecondarySilence]) {
        BOOL background = ([UIApplication sharedApplication].applicationState == UIApplicationStateBackground);
        if (!allowMix || background) {
            BoluoAudioLog(@"[BoluoAudioResume] activation ABORT: competing/secondary silence allowMix=%@ bg=%@ (attempt %ld)",
                  allowMix ? @"YES" : @"NO",
                  background ? @"YES" : @"NO",
                  (long)(retryCount + 1));
            if (completion) completion(NO);
            return;
        }
    }

    // Ensure session category is correctly set before activation.
    // ALWAYS re-apply the category. The system often changes it during interruption (e.g. to PlayAndRecord).
    // Even if it looks correct, re-setting it is the signal to the OS that we want to reclaim the "Playback" role.
    AVAudioSessionCategory category = [self categoryFromValue:self.sessionCategory];
    AVAudioSessionCategoryOptions options = [self optionsFromBits:self.sessionOptions];
    
  // 仅电话/高优先级中断恢复路径允许 Mix；从其它音乐 App 回前台用独占 Playback（options 不含 Mix）
    if (category == AVAudioSessionCategoryPlayback) {
        if (allowMix) {
            options |= AVAudioSessionCategoryOptionMixWithOthers;
        } else {
            options &= ~AVAudioSessionCategoryOptionMixWithOthers;
        }
    }
    BoluoAudioLog(@"[BoluoAudioResume] session activation attempt=%ld allowMix=%@ options=0x%lx",
          (long)(retryCount + 1), allowMix ? @"YES" : @"NO", (unsigned long)options);
    
    // 蓝牙路由重建期间 setActive:YES 可阻塞近 1 秒（等 CoreAudio 配好新路由）；
    // 阻塞调用全部放串行音频队列，结果回主线程做重试调度 / UI / completion。
    dispatch_async(BoluoAudioWorkQueue(), ^{
        // 竞态兜底：主线程预检到队列执行之间，对方可能刚按下播放/录音。独占激活此刻必顶停对方。
        BOOL background = ([UIApplication sharedApplication].applicationState == UIApplicationStateBackground);
        if ([self shouldDeferForCompetingOrSecondarySilence] && (!allowMix || background)) {
            BoluoAudioLog(@"[BoluoAudioResume] activation ABORT on queue: competing/secondary silence allowMix=%@ bg=%@",
                  allowMix ? @"YES" : @"NO", background ? @"YES" : @"NO");
            dispatch_async(dispatch_get_main_queue(), ^{
                if (completion) completion(NO);
            });
            return;
        }
        NSError *configError = nil;
        
        // Force set category every time. Do not optimize this out.
        [session setCategory:category mode:AVAudioSessionModeDefault options:options error:&configError];
        if (configError) {
             BoluoAudioLog(@"[BoluoMusicPlayerIOS] Warning: Failed to re-apply category before resume activation: %@", configError.localizedDescription);
        }
        
        // CRITICAL: Force DEACTIVATE if we are hitting InsufficientPriority repeatedly.
        // Sometimes the session gets "stuck" in a high-priority interrupt state (like phone call)
        // and won't let us reactivate until we explicitly yield (deactivate) first.
        // We only do this on retries > 0 to avoid audio glitches on first attempt.
        if (retryCount > 0) {
             [session setActive:NO error:nil];
        }

        NSError *activateError = nil;
        BOOL activated = [session setActive:YES error:&activateError];

        // 重试耗尽时的兜底 hard reset 也是阻塞调用，留在队列里做完再回主线程
        BOOL retriablePriorityError =
            (!activated && activateError.code == 560557684); // AVAudioSessionErrorCodeInsufficientPriority
        if (retriablePriorityError && retryCount >= 20) {
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] Retries exhausted. Attempting final hard reset.");
            AVAudioSessionCategoryOptions mixOptions = options | AVAudioSessionCategoryOptionMixWithOthers;
            [session setActive:NO error:nil];
            [session setCategory:category
                            mode:AVAudioSessionModeDefault
                         options:mixOptions
                           error:nil];
            [session setActive:YES error:nil];
        }

        dispatch_async(dispatch_get_main_queue(), ^{
            if (activated) {
                BoluoAudioLog(@"[BoluoMusicPlayerIOS] Audio session activated for resume");
                
                // Ensure playback controls reappear on resume.
                // We re-register remote commands because sometimes deactivating the session
                // causes the system to forget our command handlers or disable them.
                [self setupRemoteCommandsIfNeeded];
                
                // 重要：不要在“会话激活/恢复”阶段强行把 PlaybackRate 写成 1.0 来抢占 UI。
                // 这会在某些系统时机（如控制中心 Pause 收敛、音频路由/会话恢复）把 UI 状态“冲开”回播放。
                // 这里统一按真实状态刷新：updateNowPlayingInfoCenter 使用 isMuted -> Rate(0/1)。
                [self updateNowPlayingInfoCenter];
                
                if (completion) {
                    completion(YES);
                }
                return;
            }

            BoluoAudioLog(@"[BoluoMusicPlayerIOS] Warning: Failed to activate session on resume (attempt %ld): code=%ld, domain=%@, info=%@",
                  (long)(retryCount + 1),
                  (long)activateError.code,
                  activateError.domain,
                  activateError.localizedDescription);
            
            // Only retry for specific errors (e.g. !hrt 560557684)
            if (retriablePriorityError && retryCount < 20) {
                BoluoAudioLog(@"[BoluoMusicPlayerIOS] InsufficientPriority. Retrying with MixWithOthers... (attempt %ld)", (long)(retryCount + 1));
                NSTimeInterval delay = 0.5;
                dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(delay * NSEC_PER_SEC)), dispatch_get_main_queue(), ^{
                    [self performSessionActivationWithRetryCount:retryCount + 1 allowMix:YES completion:completion];
                });
                return;
            }
            if (!retriablePriorityError) {
                BoluoAudioLog(@"[BoluoMusicPlayerIOS] Non-retriable activation error: %ld", (long)activateError.code);
            }
            if (completion) {
                completion(NO);
            }
        });
    });
}
- (BOOL)isUserPaused {
    return self.userManuallyPaused;
}

- (void)syncExternalPlaybackState:(BOOL)isPlaying {
    // Logic aligned with WanosAudioSession.mm setNowPlayingState
    CFTimeInterval t0 = CACurrentMediaTime();

    BoluoAudioLog(@"[BoluoAudioResume] syncExternalPlaybackState ENTER isPlaying=%@ isMuted=%@ userPaused=%@ phase=%@ engine=%@",
          isPlaying ? @"YES" : @"NO",
          self.isMuted ? @"YES" : @"NO",
          self.userManuallyPaused ? @"YES" : @"NO",
          BoluoPlayerSessionPhaseName(self.sessionPhase),
          BoluoPlaybackStateName(GetPlaybackState()));

    // Disarmed：禁止把桥接钉成「在播/已解 mute」。否则 Start 0.5s 暖机 Sync、或退登窗口残留回调
    // 会在登录页/Rebuild 前把 isMuted=NO，后续安全网误判已完成 → 重登无声。
    if (isPlaying && BoluoAudioSession_IsLogoutSilenced() != 0) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] syncExternalPlaybackState IGNORE isPlaying=YES while logoutSilenced");
        self.isMuted = YES;
        self.isPlaying = NO;
        [self updateNowPlayingInfoCenter];
        [self logAudioResumeSnapshot:@"syncExternal-blocked-logoutSilenced"];
        BoluoNativeAudioJank_Report(@"native_sync_external",
                                   (CACurrentMediaTime() - t0) * 1000.0,
                                   @"blocked_logoutSilenced");
        return;
    }

    // 用户显式暂停期间引擎可能仍为 Started（静音式暂停），isPlaying=YES 不得据此清除 userManuallyPaused 导致自动恢复。
    if (isPlaying && self.userManuallyPaused) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] syncExternalPlaybackState IGNORE isPlaying=YES while userPaused");
        [self updateNowPlayingInfoCenter];
        [self logAudioResumeSnapshot:@"syncExternal-blocked-userPaused"];
        BoluoNativeAudioJank_Report(@"native_sync_external",
                                   (CACurrentMediaTime() - t0) * 1000.0,
                                   @"blocked_userPaused");
        return;
    }

    // 前台占位卡：引擎还没 unmute 也按播放中写 NowPlaying。后台不占。
    self.nowPlayingAdvertisedPlaying = isPlaying
        && [UIApplication sharedApplication].applicationState != UIApplicationStateBackground;

    // 进岛 Start / OnApplicationFocus 常在 200ms 内连打 SyncExternal(true)；
    // 状态已收敛时直接返回，避免重复 unmute / NowPlaying / 诊断快照叠峰（听感卡顿电流音）。
    if (isPlaying && self.isPlaying && !self.isMuted && !self.userManuallyPaused) {
        BoluoAudioLog(@"[BoluoAudioResume] syncExternalPlaybackState COALESCE already playing+unmuted");
        return;
    }
    if (!isPlaying && !self.isPlaying && self.isMuted
        && self.recoveryPhase == BoluoPlayerRecoveryPhaseNone
        && !self.deviceDirty
        && !self.wantsResumeAfterRecovery) {
        BoluoAudioLog(@"[BoluoAudioResume] syncExternalPlaybackState COALESCE already paused+muted");
        return;
    }

    if (!isPlaying) {
        // Unity 表示不在播放 → 静音书签
        self.isMuted = YES;
        self.isPlaying = NO;
        // 中断/蓝牙恢复进行中：系统 mute 后 Unity 可能同步 isPlaying=false，
        // 此时清 pending 会导致 RouteDisconnected 0.6s 后跳过恢复 → 永久无声。
        BOOL preservePending = self.recoveryPhase != BoluoPlayerRecoveryPhaseNone
            || self.deviceDirty
            || self.wantsResumeAfterRecovery;
        if (!preservePending) {
            self.wantsResumeAfterRecovery = NO;
            [self noteCompetingAudioCleared:@"syncExternal-false"];
            [NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(_pollWaitForCompetingAudioToStop) object:nil];
            self.competingAudioPollCount = 0;
        } else {
            BoluoAudioLog(@"[BoluoAudioResume] syncExternal(false) keep pending during interruption recovery");
        }
        [self updateNowPlayingInfoCenter];
        [self logAudioResumeSnapshot:@"syncExternal-exit-playing=NO"];
        BoluoNativeAudioJank_Report(@"native_sync_external",
                                   (CACurrentMediaTime() - t0) * 1000.0,
                                   @"playing=0");
        return;
    }

    // —— isPlaying=YES ——
    // 首播前（非 Engaged）：只清路由抖动留下的假 userManuallyPaused，禁止抢 DelayPlayMusic 的 unmute，
    // 也禁止把 isMuted 钉成 NO（否则 Rebuild/安全网永久跳过）。
    if (self.sessionPhase != BoluoPlayerSessionPhaseEngaged) {
        self.userManuallyPaused = NO;
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] syncExternal pre-Engaged: clear userPaused only, stay muted phase=%@",
              BoluoPlayerSessionPhaseName(self.sessionPhase));
        BoluoMusicPlayer_Trail(@"syncExternal preEngaged clearPauseOnly phase=%@",
                               BoluoPlayerSessionPhaseName(self.sessionPhase));
        [self updateNowPlayingInfoCenter];
        [self logAudioResumeSnapshot:@"syncExternal-preEngaged-no-unmute"];
        BoluoNativeAudioJank_Report(@"native_sync_external",
                                   (CACurrentMediaTime() - t0) * 1000.0,
                                   @"preEngaged");
        return;
    }

    BoluoUnmuteVerdict v = [self unmuteVerdictForReason:@"syncExternal-playing"];
    if (v == BoluoUnmuteVerdictBlockedDisarmed) {
        self.isMuted = YES;
        self.isPlaying = NO;
        [self updateNowPlayingInfoCenter];
        [self logAudioResumeSnapshot:@"syncExternal-blocked-disarmed"];
        BoluoNativeAudioJank_Report(@"native_sync_external",
                                   (CACurrentMediaTime() - t0) * 1000.0,
                                   @"blocked_disarmed");
        return;
    }
    if (v == BoluoUnmuteVerdictDeferDeviceBusy || v == BoluoUnmuteVerdictDeferRecovery) {
        // 保持 isMuted 原值；设备队列结束后由 resumeIfStillMutedAfterDeviceWork 补播。
        BoluoAudioLog(@"[BoluoAudioResume] syncExternal DEFER %@ (keep isMuted=%@)",
              BoluoUnmuteVerdictName(v), self.isMuted ? @"YES" : @"NO");
        BoluoMusicPlayer_Trail(@"syncExternal DEFER %@ muted=%d",
                               BoluoUnmuteVerdictName(v), (int)self.isMuted);
        [self updateNowPlayingInfoCenter];
        [self logAudioResumeSnapshot:@"syncExternal-defer-device-or-recovery"];
        BoluoNativeAudioJank_Report(@"native_sync_external",
                                   (CACurrentMediaTime() - t0) * 1000.0,
                                   @"defer");
        return;
    }

    self.userManuallyPaused = NO;
    if (self.isMuted) {
        if (![self unmuteEngineIfExclusiveAvailableWithReason:@"syncExternal-playing"]) {
            // 竞音推迟等：helper 未解 mute 时绝不钉 isMuted=NO
            self.isMuted = YES;
            self.isPlaying = YES;
        }
    } else {
        self.isPlaying = YES;
    }

    [self updateNowPlayingInfoCenter];
    [self logAudioResumeSnapshot:[NSString stringWithFormat:@"syncExternal-exit-playing=%@", isPlaying ? @"YES" : @"NO"]];
    BoluoNativeAudioJank_Report(@"native_sync_external",
                               (CACurrentMediaTime() - t0) * 1000.0,
                               isPlaying ? @"playing=1" : @"playing=0");
}

- (void)handleAudioInterruption:(NSNotification *)notification {
    if (![NSThread isMainThread]) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self handleAudioInterruption:notification];
        });
        return;
    }

    BoluoAudioLog(@"[BoluoMusicPlayerIOS] handleAudioInterruption invoked: %@", notification.userInfo);
    NSNumber *typeValue = notification.userInfo[AVAudioSessionInterruptionTypeKey];
    AVAudioSessionInterruptionType type = (AVAudioSessionInterruptionType)[typeValue unsignedIntegerValue];

    if (type == AVAudioSessionInterruptionTypeBegan) {
        CFTimeInterval beganT0 = CACurrentMediaTime();
        // AppWasSuspended：人还在前台、也没有别的播放器时整段 no-op（不是抢焦点）。
        // 启动期已经进后台，或对方正在播：不能 no-op，否则切抖音 / QQ 音乐叠播。
        // 旧路径对所有挂起都 mute+标脏，回前台 setActive 会咯噔。
        if (BoluoNotificationIsAppWasSuspended(notification)) {
            AVAudioSession *suspendedSession = [AVAudioSession sharedInstance];
            BOOL foreign = [suspendedSession isOtherAudioPlaying]
                || suspendedSession.secondaryAudioShouldBeSilencedHint;
            BOOL startup = BoluoAudioSession_IsStartupAudibleCommitted() == 0;
            BOOL background = [UIApplication sharedApplication].applicationState
                == UIApplicationStateBackground;
            // 已在前台出过声、且没有别的播放器：挂起不是抢焦点，保持后台续播。
            // 启动期人已经离开，或对方已在播：必须继续往下静音，否则切抖音 / QQ 音乐叠播。
            if (!foreign && !(startup && background)) {
                CFTimeInterval noopMs = (CACurrentMediaTime() - beganT0) * 1000.0;
                BoluoAudioLog(@"[BoluoAudioResume] Interruption BEGAN AppWasSuspended no-op");
                [self reportAudioFocusLossToBLSWithTag:@"audio_focus_loss_began"
                                                detail:@"reason=AppWasSuspended phase=noop"
                                            durationMs:noopMs];
                return;
            }
            BoluoAudioLog(@"[BoluoAudioResume] Interruption BEGAN AppWasSuspended continue foreign=%d startup=%d",
                  foreign ? 1 : 0, startup ? 1 : 0);
        }

        // 通知栈内同步压增益（constructor observer 已抢一次；此处再钉一次防漏）。
        // 赶在 MiniAudio 同栈 ma_device_stop 之后的硬 mute 之前，消切微信/QQ 电流音。
        BoluoSyncPreDipForSystemInterruption(@"interruption_began_pre_dip");

        BOOL isRouteDisconnected = [self isRouteDisconnectedInterruption:notification];
        self.lastInterruptionBeganEpoch = [[NSDate date] timeIntervalSince1970];
        // 刚做完后台自动恢复又被中断（对方 App 重新出声抢焦点）→ 静默窗口误判，退避加倍
        if (!isRouteDisconnected) {
            [self _penalizeBackgroundSilenceConfirmIfMisjudged];
        }
        // 设备标脏：MiniAudio 主线程通知里会/已 ma_device_stop；Began 不再自行 Reset。
        // 恢复路径（Ended / scheduleRouteDisconnectedRecovery → performDeviceResetAndResume）
        // 见脏标记只 SetPlaybackDevice，避免再 Reset 拆掉场景音效。
        BoluoAudioLog(@"[BoluoAudioResume] Interruption BEGAN isPlaying=%@ userPaused=%@ routeDisconnected=%@",
              self.isPlaying ? @"YES" : @"NO",
              self.userManuallyPaused ? @"YES" : @"NO",
              isRouteDisconnected ? @"YES" : @"NO");
        [self logAudioResumeSnapshot:@"Interruption-began"];

        // CRITICAL（Incident C7087C4A / 2026-07-16 scene-update 8BADF00D）：
        // MiniAudio OutputCoreAudio 在主线程 AVAudioSession 通知栈内同步 ma_device_stop（等 CV）。
        // 若此处再 dispatch 到 session-work 调 ResetPlaybackDevice（内部再 ma_device_stop 等 mutex），
        // 与主线程死锁 → 主线程 0% CPU 卡死 >10s → FRONTBOARD watchdog。
        // RouteDisconnected 与电话/Siri 等真中断一律：只静音+记状态，禁止 Reset / setActive:NO。
        // 设备重建推迟到中断结束后的恢复链（BoluoAudioDeviceRecoveryPolicy.ShouldSkipResetOnInterruptionBegan）。
        BoluoAudioLog(@"[BoluoAudioResume] Interruption Began: skip Reset/setActive:NO (avoid ma_device_stop deadlock with MiniAudio observer)");

        // 启动期记账 isMuted=YES，但 Unity 已直接 SetMute(false)：引擎热着就算还在响。
        BOOL engineHot = NO;
        if (!BoluoAudio_IsPlaybackDeviceRecoveryInFlight() && !sBoluoEngineHardMuted) {
            int engineState = GetPlaybackState();
            engineHot = (engineState == 2 || engineState == 3);
        }
        BOOL pendingResumeSnapshot = self.wantsResumeAfterRecovery;
        BOOL alreadyInterrupted = (self.recoveryPhase == BoluoPlayerRecoveryPhaseInterruptionActive);
        if (!alreadyInterrupted) {
            // 记录中断前是否在播；切其它 App 时中断结束后 shouldResume 常为 NO，需回前台再恢复
            // 退登静默期间禁止登记 pending，否则 setActive(NO) 自身触发的 Ended 会把登录页拉响。
            if (BoluoAudioSession_IsLogoutSilenced() != 0) {
                pendingResumeSnapshot = NO;
            } else {
                BOOL audible = self.isPlaying || !self.isMuted || engineHot;
                pendingResumeSnapshot = audible && !self.userManuallyPaused;
            }
            self.wantsResumeAfterRecovery = pendingResumeSnapshot;
            BoluoAudioLog(@"[BoluoAudioResume] Interruption BEGAN wantsResumeAfterRecovery=%@",
                  self.wantsResumeAfterRecovery ? @"YES" : @"NO");
        }
        // noteInterruptionBegan：标 deviceDirty + recoveryPhase=InterruptionActive
        [self noteInterruptionBegan:@"InterruptionBegan"];
        // 静音与 RouteDisconnected 恢复调度挪出 AVAudioSession 通知栈：
        // 1) 避免与 MiniAudio 同栈 ma_device_stop 争锁（Began 已禁 Reset，但 SetMute 仍危险）；
        // 2) 系统 mute 不再 cancel 恢复链（仅用户 Pause 才 cancel）；此处 mute 后直接 schedule RD。
        BOOL preservePending = pendingResumeSnapshot && !self.userManuallyPaused;
        BOOL needMute = !self.isMuted || engineHot;
        // 必须用 shouldPrefer（后台一律禁 duck）。旧 shouldUseTransientDuck 忽略后台
        // → 切微信/QQ Began 尚无 otherAudio 时误 duck，满能量硬停/硬 mute = 电流音。
        BOOL canDuck = [self shouldPreferTransientDuckForInterruption:notification
                                                  isRouteDisconnected:isRouteDisconnected]
            && preservePending;
        __weak __typeof(self) weakSelf = self;
        dispatch_async(dispatch_get_main_queue(), ^{
            __strong __typeof(weakSelf) strongSelf = weakSelf;
            if (!strongSelf) return;
            CFTimeInterval handleT0 = CACurrentMediaTime();
            if (strongSelf.userManuallyPaused) {
                strongSelf.wantsResumeAfterRecovery = NO;
                return;
            }
            strongSelf.wantsResumeAfterRecovery = preservePending && !strongSelf.userManuallyPaused;

            // 短暂可 duck（导航/短信）：不 mute，通知 Unity 降到 40%
            if (canDuck) {
                [strongSelf beginAudioFocusDuck];
                [strongSelf scheduleDuckEscalateCheck];
                BoluoAudioLog(@"[BoluoAudioResume] Interruption Began branch=transient_duck");
                return;
            }

            [strongSelf endAudioFocusDuck];

            // RouteDisconnected 真断开永远等不到可靠的 Ended，必须自调度恢复。
            BOOL looksLikeRouteDisconnect =
                [strongSelf shouldUseRouteDisconnectedSelfResume:isRouteDisconnected];
            // 满音量硬 SetMute → 电流声。护栏与 App 是否仍 Active 无关：
            // 切微信/QQ 时 Began 常早于 ResignActive（state 仍 Active）。
            BOOL guardedMuteFirst = !looksLikeRouteDisconnect
                && strongSelf.wantsResumeAfterRecovery
                && needMute
                && (!strongSelf.isMuted || engineHot);

            // 护栏路径禁止立刻满音量 mute；仅 RD 自恢复可直接 mute（设备已停）。
            if (!guardedMuteFirst && needMute && !strongSelf.isMuted) {
                [strongSelf setAudioMute:YES isUserAction:NO resetDevice:NO];
            }

            NSString *branch = @"none";
            if (strongSelf.wantsResumeAfterRecovery && looksLikeRouteDisconnect) {
                branch = @"route_disconnect_self_resume";
                [strongSelf logAudioResumeSnapshot:@"Interruption-schedule-self-resume"];
                [strongSelf noteRouteDisconnectedForSoftResumePolicy];
                [strongSelf scheduleRouteDisconnectedRecovery];
                [strongSelf scheduleResumeWhenDeviceIdleAfterSystemMute];
            } else if (strongSelf.wantsResumeAfterRecovery) {
                branch = guardedMuteFirst ? @"guarded_mute_competing" : @"competing_audio_wait";
                [strongSelf noteCompetingAudioDetected:@"InterruptionBeganCompeting"];
                AVAudioSession *s = [AVAudioSession sharedInstance];
                if ([s isOtherAudioPlaying]) {
                    strongSelf.competingEpisodeSawOtherAudioPlaying = YES;
                }
                if (s.secondaryAudioShouldBeSilencedHint) {
                    strongSelf.competingEpisodeSawSecondarySilence = YES;
                }
                BoluoAudioLog(@"[BoluoAudioResume] Interruption Began → competing-audio wait (not RD) guardedMute=%@ sawOtherAudio=%@ sawSecondary=%@",
                      guardedMuteFirst ? @"YES" : @"NO",
                      strongSelf.competingEpisodeSawOtherAudioPlaying ? @"YES" : @"NO",
                      strongSelf.competingEpisodeSawSecondarySilence ? @"YES" : @"NO");
                [strongSelf _scheduleWaitForCompetingAudioToStop];

                if (guardedMuteFirst) {
                    [strongSelf reportAudioFocusLossToBLSWithTag:@"audio_duck_begin"
                                                          detail:@"guarded_mute_before_competing"
                                                      durationMs:0];
                    __weak __typeof(strongSelf) softWeak = strongSelf;
                    [strongSelf guardedSystemMuteWithReason:@"InterruptionBeganCompeting" completion:^{
                        __strong __typeof(softWeak) softSelf = softWeak;
                        if (!softSelf || softSelf.userManuallyPaused) return;
                        [softSelf endAudioFocusDuck];
                        [softSelf yieldAudioSessionForCompetingAudioWithReason:@"InterruptionBeganCompeting"];
                        AVAudioSession *sessSoft = [AVAudioSession sharedInstance];
                        if ([sessSoft isOtherAudioPlaying] || sessSoft.secondaryAudioShouldBeSilencedHint) {
                            [softSelf notifyUnityCompetingOrSystemMuted:YES];
                        }
                        [softSelf reportAudioFocusLossToBLSWithTag:@"audio_duck_escalate"
                                                            detail:@"guarded_mute_to_mute"
                                                        durationMs:kBoluoGuardedMuteDelaySeconds * 1000.0];
                    }];
                } else {
                    BOOL longCompete = s.isOtherAudioPlaying || s.secondaryAudioShouldBeSilencedHint;
                    [strongSelf notifyUnityCompetingOrSystemMuted:longCompete];
                    [strongSelf noteFocusState:BoluoAudioFocusStateCompetingWait
                                        reason:@"began_competing_direct"];
                    // 后台被微信语音识别等打断：必须 deactivate 会话，否则对方 ASR「不生效」
                    [strongSelf yieldAudioSessionForCompetingAudioWithReason:@"InterruptionBeganCompeting"];
                }
            }
            CFTimeInterval handleMs = (CACurrentMediaTime() - handleT0) * 1000.0;
            NSString *handleDetail = [NSString stringWithFormat:
                @"branch=%@ needMute=%@ pendingResume=%@ phase=deferred_mute",
                branch,
                needMute ? @"YES" : @"NO",
                strongSelf.wantsResumeAfterRecovery ? @"YES" : @"NO"];
            [strongSelf reportAudioFocusLossToBLSWithTag:@"audio_focus_loss_handle"
                                                  detail:handleDetail
                                              durationMs:handleMs];
        });
    } else if (type == AVAudioSessionInterruptionTypeEnded) {
        NSNumber *optionValue = notification.userInfo[AVAudioSessionInterruptionOptionKey];
        BOOL shouldResume = (([optionValue unsignedIntegerValue] & AVAudioSessionInterruptionOptionShouldResume) != 0);
        BoluoAudioLog(@"[BoluoAudioResume] Interruption ENDED shouldResume=%@ pending=%@ userPaused=%@ engine=%@",
              shouldResume ? @"YES" : @"NO",
              self.wantsResumeAfterRecovery ? @"YES" : @"NO",
              self.userManuallyPaused ? @"YES" : @"NO",
              BoluoPlaybackStateName(GetPlaybackState()));
        [self logAudioResumeSnapshot:@"Interruption-ended"];
        // 结束短暂 duck：对方仍在播 / secondarySilence 时禁止 Unduck 回满音量
        // （偶现微信语音播放中 Ended 早到 → 音乐突然满音量叠播）。
        [self cancelTransientDuckEscalate];
        BOOL competingStill = [self shouldDeferForCompetingOrSecondarySilence];
        BOOL wasDucked = (self.recoveryPhase == BoluoPlayerRecoveryPhaseDucked);
        if (wasDucked && competingStill) {
            BoluoAudioLog(@"[BoluoAudioDuck] InterruptionEnded keep duck→mute (competing still active)");
            self.wantsResumeAfterRecovery = YES;
            [self noteCompetingAudioDetected:@"endedWhileCompeting"];
            __weak __typeof(self) weakEnded = self;
            [self guardedSystemMuteWithReason:@"EndedWhileCompeting" completion:^{
                __strong __typeof(weakEnded) strongEnded = weakEnded;
                if (!strongEnded) return;
                [strongEnded endAudioFocusDuck];
            }];
            [self _scheduleWaitForCompetingAudioToStop];
            if (self.userManuallyPaused) {
                // 用户已手动暂停：不 mute，只还原 duck 增益（引擎已静音，还原不可闻）
                BoluoApplyMusicGain(1.0f, false, @"unduck_user_paused");
            } else {
                // 护栏 mute 落地后由护栏自身还原增益（顺序不变量：先静音再回增益，旧顺序 → 电流声）
                [self guardedSystemMuteWithReason:@"EndedWhileCompeting" completion:nil];
            }
            [self reportAudioFocusLossToBLSWithTag:@"audio_duck_escalate"
                                            detail:@"ended_while_competing"
                                        durationMs:0];
        } else {
            [self endAudioFocusDuck];
            if (wasDucked) {
                // 对方已停：还原 duck 增益（0.4→1.0，原生同步；与旧 C# RestoreGain 行为一致）
                BoluoApplyMusicGain(1.0f, false, @"unduck_interruption_ended");
                [self reportAudioFocusLossToBLSWithTag:@"audio_duck_end"
                                                detail:@"interruption_ended"
                                            durationMs:0];
                if (!self.isMuted && !self.userManuallyPaused) {
                    [self noteFocusState:BoluoAudioFocusStateNormal
                                  reason:@"unduck_interruption_ended"];
                }
            }
        }
                if (self.pendingSoftEscalateBlock) {
            dispatch_block_cancel(self.pendingSoftEscalateBlock);
            self.pendingSoftEscalateBlock = nil;
        }

        // 电话等真 Ended 到达时，取消蓝牙自调度，避免与 Ended 恢复双路径打架
        if (self.pendingRouteDisconnectedRecoveryBlock) {
            dispatch_block_cancel(self.pendingRouteDisconnectedRecoveryBlock);
            self.pendingRouteDisconnectedRecoveryBlock = nil;
        }
        [NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(_pollResumeWhenDeviceIdleAfterSystemMute) object:nil];
        self.deviceIdleResumePollCount = 0;

BOOL shouldAutoResume = shouldResume
            && !self.userManuallyPaused
            && BoluoAudioSession_IsLogoutSilenced() == 0;
        // 微信语音 ASR-only：即便 shouldResume=YES（按住间隙常见），后台也禁止立刻 hard，
        // 保持暂停直到回菠萝前台（对齐 ShouldAllowBackgroundAutoResumeAfterCompetingAudioStopped）。
        if (shouldAutoResume && ![self shouldAllowBackgroundAutoResumeAfterCompetingAudioStopped]) {
            BoluoAudioLog(@"[BoluoAudioResume] InterruptionEnded shouldResume=YES but ASR-only BG — keep pending until foreground");
            self.wantsResumeAfterRecovery = YES;
            [self noteCompetingAudioDetected:@"ASR-only-BG-defer"];
            [self noteFocusState:BoluoAudioFocusStateCompetingWait
                          reason:@"ASR-only-BG-defer"];
            [NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(_pollWaitForCompetingAudioToStop) object:nil];
            self.competingAudioPollCount = 0;
            self.backgroundSilenceConfirmStreak = 0;
            self.backgroundResumeArmed = NO;
            return;
        }
        if (shouldAutoResume) {
            int state = GetPlaybackState();
            // 0: Uninitialized, 1: Stopped
            // Began 不再 Reset；MiniAudio 可能已 stop，但引擎状态仍可能读到 Started。
            // deviceDirty=YES 表示设备已脏，必须走设备恢复（脏则只 Set）。
            BOOL engineReallyStopped = (state == 0 || state == 1) || self.deviceDirty;
            
            // 电话/高优先级中断：允许 Mix 抢 session；其它 App 仍在播或要求次要静音则推迟 unmute
            self.resumeActivationUsesMixWithOthers = YES;
            if ([self shouldDeferForCompetingOrSecondarySilence]) {
                [self noteCompetingAudioDetected:@"InterruptionEndedOtherAudio"];
                [self _scheduleWaitForCompetingAudioToStop];
            } else {
                [self noteCompetingAudioCleared:@"InterruptionEndedNoOtherAudio"];
            }
            
            // 不论引擎状态如何，都先抢回 session/NowPlaying（电话挂断后常见：声音已恢复，但控制中心卡片不是我们的）
            [self attemptSessionActivationWithRetry:^(BOOL success) {
                if (success) {
                    [self _reassertNowPlayingAndCommandsWithReason:@"InterruptionEndedActivated"];
                    [self _scheduleExclusiveReclaimAfterInterruption];
                }
            }];

            if (engineReallyStopped) {
                BoluoAudioLog(@"[BoluoMusicPlayerIOS] Interruption ended with ShouldResume. Scheduling device recovery.");
                self.resumeActivationUsesMixWithOthers = YES;
                [self scheduleInterruptionRecovery];
            } else {
                BoluoAudioLog(@"[BoluoMusicPlayerIOS] Engine already running (%d), skip device recovery but keep session reclaim.", state);
                // 中断期间已静音：引擎状态可能仍为 Started，但仍需解除静音
                if (self.isMuted && !self.userManuallyPaused) {
                    BoluoAudioLog(@"[BoluoMusicPlayerIOS] Engine running but muted after interruption - finishing resume");
                    [self finishResumeAfterDeviceReady];
                }
                [self noteInterruptionEnded:@"InterruptionEndedEngineRunning"];
                self.wantsResumeAfterRecovery = NO;
                [self cancelPendingInterruptionRecovery];
            }
        } else {
            // 切换音乐 App 等场景：shouldResume=NO，但用户回菠萝后仍应恢复
            BOOL keepPending = self.wantsResumeAfterRecovery && !self.userManuallyPaused;
            BoluoAudioLog(@"[BoluoAudioResume] Interruption ended shouldResume=NO keepPendingForForeground=%@",
                  keepPending ? @"YES" : @"NO");
            [self noteInterruptionEnded:@"InterruptionEndedNoShouldResume"];
            if (!keepPending) {
                self.wantsResumeAfterRecovery = NO;
                // Began 只标脏未走恢复链时必须清脏，否则 Unity shouldDefer 永久为真，
                // 回前台 DoResume/音效路径被挡住；下次 Play 的 unmute 会自行 Reset+Set。
                self.deviceDirty = NO;
            }
            if (self.pendingActivationBlock) {
                dispatch_block_cancel(self.pendingActivationBlock);
                self.pendingActivationBlock = nil;
            }
            if (self.recoveryPhase == BoluoPlayerRecoveryPhaseRecoveringDevice) {
                [self transitionToRecoveryPhase:BoluoPlayerRecoveryPhaseNone
                                         reason:@"InterruptionEndedClearRestoring"];
            }

            if (keepPending) {
                // 其它音源可能尚未暂停：立即尝试 + 轮询等待其停止后自动恢复（不必等回前台）
                [self deferTryResumeWhenCompetingAudioStopped:@"InterruptionEndedKeepPending"];
            }
        }
    }
}

/// iOS 17+ 蓝牙设备断开会以「中断」形式下发（AVAudioSessionInterruptionReasonRouteDisconnected）。
- (BOOL)isRouteDisconnectedInterruption:(NSNotification *)notification {
    if (@available(iOS 17.0, *)) {
        NSNumber *reasonValue = notification.userInfo[AVAudioSessionInterruptionReasonKey];
        return reasonValue != nil &&
               (AVAudioSessionInterruptionReason)[reasonValue unsignedIntegerValue] ==
                   AVAudioSessionInterruptionReasonRouteDisconnected;
    }
    return NO;
}

/// 路由已落到扬声器/听筒，且无 competing → 可能是蓝牙断开收敛。
/// 不能单独作为 RD 依据——本就在扬声器时微信录音也会满足；须与 recentlyHadExternal 组合。
- (BOOL)currentRouteLooksLikeBuiltInFallback {
    AVAudioSession *session = [AVAudioSession sharedInstance];    if ([session isOtherAudioPlaying] || session.secondaryAudioShouldBeSilencedHint) {
        return NO;
    }
    for (AVAudioSessionPortDescription *output in session.currentRoute.outputs) {
        NSString *portType = output.portType;
        if ([portType isEqualToString:AVAudioSessionPortBuiltInSpeaker] ||
            [portType isEqualToString:AVAudioSessionPortBuiltInReceiver]) {
            return YES;
        }
    }
    return NO;
}

/// 系统 mute 时若 session-work 仍 inFlight：等其结束再恢复；超时则强制清 inFlight 并走恢复链。
- (void)scheduleResumeWhenDeviceIdleAfterSystemMute {
    [NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(_pollResumeWhenDeviceIdleAfterSystemMute) object:nil];
    self.deviceIdleResumePollCount = 0;
    [self performSelector:@selector(_pollResumeWhenDeviceIdleAfterSystemMute) withObject:nil afterDelay:0.25];
}

- (void)_pollResumeWhenDeviceIdleAfterSystemMute {
    if (self.userManuallyPaused) {
        self.deviceIdleResumePollCount = 0;
        return;
    }
    BOOL wantsResume = self.wantsResumeAfterRecovery
        || self.deviceDirty
        || self.recoveryPhase == BoluoPlayerRecoveryPhaseInterruptionActive
        || (self.isMuted && !self.userManuallyPaused);
    if (!wantsResume || !self.isMuted) {
        self.deviceIdleResumePollCount = 0;
        return;
    }

    self.deviceIdleResumePollCount += 1;
    // 约 5s（0.25 * 20）；Reset 挂死时不能无限等
    if (self.playbackDeviceRecoveryInFlight) {
        if (self.deviceIdleResumePollCount <= 20) {
            BoluoAudioLog(@"[BoluoAudioResume] wait device idle before resume (poll=%ld inFlight=YES)",
                  (long)self.deviceIdleResumePollCount);
            [self performSelector:@selector(_pollResumeWhenDeviceIdleAfterSystemMute) withObject:nil afterDelay:0.25];
            return;
        }
        BoluoAudioLog(@"[BoluoAudioResume] inFlight watchdog FORCE CLEAR — stuck Reset/Set blocking BT resume");
        [self logAudioResumeSnapshot:@"inFlight-watchdog-force-clear"];
        self.playbackDeviceRecoveryInFlight = NO;
    }

    if (self.recoveryPhase == BoluoPlayerRecoveryPhaseRecoveringDevice
        || self.pendingActivationBlock != nil ||
        self.pendingRouteDisconnectedRecoveryBlock != nil) {
        // RD / interruption recovery 已在路上；再等一轮，避免双发
        if (self.deviceIdleResumePollCount <= 24) {
            [self performSelector:@selector(_pollResumeWhenDeviceIdleAfterSystemMute) withObject:nil afterDelay:0.25];
            return;
        }
    }

    self.deviceIdleResumePollCount = 0;
    self.wantsResumeAfterRecovery = YES;
    [self logAudioResumeSnapshot:@"device-idle-force-resume"];
    // 微信录音期间 secondarySilence 可能晚到；强制恢复前再验一次，避免掐麦
    if ([self shouldDeferForCompetingOrSecondarySilence]) {
        BoluoAudioLog(@"[BoluoAudioResume] device idle force-resume ABORT → competing wait");
        [self noteCompetingAudioDetected:@"DeviceIdleForceResumeAbort"];
        [self _scheduleWaitForCompetingAudioToStop];
        [self yieldAudioSessionForCompetingAudioWithReason:@"DeviceIdleForceResumeAbort"];
        return;
    }
    BoluoAudioLog(@"[BoluoAudioResume] device idle → force scheduleInterruptionRecovery");
    self.resumeActivationUsesMixWithOthers = NO;
    // 若 RD block 还在，取消后由本路径统一拉起，避免 0.6s 后再跑一次
    if (self.pendingRouteDisconnectedRecoveryBlock) {
        dispatch_block_cancel(self.pendingRouteDisconnectedRecoveryBlock);
        self.pendingRouteDisconnectedRecoveryBlock = nil;
    }
    [self scheduleInterruptionRecovery];
}

- (NSTimeInterval)routeDisconnectedSettleDelaySeconds {
    return (self.routeDisconnectFlapCount >= 2)
        ? kBoluoRDSettleDelayFlapSeconds
        : kBoluoRDSettleDelayFirstSeconds;
}

- (BOOL)currentRouteHasValidOutputForResume {
    AVAudioSessionRouteDescription *route = [AVAudioSession sharedInstance].currentRoute;
    for (AVAudioSessionPortDescription *output in route.outputs) {
        NSString *portType = output.portType;
        if (portType.length > 0
            && ![portType isEqualToString:@"Unknown"]
            && ![portType isEqualToString:@"None"]) {
            return YES;
        }
    }
    return NO;
}

/// QA：周期性模拟「蓝牙 RD + 强制硬 SetPlaybackDevice」，听感一卡一卡。
- (void)startForceBluetoothRdStutterReproWithInterval:(NSTimeInterval)intervalSeconds {
    if (![NSThread isMainThread]) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self startForceBluetoothRdStutterReproWithInterval:intervalSeconds];
        });
        return;
    }
    [self stopForceBluetoothRdStutterRepro];
    NSTimeInterval interval = intervalSeconds > 0.5 ? intervalSeconds : kBoluoForceRdStutterIntervalSeconds;
    self.forceHardRdResumeForRepro = YES;
    self.forceRdStutterActive = YES;
    dispatch_source_t timer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, dispatch_get_main_queue());
    dispatch_source_set_timer(timer,
                              dispatch_time(DISPATCH_TIME_NOW, (int64_t)(2.0 * NSEC_PER_SEC)),
                              (uint64_t)(interval * NSEC_PER_SEC),
                              (uint64_t)(0.1 * NSEC_PER_SEC));
    __weak __typeof(self) weakSelf = self;
    dispatch_source_set_event_handler(timer, ^{
        [weakSelf _fireForceBluetoothRdStutterTick];
    });
    self.forceRdStutterTimer = timer;
    dispatch_resume(timer);
    BoluoAudioLog(@"[BoluoAudioResume] FORCE BT RD stutter START interval=%.1fs", interval);
}

- (void)stopForceBluetoothRdStutterRepro {
    if (![NSThread isMainThread]) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self stopForceBluetoothRdStutterRepro];
        });
        return;
    }
    if (self.forceRdStutterTimer) {
        dispatch_source_cancel(self.forceRdStutterTimer);
        self.forceRdStutterTimer = nil;
    }
    self.forceRdStutterActive = NO;
    self.forceHardRdResumeForRepro = NO;
}

- (void)_fireForceBluetoothRdStutterTick {
    if (!self.forceRdStutterActive || !self.isInitialized || self.userManuallyPaused) {
        return;
    }
    if (!self.isPlaying && self.isMuted) {
        return;
    }
    BoluoAudioLog(@"[BoluoAudioResume] FORCE BT RD stutter TICK — hard path (no soft)");
    self.forceHardRdResumeForRepro = YES;
    self.wantsResumeAfterRecovery = YES;
    self.deviceDirty = YES;
    [self noteInterruptionBegan:@"force-bt-rd-stutter"];
    if (!self.isMuted) {
        [self setAudioMute:YES isUserAction:NO resetDevice:NO];
    }
    [self noteRouteDisconnectedForSoftResumePolicy];
    // note 会把 preferSoft=YES；QA 强制硬路径必须清掉
    self.preferSoftRouteDisconnectedResume = NO;
    [self scheduleRouteDisconnectedRecovery];
}

- (void)markRouteDisconnectedResumeSucceeded {
    self.lastRouteDisconnectedResumeEpoch = [[NSDate date] timeIntervalSince1970];
    self.preferSoftRouteDisconnectedResume = NO;
    self.awaitingRouteDisconnectedResumeCompletion = NO;
}

/// RouteDisconnected 中断永远等不到 InterruptionEnded，需自行恢复：
/// 等系统把路由收敛到扬声器/剩余设备后，走标准中断恢复链
/// （激活会话 → 重建输出设备 → 解除静音继续播放）。用户主动暂停则不恢复。
/// 抖动窗内重复 RD：优先软恢复（unmute only），失败再 escalate 硬路径。
- (void)noteRouteDisconnectedForSoftResumePolicy {
    CFTimeInterval now = CACurrentMediaTime();
    if (self.routeDisconnectFlapWindowStart <= 0
        || (now - self.routeDisconnectFlapWindowStart) > kBoluoRDFlapWindowSeconds) {
        self.routeDisconnectFlapWindowStart = now;
        self.routeDisconnectFlapCount = 1;
    } else {
        self.routeDisconnectFlapCount += 1;
    }
    self.preferSoftRouteDisconnectedResume = (self.routeDisconnectFlapCount >= 2);
    BoluoAudioLog(@"[BoluoAudioResume] RD flapCount=%ld preferSoft=%@",
          (long)self.routeDisconnectFlapCount,
          self.preferSoftRouteDisconnectedResume ? @"YES" : @"NO");
}

- (BOOL)shouldAttemptSoftRouteDisconnectedResume {
    if (self.forceHardRdResumeForRepro)
        return NO;
    if (!self.preferSoftRouteDisconnectedResume)
        return NO;
    if ([self shouldDeferForCompetingOrSecondarySilence])
        return NO;
    if (![self currentRouteHasValidOutputForResume]
        && ![self currentRouteLooksLikeBuiltInFallback])
        return NO;
    int state = GetPlaybackState();
    // Started=2；抖动窗内第 2+ 次，或引擎仍在跑 → 软路径
    return self.routeDisconnectFlapCount >= 2 || state == 2;
}

- (void)performSoftRouteDisconnectedResume {
    [self logAudioResumeSnapshot:@"RD-soft-resume-begin"];
    if (self.userManuallyPaused) {
        return;
    }
    [self restoreExclusiveAudioSessionForResume:NO];
    [self unmuteEngineIfExclusiveAvailableWithReason:@"RD-soft-resume"];
    [self logAudioResumeSnapshot:@"RD-soft-resume-unmuted"];
    if (self.pendingSoftEscalateBlock) {
        dispatch_block_cancel(self.pendingSoftEscalateBlock);
        self.pendingSoftEscalateBlock = nil;
    }
    __weak __typeof(self) weakSelf = self;
    dispatch_block_t escalate = dispatch_block_create(DISPATCH_BLOCK_INHERIT_QOS_CLASS, ^{
        __strong __typeof(weakSelf) strongSelf = weakSelf;
        if (!strongSelf) return;
        strongSelf.pendingSoftEscalateBlock = nil;
        [strongSelf _escalateSoftRouteDisconnectedResumeIfNeeded];
    });
    self.pendingSoftEscalateBlock = escalate;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(kBoluoRDSoftEscalateDelaySeconds * NSEC_PER_SEC)),
                   dispatch_get_main_queue(), escalate);
}

- (void)_escalateSoftRouteDisconnectedResumeIfNeeded {
    if (self.userManuallyPaused)
        return;
    int state = GetPlaybackState();
    BOOL stillMuted = self.isMuted;
    BOOL engineOk = (state == 2); // Started
    if (!stillMuted && engineOk) {
        [self logAudioResumeSnapshot:@"RD-soft-resume-ok"];
        [self noteInterruptionEnded:@"RD-soft-ok"];
        self.wantsResumeAfterRecovery = NO;
        self.deviceDirty = NO;
        return;
    }
    [self logAudioResumeSnapshot:@"RD-soft-resume-escalate"];
    self.preferSoftRouteDisconnectedResume = NO;
    self.resumeActivationUsesMixWithOthers = NO;
    [self scheduleInterruptionRecovery];
}

- (BOOL)shouldUseTransientDuckForInterruption:(NSNotification *)notification {
    if (self.userManuallyPaused)
        return NO;
    if ([self shouldDeferForCompetingOrSecondarySilence])
        return NO;
    if (@available(iOS 14.5, *)) {
        NSNumber *reasonValue = notification.userInfo[AVAudioSessionInterruptionReasonKey];
        if (reasonValue != nil) {
            NSInteger reason = [reasonValue integerValue];
            // AppWasSuspended = 1；RouteDisconnected = 4 (iOS 17+)
            if (reason == 1 || reason == 4)
                return NO;
        }
    }
    return YES;
}

- (void)notifyUnityAudioFocusDuck:(BOOL)duck factor:(float)factor {
    NSString *go = self.unityGameObjectName ?: @"BoluoMusicPlayerIOS";
    if (duck) {
        NSString *msg = [NSString stringWithFormat:@"%.2f", factor];
        UnitySendMessage([go UTF8String], "OnAudioFocusDuck", [msg UTF8String]);
    } else {
        UnitySendMessage([go UTF8String], "OnAudioFocusUnduck", "");
    }
}

- (void)notifyUnityCompetingOrSystemMuted:(BOOL)competingLong {
    NSString *go = self.unityGameObjectName ?: @"BoluoMusicPlayerIOS";
    const char *method = competingLong ? "OnCompetingAudioMuted" : "OnSystemAudioMuted";
    UnitySendMessage([go UTF8String], method, "");
}

- (void)beginAudioFocusDuck {
    if (self.userManuallyPaused)
        return;
    [self noteDuckBegan:@"beginAudioFocusDuck"];
    BoluoApplyMusicGain(kBoluoTransientDuckGainFactor, true, @"transient_duck");
    [self notifyUnityAudioFocusDuck:YES factor:kBoluoTransientDuckGainFactor];
    BoluoAudioLog(@"[BoluoAudioResume] audio_duck_begin");
}

- (void)endAudioFocusDuck {
    if (self.pendingDuckEscalateBlock) {
        dispatch_block_cancel(self.pendingDuckEscalateBlock);
        self.pendingDuckEscalateBlock = nil;
    }
    if (self.recoveryPhase != BoluoPlayerRecoveryPhaseDucked)
        return;
    [self noteDuckEnded:@"endAudioFocusDuck"];
    [self notifyUnityAudioFocusDuck:NO factor:1.0f];
    BoluoAudioLog(@"[BoluoAudioResume] audio_duck_end");
}

- (void)scheduleDuckEscalateCheck {
    if (self.pendingDuckEscalateBlock) {
        dispatch_block_cancel(self.pendingDuckEscalateBlock);
        self.pendingDuckEscalateBlock = nil;
    }
    __weak __typeof(self) weakSelf = self;
    dispatch_block_t block = dispatch_block_create(DISPATCH_BLOCK_INHERIT_QOS_CLASS, ^{
        __strong __typeof(weakSelf) strongSelf = weakSelf;
        if (!strongSelf) return;
        strongSelf.pendingDuckEscalateBlock = nil;
        if (strongSelf.recoveryPhase != BoluoPlayerRecoveryPhaseDucked || strongSelf.userManuallyPaused)
            return;
        AVAudioSession *s = [AVAudioSession sharedInstance];
        if (s.isOtherAudioPlaying || s.secondaryAudioShouldBeSilencedHint) {
            BoluoAudioLog(@"[BoluoAudioResume] audio_duck_escalate -> guarded mute");
            // 禁止满音量硬 SetMute（切微信/QQ 电流音）。先护栏再清 duck。
            [strongSelf noteCompetingAudioDetected:@"duckEscalate"];
            [strongSelf _scheduleWaitForCompetingAudioToStop];
            [strongSelf guardedSystemMuteWithReason:@"DuckEscalateCompeting" completion:^{
                [strongSelf endAudioFocusDuck];
                [strongSelf yieldAudioSessionForCompetingAudioWithReason:@"DuckEscalateCompeting"];
                AVAudioSession *sess = [AVAudioSession sharedInstance];
                [strongSelf notifyUnityCompetingOrSystemMuted:
                    sess.isOtherAudioPlaying || sess.secondaryAudioShouldBeSilencedHint];
            }];
        }
    });
    self.pendingDuckEscalateBlock = block;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(kBoluoDuckEscalateDelaySeconds * NSEC_PER_SEC)),
                   dispatch_get_main_queue(), block);
}

- (void)scheduleRouteDisconnectedRecovery {
    // 断开前本就没在播（或用户主动暂停）：无需恢复。
    // pending 偶发被 syncExternal 清掉时，仍可用 deviceDirty/interruption/muted 判定恢复意图。
    BOOL wantsResume = !self.userManuallyPaused && (
        self.wantsResumeAfterRecovery
        || (self.isMuted && self.deviceDirty)
        || (self.isMuted && self.recoveryPhase == BoluoPlayerRecoveryPhaseInterruptionActive));
    if (!wantsResume) {
        [self noteInterruptionEnded:@"RD-nothingToResume"];
        if (self.pendingRouteDisconnectedRecoveryBlock) {
            dispatch_block_cancel(self.pendingRouteDisconnectedRecoveryBlock);
            self.pendingRouteDisconnectedRecoveryBlock = nil;
        }
        BoluoAudioLog(@"[BoluoAudioResume] RouteDisconnected: nothing to resume (pending=%@ userPaused=%@ muted=%@ deviceDirty=%@)",
              self.wantsResumeAfterRecovery ? @"YES" : @"NO",
              self.userManuallyPaused ? @"YES" : @"NO",
              self.isMuted ? @"YES" : @"NO",
              self.deviceDirty ? @"YES" : @"NO");
        return;
    }
    self.wantsResumeAfterRecovery = YES;

    // 走动时蓝牙会连发 RouteDisconnected；取消未执行的恢复，只保留最后一次，避免堆积 Reset/Set。
    if (self.pendingRouteDisconnectedRecoveryBlock) {
        dispatch_block_cancel(self.pendingRouteDisconnectedRecoveryBlock);
        self.pendingRouteDisconnectedRecoveryBlock = nil;
        BoluoAudioLog(@"[BoluoAudioResume] RouteDisconnected: coalesced pending auto-resume");
    }

    NSTimeInterval settle = (self.routeDisconnectFlapCount >= 2)
        ? kBoluoRDSettleDelayFlapSeconds
        : kBoluoRDSettleDelayFirstSeconds;
    BoluoAudioLog(@"[BoluoAudioResume] RouteDisconnected: scheduling auto-resume settle=%.1fs soft=%@",
          settle, self.preferSoftRouteDisconnectedResume ? @"YES" : @"NO");
    __weak __typeof(self) weakSelf = self;
    dispatch_block_t block = dispatch_block_create(DISPATCH_BLOCK_INHERIT_QOS_CLASS, ^{
        __strong __typeof(weakSelf) strongSelf = weakSelf;
        if (!strongSelf) {
            return;
        }
        strongSelf.pendingRouteDisconnectedRecoveryBlock = nil;
        BOOL stillWants = !strongSelf.userManuallyPaused && (
            strongSelf.wantsResumeAfterRecovery
            || (strongSelf.isMuted && strongSelf.deviceDirty)
            || (strongSelf.isMuted && strongSelf.recoveryPhase == BoluoPlayerRecoveryPhaseInterruptionActive));
        if (!stillWants) {
            [strongSelf noteInterruptionEnded:@"RD-settleSkip"];
            BoluoAudioLog(@"[BoluoAudioResume] RouteDisconnected recovery skipped (userPaused=%@ pending=%@ muted=%@)",
                  strongSelf.userManuallyPaused ? @"YES" : @"NO",
                  strongSelf.wantsResumeAfterRecovery ? @"YES" : @"NO",
                  strongSelf.isMuted ? @"YES" : @"NO");
            return;
        }
        strongSelf.wantsResumeAfterRecovery = YES;
        if ([strongSelf shouldDeferForCompetingOrSecondarySilence]) {
            BoluoAudioLog(@"[BoluoAudioResume] RouteDisconnected settle: competing — abort RD hard path");
            strongSelf.preferSoftRouteDisconnectedResume = NO;
            [strongSelf noteCompetingAudioDetected:@"RD-settleCompeting"];
            [strongSelf _scheduleWaitForCompetingAudioToStop];
            [strongSelf yieldAudioSessionForCompetingAudioWithReason:@"RDRecoveryAbortCompeting"];
            return;
        }
        if ([strongSelf shouldAttemptSoftRouteDisconnectedResume]) {
            [strongSelf performSoftRouteDisconnectedResume];
            return;
        }
        // 蓝牙断开后通常无竞争音源，用独占 Playback 激活；若被占用会自动降级重试
        strongSelf.resumeActivationUsesMixWithOthers = NO;
        [strongSelf scheduleInterruptionRecovery];
    });
    self.pendingRouteDisconnectedRecoveryBlock = block;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(settle * NSEC_PER_SEC)), dispatch_get_main_queue(), block);
}

- (void)scheduleInterruptionRecovery {
    // Defense in Depth: Guard against accidental calls when paused
    if (self.userManuallyPaused) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] scheduleInterruptionRecovery BLOCKED - User Manually Paused");
        return;
    }

    if (self.recoveryPhase == BoluoPlayerRecoveryPhaseRecoveringDevice) {
        return;
    }
    BOOL isLikelyPhoneInterruption =
        (self.recoveryPhase == BoluoPlayerRecoveryPhaseInterruptionActive) && self.wantsResumeAfterRecovery;
    [self noteDeviceRecoveryStarted:@"scheduleInterruptionRecovery"];
    self.wantsResumeAfterRecovery = YES;
    self.nextRecoveryDelay = isLikelyPhoneInterruption ? 0.5 : 0.3;
    
    __weak BoluoMusicPlayerIOSBridge *weakSelf = self;
    [self enqueueInterruptionRecoveryWithDelay:self.nextRecoveryDelay weakSelf:weakSelf];
}

- (void)cancelPendingInterruptionRecovery {
    if (self.pendingActivationBlock) {
        dispatch_block_cancel(self.pendingActivationBlock);
        self.pendingActivationBlock = nil;
    }
    if (self.pendingRouteDisconnectedRecoveryBlock) {
        dispatch_block_cancel(self.pendingRouteDisconnectedRecoveryBlock);
        self.pendingRouteDisconnectedRecoveryBlock = nil;
    }
    if (self.recoveryPhase == BoluoPlayerRecoveryPhaseRecoveringDevice) {
        [self transitionToRecoveryPhase:BoluoPlayerRecoveryPhaseNone
                                 reason:@"cancelPendingInterruptionRecovery"];
    }
    self.wantsResumeAfterRecovery = NO;
}

- (void)enqueueInterruptionRecoveryWithDelay:(NSTimeInterval)delay weakSelf:(__weak BoluoMusicPlayerIOSBridge *)weakSelf {
    // 统一 epoch：激活延迟窗（0.3~1.0s）内出现更新决策（新 Began 的护栏静音/用户暂停/新 unmute）
    // → 本次恢复激活作废。旧实现仅用户 Pause 会 cancel（FIRE FIX 3），新中断到达时陈旧激活仍会
    // 独占抢会话顶掉对方。isRestoringFromInterruption 一并复位，允许新 owner 重新调度。
    NSInteger activationEpoch = BoluoFocusEpochCurrent();
    dispatch_block_t taskBlock = ^{
        __strong BoluoMusicPlayerIOSBridge *strongSelf = weakSelf;
        if (!strongSelf) { return; }
        
        strongSelf.pendingActivationBlock = nil;
        if (activationEpoch != BoluoFocusEpochCurrent()) {
            BoluoAudioLog(@"[BoluoAudioResume] interruption recovery activation SKIP (superseded epoch=%ld now=%ld)",
                  (long)activationEpoch, (long)BoluoFocusEpochCurrent());
            strongSelf.isRestoringFromInterruption = NO;
            return;
        }
        BOOL useMix = strongSelf.resumeActivationUsesMixWithOthers;
        [strongSelf performSessionActivationWithRetryCount:0 allowMix:useMix completion:^(BOOL success) {
            if (success) {
                [strongSelf restoreExclusiveAudioSessionForResume:YES];
                strongSelf.wantsResumeAfterRecovery = NO;
                [strongSelf noteInterruptionEnded:@"enqueueRecoveryActivated"];
                if (strongSelf.recoveryPhase == BoluoPlayerRecoveryPhaseRecoveringDevice) {
                    [strongSelf transitionToRecoveryPhase:BoluoPlayerRecoveryPhaseNone
                                                   reason:@"enqueueRecoveryActivated"];
                }
                strongSelf.nextRecoveryDelay = 0.3;
                [strongSelf performDeviceResetAndResume];
            } else {
                BoluoAudioLog(@"[BoluoMusicPlayerIOS] Session activation retry exhausted, scheduling another attempt");
                if (strongSelf.recoveryPhase == BoluoPlayerRecoveryPhaseRecoveringDevice) {
                    [strongSelf transitionToRecoveryPhase:BoluoPlayerRecoveryPhaseNone
                                                   reason:@"enqueueRecoveryRetry"];
                }
                strongSelf.nextRecoveryDelay = (strongSelf.nextRecoveryDelay + 0.2 < 1.0 ? strongSelf.nextRecoveryDelay + 0.2 : 1.0);
                if (strongSelf.wantsResumeAfterRecovery) {
                    // 激活失败因对方正在出声/次要静音（独占激活 ABORT）：中断恢复重试只会反复空转/顶人，
                    // 转入竞争音源等待链，对方真正停止后再恢复
                    if ([strongSelf shouldDeferForCompetingOrSecondarySilence]) {
                        BoluoAudioLog(@"[BoluoAudioResume] recovery blocked by competing/secondary silence — switch to competing-audio wait");
                        [strongSelf noteCompetingAudioDetected:@"recoveryBlockedByOtherAudio"];
                        [strongSelf _scheduleWaitForCompetingAudioToStop];
                    } else {
                        [strongSelf scheduleInterruptionRecovery];
                    }
                }
            }
        }];
    };
    
    dispatch_block_t block = dispatch_block_create(DISPATCH_BLOCK_INHERIT_QOS_CLASS, taskBlock);
    
    self.pendingActivationBlock = block;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(delay * NSEC_PER_SEC)), dispatch_get_main_queue(), block);
}

// 公共方法：暂停播放（供定时器等外部调用）
// 参考 setupRemoteCommandsIfNeeded 中 pauseCommand 的处理逻辑
- (void)pausePlaybackWithReason:(NSString *)reason isUserAction:(BOOL)isUserAction {
    if (![NSThread isMainThread]) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self pausePlaybackWithReason:reason isUserAction:isUserAction];
        });
        return;
    }
    
    BoluoAudioLog(@"[BoluoMusicPlayerIOS] pausePlaybackWithReason: %@, isUserAction: %@", reason ?: @"Unknown", isUserAction ? @"YES" : @"NO");
    
    // 关键修改：如果未初始化（未登录），不执行暂停操作
    if (!self.isInitialized) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] pausePlaybackWithReason - Not initialized yet (waiting for login), skipping");
        return;
    }
    
    // 幂等/防抖：如果已经是 paused，不重复触发底层 reset/mute，只补一遍 NowPlayingInfo
    if (self.isMuted) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] pausePlaybackWithReason - Already muted, updating NowPlayingInfo only");
        // 定时器暂停：即使已 muted 也置 userManuallyPaused，并通知 Unity（isMuted 可能陈旧而引擎仍在播）
        if ([reason isEqualToString:@"Timer"]) {
            self.userManuallyPaused = YES;
            [self noteFocusState:BoluoAudioFocusStateUserPaused reason:@"timer_pause_already_muted"];
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] pausePlaybackWithReason - Timer pause (already muted): set userManuallyPaused=YES");
            NSString *gameObjectName = self.unityGameObjectName ?: @"BoluoMusicPlayerIOS";
            UnitySendMessage([gameObjectName UTF8String], "OnNowPlayingPause", "");
        }
        [self updateNowPlayingInfoCenter];
        // 如果是用户操作且应用在后台，确保 session deactivate 生效
        if (isUserAction) {
            [self _deactivateSessionIfBackgroundUserPaused];
        }
        return;
    }
    
    // 调用 setAudioMute:YES isUserAction:isUserAction（与系统播放器暂停逻辑一致）
    [self setAudioMute:YES isUserAction:isUserAction];
    
    // 定时器暂停：置 userManuallyPaused，禁止系统播放器自动恢复
    BOOL isTimerPause = [reason isEqualToString:@"Timer"];
    if (isTimerPause) {
        self.userManuallyPaused = YES;
        [self noteFocusState:BoluoAudioFocusStateUserPaused reason:@"timer_pause"];
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] pausePlaybackWithReason - Timer pause: set userManuallyPaused=YES to prevent auto-resume");
    }
    
    // 发送 Unity 消息通知（与系统播放器暂停逻辑一致）
    NSString *gameObjectName = self.unityGameObjectName ?: @"BoluoMusicPlayerIOS";
    UnitySendMessage([gameObjectName UTF8String], "OnNowPlayingPause", "");
    
    // OnTimerFinished 仅由 BoluoTimerIOS.onTimerFinished 发送一次，避免与此处双发反复 Pause UI
    BoluoAudioLog(@"[BoluoMusicPlayerIOS] pausePlaybackWithReason - Paused playback and sent OnNowPlayingPause message (userManuallyPaused=%@)", 
          self.userManuallyPaused ? @"YES" : @"NO");
}

@end

static void BoluoLogAudioResumeSnapshot(NSString *tag) {
    [[BoluoMusicPlayerIOSBridge sharedInstance] logAudioResumeSnapshot:tag];
}

/// ≥100ms 才抛通知；硬/软阈值与汇总在壳侧 BoluoAudioJankBLSObserver 完成。
static void BoluoNativeAudioJank_Report(NSString *phase, CFTimeInterval durationMs, NSString *detail) {
    if (durationMs < 100.0 || durationMs > 5000.0 || phase.length == 0)
        return;
    NSString *phaseCopy = [phase copy];
    NSString *detailCopy = detail.length > 0 ? [detail copy] : @"";
    NSDictionary *info = @{
        @"phase": phaseCopy,
        @"durationMs": @(durationMs),
        @"detail": detailCopy,
    };
    // 不在音频工作队列做网络；抛主线程通知给壳进程（限流在 Observer）。
    dispatch_async(dispatch_get_main_queue(), ^{
        [[NSNotificationCenter defaultCenter] postNotificationName:@"BoluoAudioJankBLSLogNotification"
                                                            object:nil
                                                          userInfo:info];
    });
}

/// 供同 Framework 内 WanosMusicPlayer.mm 等调用。
extern "C" void BoluoNativeAudioJank_ReportPhase(const char *phase, double durationMs, const char *detail) {
    NSString *phaseStr = phase ? [NSString stringWithUTF8String:phase] : @"?";
    NSString *detailStr = detail ? [NSString stringWithUTF8String:detail] : @"";
    BoluoNativeAudioJank_Report(phaseStr, durationMs, detailStr);
}

extern "C" {

void BoluoMusicPlayerIOS_Init(int category,
                              unsigned int options,
                              bool autoActivate,
                              bool notifyOthersOnDeactivate,
                              const char *unityGameObjectName) {
    BoluoMusicPlayerIOSBridge *player = [BoluoMusicPlayerIOSBridge sharedInstance];
    if (category != 4) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] Init category=%d normalized to Playback(4)", category);
        category = 4;
    }
    player.sessionCategory = category;
    player.sessionOptions = options;
    player.autoActivateSession = autoActivate ? YES : NO;
    player.notifyOthersOnDeactivate = notifyOthersOnDeactivate ? YES : NO;
    if (unityGameObjectName != NULL) {
        player.unityGameObjectName = [NSString stringWithUTF8String:unityGameObjectName];
    }
    [player configureAudioSession];
}

void BoluoMusicPlayerIOS_RestoreExclusiveMode() {
    [[BoluoMusicPlayerIOSBridge sharedInstance] restoreExclusiveAudioSession];
}

void BoluoMusicPlayerIOS_SetNowPlayingInfo(const char* title,
                                           const char* artist,
                                           const char* album,
                                           const char* artworkPath) {
    BoluoMusicPlayerIOSBridge *player = [BoluoMusicPlayerIOSBridge sharedInstance];
    NSString *titleStr = title ? [NSString stringWithUTF8String:title] : @"";
    NSString *artistStr = artist ? [NSString stringWithUTF8String:artist] : @"";
    NSString *albumStr = album ? [NSString stringWithUTF8String:album] : @"";
    NSString *artworkStr = artworkPath ? [NSString stringWithUTF8String:artworkPath] : nil;
    [player applyNowPlayingInfoWithTitle:titleStr artist:artistStr album:albumStr artwork:artworkStr];
}

// 不传参：使用原生层缓存的 nowPlayingTitle/Artist/Album/ArtworkPath 重新刷新控制中心显示
void BoluoMusicPlayerIOS_RefreshNowPlayingInfo() {
    BoluoMusicPlayerIOSBridge *player = [BoluoMusicPlayerIOSBridge sharedInstance];
    [player setupRemoteCommandsIfNeeded];
    [player updateNowPlayingInfoCenter];
}

bool BoluoMusicPlayerIOS_IsUserPaused() {
    return [[BoluoMusicPlayerIOSBridge sharedInstance] isUserPaused];
}

/// 原生桥是否处于静音（含系统中断 mute）；供 Unity 回前台对齐岛屿暂停 UI。
bool BoluoMusicPlayerIOS_IsNativeMuted(void) {
    return [BoluoMusicPlayerIOSBridge sharedInstance].isMuted ? true : false;
}

void BoluoMusicPlayerIOS_SyncExternalPlaybackState(bool isPlaying) {
    [[BoluoMusicPlayerIOSBridge sharedInstance] syncExternalPlaybackState:isPlaying ? YES : NO];
}

void BoluoMusicPlayerIOS_ApplyUserPauseFromExternal(void) {
    BoluoMusicPlayerIOSBridge *player = [BoluoMusicPlayerIOSBridge sharedInstance];
    // 退登后可能已 SetInitialized(false)；登录页禁声靠会话 Disarmed，不写 userManuallyPaused。
    if (!player.isInitialized) {
        player.isMuted = YES;
        player.isPlaying = NO;
        [player cancelPendingInterruptionRecovery];
        player.wantsResumeAfterRecovery = NO;
        [player noteCompetingAudioCleared:@"ApplyUserPauseFromExternal"];
        BoluoSetMuteWithMusicEventsNative(true);
        return;
    }
    [player setAudioMute:YES isUserAction:YES];
}

/// 退登停播 epoch：Arm / 新一轮 MarkStopped 递增后，作废进行中的 Reset 收尾（禁止再钉 mute）。
static uint64_t sLogoutStopEpoch = 0;

// 根因约定（勿再把退登 Reset 绑到 playbackDeviceRecoveryInFlight）：
// - 登录页禁声：只靠 IsLogoutSilenced（会话 Disarm）
// - inFlight：只服务活会话的 route/BT/Rebuild，供 Unmute DEFER，避免与 MiniAudio 争锁
// - 退登 MarkStopped：异步 Reset 可与再进岛重叠；Arm 用 epoch 作废收尾 + drain work queue 等 Reset 真正结束
// 曾经的竞态：MarkStopped 置 inFlight → Arm ++epoch 作废回调 → stale 不降旗 → 首播 Unmute 永久被吞

/// 退登硬停：Mute + Reset 设备 + 取消自动恢复。
/// 不写 userManuallyPaused（那是真实用户暂停）；登录页禁声靠 IsLogoutSilenced。
/// 故意不置 playbackDeviceRecoveryInFlight。
void BoluoMusicPlayerIOS_MarkStoppedForLogout(void) {
    BoluoMusicPlayerIOSBridge *player = [BoluoMusicPlayerIOSBridge sharedInstance];
    const uint64_t stopEpoch = ++sLogoutStopEpoch;
    BoluoMusicPlayer_Trail(@"MarkStoppedForLogout ENTER epoch=%llu",
                           (unsigned long long)stopEpoch);
    void (^applyFlags)(void) = ^{
        // 退登清空用户暂停意图，避免再登录被假暂停挡住首播。
        player.userManuallyPaused = NO;
        player.isMuted = YES;
        player.isPlaying = NO;
        [player noteSessionStopped:@"MarkStoppedForLogout"];
        [player cancelPendingInterruptionRecovery];
        [NSObject cancelPreviousPerformRequestsWithTarget:player
                                                 selector:@selector(_pollResumeWhenDeviceIdleAfterSystemMute)
                                                   object:nil];
        player.deviceIdleResumePollCount = 0;
        [player removeRemoteCommands];
        BoluoSetMuteWithMusicEventsNative(true);
        SetMute(true);
    };
    if ([NSThread isMainThread]) {
        applyFlags();
    } else {
        dispatch_sync(dispatch_get_main_queue(), applyFlags);
    }
    // Reset 上 work queue；不占 inFlight。
    // 关键：Arm 已 ++epoch 时必须整段跳过 Reset——若仍执行 ResetPlaybackDevice，会拆掉
    // 再进岛 CreateEngine/Rebuild 后的设备；随后 FirstPlay 钉 isMuted=NO，安全网因 muted=0
    // 跳过 → 退登重登（含游客）永久无声。
    dispatch_async(BoluoAudioWorkQueue(), ^{
        if (stopEpoch != sLogoutStopEpoch) {
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] MarkStoppedForLogout stale epoch=%llu current=%llu — skip Reset",
                  (unsigned long long)stopEpoch, (unsigned long long)sLogoutStopEpoch);
            BoluoMusicPlayer_Trail(@"MarkStopped Reset SKIP stale epoch=%llu cur=%llu",
                                   (unsigned long long)stopEpoch,
                                   (unsigned long long)sLogoutStopEpoch);
            return;
        }
        Result r = ResetPlaybackDevice();
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] MarkStoppedForLogout ResetPlaybackDevice result=%d epoch=%llu",
              r, (unsigned long long)stopEpoch);
        dispatch_async(dispatch_get_main_queue(), ^{
            if (stopEpoch != sLogoutStopEpoch) {
                BoluoAudioLog(@"[BoluoMusicPlayerIOS] MarkStoppedForLogout stale epoch=%llu current=%llu — skip mute re-nail",
                      (unsigned long long)stopEpoch, (unsigned long long)sLogoutStopEpoch);
                BoluoMusicPlayer_Trail(@"MarkStopped Reset STALE epoch=%llu cur=%llu",
                                       (unsigned long long)stopEpoch,
                                       (unsigned long long)sLogoutStopEpoch);
                return;
            }
            // 仅会话仍 Disarmed 时重钉 mute（不开闸后写回）。
            if (BoluoAudioSession_IsLogoutSilenced() != 0) {
                player.userManuallyPaused = NO;
                player.isMuted = YES;
                player.isPlaying = NO;
                player.wantsResumeAfterRecovery = NO;
                BoluoMusicPlayer_Trail(@"MarkStopped Reset DONE re-nail mute silenced=1 epoch=%llu",
                                       (unsigned long long)stopEpoch);
            } else {
                BoluoMusicPlayer_Trail(@"MarkStopped Reset DONE skip re-nail silenced=0 epoch=%llu",
                                       (unsigned long long)stopEpoch);
            }
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] MarkStoppedForLogout done silenced=%d",
                  (int)BoluoAudioSession_IsLogoutSilenced());
        });
    });
}

/// 会话 Arm：作废退登 Reset 收尾，清空暂停/中断意图。不 unmute、不 SyncPlaying（避免闪声）。
/// 不阻塞 Unity 主线程：work-queue 屏障异步完成，由 IsSessionArmWorkQueueDrained 轮询。
static volatile int32_t sArmWorkQueueDrainPending = 0;

void BoluoMusicPlayerIOS_ResetPlaybackIntentForSessionArm(void) {
    ++sLogoutStopEpoch;
    BoluoMusicPlayerIOSBridge *player = [BoluoMusicPlayerIOSBridge sharedInstance];
    void (^resetFlags)(void) = ^{
        player.userManuallyPaused = NO;
        [player noteSessionArming:@"ResetPlaybackIntentForSessionArm"];
        [player cancelPendingInterruptionRecovery];
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] ResetPlaybackIntentForSessionArm epoch=%llu phase=%@",
              (unsigned long long)sLogoutStopEpoch,
              BoluoPlayerSessionPhaseName(player.sessionPhase));
    };
    if ([NSThread isMainThread]) {
        resetFlags();
    } else {
        dispatch_sync(dispatch_get_main_queue(), resetFlags);
    }
    // 异步屏障：排在此前 MarkStopped Reset 之后；不 dispatch_sync，避免堵 Unity 主线程/Poll。
    // PR1：sArmWorkQueueDrainPending 与 sessionPhase==Arming 双写。
    sArmWorkQueueDrainPending = 1;
    dispatch_async(BoluoAudioWorkQueue(), ^{
        sArmWorkQueueDrainPending = 0;
        dispatch_async(dispatch_get_main_queue(), ^{
            [[BoluoMusicPlayerIOSBridge sharedInstance] noteSessionArmDrainComplete:@"armWorkQueueDrain"];
        });
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] SessionArm work-queue drain complete epoch=%llu",
              (unsigned long long)sLogoutStopEpoch);
    });
}

int32_t BoluoMusicPlayerIOS_IsSessionArmWorkQueueDrained(void) {
    BoluoMusicPlayerIOSBridge *player = [BoluoMusicPlayerIOSBridge sharedInstance];
    // phase!=Arming 且遗留 drain 旗为 0
    if (player.sessionPhase == BoluoPlayerSessionPhaseArming || sArmWorkQueueDrainPending != 0) {
        return 0;
    }
    return 1;
}

/// 统一 unmute 入口：FirstPlay 强制解 mute；Foreground/External 走竞音可推迟路径。
void BoluoMusicPlayerIOS_RequestUnmute(int32_t reason) {
    BoluoMusicPlayer_Trail(@"RequestUnmute ENTER reason=%d silenced=%d",
                           (int)reason,
                           (int)BoluoAudioSession_IsLogoutSilenced());
    if (BoluoAudioSession_IsLogoutSilenced() != 0) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] RequestUnmute(%d) BLOCKED logoutSilenced", (int)reason);
        BoluoMusicPlayer_Trail(@"RequestUnmute BLOCKED logoutSilenced reason=%d", (int)reason);
        return;
    }
    BoluoMusicPlayerIOSBridge *player = [BoluoMusicPlayerIOSBridge sharedInstance];
    void (^apply)(void) = ^{
        if (reason == (int32_t)BoluoUnmuteReasonFirstPlay) {
            // 与 C# ShouldDefer 对齐：Rebuild/inFlight 时不强制 unmute，改 MarkAwaiting，
            // 由 resumeIfStillMutedAfterDeviceWork 在设备队列结束后补首播。
            if (player.playbackDeviceRecoveryInFlight) {
                BoluoAudioLog(@"[BoluoMusicPlayerIOS] RequestUnmute(FirstPlay) DEFER inFlight → MarkAwaiting");
                BoluoMusicPlayer_Trail(@"RequestUnmute FirstPlay DEFER inFlight → MarkAwaiting");
                [player noteFirstPlayRequested:@"RequestUnmute-FirstPlay-inFlight"];
                return;
            }
            BoluoMusicPlayer_Trail(@"RequestUnmute → applyFirstPlayUnmuteForced");
            [player applyFirstPlayUnmuteForced];
            return;
        }
        NSString *tag = (reason == (int32_t)BoluoUnmuteReasonForegroundResume)
            ? @"RequestUnmute-Foreground"
            : @"RequestUnmute-External";
        BoluoUnmuteVerdict v = [player unmuteVerdictForReason:tag];
        if (v == BoluoUnmuteVerdictDeferDeviceBusy || v == BoluoUnmuteVerdictDeferRecovery) {
            BoluoAudioLog(@"[BoluoMusicPlayerIOS] RequestUnmute DEFER (%@) verdict=%@",
                  tag, BoluoUnmuteVerdictName(v));
            BoluoMusicPlayer_Trail(@"RequestUnmute DEFER %@ verdict=%@", tag, BoluoUnmuteVerdictName(v));
            return;
        }
        if (v == BoluoUnmuteVerdictBlockedUserPaused || v == BoluoUnmuteVerdictBlockedDisarmed) {
            BoluoMusicPlayer_Trail(@"RequestUnmute BLOCKED %@ verdict=%@", tag, BoluoUnmuteVerdictName(v));
            return;
        }
        BOOL ok = [player unmuteEngineIfExclusiveAvailableWithReason:tag];
        BoluoMusicPlayer_Trail(@"RequestUnmute exclusive ok=%d (%@)", (int)ok, tag);
    };
    if ([NSThread isMainThread]) {
        apply();
    } else {
        dispatch_sync(dispatch_get_main_queue(), apply);
    }
}

/// 进岛首播：强制解 mute。不走 isOtherAudioPlaying 推迟路径（那条会把 isMuted 钉回 YES）。
void BoluoMusicPlayerIOS_ApplyFirstPlayUnmute(void) {
    BoluoMusicPlayerIOS_RequestUnmute((int32_t)BoluoUnmuteReasonFirstPlay);
}

/// Unity 直接 SetMute(false) 不会走 RequestUnmute，桥接 isMuted 会停在初值 YES。
/// 中断处理只认 isPlaying / !isMuted，不更新的话切到抖音 / QQ 音乐不会真静音。
void BoluoMusicPlayerIOS_EnsureObservers(void) {
    (void)[BoluoMusicPlayerIOSBridge sharedInstance];
}

void BoluoMusicPlayerIOS_NoteUnityAudible(void) {
    if (BoluoAudioSession_IsLogoutSilenced() != 0) {
        return;
    }
    if (BoluoAudioSession_AllowUnmuteNow() == 0) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] NoteUnityAudible skip — foreground app owns audio");
        return;
    }
    BoluoMusicPlayerIOSBridge *player = [BoluoMusicPlayerIOSBridge sharedInstance];
    void (^note)(void) = ^{
        if (player.userManuallyPaused) {
            return;
        }
        player.isMuted = NO;
        player.isPlaying = YES;
        BoluoAudioSession_NoteStartupAudibleCommitted();
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] NoteUnityAudible isMuted=NO isPlaying=YES");
    };
    if ([NSThread isMainThread]) {
        note();
    } else {
        dispatch_sync(dispatch_get_main_queue(), note);
    }
}

void BoluoMusicPlayerIOS_NoteUnitySilent(void) {
    BoluoMusicPlayerIOSBridge *player = [BoluoMusicPlayerIOSBridge sharedInstance];
    void (^note)(void) = ^{
        if (player.userManuallyPaused) {
            return;
        }
        player.isMuted = YES;
        player.isPlaying = NO;
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] NoteUnitySilent isMuted=YES isPlaying=NO");
    };
    if ([NSThread isMainThread]) {
        note();
    } else {
        dispatch_sync(dispatch_get_main_queue(), note);
    }
}

/// C# 首播重试循环开始前调用：标记「本会话还欠一次首播 unmute」。
/// Rebuild 若跑超 C# 侧 3s 重试上限，Unity 放弃后仍靠此旗，由设备重建完成回调
/// resumeIfStillMutedAfterDeviceWork 兜底补 unmute，避免永久哑。
/// 由 ApplyFirstPlayUnmute 成功 / 下一轮 MarkStoppedForLogout 清除。
void BoluoMusicPlayerIOS_MarkAwaitingFirstPlayUnmute(void) {
    BoluoMusicPlayerIOSBridge *player = [BoluoMusicPlayerIOSBridge sharedInstance];
    void (^mark)(void) = ^{
        [player noteFirstPlayRequested:@"MarkAwaitingFirstPlayUnmute"];
    };
    if ([NSThread isMainThread]) {
        mark();
    } else {
        dispatch_async(dispatch_get_main_queue(), mark);
    }
}

void BoluoMusicPlayerIOS_ApplyUserResumeFromExternal(void) {
    if (BoluoAudioSession_IsLogoutSilenced() != 0) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] ApplyUserResumeFromExternal BLOCKED logoutSilenced");
        return;
    }
    BoluoMusicPlayerIOSBridge *player = [BoluoMusicPlayerIOSBridge sharedInstance];
    if (!player.isInitialized) return;
    // HUD Restore / 负一屏恢复：若 DelayPlayMusic 已 FirstPlay 解 mute，禁止再走 setAudioMute:NO 的
    // Reset+SetPlaybackDevice —— 会把刚拉起的音乐/环境声实例拆光，表现为退登重登后「unmute OK 但无声」。
    if (!player.isMuted && !player.userManuallyPaused) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] ApplyUserResumeFromExternal SKIP alreadyAudible (no Reset)");
        BoluoMusicPlayer_Trail(@"ApplyUserResume SKIP alreadyAudible phase=%@",
                               BoluoPlayerSessionPhaseName(player.sessionPhase));
        return;
    }
    [player setAudioMute:NO isUserAction:YES];
}

/// 供 BoluoAudioSession RestorePlayback：岛内已 Engaged 且可听时跳过破坏性 Reset。
int32_t BoluoMusicPlayerIOS_IsIslandAudible(void) {
    BoluoMusicPlayerIOSBridge *player = [BoluoMusicPlayerIOSBridge sharedInstance];
    if (player.sessionPhase != BoluoPlayerSessionPhaseEngaged) {
        return 0;
    }
    if (player.isMuted || player.userManuallyPaused) {
        return 0;
    }
    return 1;
}

/// 上一曲/下一曲时若暂停则恢复：走完整 setAudioMute 流程（激活 session、重置设备、取消静音）
void BoluoMusicPlayerIOS_ResumeForTrackChange(void) {
    if (BoluoAudioSession_IsLogoutSilenced() != 0) {
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] ResumeForTrackChange BLOCKED logoutSilenced");
        return;
    }
    BoluoMusicPlayerIOSBridge *player = [BoluoMusicPlayerIOSBridge sharedInstance];
    if (!player.isInitialized || !player.isMuted) return;
    [player setAudioMute:NO isUserAction:YES];
    // 与 _flushPendingRemoteMute 对齐：恢复播放后通知 Unity 收起岛内暂停 UI
    NSString *go = player.unityGameObjectName ?: @"BoluoMusicPlayerIOS";
    UnitySendMessage([go UTF8String], "OnNowPlayingPlay", "");
}

void BoluoMusicPlayerIOS_RemoveRemoteCommands() {
    [[BoluoMusicPlayerIOSBridge sharedInstance] removeRemoteCommands];
}

void BoluoMusicPlayerIOS_SetInitialized(bool initialized) {
    BoluoMusicPlayerIOSBridge *player = [BoluoMusicPlayerIOSBridge sharedInstance];
    player.isInitialized = initialized ? YES : NO;
    BoluoAudioLog(@"[BoluoMusicPlayerIOS] SetInitialized: %@", initialized ? @"YES" : @"NO");
    if (!initialized) {
        [player stopForceBluetoothRdStutterRepro];
    }
}

/// QA：enabled!=0 开启强制蓝牙 RD 硬恢复抖动；intervalSeconds<=0 用默认 3s。
void BoluoMusicPlayerIOS_ForceBluetoothRdStutterRepro(int enabled, double intervalSeconds) {
    BoluoMusicPlayerIOSBridge *player = [BoluoMusicPlayerIOSBridge sharedInstance];
    if (enabled) {
        [player startForceBluetoothRdStutterReproWithInterval:intervalSeconds];
    } else {
        [player stopForceBluetoothRdStutterRepro];
    }
}

void BoluoMusicPlayerIOS_LogAudioResumeSnapshot(const char *tag) {
    NSString *tagStr = tag ? [NSString stringWithUTF8String:tag] : @"?";
    [[BoluoMusicPlayerIOSBridge sharedInstance] logAudioResumeSnapshot:tagStr];
}

/// C#（Unity 主线程）侧的设备重建同样走串行音频队列：
/// 1) 不阻塞 Unity 渲染（直调 WIAEngine.Reset/SetPlaybackDevice 会在蓝牙路由切换时卡主线程秒级）；
/// 2) 与原生中断/路由恢复链路的设备操作严格串行，杜绝两条路径并发重建设备。
void BoluoAudio_ResetPlaybackDeviceAsync(void) {
    BoluoMusicPlayerIOSBridge *bridge = [BoluoMusicPlayerIOSBridge sharedInstance];
    bridge.playbackDeviceRecoveryInFlight = YES;
    dispatch_async(BoluoAudioWorkQueue(), ^{
        CFTimeInterval t0 = CACurrentMediaTime();
        Result r = ResetPlaybackDevice();
        BoluoNativeAudioJank_Report(@"native_reset_playback_device",
                                   (CACurrentMediaTime() - t0) * 1000.0,
                                   [NSString stringWithFormat:@"result=%d", r]);
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] BoluoAudio_ResetPlaybackDeviceAsync result: %d", r);
        dispatch_async(dispatch_get_main_queue(), ^{
            bridge.playbackDeviceRecoveryInFlight = NO;
            [bridge resumeIfStillMutedAfterDeviceWork:@"ResetPlaybackDeviceAsync-done"];
        });
    });
}

void BoluoAudio_SetPlaybackDeviceAsync(const char *playbackInfoJson) {
    BoluoMusicPlayerIOSBridge *bridge = [BoluoMusicPlayerIOSBridge sharedInstance];
    bridge.playbackDeviceRecoveryInFlight = YES;
    // char* 归 il2cpp 所有，返回后即失效，必须先拷贝再入队
    NSString *json = playbackInfoJson != NULL ? [NSString stringWithUTF8String:playbackInfoJson] : nil;
    dispatch_async(BoluoAudioWorkQueue(), ^{
        CFTimeInterval t0 = CACurrentMediaTime();
        const char *utf8 = json.length > 0 ? json.UTF8String : kBoluoDefaultDeviceJson;
        Result r = SetPlaybackDevice(utf8);
        BoluoNativeAudioJank_Report(@"native_set_playback_device",
                                   (CACurrentMediaTime() - t0) * 1000.0,
                                   [NSString stringWithFormat:@"result=%d", r]);
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] BoluoAudio_SetPlaybackDeviceAsync result: %d", r);
        dispatch_async(dispatch_get_main_queue(), ^{
            bridge.playbackDeviceRecoveryInFlight = NO;
            [bridge resumeIfStillMutedAfterDeviceWork:@"SetPlaybackDeviceAsync-done"];
        });
    });
}

void BoluoAudio_RebuildPlaybackDeviceAsync(const char *playbackInfoJson) {
    BoluoMusicPlayerIOSBridge *bridge = [BoluoMusicPlayerIOSBridge sharedInstance];
    bridge.playbackDeviceRecoveryInFlight = YES;
    NSString *json = playbackInfoJson != NULL ? [NSString stringWithUTF8String:playbackInfoJson] : nil;
    dispatch_async(BoluoAudioWorkQueue(), ^{
        CFTimeInterval t0 = CACurrentMediaTime();
        Result resetResult = ResetPlaybackDevice();
        const char *utf8 = json.length > 0 ? json.UTF8String : kBoluoDefaultDeviceJson;
        Result setResult = SetPlaybackDevice(utf8);
        BoluoNativeAudioJank_Report(@"native_rebuild_playback_device",
                                   (CACurrentMediaTime() - t0) * 1000.0,
                                   [NSString stringWithFormat:@"reset=%d;set=%d", resetResult, setResult]);
        BoluoAudioLog(@"[BoluoMusicPlayerIOS] BoluoAudio_RebuildPlaybackDeviceAsync Reset=%d Set=%d", resetResult, setResult);
        dispatch_async(dispatch_get_main_queue(), ^{
            bridge.playbackDeviceRecoveryInFlight = NO;
            [bridge resumeIfStillMutedAfterDeviceWork:@"RebuildPlaybackDeviceAsync-done"];
        });
    });
}

int BoluoMusicPlayerIOS_ShouldDeferUnityForegroundResume(void) {
    return [[BoluoMusicPlayerIOSBridge sharedInstance] shouldDeferUnityForegroundResume] ? 1 : 0;
}

/// 设置页「焦点变更通知」→ Unity BoluoAudioFocusNotify（亦可由 Notification BoluoAudioFocusNotifyChanged 触发）
void BoluoMusicPlayerIOS_SetUserAudioSettingsFocusNotify(int enabled) {
    NSString *payload = [NSString stringWithFormat:@"focusNotify=%d", enabled ? 1 : 0];
    NSString *go = [BoluoMusicPlayerIOSBridge sharedInstance].unityGameObjectName ?: @"BoluoMusicPlayerIOS";
    UnitySendMessage([go UTF8String], "SetUserAudioSettings", [payload UTF8String]);
}

static int BoluoAudio_IsPlaybackDeviceRecoveryInFlight(void) {
    return [BoluoMusicPlayerIOSBridge sharedInstance].playbackDeviceRecoveryInFlight ? 1 : 0;
}

}
