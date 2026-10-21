//
//  BoluoAudioSession.mm
//  薄平台 AVAudioSession 能力（原 BoluoHybridSchemeBridge 音频段）。
//  不含 GamePlay / il2cpp 业务。
//  P3：会话策略 / 退登静默门闩 / 恢复编排单一真相源（宿主只报状态/发意图）。
//

#import "BoluoAudioSession.h"
#import <AVFoundation/AVFoundation.h>
#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <objc/runtime.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>

extern "C" {
void SetMute(bool mute);
int GetPlaybackState(void);
void BoluoSetMuteWithMusicEventsNative(bool mute);
void BoluoMusicPlayerIOS_SyncExternalPlaybackState(bool isPlaying);
void BoluoMusicPlayerIOS_ApplyUserResumeFromExternal(void);
void BoluoMusicPlayerIOS_MarkStoppedForLogout(void);
void BoluoMusicPlayerIOS_ResetPlaybackIntentForSessionArm(void);
/// Engaged 且未 mute / 未用户暂停：HUD Restore 不得再 Reset。
int32_t BoluoMusicPlayerIOS_IsIslandAudible(void);
void BoluoAudio_RebuildPlaybackDeviceAsync(const char *playbackInfoJson);
}

static char sBoluoAudioDiag[2048] = "audio: not run yet";
static char sBoluoSetCatDiag[512] = "setCat: none";

/// 统一过滤字段：`[BoluoAudioDbg]`（console + Documents/boluo-audio-trail.log）。
/// console 进后台会断；退登→重登靠 trail 文件事后拉取。
static void BoluoAudioSession_Trail(NSString *fmt, ...) {
    va_list args;
    va_start(args, fmt);
    NSString *msg = [[NSString alloc] initWithFormat:fmt arguments:args];
    va_end(args);
    NSLog(@"[BoluoAudioDbg] %@", msg);
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

void BoluoAudioSession_AppendTrail(const char *msg) {
    if (msg == NULL || msg[0] == '\0') {
        return;
    }
    BoluoAudioSession_Trail(@"%s", msg);
}

static void BoluoAudioSession_UpdateDiag(const char *fmt, ...) {
    va_list args;
    va_start(args, fmt);
    vsnprintf(sBoluoAudioDiag, sizeof(sBoluoAudioDiag), fmt, args);
    va_end(args);
    BoluoAudioSession_Trail(@"DIAG %s", sBoluoAudioDiag);
}

static BOOL BoluoAudioSession_LockPlaybackNative(void) {
    AVAudioSession *session = [AVAudioSession sharedInstance];
    NSError *error = nil;
    if (![session setCategory:AVAudioSessionCategoryPlayback withOptions:0 error:&error]) {
        NSLog(@"[BoluoAudioSession] setCategory failed: %@", error);
        return NO;
    }
    if (![session setActive:YES error:&error]) {
        NSLog(@"[BoluoAudioSession] setActive failed: %@", error);
        return NO;
    }
    return YES;
}

typedef BOOL (*BoluoSetCategoryFn)(id, SEL, NSString *, AVAudioSessionCategoryOptions, NSError **);
static BoluoSetCategoryFn sBoluoOrigSetCategory = NULL;

static BOOL BoluoAudioSession_SetCategoryInterposer(id self, SEL _cmd, NSString *category,
                                                    AVAudioSessionCategoryOptions options,
                                                    NSError *__autoreleasing *outError) {
    NSError *err = nil;
    BOOL ok = sBoluoOrigSetCategory(self, _cmd, category, options, &err);
    if (ok) {
        if (outError) {
            *outError = nil;
        }
        return YES;
    }

    long firstCode = (long)err.code;
    AVAudioSession *cur = (AVAudioSession *)self;
    if ([cur.category isEqualToString:AVAudioSessionCategoryPlayback]) {
        snprintf(sBoluoSetCatDiag, sizeof(sBoluoSetCatDiag),
                 "setCat %s opts=0x%lx err=%ld | suppressed(keep Playback)",
                 category.UTF8String, (unsigned long)options, firstCode);
        NSLog(@"[BoluoAudioSession] %s", sBoluoSetCatDiag);
        if (outError) {
            *outError = nil;
        }
        return YES;
    }

    AVAudioSessionCategoryOptions sanitized = options &
        ~(AVAudioSessionCategoryOptionDefaultToSpeaker |
          AVAudioSessionCategoryOptionAllowBluetooth |
          AVAudioSessionCategoryOptionAllowBluetoothA2DP |
          AVAudioSessionCategoryOptionAllowAirPlay);
    err = nil;
    BOOL retryOk = sBoluoOrigSetCategory(self, _cmd, category, sanitized, &err);
    long retryCode = (long)err.code;
    BOOL forceOk = retryOk;
    if (!retryOk) {
        err = nil;
        forceOk = sBoluoOrigSetCategory(self, _cmd, AVAudioSessionCategoryPlayback, 0, &err);
    }

    snprintf(sBoluoSetCatDiag, sizeof(sBoluoSetCatDiag),
             "setCat %s opts=0x%lx err=%ld | strip=0x%lx %s(%ld) | forcePB=%s",
             category.UTF8String, (unsigned long)options, firstCode,
             (unsigned long)sanitized, retryOk ? "Y" : "N", retryCode,
             forceOk ? "Y" : "N");
    NSLog(@"[BoluoAudioSession] %s", sBoluoSetCatDiag);

    if (outError) {
        *outError = nil;
    }
    return forceOk;
}

static void BoluoAudioSession_InstallSetCategoryInterposer(void) {
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        Method m = class_getInstanceMethod(AVAudioSession.class,
                                           @selector(setCategory:withOptions:error:));
        if (m != NULL) {
            sBoluoOrigSetCategory = (BoluoSetCategoryFn)method_getImplementation(m);
            method_setImplementation(m, (IMP)BoluoAudioSession_SetCategoryInterposer);
            NSLog(@"[BoluoAudioSession] setCategory interposer installed");
        }
    });
}

/// 首次进岛去重；退登 / 非 Playback 时必须清掉，否则重进 8s 内 Restore 被吞 → 无声。
static BOOL sBoluoRestoreGate = NO;
/// 退登 setActive(false) 后 Wanos 设备可能仍绑在旧会话；下次 Restore 强制 Rebuild。
static BOOL sNeedsDeviceRebuildAfterLogout = NO;
/// 宿主 UIScene 前台态（与 key window 正交）。默认 0：后台拉起不自动播。
static int32_t sHostForeground = 0;
/// 会话播放总闸。1=Disarmed（登录页禁声）；0=Armed（岛内可播）。
/// 语义：SilenceForLogout=Disarm；ArmSessionForIslandPlayback=Arm。与 userManuallyPaused 正交。
static int32_t sLogoutSilenced = 0;
/// Disarm 世代：作废已排队的 RestoreAfterHudReady，避免退登后 0.3s 回调把声拉回。
static uint64_t sSilenceGeneration = 0;
/// maintainOutput 同款防抖：连续 2 tick 非 Playback 才纠偏。
static int sDriftTicks = 0;

static void BoluoAudioSession_OnMediaServicesReset(NSNotification *note) {
    (void)note;
    if (sLogoutSilenced != 0) {
        return;
    }
    BoluoAudioSession_LockPlaybackNative();
    NSLog(@"[BoluoAudioSession] mediaServicesWereReset → LockPlayback");
}

static void BoluoAudioSession_InstallMediaServicesObserver(void) {
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        [[NSNotificationCenter defaultCenter]
            addObserverForName:AVAudioSessionMediaServicesWereResetNotification
                        object:nil
                         queue:[NSOperationQueue mainQueue]
                    usingBlock:^(NSNotification *note) {
                        BoluoAudioSession_OnMediaServicesReset(note);
                    }];
        NSLog(@"[BoluoAudioSession] mediaServicesWereReset observer installed");
    });
}

/// 替代 Swift maintainOutput：前台 + 非静默时防抖锁回 Playback。
static void BoluoAudioSession_DriftTick(void) {
    if (sHostForeground == 0 || sLogoutSilenced != 0) {
        sDriftTicks = 0;
        return;
    }
    AVAudioSession *session = [AVAudioSession sharedInstance];
    if ([session.category isEqualToString:AVAudioSessionCategoryPlayback]) {
        sDriftTicks = 0;
        return;
    }
    sDriftTicks += 1;
    if (sDriftTicks < 2) {
        return;
    }
    sDriftTicks = 0;
    BoluoAudioSession_LockPlaybackNative();
    NSLog(@"[BoluoAudioSession] driftCorrect → LockPlayback");
}

static void BoluoAudioSession_InstallDriftCorrector(void) {
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        dispatch_source_t timer = dispatch_source_create(
            DISPATCH_SOURCE_TYPE_TIMER, 0, 0, dispatch_get_main_queue());
        dispatch_source_set_timer(
            timer,
            dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.25 * NSEC_PER_SEC)),
            (uint64_t)(0.25 * NSEC_PER_SEC),
            (uint64_t)(0.05 * NSEC_PER_SEC));
        dispatch_source_set_event_handler(timer, ^{
            BoluoAudioSession_DriftTick();
        });
        dispatch_resume(timer);
        // 持有 timer，避免被释放；进程生命周期内常驻。
        static dispatch_source_t sDriftTimer = nil;
        sDriftTimer = timer;
        NSLog(@"[BoluoAudioSession] drift corrector installed (0.25s)");
    });
}

/// 引擎 init 前保证 Playback 类别（替代 Swift 宿主早启）；后台拉起不 setActive，避免抢焦点出声。
static void BoluoAudioSession_BootstrapPlaybackCategory(void) {
    AVAudioSession *session = [AVAudioSession sharedInstance];
    NSError *error = nil;
    if (![session setCategory:AVAudioSessionCategoryPlayback withOptions:0 error:&error]) {
        NSLog(@"[BoluoAudioSession] bootstrap setCategory failed: %@", error);
        return;
    }
    UIApplicationState state = [UIApplication sharedApplication].applicationState;
    if (state == UIApplicationStateBackground) {
        NSLog(@"[BoluoAudioSession] bootstrap Playback category only (background, no setActive)");
        return;
    }
    error = nil;
    if (![session setActive:YES error:&error]) {
        NSLog(@"[BoluoAudioSession] bootstrap setActive failed: %@", error);
    } else {
        NSLog(@"[BoluoAudioSession] bootstrap Playback + setActive OK");
    }
}

/// 宿主前后台由 .mm 自观察（不再依赖 Swift SetHostForeground）。
/// 对齐原 SceneDelegate：resignActive→0，becomeActive→1；默认 0 避免后台拉起自动播。
/// 补种：非 Background 即视为前台（含 Inactive），避免 dylib 晚于 DidBecomeActive 加载后一直卡在 0 → Resume abort 无声。
static void BoluoAudioSession_InstallHostForegroundObservers(void) {
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        NSNotificationCenter *nc = [NSNotificationCenter defaultCenter];
        [nc addObserverForName:UIApplicationWillResignActiveNotification
                        object:nil
                         queue:[NSOperationQueue mainQueue]
                    usingBlock:^(__unused NSNotification *note) {
                        BoluoAudioSession_SetHostForeground(0);
                    }];
        [nc addObserverForName:UIApplicationDidBecomeActiveNotification
                        object:nil
                         queue:[NSOperationQueue mainQueue]
                    usingBlock:^(__unused NSNotification *note) {
                        BoluoAudioSession_SetHostForeground(1);
                    }];
        UIApplicationState state = [UIApplication sharedApplication].applicationState;
        if (state != UIApplicationStateBackground) {
            BoluoAudioSession_SetHostForeground(1);
        }
        NSLog(@"[BoluoAudioSession] host foreground observers installed (appState=%ld fg=%d)",
              (long)state, (int)sHostForeground);
    });
}

__attribute__((constructor))
static void BoluoAudioSession_Ctor(void) {
    BoluoAudioSession_InstallSetCategoryInterposer();
    BoluoAudioSession_InstallMediaServicesObserver();
    BoluoAudioSession_InstallDriftCorrector();
    // UIApplication 可能尚未 ready；下一 runloop 再装观察者与早启会话。
    dispatch_async(dispatch_get_main_queue(), ^{
        BoluoAudioSession_BootstrapPlaybackCategory();
        BoluoAudioSession_InstallHostForegroundObservers();
    });
}

void BoluoAudioSession_LockPlayback(void) {
    BoluoAudioSession_LockPlaybackNative();
}

int32_t BoluoAudioSession_IsHealthy(void) {
    AVAudioSession *session = [AVAudioSession sharedInstance];
    return [session.category isEqualToString:AVAudioSessionCategoryPlayback] ? 1 : 0;
}

static const char *BoluoAudioSession_RouteUTF8(void) {
    AVAudioSession *session = [AVAudioSession sharedInstance];
    AVAudioSessionPortDescription *out = session.currentRoute.outputs.firstObject;
    return out.portName.UTF8String ?: "?";
}

static float BoluoAudioSession_OutputVolume(void) {
    return [AVAudioSession sharedInstance].outputVolume;
}

void BoluoAudioSession_SetHostForeground(int32_t active) {
    int32_t next = active != 0 ? 1 : 0;
    int32_t prev = sHostForeground;
    if (prev == next) {
        return;
    }
    sHostForeground = next;
    BoluoAudioSession_Trail(@"SetHostForeground %d -> %d silenced=%d",
                            (int)prev, (int)next, (int)sLogoutSilenced);
    if (next == 1 && sLogoutSilenced == 0) {
        NSError *err = nil;
        [[AVAudioSession sharedInstance] setActive:YES error:&err];
        if (err) {
            BoluoAudioSession_Trail(@"SetHostForeground setActive failed: %@", err);
        }
    }
}

int32_t BoluoAudioSession_IsHostForeground(void) {
    return sHostForeground;
}

void BoluoAudioSession_SilenceForLogout(void) {
    // Disarm：先关闸再停播。登录页禁声靠本门闩；Interruption Ended 查 IsLogoutSilenced。
    sLogoutSilenced = 1;
    sSilenceGeneration += 1;
    sBoluoRestoreGate = NO;
    sNeedsDeviceRebuildAfterLogout = YES;
    sDriftTicks = 0;

    BoluoMusicPlayerIOS_MarkStoppedForLogout();
    SetMute(true);
    BoluoSetMuteWithMusicEventsNative(true);

    AVAudioSession *session = [AVAudioSession sharedInstance];
    NSError *error = nil;
    if (![session setCategory:AVAudioSessionCategoryAmbient
                  withOptions:AVAudioSessionCategoryOptionMixWithOthers
                        error:&error]) {
        NSLog(@"[BoluoAudioSession] SilenceForLogout setCategory failed: %@", error);
    }
    error = nil;
    if (![session setActive:NO
                withOptions:AVAudioSessionSetActiveOptionNotifyOthersOnDeactivation
                      error:&error]) {
        NSLog(@"[BoluoAudioSession] SilenceForLogout setActive(NO) failed: %@", error);
    }
    BoluoAudioSession_Trail(@"Disarm SilenceForLogout silenced=1 rebuildPending=1 gen=%llu",
                            (unsigned long long)sSilenceGeneration);
}

int32_t BoluoAudioSession_IsLogoutSilenced(void) {
    return sLogoutSilenced;
}

/// 会话唯一 Arm：开闸 + 重置播放意图 + LockPlayback；退登后 Rebuild（勿 SyncPlaying，正播交给 DelayPlayMusic）。
void BoluoAudioSession_ArmSessionForIslandPlayback(void) {
    const BOOL wasDisarmed = (sLogoutSilenced != 0);
    BoluoAudioSession_Trail(@"ArmSession ENTER wasDisarmed=%d rebuildPending=%d fg=%d",
                            (int)wasDisarmed,
                            (int)sNeedsDeviceRebuildAfterLogout,
                            (int)sHostForeground);
    if (wasDisarmed) {
        BoluoAudioSession_Trail(@"ArmSession clear Disarmed");
    }
    sLogoutSilenced = 0;
    // 进岛必前台：避免 HostForeground 观察者尚未补种时 Resume/DelayPlayMusic 因 fg=0 abort → 游客无声。
    BoluoAudioSession_SetHostForeground(1);
    BoluoAudioSession_LockPlaybackNative();

    // 新岛内会话：清空退登停播意图（含作废慢 Reset 回调）；勿 SyncPlaying（避免首播前闪声）。
    BoluoMusicPlayerIOS_ResetPlaybackIntentForSessionArm();

    if (sNeedsDeviceRebuildAfterLogout) {
        sNeedsDeviceRebuildAfterLogout = NO;
        sBoluoRestoreGate = NO;
        AVAudioSession *avSession = [AVAudioSession sharedInstance];
        NSError *err = nil;
        [avSession setActive:YES error:&err];
        // 保持 mute：首声只由 Unity DelayPlayMusic → PlayMusic 后 Unmute，避免此处 Resume_Music 闪一下再被 forceStop。
        BoluoAudio_RebuildPlaybackDeviceAsync("{\"play_layout\":0,\"device_id\":\"default\"}");
        BoluoAudioSession_Trail(@"ArmSession RebuildPlaybackDevice after logout (stay muted until first PlayMusic)");
    }
    BoluoAudioSession_Trail(@"ArmSessionForIslandPlayback LockPlayback fg=1 wasDisarmed=%d",
                            (int)wasDisarmed);
}

void BoluoAudioSession_PrepareAfterLogin(void) {
    // 兼容旧符号：不再清闸。开闸仅 ArmSessionForIslandPlayback（DelayPlayMusic 首播前）。
    NSLog(@"[BoluoAudioSession] PrepareAfterLogin no-op (use ArmSessionForIslandPlayback)");
}

static void BoluoAudioSession_UnmuteAndSyncPlaying(void) {
    // 岛内已首播可听：只保会话 active + 引擎解 mute，禁止 ApplyUserResume→Reset。
    if (BoluoMusicPlayerIOS_IsIslandAudible() != 0) {
        BoluoAudioSession_Trail(@"UnmuteAndSyncPlaying soft (already island audible)");
        SetMute(false);
        BoluoSetMuteWithMusicEventsNative(false);
        BoluoMusicPlayerIOS_SyncExternalPlaybackState(true);
        return;
    }
    SetMute(false);
    BoluoSetMuteWithMusicEventsNative(false);
    BoluoMusicPlayerIOS_SyncExternalPlaybackState(true);
    BoluoMusicPlayerIOS_ApplyUserResumeFromExternal();
}

void BoluoAudioSession_RestorePlayback(void) {
    // 会话总闸：Disarmed 时禁止恢复，且永不在此清闸（清闸仅 Arm）。
    if (sLogoutSilenced != 0) {
        BoluoAudioSession_Trail(@"RestorePlayback BLOCKED logoutSilenced");
        return;
    }

    // NotifyIslandReady 的 HUD Restore 常落在 DelayPlayMusic FirstPlay 之后；
    // 此时再 Reset 会拆掉已播实例 → 退登重登「书签 unmuted、实际无声」。
    if (BoluoMusicPlayerIOS_IsIslandAudible() != 0) {
        AVAudioSession *av = [AVAudioSession sharedInstance];
        NSError *err = nil;
        [av setActive:YES error:&err];
        BoluoAudioSession_Trail(@"RestorePlayback SKIP alreadyAudible (no Reset/UnmuteAndSync)");
        BoluoAudioSession_UpdateDiag(
            "restore-skip-audible st=%d vol=%.2f route=%s | %s",
            GetPlaybackState(), BoluoAudioSession_OutputVolume(),
            BoluoAudioSession_RouteUTF8(), sBoluoSetCatDiag);
        return;
    }

    AVAudioSession *avSession = [AVAudioSession sharedInstance];
    BOOL categoryHealthy =
        [avSession.category isEqualToString:AVAudioSessionCategoryPlayback];
    BOOL needRebuild = sNeedsDeviceRebuildAfterLogout;

    // Ambient / 设备脏：放开 boot 门闩，必须 Lock+unmute
    if (!categoryHealthy || needRebuild) {
        sBoluoRestoreGate = NO;
    }

    if (sBoluoRestoreGate) {
        // 类别已是 Playback，但可能 setActive(false)；门闩内也要拉活
        NSError *err = nil;
        [avSession setActive:YES error:&err];
        BoluoAudioSession_UnmuteAndSyncPlaying();
        BoluoAudioSession_UpdateDiag(
            "gate-reactivate st=%d vol=%.2f route=%s | %s",
            GetPlaybackState(), BoluoAudioSession_OutputVolume(),
            BoluoAudioSession_RouteUTF8(), sBoluoSetCatDiag);
        return;
    }

    sBoluoRestoreGate = YES;
    BoluoAudioSession_LockPlaybackNative();
    if (needRebuild) {
        // 正常应由 Arm 消化 rebuild；此处兜底（已 Armed）。
        sNeedsDeviceRebuildAfterLogout = NO;
        NSError *err = nil;
        [avSession setActive:YES error:&err];
        SetMute(false);
        BoluoSetMuteWithMusicEventsNative(false);
        BoluoAudio_RebuildPlaybackDeviceAsync("{\"play_layout\":0,\"device_id\":\"default\"}");
        BoluoAudioSession_Trail(@"RebuildPlaybackDevice (Restore, already armed)");
    } else {
        BoluoAudioSession_UnmuteAndSyncPlaying();
    }
    int state = GetPlaybackState();

    BoluoAudioSession_UpdateDiag(
        "restore st=%d vol=%.2f route=%s rebuild=%d | %s",
        state, BoluoAudioSession_OutputVolume(), BoluoAudioSession_RouteUTF8(),
        needRebuild ? 1 : 0, sBoluoSetCatDiag);

    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(8.0 * NSEC_PER_SEC)),
                   dispatch_get_main_queue(), ^{
        sBoluoRestoreGate = NO;
    });
}

void BoluoAudioSession_RestorePlaybackAfterHudReady(void) {
    // 0.3s：避开揭幕帧同 tick；Silence 后 generation 变化则丢弃回调。
    const uint64_t gen = sSilenceGeneration;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.3 * NSEC_PER_SEC)),
                   dispatch_get_main_queue(), ^{
        if (gen != sSilenceGeneration || sLogoutSilenced != 0) {
            BoluoAudioSession_Trail(@"RestorePlaybackAfterHudReady DROPPED gen=%llu now=%llu silenced=%d",
                                    (unsigned long long)gen, (unsigned long long)sSilenceGeneration,
                                    (int)sLogoutSilenced);
            return;
        }
        BoluoAudioSession_Trail(@"RestorePlaybackAfterHudReady → RestorePlayback");
        BoluoAudioSession_RestorePlayback();
    });
}
