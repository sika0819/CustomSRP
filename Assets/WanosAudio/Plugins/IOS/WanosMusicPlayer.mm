#import <Foundation/Foundation.h>
#import <MediaPlayer/MediaPlayer.h>
#import <QuartzCore/QuartzCore.h>
#include <string>
#include <vector>
#include <stdlib.h>
#include <stdio.h>

// WIAEngine 符号（与 C# DllImport 一致，iOS 使用 __Internal）
typedef int Result;
#define kEF_SUCCESS 0
#define InstanceState_Prepared 0
#define InstanceState_Playing 0x03e3
#define InstanceState_Ended 0x03e5

typedef struct { unsigned long long instanceId; int state; } EventCallbackInfo;
typedef void (*WanosInstanceStateCallBack)(EventCallbackInfo*);

extern "C" {
    Result LoadBankFromPath(const char* path);
    Result UnloadBank(const char* bankunloadTag);
    int IsBankDecoded(unsigned long long bank_id);
    unsigned long long PostEvent(unsigned long long obj_id, const char* event_name, WanosInstanceStateCallBack callback);
    int RemoveEventStateCallback(unsigned long long instance_id);
    void BoluoAudioSession_AppendTrail(const char* msg);
    /// 定义于 BoluoMusicPlayerIOS.mm；≥100ms 抛壳侧 BLS 卡顿通知。
    void BoluoNativeAudioJank_ReportPhase(const char *phase, double durationMs, const char *detail);
}

extern void UnitySendMessage(const char* obj, const char* method, const char* msg);

// 上一曲/下一曲时若暂停则先恢复：通过 ObjC 运行时调用 BoluoMusicPlayerIOSBridge，避免链接依赖
static bool WanosMusicPlayer_IsUserPaused(void) {
    Class bridgeClass = NSClassFromString(@"BoluoMusicPlayerIOSBridge");
    if (!bridgeClass) return false;
    SEL sharedSel = NSSelectorFromString(@"sharedInstance");
    SEL isUserPausedSel = NSSelectorFromString(@"isUserPaused");
    if (![bridgeClass respondsToSelector:sharedSel]) return false;
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Warc-performSelector-leaks"
    id bridge = [bridgeClass performSelector:sharedSel];
#pragma clang diagnostic pop
    if (!bridge || ![bridge respondsToSelector:isUserPausedSel]) return false;
    NSMethodSignature *sig = [bridge methodSignatureForSelector:isUserPausedSel];
    NSInvocation *inv = [NSInvocation invocationWithMethodSignature:sig];
    [inv setTarget:bridge];
    [inv setSelector:isUserPausedSel];
    [inv invoke];
    BOOL result = NO;
    [inv getReturnValue:&result];
    return result ? true : false;
}
static void WanosMusicPlayer_ResumeForTrackChange(void) {
    Class bridgeClass = NSClassFromString(@"BoluoMusicPlayerIOSBridge");
    if (!bridgeClass) return;
    SEL sharedSel = NSSelectorFromString(@"sharedInstance");
    SEL setMuteSel = NSSelectorFromString(@"setAudioMute:isUserAction:");
    if (![bridgeClass respondsToSelector:sharedSel]) return;
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Warc-performSelector-leaks"
    id bridge = [bridgeClass performSelector:sharedSel];
#pragma clang diagnostic pop
    if (!bridge || ![bridge respondsToSelector:setMuteSel]) return;
    NSMethodSignature *sig = [bridge methodSignatureForSelector:setMuteSel];
    NSInvocation *inv = [NSInvocation invocationWithMethodSignature:sig];
    [inv setTarget:bridge];
    [inv setSelector:setMuteSel];
    BOOL mute = NO;
    BOOL isUserAction = YES;
    [inv setArgument:&mute atIndex:2];
    [inv setArgument:&isUserAction atIndex:3];
    [inv invoke];
}

static CFTimeInterval g_lastSkipMediaTime = 0;
static int g_lastSkipDirection = 0;
static BOOL g_skipInFlight = NO;
static const NSTimeInterval kSkipDebounceSeconds = 0.4;       // BoluoNowPlayingRemoteCommandPolicy.SkipDebounceMs
static const NSTimeInterval kSkipInFlightTimeoutSeconds = 2.0; // BoluoNowPlayingRemoteCommandPolicy.SkipInFlightTimeoutMs

static void WanosMusicPlayer_NoteRemoteSkipSettled(void) {
    g_skipInFlight = NO;
}

static void WanosMusicPlayer_CancelPendingRemoteMuteForSkip(void) {
    Class bridgeClass = NSClassFromString(@"BoluoMusicPlayerIOSBridge");
    if (!bridgeClass) return;
    SEL sharedSel = NSSelectorFromString(@"sharedInstance");
    SEL cancelSel = NSSelectorFromString(@"cancelPendingRemoteMuteForSkip");
    if (![bridgeClass respondsToSelector:sharedSel]) return;
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Warc-performSelector-leaks"
    id bridge = [bridgeClass performSelector:sharedSel];
#pragma clang diagnostic pop
    if (!bridge || ![bridge respondsToSelector:cancelSel]) return;
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Warc-performSelector-leaks"
    [bridge performSelector:cancelSel];
#pragma clang diagnostic pop
}

/// 对齐 BoluoNowPlayingRemoteCommandPolicy.ShouldIgnoreSkip
static BOOL WanosMusicPlayer_ShouldIgnoreRemoteSkip(int direction) {
    CFTimeInterval now = CACurrentMediaTime();
    CFTimeInterval elapsed = (g_lastSkipMediaTime > 0) ? (now - g_lastSkipMediaTime) : 1e9;
    BOOL sameDir = (g_lastSkipDirection != 0 && g_lastSkipDirection == direction);
    if (g_skipInFlight && elapsed < kSkipInFlightTimeoutSeconds) return YES;
    if (sameDir && elapsed < kSkipDebounceSeconds) return YES;
    return NO;
}

static void WanosMusicPlayer_NoteRemoteSkipAccepted(int direction) {
    g_lastSkipMediaTime = CACurrentMediaTime();
    g_lastSkipDirection = direction;
    g_skipInFlight = YES;
    WanosMusicPlayer_CancelPendingRemoteMuteForSkip();
}

/// Boluo 设备重建 / 中断恢复进行中：此时 PostEvent / IsBankDecoded 忙等易与主线程 ma_device_stop 死锁。
static BOOL WanosMusicPlayer_IsAudioDeviceRecoveryInFlight(void) {
    Class bridgeClass = NSClassFromString(@"BoluoMusicPlayerIOSBridge");
    if (!bridgeClass) return NO;
    SEL sharedSel = NSSelectorFromString(@"sharedInstance");
    if (![bridgeClass respondsToSelector:sharedSel]) return NO;
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Warc-performSelector-leaks"
    id bridge = [bridgeClass performSelector:sharedSel];
#pragma clang diagnostic pop
    if (!bridge) return NO;
    SEL inFlightSel = NSSelectorFromString(@"playbackDeviceRecoveryInFlight");
    if (![bridge respondsToSelector:inFlightSel]) return NO;
    NSMethodSignature *sig = [bridge methodSignatureForSelector:inFlightSel];
    if (!sig) return NO;
    NSInvocation *inv = [NSInvocation invocationWithMethodSignature:sig];
    [inv setTarget:bridge];
    [inv setSelector:inFlightSel];
    [inv invoke];
    BOOL result = NO;
    [inv getReturnValue:&result];
    return result ? YES : NO;
}

//------------------------------------------------------------------------------
// 原生播放控制器：后台不依赖 C# 也能自动下一曲
//------------------------------------------------------------------------------

static NSString* g_persistentDataPath = nil;
static NSString* g_streamingAssetsPath = nil;
static unsigned long long g_objectId = 0;
static NSString* g_unityObjectName = @"WanosMusicPlayer";
static NSString* g_stopEventName = @"Stop_Music";
static NSString* g_breakEventName = @"Break_Music";
static unsigned long long g_currentInstanceId = 0;
static BOOL g_shouldStop = NO;
static BOOL g_remoteCommandsConfigured = NO;
static BOOL g_playlistReloadedByCSharp = NO;  // OnBankReady 时 C# 调用 ReloadPlaylistFromFile 后置为 YES，EventCallback 使用后清除
/// ReloadPlaylistFromFile 合并：Bank 连到就绪时会连发，避免 playback 队列堆积 JSON 解析。
/// （连发合并主路径在 C# CoFlushPendingBankRefresh；此处兜底队列最多一条。）
static BOOL g_reloadPlaylistQueued = NO;

// 简化的 playlist 结构（从 MusicLists.json 解析）
struct NativeMusic {
    std::string bankName;
    std::string bankGuid;  // 序列化的 Bank GUID，便于外部检测及 IsBankDecoded
    std::string eventName;
    std::string musicName;
};
struct NativeMusicList {
    std::string musicsName;
    std::vector<NativeMusic> musics;
};
struct NativePlaylist {
    int mode;  // 0=SingleLoop 1=ListLoop 2=ListShuffle
    int currentListIndex;
    int currentMusicIndex;
    std::vector<int> indexes;
    std::vector<NativeMusicList> musicLists;
};

static NativePlaylist g_playlist;
static NSMutableSet* g_loadedBanks = nil;
/** 上一首成功起播的 music bank；切不同 bank 后 Unload 压内存。 */
static NSString* g_previousMusicBankName = nil;
static dispatch_queue_t g_playbackQueue = nil;  // 串行队列，防止切歌并发
/// 每次新切歌请求自增；decode 等待期间若 generation 已变，说明被更新的请求取代，放弃 PostEvent 避免叠音
static uint64_t g_playGeneration = 0;
static const unsigned long long kControlBankGuid = 11329293679594640179ULL; // Common_Music: Stop/Break/Pause/Resume
static BOOL g_controlBankLoadRequested = NO;

static std::string getString(NSDictionary* dict, NSString* key) {
    id val = dict[key];
    if ([val isKindOfClass:[NSString class]]) {
        return [(NSString*)val UTF8String] ?: "";
    }
    return "";
}

static int getInt(NSDictionary* dict, NSString* key, int def) {
    id val = dict[key];
    if ([val isKindOfClass:[NSNumber class]]) {
        return [(NSNumber*)val intValue];
    }
    return def;
}

static void parsePlaylist(NSDictionary* json) {
    g_playlist.mode = getInt(json, @"mode", 1);
    g_playlist.currentListIndex = getInt(json, @"currentListIndex", 0);
    g_playlist.currentMusicIndex = getInt(json, @"currentMusicIndex", 0);
    g_playlist.indexes.clear();
    g_playlist.musicLists.clear();

    NSArray* lists = json[@"musicLists"];
    if ([lists isKindOfClass:[NSArray class]]) {
        for (NSDictionary* listDict in lists) {
            NativeMusicList nl;
            nl.musicsName = getString(listDict, @"musicsName");
            NSArray* musics = listDict[@"musics"];
            if ([musics isKindOfClass:[NSArray class]]) {
                for (NSDictionary* m in musics) {
                    NativeMusic nm;
                    nm.bankName = getString(m, @"bankName");
                    nm.bankGuid = getString(m, @"bankGuid");
                    nm.eventName = getString(m, @"eventName");
                    nm.musicName = getString(m, @"musicName");
                    nl.musics.push_back(nm);
                }
            }
            g_playlist.musicLists.push_back(nl);
        }
    }

    // C# JsonUtility 不序列化 List<int>「indexes」；优先读 indexesForNativeJson（int[]）
    NSArray* idxArr = json[@"indexesForNativeJson"];
    if (![idxArr isKindOfClass:[NSArray class]] || idxArr.count == 0) {
        idxArr = json[@"indexes"];
    }
    if ([idxArr isKindOfClass:[NSArray class]]) {
        for (NSNumber* n in idxArr) {
            g_playlist.indexes.push_back([n intValue]);
        }
    }
}

static void updateIndexesFromMode(void) {
    if (g_playlist.musicLists.empty()) return;
    int listIdx = (int)g_playlist.currentListIndex;
    if (listIdx < 0 || listIdx >= (int)g_playlist.musicLists.size()) return;
    const NativeMusicList& list = g_playlist.musicLists[listIdx];
    int count = (int)list.musics.size();
    if (count == 0) return;

    g_playlist.indexes.clear();
    if (g_playlist.mode == 0) {
        g_playlist.indexes.push_back(g_playlist.currentMusicIndex >= 0 && g_playlist.currentMusicIndex < count ? g_playlist.currentMusicIndex : 0);
    } else {
        for (int i = 0; i < count; i++) g_playlist.indexes.push_back(i);
        if (g_playlist.mode == 2) {
            for (int i = (int)g_playlist.indexes.size() - 1; i > 0; i--) {
                int j = arc4random_uniform(i + 1);
                std::swap(g_playlist.indexes[i], g_playlist.indexes[j]);
            }
        }
    }
}

static void savePlaylistToFile(NSString* path) {
    if (!path || g_playlist.musicLists.empty()) return;
    NSMutableDictionary* root = [NSMutableDictionary dictionary];
    root[@"mode"] = @(g_playlist.mode);
    root[@"currentListIndex"] = @(g_playlist.currentListIndex);
    root[@"currentMusicIndex"] = @(g_playlist.currentMusicIndex);
    NSMutableArray* idxArr = [NSMutableArray array];
    for (int i : g_playlist.indexes) [idxArr addObject:@(i)];
    root[@"indexes"] = idxArr;
    NSMutableArray* listsArr = [NSMutableArray array];
    for (const NativeMusicList& nl : g_playlist.musicLists) {
        NSMutableDictionary* listDict = [NSMutableDictionary dictionary];
        listDict[@"musicsName"] = [NSString stringWithUTF8String:nl.musicsName.c_str()];
        NSMutableArray* musicsArr = [NSMutableArray array];
        for (const NativeMusic& nm : nl.musics) {
            NSMutableDictionary* mDict = [NSMutableDictionary dictionary];
            mDict[@"bankName"] = [NSString stringWithUTF8String:nm.bankName.c_str()];
            mDict[@"bankGuid"] = [NSString stringWithUTF8String:nm.bankGuid.c_str()];
            mDict[@"eventName"] = [NSString stringWithUTF8String:nm.eventName.c_str()];
            mDict[@"musicName"] = [NSString stringWithUTF8String:nm.musicName.c_str()];
            [musicsArr addObject:mDict];
        }
        listDict[@"musics"] = musicsArr;
        [listsArr addObject:listDict];
    }
    root[@"musicLists"] = listsArr;
    NSError* err = nil;
    NSData* data = [NSJSONSerialization dataWithJSONObject:root options:NSJSONWritingPrettyPrinted error:&err];
    if (data) [data writeToFile:path atomically:YES];
}

static BOOL loadPlaylistFromFile(NSString* path) {
    NSData* data = [NSData dataWithContentsOfFile:path];
    if (!data || data.length == 0) return NO;
    NSError* err = nil;
    NSDictionary* json = [NSJSONSerialization JSONObjectWithData:data options:0 error:&err];
    if (!json || ![json isKindOfClass:[NSDictionary class]]) return NO;
    parsePlaylist(json);
    // indexes 为空时不回退为「全专辑序号」，与 C# 仅序列化可播曲目一致；避免未下载项进入自动下一曲后 Stop 又无音。
    return g_playlist.musicLists.size() > 0;
}

static BOOL loadPlaylistFromDefaultLocations(NSString** outPath) {
    if (g_persistentDataPath.length > 0) {
        NSString* path = [g_persistentDataPath stringByAppendingPathComponent:@"WanosAudio/MusicLists.json"];
        if (loadPlaylistFromFile(path)) {
            if (outPath) *outPath = path;
            return YES;
        }
    }
    if (g_streamingAssetsPath.length > 0) {
        NSString* path = [g_streamingAssetsPath stringByAppendingPathComponent:@"WanosAudio/MusicLists.json"];
        if (loadPlaylistFromFile(path)) {
            if (outPath) *outPath = path;
            return YES;
        }
    }
    return NO;
}

static NSString* getBankGuid(NSString* bankName) {
    NSString* metaPath = [g_persistentDataPath stringByAppendingPathComponent:@"WanosAudio/ProjectMetadata.json"];
    NSData* data = [NSData dataWithContentsOfFile:metaPath];
    if (!data && g_streamingAssetsPath.length > 0) {
        metaPath = [g_streamingAssetsPath stringByAppendingPathComponent:@"WanosAudio/ProjectMetadata.json"];
        data = [NSData dataWithContentsOfFile:metaPath];
    }
    if (!data) return nil;
    NSError* err = nil;
    NSDictionary* json = [NSJSONSerialization JSONObjectWithData:data options:0 error:&err];
    if (!json) return nil;
    NSArray* banks = json[@"Banks"];
    if (![banks isKindOfClass:[NSArray class]]) return nil;
    for (NSDictionary* b in banks) {
        if ([b[@"BankName"] isEqualToString:bankName]) {
            return b[@"Guid"];
        }
    }
    return nil;
}

static NSString* getBankGuidForMusic(const NativeMusic& music) {
    if (!music.bankGuid.empty()) {
        return [NSString stringWithUTF8String:music.bankGuid.c_str()];
    }
    return getBankGuid([NSString stringWithUTF8String:music.bankName.c_str()]);
}

static NSString* getBankPath(NSString* bankName) {
    NSString* wb = [bankName stringByAppendingPathExtension:@"wb"];
    if (g_persistentDataPath.length > 0) {
        NSString* patched = [g_persistentDataPath stringByAppendingPathComponent:[@"WanosBankPatched" stringByAppendingPathComponent:wb]];
        if ([[NSFileManager defaultManager] fileExistsAtPath:patched]) return patched;
        NSString* path = [g_persistentDataPath stringByAppendingPathComponent:[@"WanosBank" stringByAppendingPathComponent:wb]];
        if ([[NSFileManager defaultManager] fileExistsAtPath:path]) return path;
    }
    if (g_streamingAssetsPath.length > 0) {
        NSString* path = [g_streamingAssetsPath stringByAppendingPathComponent:[@"WanosBank" stringByAppendingPathComponent:wb]];
        if ([[NSFileManager defaultManager] fileExistsAtPath:path]) return path;
    }
    return nil;
}

/// DestroyEngine 后进程内缓存仍在，但原生 bank 已空；须清缓存否则会跳过 LoadBank → 重登无声。
static void invalidateNativeBankLoadCache(const char* reason) {
    NSUInteger n = g_loadedBanks != nil ? g_loadedBanks.count : 0;
    [g_loadedBanks removeAllObjects];
    g_previousMusicBankName = nil;
    g_controlBankLoadRequested = NO;
    NSLog(@"[WanosMusicPlayer] InvalidateBankCache reason=%s cleared=%lu",
          reason ? reason : "?", (unsigned long)n);
}

/// 切不同 music bank 后卸旧包；不卸 Common_* / 当前曲。
static void unloadPreviousMusicBankIfNeeded(NSString* previousBank, NSString* currentBank) {
    if (previousBank.length == 0 || currentBank.length == 0) return;
    if ([previousBank isEqualToString:currentBank]) return;
    if ([previousBank isEqualToString:@"Common_Init"] || [previousBank isEqualToString:@"Common_Music"]) return;
    Result r = UnloadBank([previousBank UTF8String]);
    [g_loadedBanks removeObject:previousBank];
    NSLog(@"[WanosMusicPlayer] Unload previous music bank=%@ result=%d", previousBank, r);
}

static BOOL loadBankIfPresent(NSString* bankName) {
    if (bankName.length == 0) return NO;
    NSString* guid = getBankGuid(bankName);
    unsigned long long guidVal = 0;
    if (guid.length > 0) {
        guidVal = strtoull([guid UTF8String], NULL, 10);
        if (guidVal != 0 && IsBankDecoded(guidVal) == kEF_SUCCESS) {
            if (!g_loadedBanks) g_loadedBanks = [NSMutableSet set];
            [g_loadedBanks addObject:bankName];
            return YES;
        }
    }
    // 缓存命中但引擎里没有（典型：Unload DestroyEngine 后重进）→ 丢弃假命中并重载
    [g_loadedBanks removeObject:bankName];
    NSString* bankPath = getBankPath(bankName);
    if (!bankPath) return NO;
    CFTimeInterval t0 = CACurrentMediaTime();
    Result r = LoadBankFromPath([bankPath UTF8String]);
    BoluoNativeAudioJank_ReportPhase("native_load_bank", (CACurrentMediaTime() - t0) * 1000.0,
                                     [[NSString stringWithFormat:@"bank=%@", bankName] UTF8String]);
    NSLog(@"[WanosMusicPlayer] preload bank %@ result=%d path=%@", bankName, r, bankPath);
    if (r == kEF_SUCCESS) {
        if (!g_loadedBanks) g_loadedBanks = [NSMutableSet set];
        [g_loadedBanks addObject:bankName];
        return YES;
    }
    return NO;
}

static BOOL ensureControlBankDecoded(int maxWaitLoops) {
    if (IsBankDecoded(kControlBankGuid) == kEF_SUCCESS) return YES;

    NSString* bankPath = getBankPath(@"Common_Music");
    if (!bankPath) {
        NSLog(@"[WanosMusicPlayer] Common_Music.wb not found; Stop/Break may be ignored early");
        return NO;
    }

    // 勿用 g_controlBankLoadRequested 永久挡住重载：DestroyEngine 后须再 Load
    g_controlBankLoadRequested = YES;
    loadBankIfPresent(@"Common_Music");

    for (int i = 0; i < maxWaitLoops; i++) {
        if (IsBankDecoded(kControlBankGuid) == kEF_SUCCESS) return YES;
        usleep(25000);
    }

    NSLog(@"[WanosMusicPlayer] Common_Music control bank not decoded yet");
    return NO;
}

static void preloadCurrentMusicBank(void) {
    NSString* listPath = nil;
    if (!loadPlaylistFromDefaultLocations(&listPath)) {
        NSLog(@"[WanosMusicPlayer] preload current music skipped: MusicLists.json unavailable");
        return;
    }
    int listIdx = g_playlist.currentListIndex;
    int musicIdx = g_playlist.currentMusicIndex;
    if (listIdx < 0 || listIdx >= (int)g_playlist.musicLists.size()) listIdx = 0;
    const NativeMusicList& list = g_playlist.musicLists[listIdx];
    if (list.musics.empty()) return;
    if (musicIdx < 0 || musicIdx >= (int)list.musics.size()) {
        musicIdx = !g_playlist.indexes.empty() ? g_playlist.indexes[0] : 0;
    }
    if (musicIdx < 0 || musicIdx >= (int)list.musics.size()) return;
    NSString* bankName = [NSString stringWithUTF8String:list.musics[musicIdx].bankName.c_str()];
    if (loadBankIfPresent(bankName)) {
        NSLog(@"[WanosMusicPlayer] preload current music bank=%@ from %@", bankName, listPath);
    }
}

static void WanosMusicPlayer_EventCallback(EventCallbackInfo* info);

/// 与曲终自动下一曲逻辑一致，计算下一曲在列表中的下标（不写回 g_playlist）
static void computeNextTrackIndices(int listIdx, int musicIdx, int& outListIdx, int& outMusicIdx) {
    outListIdx = listIdx;
    outMusicIdx = musicIdx;
    if (listIdx < 0 || listIdx >= (int)g_playlist.musicLists.size()) return;
    const NativeMusicList& list = g_playlist.musicLists[listIdx];
    if (list.musics.empty()) return;

    if (g_playlist.mode == 0) {
        outMusicIdx = musicIdx;
    } else if ((g_playlist.mode == 1 || g_playlist.mode == 2) && !g_playlist.indexes.empty()) {
        int idxPos = -1;
        for (size_t i = 0; i < g_playlist.indexes.size(); i++) {
            if (g_playlist.indexes[i] == musicIdx) { idxPos = (int)i; break; }
        }
        int nextPos = idxPos >= 0 ? (idxPos + 1) % (int)g_playlist.indexes.size() : 0;
        outMusicIdx = g_playlist.indexes[nextPos];
    } else {
        outMusicIdx = (musicIdx + 1) % (int)list.musics.size();
    }
}

static bool isNativeTrackResourcesPlayable(int listIdx, int musicIdx) {
    if (listIdx < 0 || listIdx >= (int)g_playlist.musicLists.size()) return false;
    const NativeMusicList& lst = g_playlist.musicLists[listIdx];
    if (musicIdx < 0 || musicIdx >= (int)lst.musics.size()) return false;
    const NativeMusic& m = lst.musics[musicIdx];
    if (m.bankName.empty() || m.eventName.empty()) return false;
    NSString* bn = [NSString stringWithUTF8String:m.bankName.c_str()];
    if (!getBankPath(bn)) return false;
    NSString* guidStr = getBankGuidForMusic(m);
    if (!guidStr || guidStr.length == 0) return false;
    if (strtoull([guidStr UTF8String], NULL, 0) == 0) return false;
    return true;
}

static int nativePlaylistMaxAdvanceSteps(int listIdx) {
    int m = 64;
    if (listIdx >= 0 && listIdx < (int)g_playlist.musicLists.size()) {
        int nMusics = (int)g_playlist.musicLists[listIdx].musics.size();
        if (nMusics > m) m = nMusics;
        int nIdx = (int)g_playlist.indexes.size();
        if (nIdx > m) m = nIdx;
    }
    return m;
}

static BOOL forceStopCurrentPlayback(void);

// 原生切歌核心逻辑：加载、解码、播放、更新 NowPlaying、通知 Unity（绕过 Unity 播放流程）
static void nativePlayTrackAt(int listIdx, int musicIdx, uint64_t generation) {
    if (generation != g_playGeneration) {
        NSLog(@"[WanosMusicPlayer] nativePlayTrackAt skipped (superseded) gen=%llu cur=%llu", generation, g_playGeneration);
        WanosMusicPlayer_NoteRemoteSkipSettled();
        return;
    }
    // 蓝牙断连恢复中：PostEvent / IsBankDecoded 忙等会与主线程 ma_device_stop 死锁。
    // 用户日志常停在 ReloadPlaylistFromFile updated 之后。
    if (WanosMusicPlayer_IsAudioDeviceRecoveryInFlight()) {
        NSLog(@"[WanosMusicPlayer] nativePlayTrackAt DEFER (audio device recovery inFlight) list=%d music=%d", listIdx, musicIdx);
        BoluoAudioSession_AppendTrail("native Play DEFER recoveryInFlight");
        if (!g_playbackQueue) g_playbackQueue = dispatch_queue_create("com.wanos.musicplayback", DISPATCH_QUEUE_SERIAL);
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.35 * NSEC_PER_SEC)), g_playbackQueue, ^{
            if (generation != g_playGeneration) return;
            nativePlayTrackAt(listIdx, musicIdx, generation);
        });
        return;
    }
    g_shouldStop = NO;  // 锁屏切歌时重置，避免 C# Stop 后 g_shouldStop=YES 导致无法播放
    // 每次切歌前 reload，确保 playlist 与 C# 保存的最新状态一致（mode、列表、indexes）
    NSString* listPath = nil;
    loadPlaylistFromDefaultLocations(&listPath);
    NSString* savePath = [g_persistentDataPath stringByAppendingPathComponent:@"WanosAudio/MusicLists.json"];
    if (listIdx < 0 || listIdx >= (int)g_playlist.musicLists.size()) {
        WanosMusicPlayer_NoteRemoteSkipSettled();
        return;
    }
    NativeMusicList& listForBounds = g_playlist.musicLists[listIdx];
    if (musicIdx < 0 || musicIdx >= (int)listForBounds.musics.size()) {
        WanosMusicPlayer_NoteRemoteSkipSettled();
        return;
    }

    int tryL = listIdx;
    int tryM = musicIdx;
    int maxSteps = nativePlaylistMaxAdvanceSteps(listIdx);
    int chosenL = -1;
    int chosenM = -1;
    for (int step = 0; step < maxSteps; step++) {
        if (tryL < 0 || tryL >= (int)g_playlist.musicLists.size()) break;
        const NativeMusicList& tl = g_playlist.musicLists[tryL];
        if (tryM < 0 || tryM >= (int)tl.musics.size()) break;
        if (isNativeTrackResourcesPlayable(tryL, tryM)) {
            chosenL = tryL;
            chosenM = tryM;
            break;
        }
        int nL = tryL;
        int nM = tryM;
        computeNextTrackIndices(tryL, tryM, nL, nM);
        tryL = nL;
        tryM = nM;
    }
    if (chosenL < 0) {
        NSLog(@"[WanosMusicPlayer] No playable track on disk (skip exhausted), start list=%d music=%d — not sending Stop", listIdx, musicIdx);
        WanosMusicPlayer_NoteRemoteSkipSettled();
        return;
    }
    NSLog(@"[WanosMusicPlayer][NextPrev] nativePlayTrackAt requested list=%d music=%d -> chosen list=%d music=%d%s",
          listIdx, musicIdx, chosenL, chosenM,
          (chosenL == listIdx && chosenM == musicIdx) ? "" : " (SKIPPED to next playable)");
    listIdx = chosenL;
    musicIdx = chosenM;

    g_playlist.currentListIndex = listIdx;
    g_playlist.currentMusicIndex = musicIdx;
    WanosMusicPlayer_NoteRemoteSkipSettled();
    NativeMusicList& list = g_playlist.musicLists[listIdx];
    NativeMusic& nextMusic = list.musics[musicIdx];

    NSString* bankPath = getBankPath([NSString stringWithUTF8String:nextMusic.bankName.c_str()]);
    if (!bankPath) {
        NSLog(@"[WanosMusicPlayer] Bank not found after skip logic: %s", nextMusic.bankName.c_str());
        return;
    }
    NSString* guidStr = getBankGuidForMusic(nextMusic);
    if (!guidStr || guidStr.length == 0) {
        NSLog(@"[WanosMusicPlayer] Bank Guid not found: %s", nextMusic.bankName.c_str());
        return;
    }
    unsigned long long bankGuid = strtoull([guidStr UTF8String], NULL, 0);

    NSString* bankKey = [NSString stringWithUTF8String:nextMusic.bankName.c_str()];
    BOOL bankInEngine = (bankGuid != 0 && IsBankDecoded(bankGuid) == kEF_SUCCESS);
    if (!bankInEngine) {
        // 勿信 g_loadedBanks：Unload 后集合仍含旧名，跳过 LoadBank → PostEvent 无 bank → 静音
        [g_loadedBanks removeObject:bankKey];
        Result r = LoadBankFromPath([bankPath UTF8String]);
        NSLog(@"[WanosMusicPlayer] LoadBank (engine miss) %@ result=%d", bankKey, r);
        if (r == kEF_SUCCESS) {
            if (!g_loadedBanks) g_loadedBanks = [NSMutableSet set];
            [g_loadedBanks addObject:bankKey];
        }
    } else if (g_loadedBanks != nil) {
        [g_loadedBanks addObject:bankKey];
    }
    // 禁止 usleep 忙等占住 playback 串行队列（最多约 25s）：蓝牙恢复时主线程等引擎锁 → 假死。
    // 未就绪则异步再试，队列可继续处理 ReloadPlaylist / 其它任务。
    if (IsBankDecoded(bankGuid) != kEF_SUCCESS) {
        NSLog(@"[WanosMusicPlayer] Bank decode pending, async retry: %s", nextMusic.bankName.c_str());
        if (!g_playbackQueue) g_playbackQueue = dispatch_queue_create("com.wanos.musicplayback", DISPATCH_QUEUE_SERIAL);
        __block int attemptsLeft = 500;
        __block void (^poll)(void) = nil;
        __block __weak void (^weakPoll)(void) = nil;
        poll = ^{
            void (^strongPoll)(void) = weakPoll;
            if (!strongPoll) return;
            if (generation != g_playGeneration || g_shouldStop) {
                return;
            }
            if (WanosMusicPlayer_IsAudioDeviceRecoveryInFlight()) {
                dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.35 * NSEC_PER_SEC)), g_playbackQueue, strongPoll);
                return;
            }
            if (IsBankDecoded(bankGuid) == kEF_SUCCESS) {
                nativePlayTrackAt(listIdx, musicIdx, generation);
                return;
            }
            attemptsLeft--;
            if (attemptsLeft <= 0) {
                NSLog(@"[WanosMusicPlayer] Bank decode not ready, keep current playback: %s", nextMusic.bankName.c_str());
                return;
            }
            dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(50 * NSEC_PER_MSEC)), g_playbackQueue, strongPoll);
        };
        weakPoll = poll;
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(50 * NSEC_PER_MSEC)), g_playbackQueue, poll);
        return;
    }
    if (generation != g_playGeneration) {
        NSLog(@"[WanosMusicPlayer] play aborted before PostEvent (superseded) gen=%llu cur=%llu", generation, g_playGeneration);
        return;
    }

    if (WanosMusicPlayer_IsAudioDeviceRecoveryInFlight()) {
        NSLog(@"[WanosMusicPlayer] before forceStop/PostEvent DEFER (recovery inFlight)");
        if (!g_playbackQueue) g_playbackQueue = dispatch_queue_create("com.wanos.musicplayback", DISPATCH_QUEUE_SERIAL);
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.35 * NSEC_PER_SEC)), g_playbackQueue, ^{
            if (generation != g_playGeneration) return;
            nativePlayTrackAt(listIdx, musicIdx, generation);
        });
        return;
    }

    if (!forceStopCurrentPlayback()) {
        // 控制 bank 未就绪 / 恢复窗口：稍后重试，勿静默丢弃切歌。
        NSLog(@"[WanosMusicPlayer] forceStop failed, defer play list=%d music=%d", listIdx, musicIdx);
        if (!g_playbackQueue) g_playbackQueue = dispatch_queue_create("com.wanos.musicplayback", DISPATCH_QUEUE_SERIAL);
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.2 * NSEC_PER_SEC)), g_playbackQueue, ^{
            if (generation != g_playGeneration) return;
            nativePlayTrackAt(listIdx, musicIdx, generation);
        });
        return;
    }
    if (generation != g_playGeneration) {
        NSLog(@"[WanosMusicPlayer] play aborted after forceStop (superseded) gen=%llu cur=%llu", generation, g_playGeneration);
        return;
    }

    g_currentInstanceId = PostEvent(g_objectId, nextMusic.eventName.c_str(), WanosMusicPlayer_EventCallback);
    NSLog(@"[WanosMusicPlayer][NextPrev] PostEvent posted event=%s list=%d music=%d instance=%llu", nextMusic.eventName.c_str(), listIdx, musicIdx, g_currentInstanceId);

    NSString* bankToUnload = g_previousMusicBankName;
    g_previousMusicBankName = bankKey;
    unloadPreviousMusicBankIfNeeded(bankToUnload, bankKey);

    NSString* listName = [NSString stringWithUTF8String:list.musicsName.c_str()];
    NSString* musicName = [NSString stringWithUTF8String:nextMusic.musicName.c_str()];
    NSString* artworkPath = nil;
    if (g_streamingAssetsPath.length > 0) {
        artworkPath = [[[g_streamingAssetsPath stringByAppendingPathComponent:@"WanosAudio"] stringByAppendingPathComponent:@"Artwork"] stringByAppendingPathComponent:@"boluo.png"];
        if (![[NSFileManager defaultManager] fileExistsAtPath:artworkPath]) {
            artworkPath = nil;
        }
    }
    NSString* artworkPathForBlock = artworkPath;  // 供 block 捕获
    dispatch_async(dispatch_get_main_queue(), ^{
        NSMutableDictionary* info = [NSMutableDictionary dictionary];
        info[MPMediaItemPropertyTitle] = musicName ?: @"";
        info[MPMediaItemPropertyArtist] = listName ?: @"";
        info[MPMediaItemPropertyAlbumTitle] = listName ?: @"";
        info[MPNowPlayingInfoPropertyPlaybackRate] = @(1.0);
        info[MPNowPlayingInfoPropertyElapsedPlaybackTime] = @(0.0);
        info[MPNowPlayingInfoPropertyIsLiveStream] = @(YES);
        info[MPMediaItemPropertyPlaybackDuration] = @(0.0);
        if (artworkPathForBlock.length > 0) {
            UIImage* image = [UIImage imageWithContentsOfFile:artworkPathForBlock];
            if (image) {
                MPMediaItemArtwork* artwork = [[MPMediaItemArtwork alloc] initWithBoundsSize:image.size requestHandler:^UIImage* _Nonnull(CGSize size) { return image; }];
                info[MPMediaItemPropertyArtwork] = artwork;
            }
        }
        [MPNowPlayingInfoCenter defaultCenter].nowPlayingInfo = info;
        if (g_unityObjectName.length > 0) {
            UnitySendMessage([g_unityObjectName UTF8String], "OnNativeTrackChanged", [[NSString stringWithFormat:@"%d,%d", listIdx, musicIdx] UTF8String]);
        }
        // 延迟二次刷新，防止被 BoluoMusicPlayerIOS 等逻辑覆盖导致标题丢失
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.35 * NSEC_PER_SEC)), dispatch_get_main_queue(), ^{
            if (g_shouldStop) return;
            NSMutableDictionary* info2 = [NSMutableDictionary dictionary];
            info2[MPMediaItemPropertyTitle] = musicName ?: @"";
            info2[MPMediaItemPropertyArtist] = listName ?: @"";
            info2[MPMediaItemPropertyAlbumTitle] = listName ?: @"";
            info2[MPNowPlayingInfoPropertyPlaybackRate] = @(1.0);
            info2[MPNowPlayingInfoPropertyElapsedPlaybackTime] = @(0.0);
            info2[MPNowPlayingInfoPropertyIsLiveStream] = @(YES);
            info2[MPMediaItemPropertyPlaybackDuration] = @(0.0);
            if (artworkPathForBlock.length > 0) {
                UIImage* img = [UIImage imageWithContentsOfFile:artworkPathForBlock];
                if (img) {
                    MPMediaItemArtwork* aw = [[MPMediaItemArtwork alloc] initWithBoundsSize:img.size requestHandler:^UIImage* _Nonnull(CGSize size) { return img; }];
                    info2[MPMediaItemPropertyArtwork] = aw;
                }
            }
            [MPNowPlayingInfoCenter defaultCenter].nowPlayingInfo = info2;
        });
    });

    savePlaylistToFile(savePath);
}

/// 新切歌请求：自增 generation 并立即 Stop 当前实例（不等排队任务跑到 Stop），防止快速连切叠音
static uint64_t beginPlayRequest(void) {
    return ++g_playGeneration;
}

/// 在 g_playbackQueue 上调用：Stop + Break + 短等待，给 WIA 引擎时间真正停掉上一轨（进岛前 3s 尤其需要）
static BOOL forceStopCurrentPlayback(void) {
    if (g_currentInstanceId == 0) return YES;
    if (WanosMusicPlayer_IsAudioDeviceRecoveryInFlight()) {
        NSLog(@"[WanosMusicPlayer] forceStop SKIP (recovery inFlight)");
        return NO;
    }
    // 控制 bank 未解码时不要 usleep 忙等占队列；最多轻量轮询几次。
    if (!ensureControlBankDecoded(8)) {
        return NO;
    }
    RemoveEventStateCallback(g_currentInstanceId);
    g_currentInstanceId = 0;
    if (g_objectId != 0) {
        NSLog(@"[WanosMusicPlayer] forceStop -> Stop + Break");
        PostEvent(g_objectId, [g_stopEventName UTF8String], nil);
        if (g_breakEventName.length > 0) {
            PostEvent(g_objectId, [g_breakEventName UTF8String], nil);
        }
    }
    // 50ms 足够让引擎消化 Stop；原 200ms 在蓝牙抖动时拉长卡顿窗口。
    usleep(50000);
    return YES;
}

static void enqueueNativePlay(int listIdx, int musicIdx) {
    if (!g_persistentDataPath) return;
    // 同曲已在播：勿 bump generation / forceStop（进岛重复 Play → 连环 Stop_Music）
    if (g_currentInstanceId != 0
        && !g_shouldStop
        && g_playlist.currentListIndex == listIdx
        && g_playlist.currentMusicIndex == musicIdx) {
        NSLog(@"[WanosMusicPlayer] enqueueNativePlay skip redundant same track list=%d music=%d", listIdx, musicIdx);
        return;
    }
    uint64_t gen = beginPlayRequest();
    if (!g_playbackQueue) g_playbackQueue = dispatch_queue_create("com.wanos.musicplayback", DISPATCH_QUEUE_SERIAL);
    dispatch_async(g_playbackQueue, ^{
        if (gen != g_playGeneration) return;
        nativePlayTrackAt(listIdx, musicIdx, gen);
    });
}

static MPRemoteCommandHandlerStatus handleRemoteSkip(int direction) {
    // CustomSRP：曲目权威在 Unity MusicPlaylist，原生只转发并取消 pending play/pause。
    if (WanosMusicPlayer_ShouldIgnoreRemoteSkip(direction)) {
        NSLog(@"[WanosMusicPlayer][NextPrev] skip debounce dir=%d inFlight=%d",
              direction, g_skipInFlight ? 1 : 0);
        return MPRemoteCommandHandlerStatusSuccess;
    }
    WanosMusicPlayer_NoteRemoteSkipAccepted(direction);
    const char* method = direction > 0 ? "OnMediaSkipNext" : "OnMediaSkipPrev";
    UnitySendMessage("BoluoMusicPlayerIOS", method, "");
    WanosMusicPlayer_NoteRemoteSkipSettled();
    NSLog(@"[WanosMusicPlayer][NextPrev] forwarded %@ to Unity", direction > 0 ? @"NEXT" : @"PREV");
    return MPRemoteCommandHandlerStatusSuccess;
}

// 设置 MPRemoteCommand next/previous，在原生侧处理切歌，绕过 Unity
static void setupRemoteCommandsIfNeeded(void) {
    if (g_remoteCommandsConfigured) return;
    if (!g_persistentDataPath || g_unityObjectName.length == 0) return;
    if (!g_playbackQueue) g_playbackQueue = dispatch_queue_create("com.wanos.musicplayback", DISPATCH_QUEUE_SERIAL);

    MPRemoteCommandCenter* center = [MPRemoteCommandCenter sharedCommandCenter];
    center.nextTrackCommand.enabled = YES;
    center.previousTrackCommand.enabled = YES;

    [center.nextTrackCommand addTargetWithHandler:^MPRemoteCommandHandlerStatus(MPRemoteCommandEvent* _Nonnull event) {
        return handleRemoteSkip(+1);
    }];

    [center.previousTrackCommand addTargetWithHandler:^MPRemoteCommandHandlerStatus(MPRemoteCommandEvent* _Nonnull event) {
        return handleRemoteSkip(-1);
    }];

    [[UIApplication sharedApplication] beginReceivingRemoteControlEvents];
    g_remoteCommandsConfigured = YES;
    NSLog(@"[WanosMusicPlayer] Remote next/previous commands configured (native, bypass Unity)");
}

// 实际处理逻辑（在主线程执行）
static void WanosMusicPlayer_EventCallback_Impl(int state, unsigned long long instanceId) {
    // 切歌后旧 instance 的延迟回调仍会到达；若仍处理会再发 Break/自动下一曲 → 叠音
    if (instanceId != 0 && g_currentInstanceId != 0 && instanceId != g_currentInstanceId) {
        NSLog(@"[WanosMusicPlayer] EventCallback ignored stale instance %llu (current %llu) state=0x%x",
              instanceId, g_currentInstanceId, state);
        return;
    }
    if (state == InstanceState_Playing) {
        // 成功播放后的回调：mode 1/2 时发送 Break，通知本曲不循环，播完后停止并触发 Ended 实现下一曲
        if ((g_playlist.mode == 1 || g_playlist.mode == 2) && !g_shouldStop) {
            NSLog(@"[WanosMusicPlayer] EventCallback Playing -> Sending Break event (mode=%d): %@", g_playlist.mode, g_breakEventName);
            PostEvent(g_objectId, [g_breakEventName UTF8String], nil);
        }
        return;
    }
    if (state != InstanceState_Ended) {
        NSLog(@"[WanosMusicPlayer] EventCallback ignored: state 0x%x != Ended", state);
        return;
    }
    if (g_shouldStop) {
        NSLog(@"[WanosMusicPlayer] EventCallback ignored: g_shouldStop=YES");
        return;
    }
    if (WanosMusicPlayer_IsUserPaused()) {
        // 负一屏暂停是 mute 式：曲终/循环 Ended 不得自动下一曲（否则切走当前曲，点播放会听到下一首）。
        NSLog(@"[WanosMusicPlayer] EventCallback Ended ignored: userPaused");
        g_currentInstanceId = 0;
        return;
    }

    // 磁盘 MusicLists.json 的 currentMusicIndex 可能滞后于 native 自动切歌（Unity/C# 未必每次落盘），
    // 若先 reload 再读索引，会用陈旧索引算「下一首」，最后一首结束后无法正确回到列表首。
    // 故先从内存 g_playlist 捕获当前索引，再决定是否 reload（reload 仅为刷新 indexes/musicLists）。
    int listIdx = g_playlist.currentListIndex;
    int musicIdx = g_playlist.currentMusicIndex;

    // 自动下一曲：若 C# 在 OnBankReady 时已调用 ReloadPlaylistFromFile 更新 g_playlist，则直接使用；否则从文件 reload
    if (!g_playlistReloadedByCSharp) {
        NSString* listPath = nil;
        if (!loadPlaylistFromDefaultLocations(&listPath)) {
            NSLog(@"[WanosMusicPlayer] EventCallback auto-next: failed to reload playlist from %@", listPath);
            return;
        }
    } else {
        g_playlistReloadedByCSharp = NO;
    }

    if (listIdx < 0 || listIdx >= (int)g_playlist.musicLists.size()) return;
    const NativeMusicList& list = g_playlist.musicLists[listIdx];
    if (list.musics.empty()) return;
    if (musicIdx < 0 || musicIdx >= (int)list.musics.size()) return;

    int nextListIdx = listIdx;
    int nextMusicIdx = musicIdx;

    if (g_playlist.mode == 0) {
        nextMusicIdx = musicIdx;
    } else if ((g_playlist.mode == 1 || g_playlist.mode == 2) && g_playlist.indexes.size() > 0) {
        int idxPos = -1;
        for (size_t i = 0; i < g_playlist.indexes.size(); i++) {
            if (g_playlist.indexes[i] == musicIdx) { idxPos = (int)i; break; }
        }
        int nextPos = idxPos >= 0 ? (idxPos + 1) % (int)g_playlist.indexes.size() : 0;
        nextMusicIdx = g_playlist.indexes[nextPos];
    } else {
        nextMusicIdx = (musicIdx + 1) % (int)list.musics.size();
    }

    NSLog(@"[WanosMusicPlayer] EventCallback: auto-next listIdx=%d musicIdx=%d -> nextListIdx=%d nextMusicIdx=%d", listIdx, musicIdx, nextListIdx, nextMusicIdx);
    enqueueNativePlay(nextListIdx, nextMusicIdx);
}

// 传给 PostEvent 的包装：WIA 可能在子线程回调，需 dispatch 到主线程再执行，否则 Unity 回调可能无反应
static void WanosMusicPlayer_EventCallback(EventCallbackInfo* info) {
    if (!info) return;
    int state = info->state;
    unsigned long long instanceId = info->instanceId;
    NSLog(@"[WanosMusicPlayer] EventCallback invoked (sub-thread): instanceId=%llu state=0x%x -> dispatch to main", instanceId, state);
    dispatch_async(dispatch_get_main_queue(), ^{
        WanosMusicPlayer_EventCallback_Impl(state, instanceId);
    });
}

//------------------------------------------------------------------------------
// C API (extern "C" for Unity DllImport __Internal)
//------------------------------------------------------------------------------

extern "C" {

void WanosMusicPlayer_NativeSetPaths(const char* persistentDataPath, const char* streamingAssetsPath) {
    if (persistentDataPath) {
        g_persistentDataPath = [NSString stringWithUTF8String:persistentDataPath];
    }
    if (streamingAssetsPath) {
        g_streamingAssetsPath = [NSString stringWithUTF8String:streamingAssetsPath];
    }
    if (!g_loadedBanks) {
        g_loadedBanks = [NSMutableSet set];
    }
    if (!g_playbackQueue) {
        g_playbackQueue = dispatch_queue_create("com.wanos.musicplayback", DISPATCH_QUEUE_SERIAL);
    }
    dispatch_async(g_playbackQueue, ^{
        ensureControlBankDecoded(0);
        preloadCurrentMusicBank();
    });
}

/// C# Unload/DestroyEngine 后调用：清假「已 Load」缓存，否则重登 PlayMusic 跳过 LoadBank 无声。
void WanosMusicPlayer_InvalidateBankCache(void) {
    invalidateNativeBankLoadCache("csharp_unload");
    // 作废排队中的 decode/play，避免旧 generation 在空引擎上空转
    ++g_playGeneration;
    g_shouldStop = YES;
    g_currentInstanceId = 0;
}

void WanosMusicPlayer_ReloadPlaylistFromFile(void) {
    if (!g_persistentDataPath) return;
    NSString* path = [g_persistentDataPath stringByAppendingPathComponent:@"WanosAudio/MusicLists.json"];
    if (g_playbackQueue) {
        // 禁止 dispatch_sync：Unity 主线程调用时，playback 队列常在 nativePlayTrackAt
        // （decode 轮询 / forceStop / PostEvent）上运行；蓝牙断连时 MiniAudio 又在主线程
        // 通知栈内 ma_device_stop。主线程 sync 等 playback、playback/PostEvent 等音频锁 → 死锁。
        // Bank 连到 OnBankReady 会连发本函数；合并为队列上最多一条，避免堆积。
        if (g_reloadPlaylistQueued) {
            NSLog(@"[WanosMusicPlayer] ReloadPlaylistFromFile coalesced (already queued)");
            return;
        }
        g_reloadPlaylistQueued = YES;
        dispatch_async(g_playbackQueue, ^{
            g_reloadPlaylistQueued = NO;
            CFTimeInterval t0 = CACurrentMediaTime();
            BOOL ok = loadPlaylistFromFile(path);
            BoluoNativeAudioJank_ReportPhase("native_reload_playlist",
                                            (CACurrentMediaTime() - t0) * 1000.0,
                                            ok ? "ok=1" : "ok=0");
            if (ok) {
                g_playlistReloadedByCSharp = YES;
                NSLog(@"[WanosMusicPlayer] ReloadPlaylistFromFile: updated g_playlist (indexes.size=%zu)", g_playlist.indexes.size());
            }
        });
    } else {
        CFTimeInterval t0 = CACurrentMediaTime();
        BOOL ok = loadPlaylistFromFile(path);
        BoluoNativeAudioJank_ReportPhase("native_reload_playlist",
                                        (CACurrentMediaTime() - t0) * 1000.0,
                                        ok ? "ok=1" : "ok=0");
        if (ok) g_playlistReloadedByCSharp = YES;
    }
}

void WanosMusicPlayer_NativeConfigure(unsigned long long objectId, const char* stopEvent, const char* breakEvent, const char* unityObjectName) {
    g_objectId = objectId;
    if (stopEvent) g_stopEventName = [NSString stringWithUTF8String:stopEvent];
    if (breakEvent) g_breakEventName = [NSString stringWithUTF8String:breakEvent];
    if (unityObjectName && *unityObjectName) g_unityObjectName = [NSString stringWithUTF8String:unityObjectName];
}

/// 当前音乐播放对象 id（C# Play 时经 NativeConfigure 传入；0=尚未配置）。
/// 供 BoluoMusicPlayerIOS.mm 护栏静音直接对该对象做引擎级增益，不依赖 Unity SendMessage。
unsigned long long WanosMusicPlayer_GetConfiguredObjectId(void) {
    return g_objectId;
}

void WanosMusicPlayer_NativePlay(int listIndex, int musicIndex) {
    if (!g_persistentDataPath) return;
    enqueueNativePlay(listIndex, musicIndex);
    dispatch_async(dispatch_get_main_queue(), ^{ setupRemoteCommandsIfNeeded(); });
}


void WanosMusicPlayer_NativeStop(void) {
    ++g_playGeneration;  // 作废排队中的 decode/play
    NSLog(@"[WanosMusicPlayer] Sending Stop event: %@", g_stopEventName);
    g_shouldStop = YES;
    RemoveEventStateCallback(g_currentInstanceId);
    g_currentInstanceId = 0;
    PostEvent(g_objectId, [g_stopEventName UTF8String], nil);
    dispatch_async(dispatch_get_main_queue(), ^{
        [MPNowPlayingInfoCenter defaultCenter].nowPlayingInfo = nil;
    });
}

void WanosMusicPlayer_NativeBreak(void) {
    NSLog(@"[WanosMusicPlayer] Sending Break event: %@", g_breakEventName);
    // 只发 Break，不移除当前实例的状态回调：Break 让本曲播完当前循环并触发 Ended，
    // 曲终的自动下一曲依赖该回调。若在此 RemoveEventStateCallback，C# 侧 SetPlayMode
    // (playing 时调用 PostBreakEvent) 会把 auto-next 回调拆掉，导致本曲结束后不再续播。
    // 与 Android/Standalone 的 PostBreakEvent 语义对齐（二者仅 Post Break，不动回调）。
    PostEvent(g_objectId, [g_breakEventName UTF8String], nil);
}

/// obj_id=0，无回调；供 Boluo 与引擎 SetMute 同步派发全局 Pause/Resume 音乐事件
void WanosMusicPlayer_PostGlobalPauseMusic(void) {
    PostEvent(0, "Pause_Music", nil);
}

void WanosMusicPlayer_PostGlobalResumeMusic(void) {
    PostEvent(0, "Resume_Music", nil);
}

void WanosMusicPlayer_SetNowPlayingInfo(const char* title, const char* artist, const char* album) {
    dispatch_async(dispatch_get_main_queue(), ^{
        NSMutableDictionary* info = [NSMutableDictionary dictionary];
        info[MPMediaItemPropertyTitle] = title ? [NSString stringWithUTF8String:title] : @"";
        info[MPMediaItemPropertyArtist] = artist ? [NSString stringWithUTF8String:artist] : @"";
        info[MPMediaItemPropertyAlbumTitle] = album ? [NSString stringWithUTF8String:album] : @"";
        info[MPNowPlayingInfoPropertyPlaybackRate] = @(1.0);
        info[MPNowPlayingInfoPropertyElapsedPlaybackTime] = @(0.0);
        info[MPNowPlayingInfoPropertyIsLiveStream] = @(YES);
        info[MPMediaItemPropertyPlaybackDuration] = @(0.0);
        [MPNowPlayingInfoCenter defaultCenter].nowPlayingInfo = info;
    });
}

void WanosMusicPlayer_ClearNowPlayingInfo(void) {
    dispatch_async(dispatch_get_main_queue(), ^{
        [MPNowPlayingInfoCenter defaultCenter].nowPlayingInfo = nil;
    });
}

}  // extern "C"
