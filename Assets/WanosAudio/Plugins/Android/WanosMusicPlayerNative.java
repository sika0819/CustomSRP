package com.wanos.app.health;

import android.app.ActivityManager;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.media.AudioDeviceInfo;
import android.media.AudioManager;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Looper;
import android.os.SystemClock;
import android.os.Trace;
import android.util.Log;
import com.unity3d.player.UnityPlayer;
import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Android 原生音乐播放器，与 iOS WanosMusicPlayer.mm 对齐。
 * 使用 WIAEngineJNI 桥接能力实现列表、循环、自动下一曲，绕过 Unity 内逻辑。
 * 通知栏/锁屏/负一屏上一曲下一曲走 skipFromMediaControl（bump playGeneration）。
 */
public class WanosMusicPlayerNative {

    private static final String TAG = "WanosMusicPlayer";
    private static final int InstanceState_Playing = 0x03e3;
    private static final int InstanceState_Ended = 0x03e5;
    private static final int kEF_SUCCESS = 0;
    /** getPlaybackState() 返回值：2 表示 Started（正在播放）。 */
    private static final int PlaybackState_Started = 2;
    /** 恢复播放时重置的默认输出设备，与 iOS kBoluoDefaultDeviceJson 对齐。 */
    private static final String DEFAULT_DEVICE_JSON = "{\"play_layout\":0,\"device_id\":\"default\"}";
    /** 对齐 iOS kBoluoGuardedMuteDelaySeconds：同步压增益后再 SetMute，消切微信电流音。 */
    private static final long GUARDED_MUTE_DELAY_MS = 80L;
    private static final long UNMUTE_RAMP_STEP_MS = 50L;

    private String persistentDataPath = "";
    private String streamingAssetsPath = "";
    /**
     * 与 C# {@code WanosInteraction.MusicObjectId} 对齐。
     * Play / Break_Music / Stop_Music 必须同一 game object，否则曲终不 Ended、后台切歌无声。
     */
    private static final long DEFAULT_MUSIC_OBJECT_ID = 101L;
    private long objectId = 0;
    private String stopEventName = "Stop_Music";
    private String breakEventName = "Break_Music";
    private String unityObjectName = "WanosMusicPlayer";

    /** configure 未设 objectId 时回落 101，避免传输计划起播落在 0。 */
    private long musicObjectId() {
        return objectId != 0 ? objectId : DEFAULT_MUSIC_OBJECT_ID;
    }

    private volatile boolean shouldStop = false;
    /** 用户经通知栏/锁屏主动暂停：置位后焦点回归等自动路径不得擅自续播，与 iOS userManuallyPaused 对齐。 */
    private volatile boolean userPaused = false;
    /**
     * 抢焦 / duck 系统挂起（非用户暂停）。
     * 负一屏 isPlaying=false，但回焦走自动软续；Resync 经 suspended 暴露给 Unity Gate。
     */
    private volatile boolean systemSuspended = false;
    /** 系统更新弹窗盖住游戏：没有外来媒体，不要 Pause，也不要抢回焦点。 */
    private volatile boolean systemDialogCover = false;
    private long currentInstanceId = 0;
    /**
     * Unity MusicPlaylist PostEvent 起播的 BGM voice。
     * CustomSRP 不走 nativePlayTrackAt，currentInstanceId 常为 0；靠此识别后台曲终与 keepPlaying 活实例。
     */
    private volatile long unityMusicInstanceId = 0;
    /**
     * 后台曲终但传输计划尚未就绪时暂存；PublishPlan / keepPlayingObserve 再切。
     * 0 = 无 pending。
     */
    private volatile long pendingUnityMusicEndedId = 0;
    /** forceStop 刚拆掉的 instance：Ended 回调在 sleep 窗口内不得再自动下一曲盖掉负一屏 skip。 */
    private volatile long lastStoppedInstanceId = 0;
    /** 与 iOS g_playGeneration 对齐：切歌/停止时自增，decode 等待期间若已变则放弃 Post。 */
    private volatile long playGeneration = 0;
    /**
     * 播放/暂停意图代际。只拦过期的续播和解 mute，不打断切歌改下标。
     * 切歌睡醒后看 userPaused 决定出不出声。
     */
    private volatile long audibleCommandGeneration = 0;
    /** 前后台快切时自增，取消 playbackExecutor 上已过期的 resume。 */
    private volatile long foregroundResumeGeneration = 0;
    /** Unity OnApplicationPause 同步：App 是否在后台。 */
    private volatile boolean appInBackground = false;
    /** Unity 已明确回到前台。进程 importance 滞后时仍按前台处理，避免回波萝被拒绝出声。 */
    private volatile boolean unityClaimsForeground = false;
    /** Unity 已直接解 mute。启动期 PlaybackState 尚未 Started 时，丢焦也要能停。 */
    private volatile boolean unityMarkedAudible = false;
    /** 曾在真正前台出过声。启动期（false）人已经切走则禁止再解 mute。 */
    private volatile boolean startupAudibleCommitted = false;
    /** 进岛已推过播放中占位卡。切走且还没真正出声时要收起，别占负一屏。 */
    private volatile boolean foregroundCardPublished = false;
    /** 与 iOS backgroundPlayDisable 对齐：true 时后台应静音。 */
    private volatile boolean backgroundPlayDisabled = false;
    private long lastReclaimAttemptElapsedMs = 0L;
    private static final long RECLAIM_DEBOUNCE_MS = 400L;
    /**
     * 负一屏播放/暂停/切歌。连点只落地最后一次；切歌作废尚未落地的暂停。
     * 曲目下标仍由传输计划 / Unity 持有，这里的 trackCount 只服务状态机自身。
     */
    private final BoluoNowPlayingRemoteSession remoteCommands =
            new BoluoNowPlayingRemoteSession(1, false, false);
    /** 清到点闩时递增，丢掉还在 playbackExecutor 里的 pauseForTimerEnd。 */
    private volatile int timerEndGeneration;
    private final Runnable flushMediaPlayPauseRunnable = this::flushPendingMediaPlayPause;
    /** getPlaybackState 在 Unity pause/resume 间可能短暂非 Started，先 unmute 再确认。 */
    private static final long RESUME_STATE_FLICKER_WAIT_MS = 80L;
    private final Set<String> loadedBanks = new HashSet<>();
    /** 上一首成功起播的 music bank；切不同 bank 后卸载以压内存。 */
    private volatile String previousMusicBankName = null;
    /**
     * 标准版切歌同帧 UnloadBank 会打 WIA 锁并触发 NativeAlloc GC（日志 ~110ms），听感/画面一卡。
     * 4K（BoluoDeviceProfile.isUltra4K）仍立即卸，避免双 bank 占内存。
     */
    private static final long PREVIOUS_MUSIC_BANK_UNLOAD_DEFER_MS = 2000L;
    /**
     * 冷启动 / 丢焦后为 true：unmute 必须爬坡，禁止 gain=1 硬切。
     * 岛内已在播时切预设/切歌保持 false，禁止再把 BGM 压到 5% 爬 150ms（标准版听感卡一帧）。
     */
    private volatile boolean musicEngineMuted = true;
    /**
     * focus_pause 已发 Pause_Music+Pause_ALL：回焦不能只 unmute，否则音效停在 Pause。
     * unity_pause / unity_resume 护栏 mute 不置此旗（未 Pause 内容声）。
     */
    private volatile boolean voicesPausedForFocusLoss = false;
    /**
     * 取证开关，默认关，关时与改前同一条路径。
     * C：已出声则不 setMute(false)、不清 musicEngineMuted。
     * D：native_play 且已出声时从 0.05 爬坡，不直接满增益。两者都开即 E。
     */
    static volatile boolean crackleFixCSkipRedundantUnmute = false;
    static volatile boolean crackleFixDLowGainNativePlay = false;
    private static final String PROBE = "CWPA-crackleProbe";
    private static final long GAIN_PROBE_MIN_INTERVAL_MS = 400L;
    /** JNI 无 getMute，影子只记录上次 Java 写入，用来标 noopMute。 */
    private volatile Boolean shadowMute = null;
    private volatile float shadowGain = Float.NaN;
    private volatile boolean shadowGainEnable;
    private volatile boolean shadowGainValid;
    private long lastGainProbeElapsedMs;
    private volatile String pendingUnloadBank = null;
    private Runnable pendingUnloadRunnable = null;
    private final ExecutorService playbackExecutor = Executors.newSingleThreadExecutor();
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    /** 后台续播看门狗：Unity pause 后主线程可能被 throttle，独立线程保证保活观察不断。 */
    private final Handler keepPlayingHandler;

    private static class NativeMusic {
        String bankName;
        String bankGuid;
        String eventName;
        String musicName;
    }

    private static class NativeMusicList {
        String musicsName;
        List<NativeMusic> musics = new ArrayList<>();
    }

    private static class NativePlaylist {
        int mode;  // 0=SingleLoop 1=ListLoop 2=ListShuffle
        int currentListIndex;
        int currentMusicIndex;
        List<Integer> indexes = new ArrayList<>();
        List<NativeMusicList> musicLists = new ArrayList<>();
    }

    private volatile NativePlaylist playlist = new NativePlaylist();
    private volatile boolean playlistReloadedByCSharp = false;
    /** 护栏 mute/unmute 世代：新决策作废已排队的 dip/爬坡。 */
    private volatile long guardedMuteGeneration = 0;
    /**
     * Resume_ALL / Resume_Music 短窗口只发一次。
     * 回前台 Unity 与焦点回调都会补；第二发打在已 Started 的曲子上会 Ended，听感停一拍。
     */
    private final Object voiceResumeLock = new Object();
    private long lastResumeAllElapsedMs = 0L;
    private long lastResumeMusicElapsedMs = 0L;
    private static final long VOICE_RESUME_COALESCE_MS = 350L;
    /**
     * 后台丢焦时 Unity 收不到消息；禁止 UnitySendMessage 积压。
     * 回前台若仍挂起且尚未 Restored，最多补一次 Interrupted（通常直接 Restored）。
     */
    private volatile boolean pendingFocusPauseUi = false;
    private volatile boolean pendingFocusPauseUiCompetingLong = false;
    /** 后台已经恢复出声，但 Unity 暂停着，等回前台再补一条 Restored。 */
    private volatile boolean pendingUnityAudioRestored = false;
    /** 随后又暂停：作废还没发出的 Restored，避免回前台把打断刷成播放中。 */
    private volatile int unityRestoreGeneration = 0;

    // ---------------------------------------------------------------------------
    // 后台传输计划（Unity 在后台被 pause，UnitySendMessage 无法被处理）
    //
    // 宿主 MainActivity.onStop 会 UnityPlayer.onPause → Unity 脚本循环停摆，
    // 负一屏/通知栏的播放控制若只转发给 Unity，后台点了没反应。
    // 因此由 C# 前台把「当前曲 + 上下曲 + bank 路径」推给原生，
    // 后台由原生直接驱动 WIAEngine（Pause/Resume/Stop_Music + Play_* 事件）。
    // ---------------------------------------------------------------------------

    /** 传输计划里的一首曲目。 */
    private static final class TransportTrack {
        String name = "";
        String eventName = "";
        String bankName = "";
        String bankGuid = "";
        String bankPath = "";
        boolean loaded;
    }

    /** C# 推送的曲目列表（当前专辑内、bank 已就绪的曲目，按播放顺序）。 */
    private volatile List<TransportTrack> transportTracks = Collections.emptyList();
    /** 播放模式：1=单曲循环 2=列表循环 3=随机（与 C# MusicPlayMode 对齐）。 */
    private volatile int transportMode = 1;
    /** 当前曲目在 <see cref="transportTracks"/> 中的下标，-1=未知。 */
    private volatile int transportCurrent = -1;
    /** 计划是否可用（列表非空且当前下标合法）。 */
    private volatile boolean transportPlanValid = false;

    /** 音频焦点管理器：其它 App 抢占/释放焦点时暂停并自动恢复（对齐 iOS PA-904 声音失焦未恢复）。 */
    private final WanosAudioFocusManager audioFocusManager =
            new WanosAudioFocusManager(new WanosAudioFocusManager.Callback() {
                @Override
                public void onFocusPause() {
                    systemDialogCover = false;
                    // 调用方已确认是真丢焦 / 外来音源。后台续播不能再吞掉：
                    // 切到抖音、QQ 音乐、微信语音时必须停，否则启动期出声会一直叠播。
                    // 立刻拆掉 keepPlaying 自愈，避免 Pause 后被 L0 Resume_ALL 把音效拉回来。
                    cancelKeepPlayingAliveWatch();
                    // 清 coalesce：回焦 Resume 不得被上一轮残留窗口吞掉。
                    resetVoiceResumeCoalesce();
                    applyGuardedSystemMute("focus_pause");
                    // 停声与写卡同栈：SystemSuspended → 负一屏立即 PAUSED（勿等 Unity Publish）。
                    // 不抬 userPaused：回焦仍走自动软续，不是用户暂停。
                    // 焦点回调已在主线程，当场写卡。再 post 会被抖音抢走会话，按钮停在播放图标。
                    systemSuspended = true;
                    invalidatePendingUnityRestore("focus_pause");
                    publishFocusPausedCard();
                }

                @Override
                public void onFocusPauseUi(boolean competingLong) {
                    // Unity 已 pause 时 UnitySendMessage 会积压；回前台一次性灌入
                    // OnCompetingAudioMuted，把刚 Resume 的音乐再 Pause，且 coalesce 吃掉第二次 Resume。
                    // 后台只改原生态；回前台由 OnSystemAudioRestored / Sync 对账。
                    if (effectivelyInBackground()) {
                        Log.i(TAG, "[BoluoAudioFg] defer focusPauseUi bg competingLong="
                                + competingLong);
                        pendingFocusPauseUiCompetingLong = competingLong;
                        pendingFocusPauseUi = true;
                        return;
                    }
                    pendingFocusPauseUi = false;
                    final boolean longUi = competingLong;
                    mainHandler.post(() -> {
                        try {
                            String method = longUi ? "OnCompetingAudioMuted" : "OnSystemAudioMuted";
                            UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid", method, "");
                        } catch (Exception e) {
                            Log.w(TAG, "onFocusPauseUi failed: " + e.getMessage());
                        }
                    });
                }

                @Override
                public void onFocusResume() {
                    // Unity 通知必须等 Resume_Music 落地后再发。
                    // 先发 OnSystemAudioRestored 时，C# 会把音乐闩成「已在播」并只拉起音效。
                    pendingFocusPauseUi = false;
                    resumeAfterFocusRegained();
                }

                @Override
                public void onFocusDuck() {
                    // 导航/短信：曲不停。必须在焦点线程同步写，不能再丢进 playbackExecutor，
                    // 否则护栏先把增益打到 0，这记 0.4 后到，切 QQ 就是先降再静。
                    Log.i(TAG, "[BoluoAudioFg] onFocusDuck gain≈0.4 keep playing");
                    applyMusicGainSafe(musicObjectId(), 0.4f, true);
                }

                @Override
                public void onAmbiguousDuckDip() {
                    Log.i(TAG, "[BoluoAudioFg] ambiguous CAN_DUCK dip gain=0 (no pause)");
                    applyMusicGainSafe(musicObjectId(), 0f, true);
                }

                @Override
                public void onKeepGraphRestoreGain() {
                    Log.i(TAG, "[BoluoAudioFg] keep_graph restore gain=1");
                    BoluoAudioStutterProbe.noteSwitchClass("keep_graph", "restore_gain");
                    applyMusicGainSafe(musicObjectId(), 1f, false);
                }

                @Override
                public void onFocusUnduck() {
                    Log.i(TAG, "[BoluoAudioFg] onFocusUnduck ramp to full");
                    final long obj = musicObjectId();
                    playbackExecutor.execute(() -> {
                        applyMusicGainSafe(obj, 0.55f, true);
                        try {
                            Thread.sleep(UNMUTE_RAMP_STEP_MS);
                        } catch (InterruptedException e) {
                            Thread.currentThread().interrupt();
                            return;
                        }
                        applyMusicGainSafe(obj, 1f, false);
                    });
                    mainHandler.post(() -> {
                        try {
                            UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid",
                                    "OnAudioFocusUnduck", "");
                        } catch (Exception e) {
                            Log.w(TAG, "OnAudioFocusUnduck failed: " + e.getMessage());
                        }
                    });
                }

                @Override
                public boolean isEnginePlaying() {
                    // 用户主动暂停时视为「不在播」，避免丢焦后自动恢复覆盖用户的暂停意图。
                    // 启动期 PlaybackState 可能还没到 Started，但 Unity 已经解 mute：也算在响。
                    if (shouldStop || userPaused) {
                        return false;
                    }
                    return WIAEngineJNI.getPlaybackState() == PlaybackState_Started
                            || !musicEngineMuted
                            || unityMarkedAudible;
                }

                @Override
                public boolean shouldSuppressTransientPauseInBackground() {
                    return WanosMusicPlayerNative.this.shouldSuppressTransientPauseInBackground();
                }

                @Override
                public boolean allowsUninterruptedBackgroundPlayback() {
                    return !backgroundPlayDisabled && !userPaused && !shouldStop;
                }

                @Override
                public boolean isAppInBackground() {
                    return effectivelyInBackground();
                }

                @Override
                public boolean hasLeftForeground() {
                    return appInBackground || !isProcessInForeground();
                }

                @Override
                public boolean isPlaybackGraphStarted() {
                    return WIAEngineJNI.getPlaybackState() == PlaybackState_Started;
                }

                @Override
                public void onStartupCardYield() {
                    yieldStartupPlaceholderCard();
                }

                @Override
                public void onKeepPlayingTrackGone() {
                    // 对齐线上：拆轨后必须能恢复。走 keepPlaying L0/L1（L1=reset+replay）。
                    Log.w(TAG, "[CWPA-audio] self_gone while keep playing → recover L0/L1");
                    playbackExecutor.execute(
                            () -> keepPlayingRecoverOnExecutor(foregroundResumeGeneration));
                }

                @Override
                public void onSystemDialogCover() {
                    // 仅真系统弹窗路径。搜索栏 no-op 不得再调这里。
                    systemDialogCover = true;
                    if (effectivelyInBackground()) {
                        Log.i(TAG, "[BoluoAudioFg] defer systemDialogCover bg");
                        return;
                    }
                    mainHandler.post(() -> {
                        try {
                            UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid",
                                    "OnSystemDialogCover", "");
                        } catch (Exception e) {
                            Log.w(TAG, "OnSystemDialogCover failed: " + e.getMessage());
                        }
                    });
                }

                @Override
                public void onFocusRequestFailed() {
                    mainHandler.post(() -> {
                        try {
                            UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid",
                                    "OnAudioFocusRequestFailed", "");
                        } catch (Exception e) {
                            Log.w(TAG, "OnAudioFocusRequestFailed failed: " + e.getMessage());
                        }
                    });
                }
            });

    private static WanosMusicPlayerNative sInstance;
    private BroadcastReceiver becomingNoisyReceiver;
    private boolean becomingNoisyRegistered;

    public static WanosMusicPlayerNative getInstance() {
        if (sInstance == null) sInstance = new WanosMusicPlayerNative();
        return sInstance;
    }

    private WanosMusicPlayerNative() {
        HandlerThread keepAliveThread = new HandlerThread("BoluoKeepPlaying");
        keepAliveThread.start();
        keepPlayingHandler = new Handler(keepAliveThread.getLooper());
        WIAEngineJNI.setEventCallbackListener(this::onEventCallback);
        // 注册通知栏/锁屏媒体传输控制回调，使系统媒体控件的播放/暂停/停止真正驱动引擎。
        WIAEngineJNI.setTransportControlListener(new WIAEngineJNI.TransportControlListener() {
            @Override
            public void onTransportPlay() {
                // CustomSRP：曲目权威在 Unity MusicPlaylist；原生只转发 + coalesce。
                // 后台（Unity 已 pause）走原生：UnitySendMessage 不会被处理。
                if (handleTransportInBackground(false)) return;
                scheduleMediaPlayPause(false);
            }

            @Override
            public void onTransportPause() {
                if (handleTransportInBackground(true)) return;
                scheduleMediaPlayPause(true);
            }

            @Override
            public void onTransportStop() {
                if (handleTransportInBackground(true)) return;
                scheduleMediaPlayPause(true);
            }

            @Override
            public void onTransportSkipToNext() {
                if (handleTransportSkipInBackground(+1)) return;
                skipToUnity(+1);
            }

            @Override
            public void onTransportSkipToPrevious() {
                if (handleTransportSkipInBackground(-1)) return;
                skipToUnity(-1);
            }
        });
        registerBecomingNoisyReceiver();
        // MEDIA 锚点写失败/停写 = HAL 拆轨；立刻走 L1，禁止再调 getPlaybackHeadPosition。
        BoluoKeepPlayingMediaAnchor.setOnWriteDead(() -> {
            if (!shouldSuppressTransientPauseInBackground()) {
                return;
            }
            Log.w(TAG, "[CWPA-audio] MediaAnchor write dead → L1 reset+replay");
            keepPlayingAnchorStalled = true;
            playbackExecutor.execute(
                    () -> keepPlayingRecoverOnExecutor(foregroundResumeGeneration));
        });
    }

    /**
     * 产品矩阵：蓝牙/耳机断开 → 停音乐+音效，路由落到扬声器后后台自恢复
     * （对齐 iOS RouteDisconnected）。有外来 MEDIA 时不抢，等回前台。
     */
    private void registerBecomingNoisyReceiver() {
        if (becomingNoisyRegistered) return;
        becomingNoisyReceiver = new BroadcastReceiver() {
            @Override
            public void onReceive(Context context, Intent intent) {
                if (intent == null || !AudioManager.ACTION_AUDIO_BECOMING_NOISY.equals(intent.getAction())) {
                    return;
                }
                if (userPaused || shouldStop) {
                    Log.i(TAG, "AUDIO_BECOMING_NOISY skip userPaused/shouldStop");
                    return;
                }
                Log.i(TAG, "AUDIO_BECOMING_NOISY: pause music+sfx, schedule BG resume");
                cancelKeepPlayingAliveWatch();
                audioFocusManager.onBecomingNoisy();
            }
        };
        try {
            Context ctx = resolveAppContext();
            if (ctx == null) return;
            IntentFilter filter = new IntentFilter(AudioManager.ACTION_AUDIO_BECOMING_NOISY);
            if (Build.VERSION.SDK_INT >= 33) {
                ctx.registerReceiver(becomingNoisyReceiver, filter, Context.RECEIVER_NOT_EXPORTED);
            } else {
                ctx.registerReceiver(becomingNoisyReceiver, filter);
            }
            becomingNoisyRegistered = true;
        } catch (Exception e) {
            Log.w(TAG, "register BECOMING_NOISY failed: " + e.getMessage());
        }
    }

    private Context resolveAppContext() {
        try {
            if (UnityPlayer.currentActivity != null) {
                return UnityPlayer.currentActivity.getApplicationContext();
            }
        } catch (Throwable ignored) {}
        try {
            Object app = Class.forName("android.app.ActivityThread")
                    .getMethod("currentApplication")
                    .invoke(null);
            if (app instanceof Context) {
                return ((Context) app).getApplicationContext();
            }
        } catch (Exception ignored) {}
        return null;
    }

    private void startMediaPlaybackForegroundService() {
        Context ctx = resolveAppContext();
        if (ctx != null) {
            WanosMediaPlaybackForegroundService.start(ctx);
        }
    }

    private void stopMediaPlaybackForegroundService() {
        Context ctx = resolveAppContext();
        if (ctx != null) {
            WanosMediaPlaybackForegroundService.stop(ctx);
        }
    }

    /** 提前设置路径（Awake 调用），Android streamingAssetsPath 为 jar: URL 时无法用 File 访问 */
    public void setPaths(String persistentDataPath, String streamingAssetsPath) {
        this.persistentDataPath = persistentDataPath != null ? persistentDataPath : "";
        this.streamingAssetsPath = streamingAssetsPath != null ? streamingAssetsPath : "";
        Log.i(TAG, "setPaths: persistent=" + this.persistentDataPath + " streamingUsable=" + isStreamingPathUsable());
    }

    /** 配置路径与事件名，由 C# 在 Play 前调用 */
    public void configure(String persistentDataPath, String streamingAssetsPath,
                          long objectId, String stopEvent, String breakEvent, String unityObjectName) {
        this.persistentDataPath = persistentDataPath != null ? persistentDataPath : "";
        this.streamingAssetsPath = streamingAssetsPath != null ? streamingAssetsPath : "";
        this.objectId = objectId;
        this.stopEventName = (stopEvent != null && !stopEvent.isEmpty()) ? stopEvent : "Stop_Music";
        this.breakEventName = (breakEvent != null && !breakEvent.isEmpty()) ? breakEvent : "Break_Music";
        this.unityObjectName = (unityObjectName != null && !unityObjectName.isEmpty()) ? unityObjectName : "WanosMusicPlayer";
    }

    /** 播放指定曲目，由 C# Play 调用。JSON 已由 C# 保存 */
    public void play(int listIndex, int musicIndex) {
        Log.i(TAG, "play(list=" + listIndex + ", music=" + musicIndex + ") objectId=" + objectId);
        // 同曲已在播：勿 bump generation / forceStop（进岛重复 Play → 连环 Stop_Music）
        // 但丢焦 mute 后同曲强制播放仍须 reclaim，否则 early-return 会跳过焦点门禁导致叠播/无声。
        if (currentInstanceId != 0 && !shouldStop
                && playlist != null
                && playlist.currentListIndex == listIndex
                && playlist.currentMusicIndex == musicIndex) {
            Log.i(TAG, "play skip redundant same track list=" + listIndex
                    + " music=" + musicIndex + " — ensure focus reclaim");
            playbackExecutor.execute(this::reclaimFocusForForcedPlaySameTrack);
            return;
        }
        final long gen = ++playGeneration;
        playbackExecutor.execute(() -> nativePlayTrackAt(listIndex, musicIndex, gen));
    }

    /**
     * Unity {@code OnApplicationPause} 同步宿主前后台与后台播策略。
     * 进后台时 bump generation，取消在途 resume，避免快切把 mute/unmute 叠在一起。
     */
    public void notifyAppLifecycle(boolean inBackground, boolean backgroundPlayDisabled) {
        this.appInBackground = inBackground;
        this.unityClaimsForeground = !inBackground;
        this.backgroundPlayDisabled = backgroundPlayDisabled;
        if (inBackground) {
            // 不在这里清 systemDialogCover。Unity 进后台可能比焦点回调早，
            // 清掉之后读到 false 就会 Yield。Unity 读到 true 后自己清。
            foregroundResumeGeneration++;
            yieldStartupPlaceholderCard();
            BoluoAudioStutterProbe.onEnterBackground();
            // Unity pause / 生命周期进后台：武装 self_gone 抑制，避免 AAudio 瞬时拆轨立刻 L0/L1。
            if (!backgroundPlayDisabled && !userPaused && !shouldStop) {
                audioFocusManager.noteUnityPauseEdge();
            }
            Log.i(TAG, "notifyAppLifecycle background gen=" + foregroundResumeGeneration
                    + " backgroundPlayDisabled=" + backgroundPlayDisabled
                    + " trace=" + BoluoAudioStutterProbe.traceId());
            if (!backgroundPlayDisabled && !userPaused && !shouldStop) {
                armKeepPlayingAliveWatch();
            }
        } else {
            systemDialogCover = false;
            cancelKeepPlayingAliveWatch();
            BoluoAudioStutterProbe.onEnterForeground();
            refreshSelfMixerAsync();
            Log.i(TAG, "notifyAppLifecycle foreground backgroundPlayDisabled="
                    + backgroundPlayDisabled
                    + " trace=" + BoluoAudioStutterProbe.traceId());
            // 对齐 iOS becomeActive：宿主壳里 Unity 焦点回调不一定到，负一屏必须在原生回前台时重推。
            // 切走后系统把卡片给了抖音 / QQ，暂停态推送不会把 MediaStyle 拉回来。
            reassertNowPlayingOnForeground();
        }
    }

    /**
     * 回到前台时按「播放中」重推负一屏。
     * CustomSRP 的 Java playlist 是空的，不能走 {@link #updateNowPlayingForCurrentTrack}（那条会直接 return）。
     */
    private void reassertNowPlayingOnForeground() {
        if (shouldStop || backgroundPlayDisabled) {
            Log.i(TAG, "reassertNowPlaying skip shouldStop=" + shouldStop
                    + " bgDisable=" + backgroundPlayDisabled);
            return;
        }
        if (!WIAEngineJNI.isPlaybackActive()) {
            Log.i(TAG, "reassertNowPlaying skip: playback inactive");
            return;
        }
        // 按当前 Gate 重推：用户暂停 / 系统挂起保持 PAUSED，禁止 !userPaused → 强推 PLAYING。
        boolean cardPlaying = !userPaused && !systemSuspended;
        Log.i(TAG, "reassertNowPlaying foreground playing=" + cardPlaying
                + " userPaused=" + userPaused + " systemSuspended=" + systemSuspended);
        updateTransportNowPlaying(cardPlaying);
        if (cardPlaying) {
            foregroundCardPublished = true;
        }
        audioFocusManager.ensurePlaybackMonitor();
    }

    /**
     * UnityPlayer.onPause 之前：仅禁后台播才压音。续播中 mute = HOME 停顿。
     */
    public void silenceBeforeUnityPause() {
        if (userPaused || shouldStop) {
            Log.i(TAG, "silenceBeforeUnityPause skip userPaused=" + userPaused
                    + " shouldStop=" + shouldStop);
            return;
        }
        if (!backgroundPlayDisabled) {
            appInBackground = true;
            cancelDeferredMusicBankUnload();
            Log.i(TAG, "silenceBeforeUnityPause skip (keep playing, anchor resident)");
            return;
        }
        applyGuardedSystemMute("unity_pause");
    }

    /**
     * Unity pause 之后：允许后台播则保持当前增益（禁止 mute/unmute 爬坡，否则 HOME 停顿）。
     * 只补标后台 + 拉 FGS，并武装 self_gone 看门狗（MTK 约 11s 拆 GAME 轨）。
     * 禁后台播时本方法不应被用来恢复出声。
     * <p>
     * 禁止在「其它播放器已抢焦点」时解 mute：focus_pause 先把 Music gain 压到 0 再 SetMute，
     * 解 mute 后 BGM 仍静音，但 Pause_ALL 盖不住的 3D 声景会继续响
     * （启动期切 QQ 音乐 / 抖音的典型症状）。
     */
    public void restoreAudibleAfterUnityPause() {
        crackleProbe("restoreAudible.enter", null, true);
        appInBackground = true;
        if (backgroundPlayDisabled || userPaused || shouldStop) {
            Log.i(TAG, "restoreAudibleAfterUnityPause skip bgPlayDisabled="
                    + backgroundPlayDisabled + " userPaused=" + userPaused
                    + " shouldStop=" + shouldStop);
            crackleProbe("restoreAudible.exit", "branch=skip", false);
            cancelKeepPlayingAliveWatch();
            return;
        }
        startMediaPlaybackForegroundService();
        cancelDeferredMusicBankUnload();

        boolean foreign = audioFocusManager.hasForeignMedia();
        boolean lostFocus = voicesPausedForFocusLoss
                || foreign
                || (musicEngineMuted && !audioFocusManager.hasAudioFocus());
        if (lostFocus) {
            // 保持 mute；看门狗若 Resume_ALL 会把声景又拉起来。
            Log.i(TAG, "restoreAudibleAfterUnityPause keep muted focusLoss="
                    + voicesPausedForFocusLoss
                    + " foreign=" + foreign
                    + " muted=" + musicEngineMuted
                    + " hasFocus=" + audioFocusManager.hasAudioFocus());
            cancelKeepPlayingAliveWatch();
            crackleProbe("restoreAudible.exit", "branch=focusLoss", false);
            return;
        }

        // CustomSRP 起播走 Unity PostEvent，currentInstanceId 常为 0；
        // Unity pause 还会让 getPlaybackState 短暂离开 Started。
        // 这两条都会让旧逻辑每次 HOME 都 Resume_ALL，把正在响的曲子打成 Ended、雨声断一下。
        // 续播中禁止 Resume_ALL。真死机由 keepPlaying 看门狗 L0/L1 补，不在切后台这一下重开。
        if (musicEngineMuted) {
            musicEngineMuted = false;
            probeSetMute(false);
            Log.i(TAG, "restoreAudibleAfterUnityPause keep playing (unmute only, skip Resume_ALL)");
        } else {
            Log.i(TAG, "restoreAudibleAfterUnityPause keep playing (already audible, skip mute/Resume_ALL)");
        }
        // HOST pause 常早于 Unity OnApplicationPause：先武装边沿，挡住 pause 后瞬时 self_gone。
        audioFocusManager.noteUnityPauseEdge();
        armKeepPlayingAliveWatch();
        crackleProbe("restoreAudible.exit", "branch=keepPlaying", false);
    }

    /**
     * UnityPlayer.onResume 之前：仅禁后台播/曾 mute 时才压音。
     * 后台一直在播则禁止 mute，否则切回前台会停顿。
     */
    public void prepareForUnityResume() {
        crackleProbe("prepareForUnityResume.enter", null, true);
        if (userPaused || shouldStop) {
            Log.i(TAG, "prepareForUnityResume skip userPaused=" + userPaused
                    + " shouldStop=" + shouldStop);
            crackleProbe("prepareForUnityResume.exit", "branch=userStop", false);
            return;
        }
        if (!backgroundPlayDisabled) {
            appInBackground = false;
            foregroundResumeGeneration++;
            refreshSelfMixerAsync();
            Log.i(TAG, "prepareForUnityResume skip mute (kept playing)");
            crackleProbe("prepareForUnityResume.exit", "branch=keptPlaying", false);
            return;
        }
        foregroundResumeGeneration++;
        applyGuardedSystemMute("unity_resume");
        crackleProbe("prepareForUnityResume.exit", "branch=guardedMute", false);
    }

    /**
     * Unity onResume 之后：后台续播中禁止 unmute 爬坡。
     */
    public void restoreAudibleAfterUnityResume() {
        if (userPaused || shouldStop) {
            Log.i(TAG, "restoreAudibleAfterUnityResume skip userPaused=" + userPaused
                    + " shouldStop=" + shouldStop);
            return;
        }
        if (!backgroundPlayDisabled) {
            appInBackground = false;
            Log.i(TAG, "restoreAudibleAfterUnityResume skip ramp (kept playing)");
            return;
        }
        final long gen = ++foregroundResumeGeneration;
        playbackExecutor.execute(() -> {
            try {
                Thread.sleep(150L);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return;
            }
            if (gen != foregroundResumeGeneration || userPaused || shouldStop) {
                Log.i(TAG, "restoreAudibleAfterUnityResume superseded gen=" + gen);
                return;
            }
            runGuardedUnmuteOnCurrentThread("unity_resume");
            // 冷启动尚无播放对象：发 Unduck 会清掉排队 Duck，Music_Volume 4→80 滋啦。
            if (objectId == 0) {
                Log.i(TAG, "restoreAudibleAfterUnityResume skip unduck notify obj=0 gen=" + gen);
                return;
            }
            mainHandler.post(() -> {
                try {
                    UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid",
                            "OnAudioFocusUnduck", "");
                } catch (Exception e) {
                    Log.w(TAG, "resume unduck notify failed: " + e.getMessage());
                }
            });
            Log.i(TAG, "restoreAudibleAfterUnityResume done gen=" + gen);
        });
    }

    /** 允许后台播且已在后台：忽略瞬时丢焦 mute，对齐 iOS 不在后台 transition 上硬 mute。 */
    private boolean shouldSuppressTransientPauseInBackground() {
        return appInBackground && !backgroundPlayDisabled && !userPaused && !shouldStop;
    }

    /**
     * Unity 收到 Interrupted 时：原生是否仍在丢焦 yield。
     * false = 已 Restored 的陈旧 CompetingMuted，禁止再 Pause。
     */
    public boolean isFocusYieldLatched() {
        return voicesPausedForFocusLoss
                || systemSuspended
                || (musicEngineMuted && audioFocusManager.isPendingResume());
    }

    /**
     * Unity / 宿主回前台：是否仍因丢焦护栏停着（须 Resume_ALL + Resume_Music + unmute）。
     * C# <c>CanKeepAudibleAcrossLifecycle</c> 用来避免误跳过完整恢复。
     */
    public boolean needsForegroundVoiceRestore() {
        return voicesPausedForFocusLoss || musicEngineMuted;
    }

    /**
     * 微信语音还在进行：保持暂停，等系统把焦点还回来再自动续。
     * QQ / B 站抢占：回到波萝保持暂停（{@code return_show_interrupt}），滑动或播放键才续。
     */
    private BoluoWeChatVoiceResumePolicy.Plan weChatVoiceReturnPlan(boolean returnedToBoluo) {
        return BoluoWeChatVoiceResumePolicy.plan(new BoluoWeChatVoiceResumePolicy.Input(
                userPaused,
                shouldStop,
                returnedToBoluo,
                voicesPausedForFocusLoss,
                musicEngineMuted,
                audioFocusManager.isPendingResume(),
                audioFocusManager.isWeChatVoiceSessionActive(),
                audioFocusManager.sawCompetingMediaEpisode()));
    }

    /**
     * QQ / B 站抢占后回到波萝：保持暂停，等滑动或播放键再续。
     * 微信语音不走这里，语音结束由焦点 GAIN 自动续。
     */
    public boolean shouldHoldWeChatInterruptOnReturn() {
        return "return_show_interrupt".equals(weChatVoiceReturnPlan(true).reason);
    }

    /**
     * Unity {@code OnApplicationFocus(true)} → 回前台强制抢焦点（对齐 iOS becomeActive reclaim）。
     * 未 userPaused 且有恢复意图时发起；成败以系统 {@code requestAudioFocus} 为准。
     * 丢焦曾 Pause 音乐/声景时，即使已持焦 / reclaim NOOP，也要补 Resume + unmute。
     * {@code userGesture} 为真是滑动 / 播放键：微信打断也要出声。
     */
    public void reclaimFocusOnForeground() {
        reclaimFocusOnForeground(false);
    }

    public void reclaimFocusOnForeground(boolean userGesture) {
        if (userPaused || shouldStop) {
            Log.i(TAG, "reclaimFocusOnForeground skip: userPaused=" + userPaused
                    + " shouldStop=" + shouldStop);
            return;
        }
        final BoluoWeChatVoiceResumePolicy.Plan weChatReturn = weChatVoiceReturnPlan(true);
        if ("wechat_voice_hold".equals(weChatReturn.reason)) {
            systemSuspended = true;
            invalidatePendingUnityRestore("wechat_voice_hold");
            Log.i(TAG, "reclaimFocusOnForeground hold wechat voice until GAIN");
            reassertNowPlayingOnForeground();
            flushDeferredFocusPauseUi("wechat_voice_hold");
            return;
        }
        final boolean holdWeChatInterrupt =
                "return_show_interrupt".equals(weChatReturn.reason);
        if (holdWeChatInterrupt && !userGesture) {
            systemSuspended = true;
            invalidatePendingUnityRestore("media_hold_return");
            Log.i(TAG, "reclaimFocusOnForeground hold wechat interrupt for swipe");
            reassertNowPlayingOnForeground();
            flushDeferredFocusPauseUi("media_hold_return");
            return;
        }
        if (holdWeChatInterrupt) {
            systemSuspended = false;
        }
        // 抢焦点可能因已持焦 / debounce 直接返回，卡片仍要重推。
        reassertNowPlayingOnForeground();
        if (pendingUnityAudioRestored && !systemSuspended) {
            pendingUnityAudioRestored = false;
            notifyUnityAudioRestored("foreground_deferred");
        }
        final boolean restoreWeChatVoices = weChatReturn.restoresMusicAndEffects();
        // 后台续播：禁止 reclaim→Reset。HOME 期间误调会把自己拆静音。
        // 例外：丢焦护栏仍 latch 时必须抢焦并恢复（切 QQ 音乐再回波萝）。
        // 微信发语音取消：自动回前台不出声；滑动续播才成对拉起。
        if (shouldSuppressTransientPauseInBackground()
                && !needsForegroundVoiceRestore()
                && !restoreWeChatVoices) {
            Log.i(TAG, "reclaimFocusOnForeground skip: keep playing in background");
            return;
        }
        boolean engineStarted = WIAEngineJNI.getPlaybackState() == PlaybackState_Started;
        boolean hasInstance = hasLiveMusicInstance();
        boolean needsVoiceRestore = needsForegroundVoiceRestore();
        // 标准版不能滑动续播：系统打断后回前台必须自动续。
        // pending / 拆轨 / duck / 未持焦 / 引擎未 Started / 丢焦 Pause 过 都算恢复意图。
        boolean hasResumeIntent = audioFocusManager.isPendingResume()
                || audioFocusManager.needsPlaybackReopen()
                || audioFocusManager.isDucked()
                || (!audioFocusManager.hasAudioFocus() && (engineStarted || hasInstance))
                || (hasInstance && !engineStarted)
                || needsVoiceRestore
                || restoreWeChatVoices;
        long now = SystemClock.elapsedRealtime();
        // 已持焦但 duck/拆轨/pending/丢焦 Pause 时禁止 debounce 跳过，否则标准版回前台无法自动续
        if (audioFocusManager.hasAudioFocus()
                && !audioFocusManager.isPendingResume()
                && !audioFocusManager.needsPlaybackReopen()
                && !audioFocusManager.isDucked()
                && !needsVoiceRestore
                && !restoreWeChatVoices
                && now - lastReclaimAttemptElapsedMs < RECLAIM_DEBOUNCE_MS) {
            Log.i(TAG, "reclaimFocusOnForeground debounced interval="
                    + (now - lastReclaimAttemptElapsedMs) + "ms");
            return;
        }
        lastReclaimAttemptElapsedMs = now;
        Log.i(TAG, "reclaimFocusOnForeground pendingResume=" + audioFocusManager.isPendingResume()
                + " hasFocus=" + audioFocusManager.hasAudioFocus()
                + " engineStarted=" + engineStarted
                + " hasInstance=" + hasInstance
                + " hasResumeIntent=" + hasResumeIntent
                + " needsVoiceRestore=" + needsVoiceRestore
                + " weChatResume=" + restoreWeChatVoices
                + " weChatReason=" + weChatReturn.reason
                + " reopen_reason=" + audioFocusManager.peekReopenReason()
                + " trace=" + BoluoAudioStutterProbe.traceId());
        audioFocusManager.reclaimOnForeground(userPaused, hasResumeIntent);
        // reclaim 可能 NOOP（已持焦且 pending 已清），仍补 Resume_ALL + Resume_Music + unmute。
        // 自动回前台的微信打断已在上面 return。这里是滑动续播，或 QQ 等仍要成对拉起。
        if (restoreWeChatVoices || needsForegroundVoiceRestore()) {
            final long gen = foregroundResumeGeneration;
            final String resumeReason = restoreWeChatVoices
                    ? weChatReturn.reason
                    : "reclaim_foreground_fallback";
            playbackExecutor.execute(() -> {
                if (gen != foregroundResumeGeneration || userPaused || shouldStop) {
                    return;
                }
                // 进门时策略已要求成对恢复：闩被并发清掉也不能改成只 unmute。
                if (!restoreWeChatVoices && !needsForegroundVoiceRestore()) {
                    return;
                }
                resumePairedVoices(resumeReason);
                runGuardedUnmuteOnCurrentThread("reclaim_foreground_fallback");
                systemSuspended = false;
                mainHandler.post(this::updateNowPlayingForCurrentTrack);
                notifyUnityAudioRestored(resumeReason);
                Log.i(TAG, "[BoluoAudioFg] reclaim fallback Resume+unmute reason=" + resumeReason);
            });
        }
    }

    /**
     * 起播/续播门禁：必须先拿到 AUDIOFOCUS_GAIN 再 unmute/出声。
     * @return false 时调用方须 abort，禁止叠播
     */
    private boolean ensureFocusBeforeAudible(String reason) {
        boolean granted = audioFocusManager.requestFocusBlocking(true);
        if (!granted) {
            Log.w(TAG, reason + ": focus REQUEST_FAILED — abort audible (no overlap play)");
            musicEngineMuted = true;
            probeSetMute(true);
            return false;
        }
        Log.i(TAG, reason + ": focus GRANTED — may unmute/play");
        return true;
    }

    /** 同曲 redundant play：不重 PostEvent，只强制抢焦后 unmute（mute 态点播放）。 */
    private void reclaimFocusForForcedPlaySameTrack() {
        userPaused = false;
        shouldStop = false;
        if (!ensureFocusBeforeAudible("play_redundant")) {
            return;
        }
        systemSuspended = false;
        runGuardedUnmuteOnCurrentThread("play_redundant");
        startMediaPlaybackForegroundService();
        mainHandler.post(this::updateNowPlayingForCurrentTrack);
        notifyUnityPlaybackState(true);
        Log.i(TAG, "play_redundant: reclaim+unmute done");
    }

    /** 用户是否主动暂停（负一屏/岛内点按暂停）。自动续播路径必须读这个，而不是引擎 Started。 */
    public boolean isUserPaused() {
        return userPaused;
    }

    /** 系统弹窗盖住游戏、且这次丢焦被判定为可忽略。 */
    public boolean isSystemDialogCover() {
        return systemDialogCover;
    }

    /** Unity 进后台读过之后清掉，避免下一次按 Home 还被当成弹窗。 */
    public void clearSystemDialogCover() {
        systemDialogCover = false;
    }

    /**
     * 用户明确续播：负一屏「播放」、岛内点按。自动路径（焦点 GAIN / 回前台 reclaim / 曲终下一曲）禁止调用。
     */
    public void resumeFromUserAction() {
        if (remoteCommands.isPausedForTimerEnd()) {
            Log.i(TAG, "resumeFromUserAction ignored: timer end");
            return;
        }
        resumeFromMediaControl();
    }

    /**
     * 定时结束弹窗「取消/重复」专用：只清 userPaused + unmute。
     * <p>
     * 禁止在此发 Resume_ALL / Resume_Music。续播由 Unity ResumePaired 从暂停处拉起音乐和音效。
     */
    public void resumeAfterTimerEnd() {
        playbackExecutor.execute(() -> {
            userPaused = false;
            shouldStop = false;
            if (!ensureFocusBeforeAudible("timer_end_resume")) {
                Log.w(TAG, "resumeAfterTimerEnd: focus failed");
                return;
            }
            systemSuspended = false;
            Log.i(TAG, "resumeAfterTimerEnd: unmute only (Unity ResumePaired owns voices)");
            runGuardedUnmuteOnCurrentThread("timer_end_resume");
            startMediaPlaybackForegroundService();
            mainHandler.post(this::updateNowPlayingForCurrentTrack);
        });
    }

    /**
     * 退登 {@link #stop()} 钉住的 shouldStop / userPaused：二次进岛 Arm 前由宿主清掉。
     * 不在此处 unmute（揭幕仍走 ProcedureIsland.Uncover / ensureAudibleForUnityPlayback）。
     */
    public void clearLogoutStopLatch() {
        long t0 = SystemClock.elapsedRealtime();
        shouldStop = false;
        userPaused = false;
        remoteCommands.notePausedForTimerEnd(false);
        WIAEngineJNI.setPlaybackActive(true);
        Log.i(TAG, "clearLogoutStopLatch");
        Log.i(TAG, "[Hitch] clearLogoutStopLatch ms=" + (SystemClock.elapsedRealtime() - t0)
                + " thread=" + Thread.currentThread().getName());
    }

    /**
     * 退登 / 切 Login 拆除：撤掉排队 NowPlaying、关闸、停 FGS。
     * 必须在 LoadScene(Login) 前由 C# CleanupAfterLogout 调用，
     * 否则残留 mainHandler 推送会在主线程拥堵时误拉起 FGS 超时杀进程。
     */
    public void teardownForLogout() {
        long t0 = SystemClock.elapsedRealtime();
        ++playGeneration;
        shouldStop = true;
        userPaused = true;
        remoteCommands.notePausedForTimerEnd(false);
        try {
            mainHandler.removeCallbacksAndMessages(null);
        } catch (Exception e) {
            Log.w(TAG, "teardownForLogout removeCallbacks: " + e.getMessage());
        }
        audioFocusManager.abandonFocus();
        stopMediaPlaybackForegroundService();
        unityMarkedAudible = false;
        startupAudibleCommitted = false;
        foregroundCardPublished = false;
        WIAEngineJNI.setPlaybackActive(false);
        Log.i(TAG, "teardownForLogout");
        Log.i(TAG, "[Hitch] teardownForLogout ms=" + (SystemClock.elapsedRealtime() - t0)
                + " thread=" + Thread.currentThread().getName());
    }

    /**
     * 揭罩出声前同步申请 {@code AUDIOFOCUS_GAIN} 并注册监听。
     * 已在后台，或焦点被拒：保持 mute，禁止和抖音 / QQ 音乐叠播。
     *
     * @return true 可以 unmute
     */
    public boolean armFocusBeforeAudible() {
        if (!WIAEngineJNI.isPlaybackActive()) {
            WIAEngineJNI.setPlaybackActive(true);
        }
        if (!isProcessInForeground()) {
            appInBackground = true;
            unityClaimsForeground = false;
        }
        if (appInBackground) {
            Log.i(TAG, "armFocusBeforeAudible: already background — stay muted");
            audioFocusManager.ensurePlaybackMonitor();
            musicEngineMuted = true;
            probeSetMute(true);
            return false;
        }
        boolean granted = audioFocusManager.requestFocusBlocking(true);
        if (!granted) {
            Log.w(TAG, "armFocusBeforeAudible: focus denied — stay muted");
            musicEngineMuted = true;
            probeSetMute(true);
            return false;
        }
        Log.i(TAG, "armFocusBeforeAudible: focus granted");
        return true;
    }

    /**
     * C# 已把负一屏写成播放中。顺手挂上对方播放器监听，启动期切走能收卡。
     * <p>
     * 出「播放中」卡即新会话已对外宣称在播：必须清掉 teardownForLogout 陈旧闩。
     * 否则宿主 {@code shouldKeepPlayingInBackground} 仍读到 shouldStop/userPaused，
     * 上滑任务栏误走 {@code unity_pause_preview} 静音且不写卡 → 声停、负一屏仍 PLAYING。
     */
    public void noteForegroundCardPublished() {
        foregroundCardPublished = true;
        if (shouldStop || userPaused) {
            Log.i(TAG, "noteForegroundCardPublished clear stale stop latch shouldStop="
                    + shouldStop + " userPaused=" + userPaused);
            shouldStop = false;
            userPaused = false;
        }
        audioFocusManager.ensurePlaybackMonitor();
        Log.i(TAG, "noteForegroundCardPublished");
    }

    /** 暂停或清卡：占位旗标放下，避免之后切后台又把卡片建回来。 */
    public void noteForegroundCardCleared() {
        foregroundCardPublished = false;
    }

    /**
     * 占位卡还没在前台出过声：仅真正 BackgroundYield（禁后台播 / 启动放弃）时收卡停 FGS。
     * 任务栏预览 / 允许后台续播绝不走这里——否则冷启动后上滑 Recents 会误暂停。
     */
    private void yieldStartupPlaceholderCard() {
        if (!foregroundCardPublished || startupAudibleCommitted || userPaused) {
            return;
        }
        // 允许后台播：Recents / HOME 续播，占位卡保持。
        if (!backgroundPlayDisabled) {
            return;
        }
        if (!effectivelyInBackground() && isProcessInForeground()) {
            return;
        }
        foregroundCardPublished = false;
        Log.i(TAG, "yieldStartupPlaceholderCard (bg play disabled, never audible)");
        updateTransportNowPlaying(false);
        stopMediaPlaybackForegroundService();
    }

    /**
     * 谁在前台谁播。启动期进程已不在前台，或对方媒体正在出声：禁止解 mute。
     */
    public boolean allowUnmuteNow() {
        // Unity 已说回到前台：即使进程 importance 还没翻过来，也允许出声、出负一屏。
        if (!effectivelyInBackground()) {
            return true;
        }
        boolean foreign = audioFocusManager.hasForeignMedia();
        if (!startupAudibleCommitted || foreign) {
            Log.i(TAG, "allowUnmuteNow deny committed=" + startupAudibleCommitted
                    + " foreign=" + foreign);
            audioFocusManager.ensurePlaybackMonitor();
            musicEngineMuted = true;
            unityMarkedAudible = false;
            return false;
        }
        return true;
    }

    /**
     * Unity 还没收到 pause 时，用进程 importance 判定已经切走。
     * Unity 明确回到前台后，不再被滞后的 importance 钉在后台。
     */
    private boolean effectivelyInBackground() {
        if (unityClaimsForeground && !appInBackground) {
            return false;
        }
        if (appInBackground) {
            return true;
        }
        return !isProcessInForeground();
    }

    /** Unity 确认已经出声，且当时进程在前台，才算启动期结束。 */
    public void noteUnityAudible() {
        unityMarkedAudible = true;
        musicEngineMuted = false;
        if (isProcessInForeground() || (unityClaimsForeground && !appInBackground)) {
            startupAudibleCommitted = true;
        }
    }

    public void noteUnitySilent() {
        unityMarkedAudible = false;
        musicEngineMuted = true;
    }

    /**
     * 进程是否还在前台。读不到时退回 Unity 的 pause 旗，避免误把前台当成后台。
     */
    private boolean isProcessInForeground() {
        Boolean queried = queryProcessForeground();
        if (queried == null) {
            return !appInBackground;
        }
        return queried;
    }

    private Boolean queryProcessForeground() {
        try {
            Context ctx = null;
            try {
                Object app = Class.forName("android.app.ActivityThread")
                        .getMethod("currentApplication")
                        .invoke(null);
                if (app instanceof Context) {
                    ctx = (Context) app;
                }
            } catch (Exception ignored) {
                ctx = null;
            }
            if (ctx == null) {
                return null;
            }
            ActivityManager am = (ActivityManager) ctx.getSystemService(Context.ACTIVITY_SERVICE);
            if (am == null) {
                return null;
            }
            List<ActivityManager.RunningAppProcessInfo> procs = am.getRunningAppProcesses();
            if (procs == null) {
                return null;
            }
            int pid = android.os.Process.myPid();
            for (ActivityManager.RunningAppProcessInfo info : procs) {
                if (info != null && info.pid == pid) {
                    int importance = info.importance;
                    return importance == ActivityManager.RunningAppProcessInfo.IMPORTANCE_FOREGROUND
                            || importance == ActivityManager.RunningAppProcessInfo.IMPORTANCE_VISIBLE;
                }
            }
            return null;
        } catch (Throwable t) {
            Log.d(TAG, "queryProcessForeground: " + t.getMessage());
            return null;
        }
    }

    /**
     * Unity {@code MusicPlaylist} 直发 PostEvent 起播时调用：补 AudioFocus + unmute。
     * CustomSRP 不再走 {@link #play(int, int)}，否则真机无焦点/mute 影子态会导致有 PostEvent 无听感。
     * <p>
     * 拆除期（{@link #teardownForLogout} 已 {@code setPlaybackActive(false)}）直接 return：
     * 禁止重开闸、禁止 {@code startForegroundService}——Login 场景加载拥堵时会
     * ForegroundServiceDidNotStartInTimeException 杀进程。FGS 只由
     * {@code updateNowPlayingInfo(isPlaying=true)} → {@code notifyNowPlayingChanged} 拉起。
     */
    public void ensureAudibleForUnityPlayback() {
        if (!WIAEngineJNI.isPlaybackActive()) {
            Log.w(TAG, "ensureAudibleForUnityPlayback skipped: playback inactive (logout teardown)");
            return;
        }
        if (!allowUnmuteNow()) {
            Log.i(TAG, "ensureAudibleForUnityPlayback denied — foreground app owns audio");
            musicEngineMuted = true;
            unityMarkedAudible = false;
            probeSetMute(true);
            return;
        }
        userPaused = false;
        shouldStop = false;
        playbackExecutor.execute(() -> {
            if (!WIAEngineJNI.isPlaybackActive()) {
                Log.w(TAG, "ensureAudibleForUnityPlayback aborted: became inactive");
                return;
            }
            if (!ensureFocusBeforeAudible("unity_play")) {
                Log.w(TAG, "ensureAudibleForUnityPlayback: focus failed — stay muted");
                return;
            }
            runGuardedUnmuteOnCurrentThread("unity_play");
            // 不在此处 start FGS：与 NowPlaying isPlaying 守卫同一路径，避免拆除窗口误拉。
            Log.i(TAG, "ensureAudibleForUnityPlayback: focus+unmute done");
        });
    }

    /**
     * GameData.PauseAll/ResumeAll 同步 userPaused。
     * 暂停时立刻把负一屏打成暂停；恢复时只清旗标，播放图标由真正出声的 resumeFromUserAction / nativePlayTrackAt 刷新，
     * 避免「负一屏已是播放、歌还没起来」。
     */
    public void markUserPausedFromUnity(boolean paused) {
        if (paused) {
            // 新的暂停作废还在飞的续播。切歌下标仍走 playGeneration，这里不挡。
            ++audibleCommandGeneration;
            userPaused = true;
            remoteCommands.adoptPlayback(false, true);
            remoteCommands.cancelPendingPlayPause();
            mainHandler.removeCallbacks(flushMediaPlayPauseRunnable);
            mainHandler.post(() -> updateNowPlayingPlayingState(false));
        } else {
            userPaused = false;
            remoteCommands.adoptPlayback(!musicEngineMuted, false);
        }
        Log.i(TAG, "markUserPausedFromUnity: userPaused=" + paused
                + " audibleGen=" + audibleCommandGeneration);
    }

    /** Unity Playing / 超时：清 skip in-flight，放行下一笔负一屏 skip。 */
    public void noteSkipSettled() {
        remoteCommands.noteSkipSettled();
        Log.i(TAG, "noteSkipSettled");
    }

    /** 音乐和音效一起暂停，保留曲目实例。 */
    private void stopPairedVoices(String reason) {
        postMusicControlEvent("Pause_Music");
        postMusicControlEvent("Pause_ALL");
        Log.i(TAG, "stopPaired " + reason);
    }

    /**
     * 音乐和音效从暂停处一起续。
     * CustomSRP 的曲子由 Unity 打在 MusicObjectId 上，{@link #currentInstanceId} 经常是 0。
     * 以前这时去重播传输计划并 return，Resume_Music 根本没发出，回前台只剩音效。
     */
    private void resumePairedVoices(String reason) {
        postMusicControlEvent("Resume_ALL");
        postMusicControlEvent("Resume_Music");
        voicesPausedForFocusLoss = false;
        Log.i(TAG, "[BoluoAudioFg] resumePaired music+sfx reason=" + reason
                + " live=" + hasLiveMusicInstance()
                + " unityVoice=" + unityMusicInstanceId
                + " musicObj=" + musicObjectId());
    }

    /** 丢焦后下一轮 Resume 必须能真正 Post，禁止被上一轮 coalesce 窗口吞掉。 */
    private void resetVoiceResumeCoalesce() {
        synchronized (voiceResumeLock) {
            lastResumeAllElapsedMs = 0L;
            lastResumeMusicElapsedMs = 0L;
        }
        Log.i(TAG, "[BoluoAudioFg] voice resume coalesce reset");
    }

    /**
     * 回前台对账前丢掉积压的 Interrupted。Restored 必须压过旧 CompetingMuted。
     */
    private void dropDeferredFocusPauseUi(String reason) {
        if (!pendingFocusPauseUi) {
            return;
        }
        Log.i(TAG, "[BoluoAudioFg] drop deferred focusPauseUi reason=" + reason
                + " competingLong=" + pendingFocusPauseUiCompetingLong);
        pendingFocusPauseUi = false;
    }

    private void invalidatePendingUnityRestore(String reason) {
        unityRestoreGeneration++;
        if (!pendingUnityAudioRestored) {
            return;
        }
        pendingUnityAudioRestored = false;
        Log.i(TAG, "[BoluoAudioFg] drop deferred unity restored reason=" + reason
                + " gen=" + unityRestoreGeneration);
    }

    /** 回前台仍暂停：把后台没送出的打断补进 Unity，岛上才显示暂停。 */
    private void flushDeferredFocusPauseUi(String reason) {
        if (!pendingFocusPauseUi) {
            Log.i(TAG, "[BoluoAudioFg] no deferred focusPauseUi reason=" + reason);
            return;
        }
        final boolean longUi = pendingFocusPauseUiCompetingLong;
        pendingFocusPauseUi = false;
        Log.i(TAG, "[BoluoAudioFg] flush focusPauseUi reason=" + reason
                + " competingLong=" + longUi);
        mainHandler.post(() -> {
            try {
                String method = longUi ? "OnCompetingAudioMuted" : "OnSystemAudioMuted";
                UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid", method, "");
            } catch (Exception e) {
                Log.w(TAG, "flush focusPauseUi failed: " + e.getMessage());
            }
        });
    }

    /** 原生已经 Resume_Music + Resume_ALL 之后再告诉 Unity，避免只对账音效。 */
    private void notifyUnityAudioRestored(String reason) {
        if (userPaused || shouldStop || remoteCommands.isPausedForTimerEnd()) {
            Log.i(TAG, "[BoluoAudioFg] skip unity restored user/timer reason=" + reason);
            return;
        }
        if (systemSuspended && audioFocusManager.sawCompetingMediaEpisode()) {
            Log.i(TAG, "[BoluoAudioFg] skip unity restored, media held reason=" + reason);
            return;
        }
        // Unity 暂停时消息会整包积压。QQ 暂停之后这些旧 Restored 回前台会把岛刷成播放中。
        if (effectivelyInBackground()) {
            pendingUnityAudioRestored = true;
            Log.i(TAG, "[BoluoAudioFg] defer unity restored reason=" + reason);
            return;
        }
        dropDeferredFocusPauseUi(reason);
        pendingUnityAudioRestored = false;
        final int gen = unityRestoreGeneration;
        Log.i(TAG, "[BoluoAudioFg] notify unity restored reason=" + reason
                + " gen=" + gen);
        mainHandler.post(() -> {
            if (gen != unityRestoreGeneration) {
                Log.i(TAG, "[BoluoAudioFg] drop stale unity restored reason=" + reason
                        + " gen=" + gen + " cur=" + unityRestoreGeneration);
                return;
            }
            if (systemSuspended && audioFocusManager.sawCompetingMediaEpisode()) {
                Log.i(TAG, "[BoluoAudioFg] drop unity restored, media held reason=" + reason);
                return;
            }
            try {
                UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid",
                        "OnAudioFocusUnduck", "");
                UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid",
                        "OnSystemAudioRestored", "");
                UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid",
                        "OnNowPlayingPlay", "ui");
            } catch (Exception e) {
                Log.w(TAG, "focus resume UI notify failed: " + e.getMessage());
            }
        });
    }

    /**
     * Unity 弹窗续播 / 离岛：只改到点闩，不停声、不解 mute。
     * 清闩时作废还在排队的 {@link #pauseForTimerEnd()}，避免续播后又被补上的 Pause 盖回去。
     */
    public void notePausedForTimerEnd(boolean paused) {
        if (!paused) {
            timerEndGeneration++;
        }
        remoteCommands.notePausedForTimerEnd(paused);
    }

    /**
     * Unity 已经冻住、收不到定时进度时，由宿主直接停播。
     * Unity 醒着时不要再调：停播走 StopPaired，这里只会再 Pause 一次并 SetMute。
     * Pause_Music 停 BGM（保留实例便于弹窗「取消/重复」续播）；
     * Pause_ALL + mute 停环境/天气等音效；userPaused 防焦点回归擅自出声。
     */
    public void pauseForTimerEnd() {
        final int gen = timerEndGeneration;
        playbackExecutor.execute(() -> {
            if (gen != timerEndGeneration) {
                Log.i(TAG, "pauseForTimerEnd dropped: timer cleared gen=" + gen);
                return;
            }
            userPaused = true;
            remoteCommands.notePausedForTimerEnd(true);
            mainHandler.removeCallbacks(flushMediaPlayPauseRunnable);
            musicEngineMuted = true;
            probeSetMute(true);
            stopPairedVoices("timer-end");
            Log.i(TAG, "pauseForTimerEnd: stopPaired userPaused=true");
            mainHandler.post(() -> updateNowPlayingPlayingState(false));
            notifyUnityPlaybackState(false);
        });
    }

    /**
     * 系统丢焦护栏静音：先同步压对象增益到 0，短缓冲后再 SetMute。
     * 禁止满音量硬 mute（切微信/QQ/B 站电流音）。mute 后保持衰减，由 unmute 爬坡回满。
     * <p>
     * {@code focus_pause} 先把音乐增益打到 0，短延迟后再 Pause_Music + Pause_ALL。
     * 满音量 Pause 就是切搜索栏 / QQ 音乐 / 抖音时的「卡一下」。
     * Pause 仍要做：只 mute 时环境音效会留下来。回前台必须成对 Resume。
     */
    private void applyGuardedSystemMute(String reason) {
        final long gen = ++guardedMuteGeneration;
        final long obj = musicObjectId();
        final boolean pauseVoices = "focus_pause".equals(reason);
        Log.i(TAG, "[BoluoAudioFg] guarded mute schedule reason=" + reason
                + " pauseVoices=" + pauseVoices + " obj=" + obj + " gen=" + gen);
        BoluoAudioStutterProbe.noteSwitchClass("guard_mute", reason);
        Log.i("BoluoAudioStutter", "mute_schedule reason=" + reason
                + " pauseVoices=" + pauseVoices
                + " muted=" + musicEngineMuted
                + " voicesPaused=" + voicesPausedForFocusLoss
                + " suspended=" + systemSuspended
                + " bg=" + effectivelyInBackground()
                + " pending=" + audioFocusManager.isPendingResume());
        // 先在回调线程把增益打到 0。等播放线程再压，系统已经把满音量掐断了，就是切微信/QQ 的卡一下。
        applyMusicGainSafe(obj, 0f, true);
        playbackExecutor.execute(() -> {
            // 再 Pause。满音量 Pause 就是切走时的「卡一下」。
            applyMusicGainSafe(obj, 0f, true);
            Log.i(TAG, "[BoluoAudioFg] guarded mute dip-first reason=" + reason
                    + " obj=" + obj + " gen=" + gen);
            try {
                Thread.sleep(GUARDED_MUTE_DELAY_MS);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return;
            }
            if (gen != guardedMuteGeneration) {
                Log.i(TAG, "[BoluoAudioFg] guarded mute superseded reason=" + reason
                        + " gen=" + gen + " now=" + guardedMuteGeneration);
                return;
            }
            // 对齐线上 96ec0d5f：先 SetMute 把输出压静，再 Pause。满音量 Pause 就是切 App 的「卡一下」。
            // Pause 仍要做：只 mute 时环境音效会留下来。回前台必须成对 Resume。
            musicEngineMuted = true;
            probeSetMute(true);
            if (pauseVoices) {
                stopPairedVoices("focus_pause");
                voicesPausedForFocusLoss = true;
            }
            Log.i(TAG, "[BoluoAudioFg] guarded mute landed reason=" + reason
                    + " pauseVoices=" + pauseVoices);
        });
    }

    /** 焦点恢复：先 unmute（增益仍近 0），再两步爬满，消解静音爆音。须在 playbackExecutor 上调用。 */
    private void runGuardedUnmuteOnCurrentThread(String reason) {
        crackleProbe("guardedUnmute.enter", "reason=" + reason, true);
        final long gen = ++guardedMuteGeneration;
        // 与 dip 同一对象。objectId 还是 0 时增益打不中，回前台音乐会停在 0、音效却响。
        final long obj = musicObjectId();
        if (!musicEngineMuted) {
            if (crackleFixDLowGainNativePlay && "native_play".equals(reason)) {
                crackleProbe("guardedUnmute.flagD", "lowGainRamp reason=" + reason, false);
                applyMusicGainSafe(obj, 0.05f, true);
                probeSetMute(false);
                if (!sleepWhileCurrent(gen, UNMUTE_RAMP_STEP_MS)) {
                    return;
                }
                applyMusicGainSafe(obj, 0.25f, true);
                if (!sleepWhileCurrent(gen, UNMUTE_RAMP_STEP_MS)) {
                    return;
                }
                applyMusicGainSafe(obj, 0.55f, true);
                if (!sleepWhileCurrent(gen, UNMUTE_RAMP_STEP_MS)) {
                    return;
                }
                applyMusicGainSafe(obj, 1f, false);
                Log.i(TAG, "flag D native_play ramp done reason=" + reason);
                crackleProbe("guardedUnmute.exit", "branch=flagD reason=" + reason, false);
                return;
            }
            applyMusicGainSafe(obj, 1f, false);
            probeSetMute(false);
            Log.i(TAG, "skip guarded unmute ramp, already audible reason=" + reason);
            crackleProbe("guardedUnmute.exit", "branch=alreadyAudible reason=" + reason, false);
            return;
        }
        applyMusicGainSafe(obj, 0.05f, true);
        probeSetMute(false);
        Log.i(TAG, "guarded unmute start reason=" + reason + " obj=" + obj);
        if (!sleepWhileCurrent(gen, UNMUTE_RAMP_STEP_MS)) {
            return;
        }
        applyMusicGainSafe(obj, 0.25f, true);
        if (!sleepWhileCurrent(gen, UNMUTE_RAMP_STEP_MS)) {
            return;
        }
        applyMusicGainSafe(obj, 0.55f, true);
        if (!sleepWhileCurrent(gen, UNMUTE_RAMP_STEP_MS)) {
            return;
        }
        applyMusicGainSafe(obj, 1f, false);
        musicEngineMuted = false;
        Log.i(TAG, "guarded unmute ramp done reason=" + reason);
        crackleProbe("guardedUnmute.exit", "branch=ramp reason=" + reason, false);
    }

    private boolean sleepWhileCurrent(long gen, long ms) {
        try {
            Thread.sleep(ms);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            return false;
        }
        return gen == guardedMuteGeneration;
    }

    private static void applyMusicGainSafe(long objId, float gain, boolean enable) {
        if (objId == 0) {
            return;
        }
        WanosMusicPlayerNative self = sInstance;
        if (self != null) {
            self.noteGainProbe(objId, gain, enable);
        }
        try {
            WIAEngineJNI.setGameObjAttenuationGainEnable(objId, enable);
            WIAEngineJNI.setGameObjAttenuationGain(objId, gain);
        } catch (UnsatisfiedLinkError e) {
            Log.w(TAG, "applyMusicGain JNI missing: " + e.getMessage());
        }
    }

    /** 同增益 400ms 内不重复打日志。仍照常写 JNI。 */
    private void noteGainProbe(long objId, float gain, boolean enable) {
        boolean changed = !shadowGainValid
                || Float.compare(shadowGain, gain) != 0
                || shadowGainEnable != enable;
        long now = SystemClock.elapsedRealtime();
        boolean due = changed || now - lastGainProbeElapsedMs >= GAIN_PROBE_MIN_INTERVAL_MS;
        shadowGain = gain;
        shadowGainEnable = enable;
        shadowGainValid = true;
        if (!due) {
            return;
        }
        lastGainProbeElapsedMs = now;
        crackleProbe("applyGain",
                "obj=" + objId + " targetGain=" + gain + " enable=" + enable + " changed=" + changed,
                false);
    }

    /**
     * 包一层 setMute：先打探针再调 JNI。默认路径与直接 setMute 相同。
     */
    private void probeSetMute(boolean mute) {
        boolean noop = shadowMute != null && shadowMute.booleanValue() == mute;
        crackleProbe("setMute", "targetMute=" + mute + " noopMute=" + noop, true);
        shadowMute = Boolean.valueOf(mute);
        WIAEngineJNI.setMute(mute);
    }

    private void crackleProbe(String site, String extra, boolean withStack) {
        int state = -1;
        try {
            state = WIAEngineJNI.getPlaybackState();
        } catch (Throwable ignored) {
            state = -2;
        }
        int listIdx = -1;
        int musicIdx = -1;
        NativePlaylist snap = playlist;
        if (snap != null) {
            listIdx = snap.currentListIndex;
            musicIdx = snap.currentMusicIndex;
        }
        StringBuilder sb = new StringBuilder(320);
        sb.append("site=").append(site);
        sb.append(" wallMs=").append(System.currentTimeMillis());
        sb.append(" tid=").append(android.os.Process.myTid());
        sb.append(" thread=").append(Thread.currentThread().getName());
        sb.append(" musicEngineMuted=").append(musicEngineMuted);
        sb.append(" shadowMute=").append(shadowMute);
        sb.append(" shadowGain=").append(shadowGainValid ? String.valueOf(shadowGain) : "?");
        sb.append(" shadowGainEnable=").append(shadowGainEnable);
        sb.append(" appInBackground=").append(appInBackground);
        sb.append(" bgPlayDisabled=").append(backgroundPlayDisabled);
        sb.append(" playbackState=").append(state);
        sb.append(" isPlaying=").append(state == PlaybackState_Started);
        sb.append(" instanceId=").append(currentInstanceId);
        sb.append(" listIdx=").append(listIdx);
        sb.append(" musicIdx=").append(musicIdx);
        sb.append(" fgs=").append(WanosMediaPlaybackForegroundService.isRunning());
        sb.append(" flagC=").append(crackleFixCSkipRedundantUnmute);
        sb.append(" flagD=").append(crackleFixDLowGainNativePlay);
        sb.append(' ').append(audioRouteSnapshot());
        if (extra != null && extra.length() > 0) {
            sb.append(' ').append(extra);
        }
        if (withStack) {
            sb.append(" stack=").append(stackHead(8));
        }
        Log.i(PROBE, sb.toString());
    }

    private String audioRouteSnapshot() {
        Context ctx = resolveAppContext();
        if (ctx == null) {
            return "mode=? musicActive=? vol=? outs=?";
        }
        AudioManager am = (AudioManager) ctx.getSystemService(Context.AUDIO_SERVICE);
        if (am == null) {
            return "mode=? musicActive=? vol=? outs=?";
        }
        int vol = am.getStreamVolume(AudioManager.STREAM_MUSIC);
        int max = am.getStreamMaxVolume(AudioManager.STREAM_MUSIC);
        StringBuilder outs = new StringBuilder();
        AudioDeviceInfo[] devices = am.getDevices(AudioManager.GET_DEVICES_OUTPUTS);
        if (devices != null) {
            for (int i = 0; i < devices.length; i++) {
                if (i > 0) {
                    outs.append(',');
                }
                outs.append(devices[i].getType());
            }
        }
        return "mode=" + am.getMode()
                + " musicActive=" + am.isMusicActive()
                + " vol=" + vol + "/" + max
                + " outs=" + outs;
    }

    private static String stackHead(int max) {
        StackTraceElement[] els = new Throwable().getStackTrace();
        StringBuilder sb = new StringBuilder();
        int n = 0;
        for (int i = 0; i < els.length && n < max; i++) {
            String name = els[i].getMethodName();
            if ("stackHead".equals(name) || "crackleProbe".equals(name) || "probeSetMute".equals(name)) {
                continue;
            }
            if (sb.length() > 0) {
                sb.append('<');
            }
            sb.append(name).append(':').append(els[i].getLineNumber());
            n++;
        }
        return sb.toString();
    }

    /** 停止播放（对齐 iOS WanosMusicPlayer_NativeStop：发 Stop 事件、移除回调、清锁屏元数据）。 */
    public void stop() {
        ++playGeneration;
        shouldStop = true;
        userPaused = false;
        audioFocusManager.abandonFocus();
        stopMediaPlaybackForegroundService();
        playbackExecutor.execute(() -> {
            lastStoppedInstanceId = currentInstanceId;
            WIAEngineJNI.removeEventStateCallback(currentInstanceId);
            currentInstanceId = 0;
            if (objectId != 0) {
                WIAEngineJNI.postEvent(objectId, stopEventName);
            }
            musicEngineMuted = true;
            probeSetMute(true);
            mainHandler.post(() -> {
                WIAEngineJNI.updateNowPlayingInfo("", "", false);
                notifyUnityPlaybackState(false);
            });
        });
    }

    /** C# 在 OnBankReady 后调用，重新加载 playlist */
    public void reloadPlaylistFromFile() {
        String path = persistentDataPath + "/WanosAudio/MusicLists.json";
        Log.i(TAG, "reloadPlaylistFromFile path=" + path);
        boolean ok = loadPlaylistFromFile(path);
        Log.i(TAG, "reloadPlaylistFromFile result=" + ok + " albumCount=" + (playlist != null ? playlist.musicLists.size() : 0));
        if (ok) {
            playlistReloadedByCSharp = true;
        }
    }

    private String readFileUtf8(String path) {
        try {
            FileInputStream fis = new FileInputStream(path);
            ByteArrayOutputStream baos = new ByteArrayOutputStream();
            byte[] buf = new byte[4096];
            int n;
            while ((n = fis.read(buf)) != -1) baos.write(buf, 0, n);
            fis.close();
            return new String(baos.toByteArray(), "UTF-8");
        } catch (Exception e) { return null; }
    }

    private boolean loadPlaylistFromFile(String path) {
        try {
            File f = new File(path);
            if (!f.exists() || f.length() == 0) {
                Log.w(TAG, "loadPlaylistFromFile path not exist or empty: " + path);
                return false;
            }
            String json = readFileUtf8(path);
            if (json == null) {
                Log.w(TAG, "loadPlaylistFromFile readFileUtf8 returned null");
                return false;
            }
            JSONObject root = new JSONObject(json);
            parsePlaylist(root);
            // indexes 为空时不以全专辑回填，与 C# 仅可播项写入 indexes 一致，避免未下载曲进入自动下一曲。
            boolean ok = !playlist.musicLists.isEmpty();
            Log.i(TAG, "loadPlaylistFromFile ok=" + ok + " albumCount=" + playlist.musicLists.size() + " indexes=" + playlist.indexes.size());
            return ok;
        } catch (Exception e) {
            Log.e(TAG, "loadPlaylistFromFile failed: " + e.getMessage());
            return false;
        }
    }

    private void parsePlaylist(JSONObject json) {
        playlist.mode = json.optInt("mode", 1);
        playlist.currentListIndex = json.optInt("currentListIndex", 0);
        playlist.currentMusicIndex = json.optInt("currentMusicIndex", 0);
        playlist.indexes.clear();
        playlist.musicLists.clear();

        JSONArray lists = json.optJSONArray("musicLists");
        if (lists != null) {
            for (int i = 0; i < lists.length(); i++) {
                JSONObject listObj = lists.optJSONObject(i);
                if (listObj == null) continue;
                NativeMusicList nl = new NativeMusicList();
                nl.musicsName = listObj.optString("musicsName", "");
                JSONArray musics = listObj.optJSONArray("musics");
                if (musics != null) {
                    for (int j = 0; j < musics.length(); j++) {
                        JSONObject m = musics.optJSONObject(j);
                        if (m == null) continue;
                        NativeMusic nm = new NativeMusic();
                        nm.bankName = m.optString("bankName", "");
                        nm.bankGuid = m.optString("bankGuid", "");
                        nm.eventName = m.optString("eventName", "");
                        nm.musicName = m.optString("musicName", "");
                        nl.musics.add(nm);
                    }
                }
                playlist.musicLists.add(nl);
            }
        }

        // C# JsonUtility 不序列化 List<int>「indexes」，真机 JSON 中常为空；可读 indexesForNativeJson（int[]）
        JSONArray idxArr = json.optJSONArray("indexesForNativeJson");
        if (idxArr == null || idxArr.length() == 0) {
            idxArr = json.optJSONArray("indexes");
        }
        if (idxArr != null) {
            for (int i = 0; i < idxArr.length(); i++) {
                playlist.indexes.add(idxArr.optInt(i, 0));
            }
        }
    }

    private void updateIndexesFromMode() {
        if (playlist.musicLists.isEmpty()) return;
        int listIdx = playlist.currentListIndex;
        if (listIdx < 0 || listIdx >= playlist.musicLists.size()) return;
        NativeMusicList list = playlist.musicLists.get(listIdx);
        int count = list.musics.size();
        if (count == 0) return;

        playlist.indexes.clear();
        if (playlist.mode == 0) {
            int idx = (playlist.currentMusicIndex >= 0 && playlist.currentMusicIndex < count) ? playlist.currentMusicIndex : 0;
            playlist.indexes.add(idx);
        } else {
            for (int i = 0; i < count; i++) playlist.indexes.add(i);
            if (playlist.mode == 2) Collections.shuffle(playlist.indexes);
        }
    }

    /** Android 上 streamingAssetsPath 为 jar:file://...!/assets，无法用 File 访问 */
    private boolean isStreamingPathUsable() {
        if (streamingAssetsPath == null || streamingAssetsPath.isEmpty()) return false;
        return !streamingAssetsPath.contains("jar:") && !streamingAssetsPath.contains("!/");
    }

    private String getBankGuid(String bankName) {
        try {
            String metaPath = persistentDataPath + "/WanosAudio/ProjectMetadata.json";
            File f = new File(metaPath);
            if (!f.exists() && isStreamingPathUsable()) {
                metaPath = streamingAssetsPath + "/WanosAudio/ProjectMetadata.json";
                f = new File(metaPath);
            }
            if (!f.exists() || f.length() == 0) return null;
            String json = readFileUtf8(metaPath);
            if (json == null) return null;
            JSONObject root = new JSONObject(json);
            JSONArray banks = root.optJSONArray("Banks");
            if (banks == null) return null;
            for (int i = 0; i < banks.length(); i++) {
                JSONObject b = banks.optJSONObject(i);
                if (b != null && bankName.equals(b.optString("BankName", ""))) {
                    return b.optString("Guid", null);
                }
            }
        } catch (Exception e) {
            Log.e(TAG, "getBankGuid failed: " + e.getMessage());
        }
        return null;
    }

    private String getBankGuidForMusic(NativeMusic music) {
        if (music.bankGuid != null && !music.bankGuid.isEmpty()) return music.bankGuid;
        return getBankGuid(music.bankName);
    }

    /** 兼容低版本 API，parseUnsignedLong 需 Java 8 */
    private long parseBankGuid(String s) {
        try {
            return Long.parseUnsignedLong(s);
        } catch (NumberFormatException e) {
            try {
                return Long.parseLong(s);
            } catch (NumberFormatException e2) {
                Log.e(TAG, "Invalid bank guid: " + s);
                return 0;
            }
        }
    }

    private String getBankPath(String bankName) {
        String wb = bankName + ".wb";
        if (!persistentDataPath.isEmpty()) {
            String patched = persistentDataPath + "/WanosBankPatched/" + wb;
            if (new File(patched).exists()) {
                Log.i(TAG, "bank path seamless sidecar " + bankName);
                return patched;
            }
            String path = persistentDataPath + "/WanosBank/" + wb;
            if (new File(path).exists()) return path;
        }
        if (isStreamingPathUsable()) {
            String path = streamingAssetsPath + "/WanosBank/" + wb;
            if (new File(path).exists()) return path;
        }
        return null;
    }

    /** 获取封面路径，用于 NowPlaying。Android jar StreamingAssets 不可 File 访问，改传 asset 相对路径。 */
    private String getCoverPathForNowPlaying() {
        if (isStreamingPathUsable()) {
            String artPath = streamingAssetsPath + "/WanosAudio/Artwork/boluo.png";
            if (new File(artPath).exists()) return artPath;
        }
        if (!persistentDataPath.isEmpty()) {
            String artPath = persistentDataPath + "/WanosAudio/Artwork/boluo.png";
            if (new File(artPath).exists()) return artPath;
        }
        // jar:file://...!/assets — decodeCoverBitmap 走 AssetManager
        return "WanosAudio/Artwork/boluo.png";
    }

    /** 根据 playlist 当前索引更新 NowPlaying（主线程调用，如 Playing 回调时） */
    private void updateNowPlayingForCurrentTrack() {
        if (userPaused || systemSuspended) {
            updateNowPlayingPlayingState(false);
            return;
        }
        if (shouldStop || !WIAEngineJNI.isPlaybackActive()) {
            Log.d(TAG, "updateNowPlayingForCurrentTrack skipped: stopped/inactive");
            return;
        }
        if (playlist == null || playlist.musicLists.isEmpty()) {
            // CustomSRP 的 Java playlist 是空的，曲名在传输计划里。
            updateTransportNowPlaying(true);
            return;
        }
        int li = playlist.currentListIndex;
        int mi = playlist.currentMusicIndex;
        if (li < 0 || li >= playlist.musicLists.size()) return;
        NativeMusicList list = playlist.musicLists.get(li);
        if (list.musics.isEmpty() || mi < 0 || mi >= list.musics.size()) return;
        NativeMusic music = list.musics.get(mi);
        String name = (music.musicName != null && !music.musicName.isEmpty())
                ? music.musicName : (list.musicsName != null ? list.musicsName : "");
        WIAEngineJNI.updateNowPlayingInfo(name, getCoverPathForNowPlaying(), true);
    }

    /** 与曲终下一曲一致，返回下一曲下标到 out[0]=list, out[1]=music */
    private void computeNextTrackIndices(int listIdx, int musicIdx, int[] out) {
        out[0] = listIdx;
        out[1] = musicIdx;
        if (listIdx < 0 || listIdx >= playlist.musicLists.size()) return;
        NativeMusicList list = playlist.musicLists.get(listIdx);
        if (list.musics.isEmpty()) return;

        if (playlist.mode == 0) {
            out[1] = musicIdx;
        } else if ((playlist.mode == 1 || playlist.mode == 2) && !playlist.indexes.isEmpty()) {
            int idxPos = playlist.indexes.indexOf(musicIdx);
            int nextPos = idxPos >= 0 ? (idxPos + 1) % playlist.indexes.size() : 0;
            out[1] = playlist.indexes.get(nextPos);
        } else {
            out[1] = (musicIdx + 1) % list.musics.size();
        }
    }

    /**
     * C# 已通过 CheckBankReady + indexes 写入可播集合；不重复 MD5，避免假阴性导致切歌失败。
     * 仍要求 .wb 路径可解析且 Guid 有效。
     */
    private boolean isNativeTrackResourcesPlayable(int listIdx, int musicIdx) {
        if (listIdx < 0 || listIdx >= playlist.musicLists.size()) return false;
        NativeMusicList lst = playlist.musicLists.get(listIdx);
        if (musicIdx < 0 || musicIdx >= lst.musics.size()) return false;
        NativeMusic m = lst.musics.get(musicIdx);
        if (m.bankName == null || m.bankName.isEmpty() || m.eventName == null || m.eventName.isEmpty())
            return false;
        if (getBankPath(m.bankName) == null) return false;
        String guidStr = getBankGuidForMusic(m);
        if (guidStr == null || guidStr.isEmpty()) return false;
        return parseBankGuid(guidStr) != 0;
    }

    private int nativePlaylistMaxAdvanceSteps(int listIdx) {
        int m = 64;
        if (listIdx >= 0 && listIdx < playlist.musicLists.size()) {
            int nMusics = playlist.musicLists.get(listIdx).musics.size();
            if (nMusics > m) m = nMusics;
            int nIdx = playlist.indexes.size();
            if (nIdx > m) m = nIdx;
        }
        return m;
    }

    private boolean forceStopCurrentPlayback() {
        if (currentInstanceId == 0) return true;
        lastStoppedInstanceId = currentInstanceId;
        WIAEngineJNI.removeEventStateCallback(currentInstanceId);
        currentInstanceId = 0;
        if (objectId != 0) {
            WIAEngineJNI.postEvent(objectId, stopEventName);
            if (breakEventName != null && !breakEventName.isEmpty()) {
                WIAEngineJNI.postEvent(objectId, breakEventName);
            }
        }
        // 50ms 足够让引擎消化 Stop；原 200ms 切预设/切歌会空一截（听感卡一下）。与 iOS usleep(50ms) 对齐。
        try { Thread.sleep(50); } catch (InterruptedException ignored) {}
        return true;
    }

    private void nativePlayTrackAt(int listIdx, int musicIdx) {
        nativePlayTrackAt(listIdx, musicIdx, playGeneration);
    }

    private void nativePlayTrackAt(int listIdx, int musicIdx, long generation) {
        if (generation != playGeneration) {
            Log.i(TAG, "nativePlayTrackAt skipped (superseded) gen=" + generation + " cur=" + playGeneration);
            return;
        }
        shouldStop = false;
        // 真正起播（含 UI 播放、自动下一曲、媒体控制续播）均属于「正在播放」，清除用户暂停标记。
        userPaused = false;
        // 焦点门禁前保持 mute：REQUEST_FAILED 时禁止出声叠播（旧路径先 unmute/PostEvent 再异步 requestFocus）。
        Log.i(TAG, "nativePlayTrackAt listIdx=" + listIdx + " musicIdx=" + musicIdx);
        crackleProbe("nativePlayTrackAt.enter",
                "reqListIdx=" + listIdx + " reqMusicIdx=" + musicIdx + " gen=" + generation,
                true);

        String listPath = persistentDataPath + "/WanosAudio/MusicLists.json";
        if (playlist == null || playlist.musicLists.isEmpty()) {
            if (!loadPlaylistFromFile(listPath)) {
                Log.e(TAG, "nativePlayTrackAt loadPlaylistFromFile failed");
                return;
            }
        } else {
            Log.i(TAG, "skip loadPlaylistFromFile, already in memory albumCount=" + playlist.musicLists.size());
        }
        if (listIdx < 0 || listIdx >= playlist.musicLists.size()) return;
        NativeMusicList listBounds = playlist.musicLists.get(listIdx);
        if (musicIdx < 0 || musicIdx >= listBounds.musics.size()) return;

        int tryL = listIdx;
        int tryM = musicIdx;
        int maxSteps = nativePlaylistMaxAdvanceSteps(listIdx);
        int chosenL = -1;
        int chosenM = -1;
        int[] nextPair = new int[2];
        for (int step = 0; step < maxSteps; step++) {
            if (tryL < 0 || tryL >= playlist.musicLists.size()) break;
            NativeMusicList tl = playlist.musicLists.get(tryL);
            if (tryM < 0 || tryM >= tl.musics.size()) break;
            if (isNativeTrackResourcesPlayable(tryL, tryM)) {
                chosenL = tryL;
                chosenM = tryM;
                break;
            }
            computeNextTrackIndices(tryL, tryM, nextPair);
            tryL = nextPair[0];
            tryM = nextPair[1];
        }
        if (chosenL < 0) {
            Log.w(TAG, "No playable track on disk after skip, start list=" + listIdx + " music=" + musicIdx + " — not sending Stop");
            return;
        }
        listIdx = chosenL;
        musicIdx = chosenM;

        NativeMusicList list = playlist.musicLists.get(listIdx);
        playlist.currentListIndex = listIdx;
        playlist.currentMusicIndex = musicIdx;
        NativeMusic music = list.musics.get(musicIdx);

        String bankPath = getBankPath(music.bankName);
        if (bankPath == null) {
            Log.e(TAG, "Bank not found after skip: " + music.bankName);
            return;
        }

        String guidStr = getBankGuidForMusic(music);
        if (guidStr == null || guidStr.isEmpty()) {
            Log.e(TAG, "Bank Guid not found: " + music.bankName + ", check ProjectMetadata.json in " + persistentDataPath + "/WanosAudio/");
            return;
        }
        long bankGuid = parseBankGuid(guidStr);
        if (bankGuid == 0) {
            Log.e(TAG, "Invalid bank guid for: " + music.bankName);
            return;
        }

        // C# 面板预热 / 冷启动已 decode 时禁止再 LoadBank（会与 Unity 抢 WIA 锁卡一帧）。
        cancelDeferredUnloadIfMatches(music.bankName);
        boolean alreadyDecoded = WIAEngineJNI.isBankDecoded(bankGuid) == kEF_SUCCESS;
        if (alreadyDecoded) {
            loadedBanks.add(music.bankName);
            Log.i(TAG, "skip LoadBank, already decoded: " + music.bankName);
        } else if (!loadedBanks.contains(music.bankName)) {
            int r = WIAEngineJNI.loadBankFromPath(bankPath);
            if (r == kEF_SUCCESS) loadedBanks.add(music.bankName);
        }

        boolean decoded = false;
        for (int i = 0; i < 500; i++) {
            if (shouldStop) return;
            if (generation != playGeneration) {
                Log.i(TAG, "decode wait aborted (superseded) gen=" + generation + " cur=" + playGeneration);
                return;
            }
            if (WIAEngineJNI.isBankDecoded(bankGuid) == kEF_SUCCESS) {
                decoded = true;
                break;
            }
            try { Thread.sleep(50); } catch (InterruptedException ignored) {}
        }
        if (!decoded) {
            Log.w(TAG, "Bank decode not ready, keep current playback: " + music.bankName);
            return;
        }
        if (generation != playGeneration) return;

        // 先抢焦点再 PostEvent/unmute：失败则 abort，对方继续播、波萝保持静音。
        if (!ensureFocusBeforeAudible("nativePlayTrackAt")) {
            return;
        }
        if (generation != playGeneration) return;
        if (!forceStopCurrentPlayback()) return;
        if (generation != playGeneration) return;

        String bankToUnload = previousMusicBankName;
        currentInstanceId = WIAEngineJNI.postEventWithCallback(objectId, music.eventName);
        Log.i(TAG, "PostEventWithCallback: event=" + music.eventName + " instanceId=" + currentInstanceId);

        // 禁止满音量硬 unmute（冷启动 AAudio 刚 start 时 gain=1 + SetMute(false) = 滋啦）。
        // 近静音 unmute 再爬坡；末步会清衰减（applyMusicGain 1.0 enable=false）。
        runGuardedUnmuteOnCurrentThread("native_play");

        previousMusicBankName = music.bankName;
        unloadPreviousMusicBankIfNeeded(bankToUnload, music.bankName);

        startMediaPlaybackForegroundService();
        BoluoKeepPlayingMediaAnchor.ensureResident();

        // Update NowPlaying
        String listName = list.musicsName != null ? list.musicsName : "";
        String musicName = music.musicName != null ? music.musicName : "";
        WIAEngineJNI.updateNowPlayingInfo(
                musicName.isEmpty() ? listName : musicName,
                getCoverPathForNowPlaying(),
                true);

        // Notify Unity（lambda 只能捕获 final / effectively final，故用拷贝）。
        // 必须 BoluoMusicPlayerAndroid：默认 unityObjectName=WanosMusicPlayer 无此回调。
        final int notifyListIdx = listIdx;
        final int notifyMusicIdx = musicIdx;
        mainHandler.post(() -> {
            try {
                UnityPlayer.UnitySendMessage(
                        "BoluoMusicPlayerAndroid",
                        "OnNativeTrackChanged",
                        notifyListIdx + "," + notifyMusicIdx);
            } catch (Exception e) {
                Log.w(TAG, "UnitySendMessage failed: " + e.getMessage());
            }
        });
    }

    /** 切不同 music bank 后卸旧包；不卸 Common_* / 当前曲。标准版延迟 2s，避开起播帧 GC。 */
    private void unloadPreviousMusicBankIfNeeded(String previousBank, String currentBank) {
        if (previousBank == null || previousBank.isEmpty()) return;
        if (previousBank.equals(currentBank)) return;
        if ("Common_Init".equals(previousBank) || "Common_Music".equals(previousBank)) return;
        cancelDeferredUnloadIfMatches(currentBank);
        if (!shouldDeferPreviousMusicBankUnload()) {
            doUnloadMusicBank(previousBank);
            return;
        }
        cancelDeferredMusicBankUnload();
        pendingUnloadBank = previousBank;
        final String bank = previousBank;
        pendingUnloadRunnable = () -> playbackExecutor.execute(() -> {
            if (bank.equals(previousMusicBankName))
                return;
            if (!bank.equals(pendingUnloadBank))
                return;
            doUnloadMusicBank(bank);
            pendingUnloadBank = null;
            pendingUnloadRunnable = null;
        });
        mainHandler.postDelayed(pendingUnloadRunnable, PREVIOUS_MUSIC_BANK_UNLOAD_DEFER_MS);
        Log.i(TAG, "defer Unload previous music bank=" + previousBank
                + " in " + PREVIOUS_MUSIC_BANK_UNLOAD_DEFER_MS + "ms (standard)");
    }

    private void doUnloadMusicBank(String bank) {
        if (shouldPinLoadedBanksWhileKeepPlaying()) {
            Log.i(TAG, "[CWPA-audio] pin bank skip unload=" + bank);
            return;
        }
        int result = WIAEngineJNI.unloadBank(bank);
        loadedBanks.remove(bank);
        Log.i(TAG, "Unload previous music bank=" + bank + " result=" + result);
    }

    /** 后台续播：Unity 已 pause，禁止卸 Music/已加载 bank，循环流只靠原生引擎。 */
    private boolean shouldPinLoadedBanksWhileKeepPlaying() {
        return appInBackground && !backgroundPlayDisabled && !userPaused && !shouldStop;
    }

    private void cancelDeferredMusicBankUnload() {
        if (pendingUnloadRunnable != null) {
            mainHandler.removeCallbacks(pendingUnloadRunnable);
            pendingUnloadRunnable = null;
        }
        pendingUnloadBank = null;
    }

    private void cancelDeferredUnloadIfMatches(String bank) {
        if (bank != null && bank.equals(pendingUnloadBank)) {
            Log.i(TAG, "cancel deferred unload, bank reused: " + bank);
            cancelDeferredMusicBankUnload();
        }
    }

    /**
     * 与宿主 {@code BoluoDeviceProfile.isUltra4K} 对齐：4K 立即卸；标准版延迟。
     * 名单禁止在此复制，缺类时保持旧行为（立即卸）以免误伤 4K。
     */
    private boolean shouldDeferPreviousMusicBankUnload() {
        try {
            Class<?> profile = Class.forName("com.wanos.boluo.basic.device.BoluoDeviceProfile");
            Context ctx = resolveAppContext();
            Boolean ultra;
            if (ctx != null) {
                ultra = (Boolean) profile.getMethod("isUltra4K", Context.class).invoke(null, ctx);
            } else {
                ultra = (Boolean) profile.getMethod("isUltra4K").invoke(null);
            }
            return ultra == null || !ultra;
        } catch (Throwable t) {
            Log.w(TAG, "BoluoDeviceProfile unavailable, keep immediate unload: " + t.getMessage());
            return false;
        }
    }

    /**
     * 当前 BGM 实例是否还在。全局 getPlaybackState() 会把雨/风/脚步算进 Started，
     * 不能当作「歌还在」——负一屏暂停 forceStop 后实例已没，场景音仍让引擎 Started。
     * Unity 直发 PostEvent 时认 {@link #unityMusicInstanceId}。
     */
    private boolean hasLiveMusicInstance() {
        return currentInstanceId != 0 || unityMusicInstanceId != 0;
    }

    /** Unity AttachMusicVoice / Detach：登记或清空后台曲终用的 voice。 */
    public void noteUnityMusicVoice(long instanceId) {
        unityMusicInstanceId = instanceId;
        // CustomSRP 不走 configure(objectId=101)；在此钉死，后台 Stop/Play/Break 才与 C# 同对象。
        if (objectId == 0) {
            objectId = DEFAULT_MUSIC_OBJECT_ID;
        }
        Log.i(TAG, "noteUnityMusicVoice id=" + instanceId + " musicObj=" + musicObjectId());
    }

    /**
     * C# 引擎回调线程经 {@code AndroidJNI.InvokeAttached} 直调（勿用 AndroidJavaObject）。
     * Unity pause 后 PlayerLoop 停，只有这条能在锁屏曲终切下一首。
     */
    public static boolean tryHandleBackgroundMusicEndedStatic(long instanceId) {
        try {
            return getInstance().tryHandleBackgroundMusicEnded(instanceId);
        } catch (Throwable t) {
            Log.w(TAG, "tryHandleBackgroundMusicEndedStatic: " + t.getMessage());
            return false;
        }
    }

    /**
     * 锁屏曲终（Unity 回调线程经 JNI）：循环/随机按传输计划切下一首。
     * @return true 已处理（C# 勿再入队 Advance）
     */
    public boolean tryHandleBackgroundMusicEnded(long instanceId) {
        if (!appInBackground) {
            return false;
        }
        if (userPaused || shouldStop) {
            Log.i(TAG, "bg Ended ignored userPaused=" + userPaused + " shouldStop=" + shouldStop);
            return false;
        }
        if (!transportPlanValid) {
            // 计划稍后由 PublishPlan 补上；pending 给 observe / 下次 SetPlan 再切。
            pendingUnityMusicEndedId = instanceId;
            Log.w(TAG, "bg Ended deferred: no transport plan id=" + instanceId);
            return false;
        }
        // C# MusicPlayMode：1 单曲自循环不切；2 列表 / 3 随机才自动下一首。
        if (transportMode != 2 && transportMode != 3) {
            Log.i(TAG, "bg Ended ignored: single mode=" + transportMode);
            return false;
        }
        if (instanceId != 0) {
            boolean matchUnity = unityMusicInstanceId != 0 && instanceId == unityMusicInstanceId;
            boolean matchNative = currentInstanceId != 0 && instanceId == currentInstanceId;
            if (!matchUnity && !matchNative
                    && (unityMusicInstanceId != 0 || currentInstanceId != 0)) {
                Log.d(TAG, "bg Ended ignored stale instance=" + instanceId
                        + " unity=" + unityMusicInstanceId + " native=" + currentInstanceId);
                return false;
            }
        }
        pendingUnityMusicEndedId = 0;
        unityMusicInstanceId = 0;
        if (currentInstanceId != 0 && (instanceId == 0 || instanceId == currentInstanceId)) {
            lastStoppedInstanceId = currentInstanceId;
            currentInstanceId = 0;
        }
        Log.i(TAG, "bg Ended → transport auto-next mode=" + transportMode
                + " current=" + transportCurrent + " musicObj=" + musicObjectId());
        mainHandler.post(() -> playTransportTrackRelative(+1, "bg_ended"));
        return true;
    }

    /** 供调试 / 外部登记「后台曲终待切」；observe / PublishPlan 消费。 */
    public static void notePendingUnityMusicEnded(long instanceId) {
        WanosMusicPlayerNative inst = getInstance();
        inst.pendingUnityMusicEndedId = instanceId;
        Log.i(TAG, "notePendingUnityMusicEnded id=" + instanceId);
    }

    public void refreshSelfMixerAsync() {
        playbackExecutor.execute(() -> {
            boolean active = audioFocusManager.scanSelfMediaPlaybackActive();
            BoluoAudioStutterProbe.cacheMixer(active);
            Log.i(TAG, "mixer cache refresh selfActive=" + active
                    + " trace=" + BoluoAudioStutterProbe.traceId());
        });
    }

    private boolean trySkipKeepPlayingReset(String reopenReason, boolean engineStarted) {
        boolean flagOn = BoluoAudioStutterProbe.skipReclaimReset(null);
        boolean forceLegacy = BoluoAudioStutterProbe.forceLegacy(null);
        if (!BoluoAudioStutterProbe.shouldSkipKeepPlayingReset(
                flagOn,
                forceLegacy,
                userPaused,
                shouldStop,
                backgroundPlayDisabled,
                engineStarted,
                reopenReason)) {
            return false;
        }
        BoluoAudioStutterProbe.MixerCache mixer = BoluoAudioStutterProbe.readMixerCache();
        if (mixer.stale) {
            BoluoAudioStutterProbe.noteStaleMixer();
            Log.i(TAG, "skip reset aborted stream_health=stale reason=" + reopenReason
                    + " trace=" + BoluoAudioStutterProbe.traceId());
            return false;
        }
        if (!mixer.active) {
            Log.i(TAG, "skip reset aborted stream_health=dead reason=" + reopenReason
                    + " trace=" + BoluoAudioStutterProbe.traceId());
            return false;
        }
        BoluoAudioStutterProbe.noteSkipReset(reopenReason, "ok");
        return true;
    }

    /**
     * 后台续播自愈：不靠 Unity Update。
     * selfAlive 假阴性不得单独当死机；静音 / 引擎非 Started / BGM 实例丢失才升级。
     * L0 只 Resume_ALL+unmute；L1 同一代表多一次 Reset（禁止周期 prophylactic）。
     */
    private static final long KEEP_PLAYING_OBSERVE_FIRST_MS = 8_000L;
    private static final long KEEP_PLAYING_OBSERVE_INTERVAL_MS = 5_000L;
    private static final long KEEP_PLAYING_RECOVER_COOLDOWN_MS = 3_000L;
    private final Runnable keepPlayingObserve = this::onKeepPlayingObserve;
    private volatile int keepPlayingRecoverStage = 0;
    private volatile long keepPlayingRecoverGen = -1L;
    private volatile long keepPlayingLastRecoverElapsed = 0L;
    /** 同一 foregroundResumeGeneration 内 L1 Reset 只允许一次。 */
    private volatile boolean keepPlayingL1Used = false;
    /** 静音锚点播放头连续不动的次数。selfAlive 假阴性不能当死机，头不走才是 HAL 拆输出。 */
    private int keepPlayingAnchorHead = -1;
    private int keepPlayingAnchorStallStreak = 0;
    private volatile boolean keepPlayingAnchorStalled = false;

    private void armKeepPlayingAliveWatch() {
        keepPlayingRecoverStage = 0;
        keepPlayingRecoverGen = foregroundResumeGeneration;
        keepPlayingLastRecoverElapsed = 0L;
        keepPlayingL1Used = false;
        keepPlayingAnchorHead = -1;
        keepPlayingAnchorStallStreak = 0;
        keepPlayingAnchorStalled = false;
        keepPlayingHandler.removeCallbacks(keepPlayingObserve);
        keepPlayingHandler.postDelayed(keepPlayingObserve, KEEP_PLAYING_OBSERVE_FIRST_MS);
        Log.i(TAG, "[CWPA-audio] keepPlayingObserve armed (MEDIA keep-alive, no device reopen)");
    }

    private void cancelKeepPlayingAliveWatch() {
        keepPlayingHandler.removeCallbacks(keepPlayingObserve);
        keepPlayingRecoverStage = 0;
    }

    private void onKeepPlayingObserve() {
        if (!shouldSuppressTransientPauseInBackground()) {
            cancelKeepPlayingAliveWatch();
            return;
        }
        // C# 曲终 JNI 若未即时切歌，消费 pending，避免锁屏后只剩声景直到开屏。
        long pendingEnded = pendingUnityMusicEndedId;
        if (pendingEnded != 0) {
            pendingUnityMusicEndedId = 0;
            if (tryHandleBackgroundMusicEnded(pendingEnded)) {
                Log.i(TAG, "[CWPA-audio] keepPlayingObserve drained pending Ended → auto-next");
                keepPlayingHandler.postDelayed(keepPlayingObserve, KEEP_PLAYING_OBSERVE_INTERVAL_MS);
                return;
            }
        }
        final boolean selfActive = audioFocusManager.scanSelfMediaPlaybackActive();
        final int state = WIAEngineJNI.getPlaybackState();
        BoluoAudioStutterProbe.cacheMixer(selfActive);
        boolean anchorStalled = noteAnchorHeadStalled();
        keepPlayingAnchorStalled = anchorStalled;
        // selfAlive 在创维上对 USAGE_GAME / uid=-1 假阴性：不得单独触发 Resume_ALL。
        // 锚点头连续两拍不动 ≈ MEDIA 保活轨被 HAL 拆成僵尸；只重建 MEDIA，禁止 Reset WIA。
        final boolean hardDead = musicEngineMuted
                || state != PlaybackState_Started
                || !hasLiveMusicInstance()
                || anchorStalled;
        Log.i(TAG, "[CWPA-audio] keepPlayingObserve selfAlive=" + selfActive
                + " state=" + state
                + " muted=" + musicEngineMuted
                + " musicLive=" + hasLiveMusicInstance()
                + " hardDead=" + hardDead
                + " anchorStall=" + anchorStalled
                + " stage=" + keepPlayingRecoverStage);
        if (!hardDead) {
            if (keepPlayingRecoverStage != 0 || keepPlayingL1Used) {
                keepPlayingRecoverStage = 0;
                keepPlayingL1Used = false;
            }
            keepPlayingHandler.postDelayed(keepPlayingObserve, KEEP_PLAYING_OBSERVE_INTERVAL_MS);
            return;
        }
        final long gen = foregroundResumeGeneration;
        playbackExecutor.execute(() -> keepPlayingRecoverOnExecutor(gen));
        keepPlayingHandler.postDelayed(keepPlayingObserve, KEEP_PLAYING_OBSERVE_INTERVAL_MS);
    }

    /**
     * 锚点写心跳停转 = HAL 拆了 MEDIA 保活轨。
     * 禁止 getPlaybackHeadPosition：SKS30 NoAudioTrack 后会挂死 HandlerThread，L1 永不到达。
     */
    private boolean noteAnchorHeadStalled() {
        // 2.5s 无成功 write ≈ 一帧以上停写；一次即硬死，勿等两拍（observe 间隔 5s）。
        if (BoluoKeepPlayingMediaAnchor.isWriteStalled(2_500L)) {
            keepPlayingAnchorStallStreak = 2;
            return true;
        }
        if (!BoluoKeepPlayingMediaAnchor.isRunning()) {
            keepPlayingAnchorStallStreak = 0;
            keepPlayingAnchorHead = -1;
            return false;
        }
        keepPlayingAnchorStallStreak = 0;
        return false;
    }

    /**
     * 焦点回调在后台禁止自己 Reset 风暴，改走与 observe 共用的 MEDIA 保活恢复。
     */
    private void requestKeepPlayingRecoverFromFocus(String reason) {
        Log.i(TAG, "[CWPA-audio] focus recover delegate reason=" + reason);
        keepPlayingRecoverOnExecutor(foregroundResumeGeneration);
    }

    /** 长后台：MEDIA 锚点脉冲保活间隔（禁止 ResetPlaybackDevice）。 */
    private static final long KEEP_PLAYING_ANCHOR_PULSE_MS = 30_000L;

    private void keepPlayingRecoverOnExecutor(long gen) {
        if (gen != foregroundResumeGeneration || !shouldSuppressTransientPauseInBackground()) {
            return;
        }
        // 微信/来电丢焦期间禁止 L0 Resume_ALL：否则音效约数秒回潮、音乐仍 Pause。
        if (voicesPausedForFocusLoss || audioFocusManager.isPendingResume()) {
            Log.i(TAG, "[CWPA-audio] keepPlayingRecover skip (focus yield)"
                    + " voicesPaused=" + voicesPausedForFocusLoss
                    + " pendingResume=" + audioFocusManager.isPendingResume());
            return;
        }
        long now = SystemClock.elapsedRealtime();
        if (keepPlayingRecoverGen != gen) {
            keepPlayingRecoverGen = gen;
            keepPlayingRecoverStage = 0;
            keepPlayingL1Used = false;
            keepPlayingLastRecoverElapsed = 0L;
        }
        int state = WIAEngineJNI.getPlaybackState();
        boolean stillDead = musicEngineMuted
                || state != PlaybackState_Started
                || !hasLiveMusicInstance()
                || keepPlayingAnchorStalled;
        if (!stillDead) {
            keepPlayingRecoverStage = 0;
            keepPlayingL1Used = false;
            return;
        }
        String why = keepPlayingAnchorStalled ? "anchor_stall"
                : musicEngineMuted ? "muted"
                : state != PlaybackState_Started ? "not_started"
                : !hasLiveMusicInstance() ? "no_instance"
                : "hard";
        if (keepPlayingL1Used) {
            if (now - keepPlayingLastRecoverElapsed < KEEP_PLAYING_ANCHOR_PULSE_MS) {
                return;
            }
            Log.i(TAG, "[CWPA-audio] keepPlayingRecover MEDIA pulse why=" + why);
            applyKeepPlayingMediaKeepAlive(why, /*forcePulse*/ true);
            keepPlayingLastRecoverElapsed = now;
            return;
        }
        if (keepPlayingRecoverStage > 0
                && now - keepPlayingLastRecoverElapsed < KEEP_PLAYING_RECOVER_COOLDOWN_MS) {
            Log.i(TAG, "[CWPA-audio] keepPlayingRecover cooldown stage=" + keepPlayingRecoverStage);
            return;
        }
        if (keepPlayingRecoverStage == 0) {
            // 锚点头不动 = HAL 已拆输出：L0 软保活不够，直接 L1 reset+replay（线上同语义）。
            if ("anchor_stall".equals(why)) {
                Log.i("BoluoAudioStutter", "recover L1_direct why=" + why
                        + " stage=0 muted=" + musicEngineMuted
                        + " state=" + state);
                if (applyKeepPlayingRecoverL1(why)) {
                    keepPlayingRecoverStage = 2;
                    keepPlayingL1Used = true;
                }
            } else {
                Log.i("BoluoAudioStutter", "recover L0 why=" + why
                        + " muted=" + musicEngineMuted
                        + " state=" + state);
                applyKeepPlayingRecoverL0(why);
                keepPlayingRecoverStage = 1;
            }
            keepPlayingLastRecoverElapsed = now;
            return;
        }
        if (keepPlayingRecoverStage == 1) {
            Log.i("BoluoAudioStutter", "recover L1_escalate why=" + why
                    + " muted=" + musicEngineMuted
                    + " state=" + state);
            if (applyKeepPlayingRecoverL1(why)) {
                keepPlayingRecoverStage = 2;
                keepPlayingL1Used = true;
            }
            keepPlayingLastRecoverElapsed = now;
        }
    }

    private void applyKeepPlayingRecoverL0(String why) {
        Log.i(TAG, "[CWPA-audio] keepPlayingRecover L0 keep-alive why=" + why);
        applyKeepPlayingMediaKeepAlive(why, /*forcePulse*/ "anchor_stall".equals(why));
        int state = WIAEngineJNI.getPlaybackState();
        // 仅 mute / 锚点僵尸：不发 Resume_ALL（会 Ended 切歌）。实例已死或未 Started 才 Resume。
        if ("muted".equals(why)
                || "anchor_stall".equals(why)
                || (hasLiveMusicInstance() && state == PlaybackState_Started)) {
            Log.i(TAG, "[CWPA-audio] keepPlayingRecover L0 unmute/MEDIA only why=" + why);
            return;
        }
        try {
            WIAEngineJNI.postEvent(0, "Resume_ALL");
            Log.i(TAG, "[CWPA-audio] keepPlayingRecover L0 Resume_ALL why=" + why);
        } catch (Exception e) {
            Log.w(TAG, "keepPlayingRecover L0 Resume_ALL: " + e.getMessage());
        }
    }

    /**
     * L1：对齐线上 3eec80b6 — HAL/UsecaseValidator 拆掉输出后 unmute 无效，
     * 必须 ResetPlaybackDevice + 重挂设备 + 续播当前曲，才能一直播。
     */
    private boolean applyKeepPlayingRecoverL1(String why) {
        int stateNow = WIAEngineJNI.getPlaybackState();
        if (stateNow == PlaybackState_Started && !musicEngineMuted) {
            Log.i(TAG, "[BoluoAudioFg] keepPlaying L1 skipped (graph started) why=" + why);
            BoluoAudioStutterProbe.noteSwitchClass("keep_graph", "skip_l1_" + why);
            applyKeepPlayingMediaKeepAlive(why, true);
            return false;
        }
        Log.i(TAG, "[CWPA-audio] keepPlayingRecover L1 reset+replay why=" + why
                + " (match online 3eec80b6)");
        Log.i("BoluoAudioStutter", "recover L1_reset+replay why=" + why
                + " — audible glitch risk");
        startMediaPlaybackForegroundService();
        try {
            BoluoKeepPlayingMediaAnchor.restartIfZombieOrStopped();
        } catch (Exception e) {
            Log.w(TAG, "keepPlayingRecover L1 anchor: " + e.getMessage());
        }
        keepPlayingAnchorHead = -1;
        keepPlayingAnchorStallStreak = 0;
        keepPlayingAnchorStalled = false;
        musicEngineMuted = true;
        probeSetMute(true);
        resetAndSetPlaybackDevice(true);
        musicEngineMuted = false;
        probeSetMute(false);
        try {
            if (hasLiveMusicInstance()) {
                WIAEngineJNI.postEvent(musicObjectId(), "Resume_ALL");
                runGuardedUnmuteOnCurrentThread("keep_playing_l1");
                Log.i(TAG, "[CWPA-audio] keepPlayingRecover L1 Resume_ALL after reset");
            } else if (transportPlanValid) {
                // CustomSRP Java playlist 空，必须走传输计划，不能 nativePlayTrackAt。
                playTransportTrackRelative(0, "keep_playing_l1");
                Log.i(TAG, "[CWPA-audio] keepPlayingRecover L1 transport replay after reset");
            } else {
                nativePlayTrackAt(playlist.currentListIndex, playlist.currentMusicIndex);
                Log.i(TAG, "[CWPA-audio] keepPlayingRecover L1 replay after reset");
            }
        } catch (Exception e) {
            Log.w(TAG, "keepPlayingRecover L1 replay: " + e.getMessage());
            try {
                if (transportPlanValid) {
                    playTransportTrackRelative(0, "keep_playing_l1_fallback");
                } else {
                    nativePlayTrackAt(playlist.currentListIndex, playlist.currentMusicIndex);
                }
            } catch (Exception e2) {
                Log.w(TAG, "keepPlayingRecover L1 nativePlay: " + e2.getMessage());
            }
        }
        return true;
    }

    /** 拉 FGS、重建僵尸 MEDIA 锚点、确保 unmute。L0 软保活，不 Reset。 */
    private void applyKeepPlayingMediaKeepAlive(String why, boolean forcePulse) {
        startMediaPlaybackForegroundService();
        if (forcePulse
                || "anchor_stall".equals(why)
                || !BoluoKeepPlayingMediaAnchor.isRunning()) {
            try {
                BoluoKeepPlayingMediaAnchor.restartIfZombieOrStopped();
            } catch (Exception e) {
                Log.w(TAG, "keepPlaying MEDIA restart: " + e.getMessage());
            }
            keepPlayingAnchorHead = -1;
            keepPlayingAnchorStallStreak = 0;
            keepPlayingAnchorStalled = false;
        } else {
            BoluoKeepPlayingMediaAnchor.ensureResident();
        }
        musicEngineMuted = false;
        probeSetMute(false);
    }

    /**
     * @deprecated 周期/预防性 reopen 不是保活根解；恢复只走 MEDIA 锚点保活。
     */
    @Deprecated
    private void scheduleKeepPlayingReopen(String reason) {
        Log.w(TAG, "[CWPA-audio] scheduleKeepPlayingReopen DISABLED reason=" + reason
                + " (use MEDIA keep-alive L0/L1)");
    }

    private void resetAndSetPlaybackDevice(boolean muteBetween) {
        // 对齐线上 3eec80b6：丢轨后必须能 Reset 再建，否则 OEM 拆 GAME 后永久无声。
        // 「禁 reset」是后加的刺啦防护，会把后台续播修成停播。
        Trace.beginSection("resetPlaybackDevice");
        try {
            WIAEngineJNI.resetPlaybackDevice();
        } finally {
            Trace.endSection();
        }
        if (muteBetween) {
            probeSetMute(true);
        }
        Trace.beginSection("setPlaybackDevice");
        try {
            WIAEngineJNI.setPlaybackDevice(DEFAULT_DEVICE_JSON);
        } finally {
            Trace.endSection();
        }
    }

    /**
     * 用户明确续播：Resume_ALL + Resume_Music + unmute。
     * 负一屏 play、岛内点按、定时结束弹窗「取消/重复」resumeFromUserAction 共用。
     * 焦点失败返回 false，调用方不得 RequestResumeAll。
     * <p>
     * CustomSRP 起播走 Unity {@code PostEvent}，{@link #currentInstanceId} 常为 0
     * （仅 {@link #nativePlayTrackAt} 会写入）。{@link #pauseForTimerEnd()} 仍发全局
     * Pause_Music + mute，故续播不能按「有无 native instance」分支去
     * {@code nativePlayTrackAt}（playlist 空/资源未齐时静默 return，且不 unmute → 取消无声）。
     */
    private boolean resumeAudibleMusic(String reason) {
        if (!ensureFocusBeforeAudible(reason)) {
            return false;
        }
        // 焦点已经回来：用户点播放可以出声，卡片不再被抢焦闩压成暂停。
        systemSuspended = false;
        Log.i(TAG, reason + ": resumePaired liveInstance=" + hasLiveMusicInstance());
        resumePairedVoices(reason);
        runGuardedUnmuteOnCurrentThread(reason);
        mainHandler.post(this::updateNowPlayingForCurrentTrack);
        return true;
    }

    /**
     * 丢焦曾 Pause_Music+Pause_ALL 时，回焦不能只 unmute（音效会停在 Pause）。
     * 曲已 Pause_Music 非 Started，Resume_ALL 不会踩活实例 Ended 切歌坑。
     */
    private boolean resumeVoicesAfterFocusIfNeeded(String reason) {
        if (!voicesPausedForFocusLoss) {
            return false;
        }
        resumePairedVoices(reason);
        Log.i(TAG, "[BoluoAudioFg] resumeVoicesAfterFocus reason=" + reason);
        return true;
    }

    /**
     * 重新获得音频焦点后恢复播放（在焦点管理器回调中触发）。
     * 以 BGM 实例是否还在为准，不用全局引擎 Started（场景音会把 Started 撑住）。
     * 用户暂停不得走这条自动路。
     */
    private void resumeAfterFocusRegained() {
        final long scheduleGen = foregroundResumeGeneration;
        playbackExecutor.execute(() -> {
            if (scheduleGen != foregroundResumeGeneration) {
                Log.i(TAG, "resumeAfterFocusRegained aborted (stale gen="
                        + scheduleGen + " cur=" + foregroundResumeGeneration + ")");
                return;
            }
            if (shouldStop || userPaused || remoteCommands.isPausedForTimerEnd()) {
                // 用户已暂停或定时到点：不出声，但清掉抢焦残留，避免回前台对账把这次暂停降级成系统挂起。
                systemSuspended = false;
                voicesPausedForFocusLoss = false;
                Log.i(TAG, "resumeAfterFocusRegained skip audible, cleared sticky focus flags"
                        + " userPaused=" + userPaused + " shouldStop=" + shouldStop
                        + " timerEnd=" + remoteCommands.isPausedForTimerEnd());
                return;
            }
            BoluoWeChatVoiceResumePolicy.Plan weChatPlan = weChatVoiceReturnPlan(!appInBackground);
            boolean focusLossHold = voicesPausedForFocusLoss
                    || (musicEngineMuted && audioFocusManager.isPendingResume());
            if (focusLossHold && !weChatPlan.restoresMusicAndEffects()) {
                // 仍按住微信说话，或 QQ/B 站还在播：禁止只把音效 Resume 回来。
                Log.i(TAG, "resumeAfterFocusRegained deferred reason=" + weChatPlan.reason);
                return;
            }
            systemSuspended = false;
            boolean reopenDevice = audioFocusManager.needsPlaybackReopen();
            String reopenReason = audioFocusManager.consumeReopenReason();
            int state = WIAEngineJNI.getPlaybackState();
            boolean started = state == PlaybackState_Started;
            // 前后台 / 切 QQ 只是丢焦。self_gone 在 Pause 后或 USAGE_GAME 扫不到自己的轨时是假阳性，
            // 不能当成输出被拆：回前台会先爬满再 ResetPlaybackDevice，听感咯噔。
            // 只有耳机/路由断开才强制重建。实例已死、引擎不在 Started 时仍走下面的重建。
            // 引擎仍是 Started 时，路由标记也先保住图。出声时 ResetPlaybackDevice 就是卡一下。
            boolean outputTornDown = BoluoAudioStutterProbe.REOPEN_DEVICE_ROUTE.equals(reopenReason)
                    && !started;
            boolean keepGraph = !outputTornDown;
            if (trySkipKeepPlayingReset(reopenReason, started)) {
                if (hasLiveMusicInstance() || voicesPausedForFocusLoss) {
                    boolean resumed = resumeVoicesAfterFocusIfNeeded("focus_resume_skip_reset");
                    runGuardedUnmuteOnCurrentThread("focus_resume_skip_reset");
                    mainHandler.post(this::updateNowPlayingForCurrentTrack);
                    notifyUnityTrackChanged(playlist.currentListIndex, playlist.currentMusicIndex);
                    if (resumed) {
                        notifyUnityAudioRestored("focus_resume_skip_reset");
                    }
                }
                return;
            }
            if (hasLiveMusicInstance() && started && keepGraph) {
                boolean didResumeVoices = resumeVoicesAfterFocusIfNeeded("focus_resume");
                runGuardedUnmuteOnCurrentThread("focus_resume");
                Log.i(TAG, "[BoluoAudioFg] resume live started voices=" + didResumeVoices);
                mainHandler.post(this::updateNowPlayingForCurrentTrack);
                notifyUnityTrackChanged(playlist.currentListIndex, playlist.currentMusicIndex);
                if (didResumeVoices) {
                    notifyUnityAudioRestored("focus_resume");
                }
                return;
            }
            if (hasLiveMusicInstance() && keepGraph) {
                boolean didResumeVoices = resumeVoicesAfterFocusIfNeeded("focus_resume");
                runGuardedUnmuteOnCurrentThread("focus_resume");
                int after = WIAEngineJNI.getPlaybackState();
                if (after == PlaybackState_Started) {
                    Log.i(TAG, "[BoluoAudioFg] resume live unmuted voices=" + didResumeVoices);
                    mainHandler.post(this::updateNowPlayingForCurrentTrack);
                    notifyUnityTrackChanged(playlist.currentListIndex, playlist.currentMusicIndex);
                    if (didResumeVoices) {
                        notifyUnityAudioRestored("focus_resume");
                    }
                    return;
                }
                // Unity pause/resume 间 getPlaybackState 可能短暂非 Started；先 unmute 再确认，避免 Reset 音效卡顿
                try {
                    Thread.sleep(RESUME_STATE_FLICKER_WAIT_MS);
                } catch (InterruptedException ignored) {
                    Thread.currentThread().interrupt();
                    return;
                }
                if (scheduleGen != foregroundResumeGeneration || shouldStop || userPaused
                        || remoteCommands.isPausedForTimerEnd()) {
                    Log.i(TAG, "resumeAfterFocusRegained aborted after flicker wait"
                            + " timerEnd=" + remoteCommands.isPausedForTimerEnd());
                    return;
                }
                if (hasLiveMusicInstance()
                        && WIAEngineJNI.getPlaybackState() == PlaybackState_Started) {
                    Log.i(TAG, "[BoluoAudioFg] resume after flicker voices=" + didResumeVoices);
                    mainHandler.post(this::updateNowPlayingForCurrentTrack);
                    notifyUnityTrackChanged(playlist.currentListIndex, playlist.currentMusicIndex);
                    if (didResumeVoices) {
                        notifyUnityAudioRestored("focus_resume_flicker");
                    }
                    return;
                }
            }
            // 实例还在、输出没被路由拆掉：Resume + 爬坡。禁止 ResetPlaybackDevice。
            // 切微信/QQ 后 playbackState 经常短暂离开 Started，以前落到重建，满增益再 mute 再拆设备，听感卡一下，回前台还可能没声。
            if (keepGraph) {
                boolean didResumeVoices = resumeVoicesAfterFocusIfNeeded("focus_resume_keep_graph");
                runGuardedUnmuteOnCurrentThread("focus_resume_keep_graph");
                Log.i(TAG, "[BoluoAudioFg] resume keep graph no reset voices="
                        + didResumeVoices + " state=" + WIAEngineJNI.getPlaybackState());
                mainHandler.post(this::updateNowPlayingForCurrentTrack);
                notifyUnityTrackChanged(playlist.currentListIndex, playlist.currentMusicIndex);
                if (didResumeVoices) {
                    notifyUnityAudioRestored("focus_resume_keep_graph");
                }
                return;
            }
            Log.i(TAG, "resumeAfterFocusRegained: reopen device instanceLive="
                    + hasLiveMusicInstance() + " started=" + started + " reopen=" + reopenDevice
                    + " reopen_reason=" + reopenReason);
            boolean keepPlayingBg = appInBackground && !backgroundPlayDisabled
                    && !userPaused && !shouldStop;
            if (keepPlayingBg) {
                requestKeepPlayingRecoverFromFocus(reopenReason);
                return;
            }
            Log.i(TAG, "[PA-904] resumeAfterFocusRegained resetPlaybackDevice");
            BoluoAudioStutterProbe.noteReset(reopenReason);
            musicEngineMuted = true;
            probeSetMute(true);
            resetAndSetPlaybackDevice(true);
            if (hasLiveMusicInstance()) {
                boolean resumed = resumeVoicesAfterFocusIfNeeded("focus_resume_reopen");
                runGuardedUnmuteOnCurrentThread("focus_resume_reopen");
                mainHandler.post(this::updateNowPlayingForCurrentTrack);
                notifyUnityTrackChanged(playlist.currentListIndex, playlist.currentMusicIndex);
                if (resumed) {
                    notifyUnityAudioRestored("focus_resume_reopen");
                }
            } else {
                nativePlayTrackAt(playlist.currentListIndex, playlist.currentMusicIndex);
                notifyUnityAudioRestored("focus_resume_replay");
            }
        });
    }

    /**
     * 通知 Unity 当前曲目已变（后台负一屏切歌后对账）。
     * 必须发到 BoluoMusicPlayerAndroid：unityObjectName 默认 WanosMusicPlayer，那边没有
     * OnNativeTrackChanged，消息会静默丢光，岛内曲名/播放态永远对不上。
     */
    private void notifyUnityTrackChanged(int listIdx, int musicIdx) {
        mainHandler.post(() -> {
            try {
                UnityPlayer.UnitySendMessage(
                        "BoluoMusicPlayerAndroid", "OnNativeTrackChanged", listIdx + "," + musicIdx);
            } catch (Exception e) {
                Log.w(TAG, "notifyUnityTrackChanged failed: " + e.getMessage());
            }
        });
    }

    /**
     * 通知栏/锁屏/负一屏「暂停」：用户暂停意图。
     * 暂停当前曲并作废在途自动下一曲，负一屏保持暂停。不拆实例，续播从暂停处继续。
     */
    private void pauseFromMediaControl() {
        playbackExecutor.execute(() -> {
            userPaused = true;
            final long gen = ++playGeneration;
            musicEngineMuted = true;
            probeSetMute(true);
            stopPairedVoices("media-pause");
            Log.i(TAG, "pauseFromMediaControl: userPaused=true paused gen=" + gen);
            mainHandler.post(() -> updateNowPlayingPlayingState(false));
            notifyUnityPlaybackState(false);
        });
    }

    /**
     * 负一屏切歌闸门。忽略时不改播放态；接受则作废尚未落地的播放/暂停，并同步 userPaused。
     * @return true 表示这次切歌应执行
     */
    private boolean acceptRemoteSkip(int direction, long nowElapsedMs) {
        if (!remoteCommands.seedAndSkip(!musicEngineMuted, userPaused, direction, nowElapsedMs)) {
            return false;
        }
        mainHandler.removeCallbacks(flushMediaPlayPauseRunnable);
        userPaused = remoteCommands.isUserManuallyPaused();
        // 对齐线上 96ec0d5f：暂停态切歌清掉暂停并出声。抢焦挂起不能把按钮继续钉在暂停。
        if (!userPaused && !remoteCommands.isPausedForTimerEnd()) {
            systemSuspended = false;
        }
        return true;
    }

    // ---------------------------------------------------------------------------
    // 后台传输：Unity 被 pause 时由原生直接驱动引擎
    // ---------------------------------------------------------------------------

    /**
     * C#（前台）推送后台传输计划。
     * <p>JSON: {@code {"mode":2,"current":0,"tracks":[{"name":"曲名","event":"Play_Music1_2",
     * "bank":"Music1_2","guid":"...","path":"/abs/Music1_2.wsbk","loaded":false}]}}
     * <p>只有 bank 文件已就绪的曲目才会进列表，保证后台切歌有东西可播。
     */
    public void setBackgroundTransportPlan(String json) {
        if (json == null || json.isEmpty()) {
            clearBackgroundTransportPlan();
            return;
        }
        try {
            JSONObject root = new JSONObject(json);
            int mode = root.optInt("mode", 1);
            JSONArray arr = root.optJSONArray("tracks");
            List<TransportTrack> list = new ArrayList<>();
            if (arr != null) {
                for (int i = 0; i < arr.length(); i++) {
                    JSONObject o = arr.optJSONObject(i);
                    if (o == null) continue;
                    TransportTrack t = new TransportTrack();
                    t.name = o.optString("name", "");
                    t.eventName = o.optString("event", "");
                    t.bankName = o.optString("bank", "");
                    t.bankGuid = o.optString("guid", "");
                    t.bankPath = o.optString("path", "");
                    t.loaded = o.optBoolean("loaded", false);
                    if (t.eventName.isEmpty()) continue;
                    list.add(t);
                }
            }
            int current = root.optInt("current", -1);
            transportTracks = list;
            transportMode = mode;
            transportCurrent = current;
            transportPlanValid = !list.isEmpty() && current >= 0 && current < list.size();
            Log.i(TAG, "setBackgroundTransportPlan tracks=" + list.size()
                    + " current=" + current + " mode=" + mode
                    + " valid=" + transportPlanValid);
            // 曲终时计划还没到：补计划后立刻切下一首（锁屏 Unity 已 pause）。
            if (transportPlanValid && appInBackground) {
                long pending = pendingUnityMusicEndedId;
                if (pending != 0) {
                    pendingUnityMusicEndedId = 0;
                    final long endedId = pending;
                    mainHandler.post(() -> tryHandleBackgroundMusicEnded(endedId));
                }
            }
        } catch (Exception e) {
            Log.w(TAG, "setBackgroundTransportPlan failed: " + e.getMessage());
            clearBackgroundTransportPlan();
        }
    }

    /** 退岛/退登：清掉后台传输计划，避免原生继续切已经失效的曲目。 */
    public void clearBackgroundTransportPlan() {
        transportTracks = Collections.emptyList();
        transportCurrent = -1;
        transportPlanValid = false;
        Log.i(TAG, "clearBackgroundTransportPlan");
    }

    /**
     * 回前台时 C# 用来对账：当前曲（事件名）+ 播放态 + 计划是否有效。
     * JSON: {@code {"event":"…","paused":false,"suspended":false,"index":1,"valid":true}}
     * paused=userPaused；suspended=抢焦挂起（PlaybackGate.SystemSuspended）。
     */
    public String getBackgroundTransportState() {
        JSONObject o = new JSONObject();
        TransportTrack cur = currentTransportTrack();
        try {
            o.put("event", cur != null ? cur.eventName : "");
            o.put("name", cur != null ? cur.name : "");
            o.put("paused", userPaused);
            o.put("suspended", systemSuspended || voicesPausedForFocusLoss);
            o.put("index", transportCurrent);
            o.put("valid", transportPlanValid);
            o.put("inBackground", appInBackground);
        } catch (Exception e) {
            Log.w(TAG, "getBackgroundTransportState failed: " + e.getMessage());
        }
        return o.toString();
    }

    private TransportTrack currentTransportTrack() {
        List<TransportTrack> list = transportTracks;
        int idx = transportCurrent;
        if (list == null || list.isEmpty()) return null;
        if (idx < 0 || idx >= list.size()) return null;
        return list.get(idx);
    }

    /** 当前曲的引擎事件名（回前台对账用）。 */
    public String getBackgroundTransportCurrentEvent() {
        TransportTrack cur = currentTransportTrack();
        return cur != null ? cur.eventName : "";
    }

    /** 当前曲名（回前台对账用）。 */
    public String getBackgroundTransportCurrentName() {
        TransportTrack cur = currentTransportTrack();
        return cur != null ? cur.name : "";
    }

    /** 原生侧的用户暂停态。 */
    public boolean isBackgroundTransportPaused() {
        return userPaused;
    }

    /** 当前曲在计划列表里的下标，-1 = 未知。 */
    public int getBackgroundTransportCurrentIndex() {
        return transportCurrent;
    }

    /** 传输计划是否可用。 */
    public boolean isBackgroundTransportPlanValid() {
        return transportPlanValid;
    }

    /**
     * 后台播放/暂停：Unity 已被 pause，走原生直接控制引擎。
     * @return true 表示已由原生处理（调用方不要再转发给 Unity）。
     */
    private boolean handleTransportInBackground(boolean pause) {
        if (!appInBackground) return false;           // 前台：交给 Unity
        if (currentTransportTrack() == null) {
            Log.w(TAG, "bg transport " + (pause ? "pause" : "play")
                    + " ignored: no transport plan");
            return false;                              // 没有计划：仍转发 Unity
        }
        // 连点合并后再落引擎，避免 pause/play/pause 的异步 unmute 把最终暂停盖掉。
        scheduleMediaPlayPause(pause);
        return true;
    }

    /** 后台传输：把已经合并好的暂停/播放意图落到引擎和负一屏卡片。 */
    private void applyBackgroundTransportPaused(boolean pause) {
        if (!pause && remoteCommands.isPausedForTimerEnd()) {
            Log.i(TAG, "bg transport resume ignored: timer end");
            updateTransportNowPlaying(false);
            return;
        }
        final long gen = ++audibleCommandGeneration;
        userPaused = pause;
        remoteCommands.adoptPlayback(!pause, pause);
        TransportTrack cur = currentTransportTrack();
        if (pause) {
            musicEngineMuted = true;
            probeSetMute(true);
            stopPairedVoices("bg-transport");
            updateTransportNowPlaying(false);
            notifyUnityPlaybackState(false);
        } else {
            // 与 flush 同栈再摘一次：写卡在本方法里，挂起闩还在就会把 PLAYING 写成 PAUSED。
            systemSuspended = false;
            resumePairedVoices("bg-transport");
            if (musicEngineMuted) {
                playbackExecutor.execute(() -> {
                    if (!BoluoNowPlayingRemoteCommandPolicy.shouldCommitAudible(
                            gen, audibleCommandGeneration, userPaused)) {
                        Log.i(TAG, "bg resume unmute dropped gen=" + gen
                                + " cur=" + audibleCommandGeneration + " userPaused=" + userPaused);
                        return;
                    }
                    runGuardedUnmuteOnCurrentThread("bg_resume");
                });
            }
            updateTransportNowPlaying(true);
            notifyUnityPlaybackState(true);
        }
        Log.i(TAG, "bg transport " + (pause ? "pause" : "resume")
                + " event=" + (cur != null ? cur.eventName : "")
                + " muted=" + musicEngineMuted
                + " gen=" + gen);
    }

    /**
     * 后台切歌：原生直接 Stop_Music + Play_* 事件。
     * @return true 表示已由原生处理（调用方不要再转发给 Unity）。
     */
    private boolean handleTransportSkipInBackground(int direction) {
        if (!appInBackground) return false;
        if (!transportPlanValid) {
            Log.w(TAG, "bg skip ignored: no transport plan dir=" + direction);
            return false;
        }
        long now = SystemClock.elapsedRealtime();
        if (!acceptRemoteSkip(direction, now)) {
            Log.i(TAG, "bg skip debounce dir=" + direction
                    + " inFlight=" + remoteCommands.isSkipInFlight());
            return true;
        }
        boolean ok = playTransportTrackRelative(direction, "bg_skip");
        // 后台无 Unity Playing 回调时靠超时放行；起播失败立刻清。
        if (!ok)
            remoteCommands.noteSkipSettled();
        return ok;
    }

    /**
     * 后台传输计划起播：postEventWithCallback，后续曲终继续走原生 Ended 链。
     * 循环/随机在 Playing 时补 Break_Music（与前台 MusicPlaylist 一致）。
     */
    private boolean playTransportTrackRelative(int direction, String reason) {
        if (!transportPlanValid) {
            Log.w(TAG, "transport play ignored: no plan reason=" + reason);
            return false;
        }
        // bg_skip：手动切歌始终本专辑取模（与 mode 无关）；邻接未就绪 → 重播当前。
        // bg_ended 等仍跟 mode，且只落可播曲。
        boolean manualSkip = "bg_skip".equals(reason);
        int target = resolveTransportTargetIndex(direction, manualSkip);
        if (target < 0) {
            Log.w(TAG, "transport play no target dir=" + direction
                    + " current=" + transportCurrent + " reason=" + reason);
            return true;
        }
        TransportTrack t = transportTracks.get(target);
        if (!ensureTransportBankLoaded(t)) {
            // 手动邻接未下完：回落重播当前，禁止停在空曲。
            if (manualSkip && target != transportCurrent
                    && transportCurrent >= 0 && transportCurrent < transportTracks.size()) {
                Log.i(TAG, "transport play adjacent not ready → replay current="
                        + transportCurrent + " tried=" + target + " bank=" + t.bankName);
                target = transportCurrent;
                t = transportTracks.get(target);
                if (!ensureTransportBankLoaded(t)) {
                    Log.w(TAG, "transport play current also not ready bank=" + t.bankName);
                    return true;
                }
            } else {
                Log.w(TAG, "transport play bank not ready reason=" + reason
                        + " bank=" + t.bankName);
                return true;
            }
        }

        unityMusicInstanceId = 0;
        long musicObj = musicObjectId();
        // 对齐前台 HardStop / forceStop：Stop+Break + 短 settle，避免 Sequence createInst 竞态。
        // 不在睡前清 userPaused：睡醒前若已暂停，禁止再 Post 播放把负一屏盖回播放中。
        long gen = ++playGeneration;
        postMusicControlEvent("Stop_Music");
        postMusicControlEvent("Break_Music");
        try { Thread.sleep(50); } catch (InterruptedException ignored) {}
        if (gen != playGeneration) {
            Log.i(TAG, "transport play superseded reason=" + reason + " gen=" + gen);
            return true;
        }
        transportCurrent = target;
        if (!BoluoNowPlayingRemoteCommandPolicy.shouldAudiblyCommitSkip(gen, playGeneration, userPaused)) {
            updateTransportNowPlaying(false);
            notifyUnityPlaybackState(false);
            notifyUnityTrackChanged(-1, target);
            Log.i(TAG, "transport play held paused reason=" + reason
                    + " index=" + target + " gen=" + gen);
            return true;
        }
        // 必须与 C# MusicObjectId 同对象，否则 Break_Music 打不中、下一首又曲终不了。
        currentInstanceId = WIAEngineJNI.postEventWithCallback(musicObj, t.eventName);
        updateTransportNowPlaying(true);
        notifyUnityPlaybackState(true);
        notifyUnityTrackChanged(-1, target);
        Log.i(TAG, "transport play reason=" + reason + " -> index=" + target
                + " event=" + t.eventName + " instance=" + currentInstanceId
                + " musicObj=" + musicObj + " gen=" + gen);
        return true;
    }

    /**
     * 传输计划下标。
     * @param manualSkip true=负一手动：本专辑取模；邻接 path 空/不可播 → 当前（重播）。
     *                   false=曲终 auto：2 环绕、3 计划内随机（均跳过不可播）；单曲(1)到边界停。
     */
    private int resolveTransportTargetIndex(int direction, boolean manualSkip) {
        List<TransportTrack> list = transportTracks;
        int n = list == null ? 0 : list.size();
        if (n == 0) return -1;
        int cur = transportCurrent;
        if (cur < 0 || cur >= n) {
            int fallback = findPlayableTransportIndex(direction > 0 ? 0 : n - 1, direction, n);
            return fallback;
        }
        if (manualSkip) {
            int adjacent = ((cur + direction) % n + n) % n;
            if (adjacent == cur) return cur;
            if (!isTransportTrackPlayable(list.get(adjacent))) {
                Log.i(TAG, "bg manual skip adjacent not ready → replay cur=" + cur
                        + " adjacent=" + adjacent);
                return cur;
            }
            return adjacent;
        }
        if (transportMode == 3) {
            return pickRandomPlayableTransportIndex(cur, n);
        }
        if (transportMode == 2) {
            int found = findPlayableTransportIndex(
                    ((cur + direction) % n + n) % n, direction, n);
            return found >= 0 ? found : cur;
        }
        // 单曲循环曲终/非手动：越界停在边界曲（跳过不可播）。
        int next = cur + direction;
        if (next < 0) next = 0;
        if (next >= n) next = n - 1;
        if (isTransportTrackPlayable(list.get(next))) return next;
        return cur;
    }

    /** 计划项是否盘上可播（未下载完 path 为空）。 */
    private boolean isTransportTrackPlayable(TransportTrack t) {
        if (t == null) return false;
        if (t.loaded) return true;
        return t.bankPath != null && !t.bankPath.isEmpty();
    }

    /** 从 start 起沿 direction 找下一可播；转一圈都没有 → -1。 */
    private int findPlayableTransportIndex(int start, int direction, int n) {
        if (n <= 0) return -1;
        if (direction == 0) direction = 1;
        int idx = ((start % n) + n) % n;
        for (int i = 0; i < n; i++) {
            if (isTransportTrackPlayable(transportTracks.get(idx)))
                return idx;
            idx = ((idx + direction) % n + n) % n;
        }
        return -1;
    }

    /** 随机可播（尽量避开当前）；仅当前可播 → 当前。 */
    private int pickRandomPlayableTransportIndex(int cur, int n) {
        if (n <= 0) return -1;
        int playable = 0;
        for (int i = 0; i < n; i++) {
            if (isTransportTrackPlayable(transportTracks.get(i)))
                playable++;
        }
        if (playable == 0) return -1;
        if (playable == 1) {
            int only = findPlayableTransportIndex(0, +1, n);
            return only >= 0 ? only : cur;
        }
        int pick = cur;
        int guard = 0;
        while (pick == cur || !isTransportTrackPlayable(transportTracks.get(pick))) {
            pick = (int) (Math.random() * n);
            if (pick >= n) pick = n - 1;
            if (++guard > 64) {
                int found = findPlayableTransportIndex((cur + 1) % n, +1, n);
                return found >= 0 ? found : cur;
            }
        }
        return pick;
    }

    /** 后台切歌前确保目标 bank 已在引擎里。 */
    private boolean ensureTransportBankLoaded(TransportTrack t) {
        if (t == null) return false;
        if (t.loaded) return true;
        if (t.bankPath == null || t.bankPath.isEmpty()) return false;
        File f = new File(t.bankPath);
        if (!f.exists() || f.length() < 256) {
            Log.w(TAG, "bg skip bank file missing/short: " + t.bankPath);
            return false;
        }
        if (!looksLikeWanosBank(f)) {
            Log.w(TAG, "bg skip bank header rejected: " + t.bankPath);
            return false;
        }
        try {
            int r = WIAEngineJNI.loadBankFromPath(t.bankPath);
            if (r != kEF_SUCCESS) {
                Log.w(TAG, "bg skip loadBankFromPath failed bank=" + t.bankName + " r=" + r);
                return false;
            }
            if (t.bankName != null && !t.bankName.isEmpty()) {
                loadedBanks.add(t.bankName);
            }
            Log.i(TAG, "bg skip loaded bank=" + t.bankName + " path=" + t.bankPath);
            return true;
        } catch (Throwable e) {
            Log.w(TAG, "bg skip loadBank threw: " + e.getMessage());
            return false;
        }
    }

    /**
     * 与 C# WanosBankStore.IsLikelyValidBankFile 同规则：
     * 必须是 Wanos BankHeader 归档；legacy BKHD / 文本 / 半截文件一律拒绝
     * （native loadBankHeader 遇到坏头会 C++ throw → SIGABRT）。
     */
    private boolean looksLikeWanosBank(File f) {
        FileInputStream fis = null;
        try {
            fis = new FileInputStream(f);
            byte[] head = new byte[256];
            int n = fis.read(head);
            if (n < 4) return false;
            if (head[0] == 'B' && head[1] == 'K' && head[2] == 'H' && head[3] == 'D') return false;
            if (head[0] == '<' || head[0] == '{' || head[0] == '['
                    || head[0] == '#' || head[0] == '/') return false;
            byte[] marker = "BankHeader".getBytes("US-ASCII");
            outer:
            for (int i = 0; i + marker.length <= n; i++) {
                for (int j = 0; j < marker.length; j++) {
                    if (head[i + j] != marker[j]) continue outer;
                }
                return true;
            }
            return false;
        } catch (Exception e) {
            Log.w(TAG, "looksLikeWanosBank failed: " + e.getMessage());
            return false;
        } finally {
            if (fis != null) {
                try { fis.close(); } catch (Exception ignored) { }
            }
        }
    }

    /**
     * Unity 与本类共用。true：这次应该 Post。false：窗口内已经发过，跳过。
     */
    public boolean claimVoiceResume(String eventName) {
        if (eventName == null) {
            return true;
        }
        boolean all = "Resume_ALL".equals(eventName);
        boolean music = "Resume_Music".equals(eventName);
        if (!all && !music) {
            return true;
        }
        // 微信还在说话：Unity 回前台补的 Resume 不能出声。语音结束的 GAIN 到了这里已不再活跃。
        if (audioFocusManager.isWeChatVoiceSessionActive()) {
            Log.i(TAG, "claimVoiceResume hold " + eventName + " (wechat voice still active)");
            return false;
        }
        synchronized (voiceResumeLock) {
            long now = SystemClock.elapsedRealtime();
            long last = all ? lastResumeAllElapsedMs : lastResumeMusicElapsedMs;
            if (now - last < VOICE_RESUME_COALESCE_MS) {
                Log.i(TAG, "claimVoiceResume skip " + eventName
                        + " ageMs=" + (now - last));
                return false;
            }
            if (all) {
                lastResumeAllElapsedMs = now;
            } else {
                lastResumeMusicElapsedMs = now;
            }
            return true;
        }
    }

    /** 音乐控制事件统一走 objId 0，与 C# WanosNative.PostEvent(0, ...) 一致。 */
    private void postMusicControlEvent(String eventName) {
        if (("Resume_ALL".equals(eventName) || "Resume_Music".equals(eventName))
                && !claimVoiceResume(eventName)) {
            return;
        }
        try {
            // Pause_ALL 等全局事件 obj=0 也行；Stop/Break/Resume_Music 必须打在 MusicObjectId。
            long obj = "Pause_ALL".equals(eventName) || "Resume_ALL".equals(eventName)
                    ? 0L
                    : musicObjectId();
            WIAEngineJNI.postEvent(obj, eventName);
            Log.i(TAG, "post control event=" + eventName + " obj=" + obj);
        } catch (Throwable e) {
            Log.w(TAG, "post control event failed " + eventName + ": " + e.getMessage());
        }
    }

    /** 抢焦挂起期间，C# 若仍按 Playing 刷卡，负一屏必须保持暂停。 */
    static boolean isFocusSuspendedForCard() {
        WanosMusicPlayerNative inst = sInstance;
        return inst != null && inst.systemSuspended;
    }

    /** 焦点回调线程上立刻把负一屏写成暂停，并刷新媒体通知按钮。 */
    private void publishFocusPausedCard() {
        if (Looper.myLooper() == Looper.getMainLooper()) {
            updateTransportNowPlaying(false);
            return;
        }
        mainHandler.post(() -> updateTransportNowPlaying(false));
    }

    /** 用传输计划里的曲名刷新负一屏卡片（Java 侧 playlist 在 CustomSRP 是空的）。 */
    private void updateTransportNowPlaying(boolean playing) {
        if (playing && (systemSuspended || userPaused)) {
            playing = false;
        }
        TransportTrack cur = currentTransportTrack();
        // 曲名为空时不能传空串：updateNowPlayingInfo 会把 MediaSession 整个释放掉。
        String name = cur != null && cur.name != null && !cur.name.isEmpty() ? cur.name : "波萝";
        try {
            WIAEngineJNI.updateNowPlayingInfo(name, getCoverPathForNowPlaying(), playing);
        } catch (Throwable e) {
            Log.w(TAG, "updateTransportNowPlaying failed: " + e.getMessage());
        }
    }

    private void scheduleMediaPlayPause(boolean pause) {
        if (Looper.myLooper() != Looper.getMainLooper()) {
            mainHandler.post(() -> scheduleMediaPlayPause(pause));
            return;
        }
        remoteCommands.seedAndPress(
                !musicEngineMuted, userPaused, pause, SystemClock.elapsedRealtime(), "");
        mainHandler.removeCallbacks(flushMediaPlayPauseRunnable);
        long due = remoteCommands.getPendingAtMs();
        long delay = BoluoNowPlayingRemoteCommandPolicy.PLAY_PAUSE_COALESCE_MS;
        if (due >= 0) {
            long remain = due - SystemClock.elapsedRealtime();
            if (remain > 0 && remain < delay) {
                delay = remain;
            }
        }
        mainHandler.postDelayed(flushMediaPlayPauseRunnable, delay);
    }

    private void flushPendingMediaPlayPause() {
        long now = SystemClock.elapsedRealtime();
        long due = remoteCommands.getPendingAtMs();
        if (due >= 0 && now < due) {
            now = due;
        }
        int pauses = remoteCommands.getEnginePauseCount();
        int resumes = remoteCommands.getEngineResumeCount();
        remoteCommands.tick(now);
        Boolean want = remoteCommands.pollFlushedWantMute();
        if (want == null) {
            return;
        }
        boolean engineChanged = remoteCommands.getEnginePauseCount() != pauses
                || remoteCommands.getEngineResumeCount() != resumes;
        userPaused = remoteCommands.isUserManuallyPaused();
        if (appInBackground && currentTransportTrack() != null) {
            // 抢焦挂起不是用户暂停。QQ 被杀后负一屏仍是暂停，点播放会出声，
            // 但不摘 systemSuspended，updateTransportNowPlaying 会把图标压回暂停。
            if (!userPaused && !remoteCommands.isPausedForTimerEnd()) {
                systemSuspended = false;
            }
            if (engineChanged) {
                applyBackgroundTransportPaused(userPaused);
            } else {
                updateTransportNowPlaying(!userPaused && !remoteCommands.isPausedForTimerEnd());
            }
            return;
        }
        if (!userPaused && !remoteCommands.isPausedForTimerEnd()) {
            systemSuspended = false;
        }
        // 对齐线上 96ec0d5f：合并落地后立刻把负一屏按钮写成暂停或播放，不等 Unity 回刷。
        updateTransportNowPlaying(!userPaused && !remoteCommands.isPausedForTimerEnd());
        if (!engineChanged) {
            Log.i(TAG, "flushPendingMediaPlayPause already consistent wantPause=" + want
                    + " userPaused=" + userPaused);
            return;
        }
        // 前台曲目权威在 Unity。只把合并后的最后一次意图转发，避免和引擎双开播。
        try {
            if (userPaused) {
                UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid", "OnNowPlayingPause", "");
            } else {
                UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid", "OnNowPlayingPlay", "");
            }
        } catch (Exception e) {
            Log.w(TAG, "flushPendingMediaPlayPause UnitySendMessage failed: " + e.getMessage());
        }
    }

    /** 负一屏切歌：debounce + inFlight 后交给 Unity Next/Prev（持有到 Playing/超时 settle）。 */
    private void skipToUnity(int direction) {
        if (Looper.myLooper() != Looper.getMainLooper()) {
            mainHandler.post(() -> skipToUnity(direction));
            return;
        }
        long now = SystemClock.elapsedRealtime();
        if (!acceptRemoteSkip(direction, now)) {
            Log.i(TAG, "skipToUnity debounce dir=" + direction
                    + " inFlight=" + remoteCommands.isSkipInFlight());
            return;
        }
        // 暂停态切歌已清闩：按钮先变成播放中，曲名等 Unity 换曲后再刷。
        updateTransportNowPlaying(!userPaused && !remoteCommands.isPausedForTimerEnd());
        final String method = direction > 0 ? "OnMediaSkipNext" : "OnMediaSkipPrev";
        try {
            UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid", method, "");
        } catch (Exception e) {
            remoteCommands.noteSkipSettled();
            Log.w(TAG, "skipToUnity failed: " + e.getMessage());
        }
    }

    /**
     * 通知栏/锁屏上一曲/下一曲（原生 playlist 路径）。
     * <p><b>CustomSRP 未接线</b>：MediaSession 前台走 {@link #skipToUnity}，后台走
     * {@link #handleTransportSkipInBackground}。保留本方法仅作死路径兼容，勿再扩展。
     * @param direction +1 下一曲，-1 上一曲
     */
    private void skipFromMediaControl(int direction) {
        if (Looper.myLooper() != Looper.getMainLooper()) {
            mainHandler.post(() -> skipFromMediaControl(direction));
            return;
        }
        if (shouldStop) {
            Log.w(TAG, "skipFromMediaControl ignored shouldStop dir=" + direction
                    + " gen=" + playGeneration + " userPaused=" + userPaused);
            return;
        }
        long now = SystemClock.elapsedRealtime();
        if (!acceptRemoteSkip(direction, now)) {
            Log.i(TAG, "skipFromMediaControl debounce dir=" + direction
                    + " inFlight=" + remoteCommands.isSkipInFlight()
                    + " gen=" + playGeneration);
            return;
        }
        playbackExecutor.execute(() -> {
            try {
                skipFromMediaControlLocked(direction);
            } finally {
                remoteCommands.noteSkipSettled();
            }
        });
    }

    private void skipFromMediaControlLocked(int direction) {
        // 内存索引优先：磁盘 currentMusicIndex 可能滞后于 native 自动切歌
        int listIdx = playlist.currentListIndex;
        int musicIdx = playlist.currentMusicIndex;

        String listPath = persistentDataPath + "/WanosAudio/MusicLists.json";
        if (!loadPlaylistFromFile(listPath)) {
            Log.w(TAG, "skipFromMediaControl: loadPlaylist failed dir=" + direction
                    + " mode=" + playlist.mode + " cur=" + musicIdx
                    + " gen=" + playGeneration + " userPaused=" + userPaused);
            return;
        }
        if (listIdx < 0 || listIdx >= playlist.musicLists.size()) {
            Log.w(TAG, "skipFromMediaControl: bad listIdx=" + listIdx + " dir=" + direction
                    + " mode=" + playlist.mode + " cur=" + musicIdx
                    + " gen=" + playGeneration + " userPaused=" + userPaused);
            return;
        }
        NativeMusicList list = playlist.musicLists.get(listIdx);
        if (list.musics.isEmpty()) {
            Log.w(TAG, "skipFromMediaControl: empty list dir=" + direction
                    + " mode=" + playlist.mode + " cur=" + musicIdx
                    + " gen=" + playGeneration + " userPaused=" + userPaused);
            return;
        }
        if (musicIdx < 0 || musicIdx >= list.musics.size()) {
            musicIdx = playlist.currentMusicIndex;
            if (musicIdx < 0 || musicIdx >= list.musics.size()) {
                Log.w(TAG, "skipFromMediaControl: bad musicIdx dir=" + direction
                        + " mode=" + playlist.mode + " cur=" + musicIdx
                        + " gen=" + playGeneration + " userPaused=" + userPaused);
                return;
            }
        }

        int nextMusicIdx = musicIdx;
        if ((playlist.mode == 0 || playlist.mode == 1 || playlist.mode == 2) && !playlist.indexes.isEmpty()) {
            int idxPos = playlist.indexes.indexOf(musicIdx);
            int n = playlist.indexes.size();
            int nextPos = idxPos >= 0
                    ? (idxPos + direction + n) % n
                    : (direction > 0 ? 0 : n - 1);
            nextMusicIdx = playlist.indexes.get(nextPos);
        } else {
            int n = list.musics.size();
            nextMusicIdx = (musicIdx + direction + n) % n;
        }

        final long gen = ++playGeneration;
        Log.i(TAG, "skipFromMediaControl dir=" + direction + " mode=" + playlist.mode
                + " list=" + listIdx + " cur=" + musicIdx + " -> " + nextMusicIdx
                + " gen=" + gen + " indexes=" + playlist.indexes.size()
                + " userPaused=" + userPaused);
        if (nextMusicIdx == musicIdx) {
            Log.w(TAG, "skipFromMediaControl same-track dir=" + direction
                    + " mode=" + playlist.mode + " cur=" + musicIdx + " next=" + nextMusicIdx
                    + " gen=" + gen + " indexes=" + playlist.indexes.size()
                    + " userPaused=" + userPaused);
        }

        // 对齐线上 96ec0d5f：暂停态切歌先清暂停再起播。定时到点保持暂停。
        userPaused = remoteCommands.isUserManuallyPaused();
        if (userPaused || remoteCommands.isPausedForTimerEnd()) {
            Log.i(TAG, "skipFromMediaControl held paused dir=" + direction
                    + " next=" + nextMusicIdx + " gen=" + gen);
            mainHandler.post(() -> updateNowPlayingPlayingState(false));
            return;
        }
        systemSuspended = false;
        nativePlayTrackAt(listIdx, nextMusicIdx, gen);
    }

    /**
     * 通知栏/锁屏点击「播放」：清除 userPaused 后续播当前曲。
     * 以 BGM 实例是否还在决定 unmute 还是重播，不用全局引擎 Started。
     */
    private void resumeFromMediaControl() {
        final long gen = ++audibleCommandGeneration;
        playbackExecutor.execute(() -> {
            if (!BoluoNowPlayingRemoteCommandPolicy.shouldCommitAudible(
                    gen, audibleCommandGeneration, false)) {
                Log.i(TAG, "resumeFromMediaControl stale gen=" + gen
                        + " cur=" + audibleCommandGeneration);
                return;
            }
            remoteCommands.notePausedForTimerEnd(false);
            remoteCommands.adoptPlayback(true, false);
            userPaused = false;
            shouldStop = false;
            if (!resumeAudibleMusic("resumeFromMediaControl")) {
                return;
            }
            if (!BoluoNowPlayingRemoteCommandPolicy.shouldCommitAudible(
                    gen, audibleCommandGeneration, userPaused)) {
                Log.i(TAG, "resumeFromMediaControl dropped after resume gen=" + gen
                        + " userPaused=" + userPaused);
                if (userPaused) {
                    stopPairedVoices("resume-dropped");
                    mainHandler.post(() -> updateNowPlayingPlayingState(false));
                }
                return;
            }
            startMediaPlaybackForegroundService();
            notifyUnityPlaybackState(true);
        });
    }

    /** 按当前曲目刷新锁屏/通知栏元数据，并指定播放/暂停状态（用于媒体控制暂停/续播时同步图标）。 */
    private void updateNowPlayingPlayingState(boolean playing) {
        if (shouldStop) {
            Log.d(TAG, "updateNowPlayingPlayingState skipped: shouldStop");
            return;
        }
        // 暂停必须写卡（岛内 Pause / 打断后引擎可能已 inactive）；播放仍要求会话活着。
        if (playing && !WIAEngineJNI.isPlaybackActive()) {
            Log.d(TAG, "updateNowPlayingPlayingState skipped: inactive play");
            return;
        }
        // CustomSRP Java playlist 常空：优先走传输计划曲名，与 updateTransportNowPlaying 同口径。
        if (currentTransportTrack() != null) {
            updateTransportNowPlaying(playing);
            return;
        }
        String name = "";
        if (playlist != null && !playlist.musicLists.isEmpty()) {
            int li = playlist.currentListIndex;
            int mi = playlist.currentMusicIndex;
            if (li >= 0 && li < playlist.musicLists.size()) {
                NativeMusicList list = playlist.musicLists.get(li);
                if (!list.musics.isEmpty() && mi >= 0 && mi < list.musics.size()) {
                    NativeMusic m = list.musics.get(mi);
                    name = (m.musicName != null && !m.musicName.isEmpty())
                            ? m.musicName : (list.musicsName != null ? list.musicsName : "");
                }
            }
        }
        if (name == null || name.isEmpty())
            name = "波萝";
        WIAEngineJNI.updateNowPlayingInfo(name, getCoverPathForNowPlaying(), playing);
    }

    /** 通知 Unity 媒体控制引发的播放/暂停状态变化，驱动 C# 同步岛内 UI。value: "1"=播放, "0"=暂停。 */
    private void notifyUnityPlaybackState(boolean playing) {
        final String value = playing ? "1" : "0";
        mainHandler.post(() -> {
            try {
                // 必须发到 BoluoMusicPlayerAndroid（与 OnNowPlayingPlay 等一致）。
                // unityObjectName 默认是 WanosMusicPlayer，那边没有 OnNativePlaybackStateChanged，
                // 定时结束弹窗续播时 Unity 侧 RequestResumeAll 会静默丢单。
                UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid", "OnNativePlaybackStateChanged", value);
            } catch (Exception e) {
                Log.w(TAG, "notifyUnityPlaybackState failed: " + e.getMessage());
            }
        });
    }

    private void onEventCallback(long instanceId, int state) {
        Log.d(TAG, "onEventCallback instanceId=" + instanceId + " state=0x" + Integer.toHexString(state));
        /* Playing+Break：在回调线程立即预更新下一曲 — 已关闭，锁屏/通知栏只展示当前曲 */
        /*
        if (state == InstanceState_Playing && (playlist.mode == 1 || playlist.mode == 2) && !shouldStop) {
            try {
                int li = playlist.currentListIndex;
                int mi = playlist.currentMusicIndex;
                if (li >= 0 && li < playlist.musicLists.size()) {
                    NativeMusicList lst = playlist.musicLists.get(li);
                    if (!lst.musics.isEmpty() && !playlist.indexes.isEmpty()) {
                        int idxPos = playlist.indexes.indexOf(mi);
                        int nextPos = idxPos >= 0 ? (idxPos + 1) % playlist.indexes.size() : 0;
                        int nextMi = playlist.indexes.get(nextPos);
                        if (nextMi >= 0 && nextMi < lst.musics.size()) {
                            NativeMusic nm = lst.musics.get(nextMi);
                            String name = (nm.musicName != null && !nm.musicName.isEmpty())
                                    ? nm.musicName : (lst.musicsName != null ? lst.musicsName : "");
                            WIAEngineJNI.updateNowPlayingInfo(name, getCoverPathForNowPlaying(), true);
                        }
                    }
                }
            } catch (Exception e) { Log.w(TAG, "Playing pre-update failed", e); }
        }
        */
        mainHandler.post(() -> onEventCallbackImpl(state, instanceId));
    }

    private void onEventCallbackImpl(int state, long instanceId) {
        if (instanceId != 0 && instanceId == lastStoppedInstanceId) {
            Log.d(TAG, "EventCallback ignored stopped instance " + instanceId
                    + " state=0x" + Integer.toHexString(state));
            return;
        }
        if (instanceId != 0 && currentInstanceId != 0 && instanceId != currentInstanceId) {
            Log.d(TAG, "EventCallback ignored stale instance " + instanceId + " (current " + currentInstanceId + ")");
            return;
        }
        if (state == InstanceState_Playing) {
            // Sequence createInst 已落地：放行下一笔 skip（前台由 Unity NoteSkipSettled）。
            remoteCommands.noteSkipSettled();
            if (userPaused) {
                // 创建窗口里的 Pause 会被吞。声音起来时如果用户已经暂停，再停一次，卡片保持暂停。
                Log.i(TAG, "EventCallback Playing while userPaused → pause gen=" + playGeneration);
                updateNowPlayingPlayingState(false);
                playbackExecutor.execute(() -> {
                    if (!userPaused) {
                        return;
                    }
                    stopPairedVoices("playing-while-paused");
                });
                return;
            }
            if (systemSuspended) {
                Log.i(TAG, "EventCallback Playing keep paused systemSuspended");
                updateNowPlayingPlayingState(false);
            } else {
                updateNowPlayingForCurrentTrack();
            }
            // 原生 playlist.mode：1/2；传输计划 transportMode：C# 2/3。后台 transport 起播也要 Break。
            boolean breakForPlaylist = (playlist.mode == 1 || playlist.mode == 2);
            boolean breakForTransport = transportPlanValid
                    && (transportMode == 2 || transportMode == 3)
                    && currentInstanceId != 0
                    && instanceId == currentInstanceId;
            if ((breakForPlaylist || breakForTransport) && !shouldStop) {
                final long breakObj = musicObjectId();
                Log.d(TAG, "EventCallback Playing -> Sending Break playlistMode="
                        + playlist.mode + " transportMode=" + transportMode
                        + " musicObj=" + breakObj);
                playbackExecutor.execute(() -> WIAEngineJNI.postEvent(breakObj, breakEventName));
            }
            return;
        }
        if (state != InstanceState_Ended) return;
        if (shouldStop) return;
        if (userPaused) {
            // 用户暂停不是「还在播」：曲终不得当自动续播。
            Log.i(TAG, "EventCallback Ended ignored (userPaused) — no auto-next gen="
                    + playGeneration);
            lastStoppedInstanceId = instanceId;
            currentInstanceId = 0;
            unityMusicInstanceId = 0;
            mainHandler.post(() -> updateNowPlayingPlayingState(false));
            return;
        }
        // 后台传输计划优先：锁屏曲终 / 原生 transport 起播后的下一首。
        if (appInBackground && transportPlanValid
                && (transportMode == 2 || transportMode == 3)) {
            Log.i(TAG, "EventCallback Ended → transport auto-next (bg) instance=" + instanceId);
            lastStoppedInstanceId = instanceId;
            currentInstanceId = 0;
            unityMusicInstanceId = 0;
            playbackExecutor.execute(() -> playTransportTrackRelative(+1, "native_ended"));
            return;
        }
        if (currentInstanceId == 0) {
            Log.i(TAG, "EventCallback Ended ignored (no current instance, skip/stop in flight) gen="
                    + playGeneration);
            return;
        }

        /* 磁盘 MusicLists.json 的 currentMusicIndex 可能滞后于 Native 自动切歌（Unity 未必每次 Save），
         * 若先 reload 再读 playlist，会用陈旧索引算「下一首」，最后一首结束后无法正确回到列表首。 */
        int listIdx = playlist.currentListIndex;
        int musicIdx = playlist.currentMusicIndex;

        if (!playlistReloadedByCSharp) {
            String listPath = persistentDataPath + "/WanosAudio/MusicLists.json";
            if (!loadPlaylistFromFile(listPath)) return;
        } else {
            playlistReloadedByCSharp = false;
        }

        if (listIdx < 0 || listIdx >= playlist.musicLists.size()) return;
        NativeMusicList list = playlist.musicLists.get(listIdx);
        if (list.musics.isEmpty()) return;
        if (musicIdx < 0 || musicIdx >= list.musics.size()) return;

        int nextListIdx = listIdx;
        int nextMusicIdx = musicIdx;

        if (playlist.mode == 0) {
            nextMusicIdx = musicIdx;
        } else if ((playlist.mode == 1 || playlist.mode == 2) && !playlist.indexes.isEmpty()) {
            int idxPos = playlist.indexes.indexOf(musicIdx);
            int nextPos = idxPos >= 0 ? (idxPos + 1) % playlist.indexes.size() : 0;
            nextMusicIdx = playlist.indexes.get(nextPos);
        } else {
            nextMusicIdx = (musicIdx + 1) % list.musics.size();
        }

        /* Ended 时抢先刷下一首 — 已关闭；下一首元数据由 nativePlayTrackAt 起播时更新 */
        /*
        if (nextMusicIdx >= 0 && nextMusicIdx < list.musics.size()) {
            NativeMusic nextMusic = list.musics.get(nextMusicIdx);
            String name = (nextMusic.musicName != null && !nextMusic.musicName.isEmpty())
                    ? nextMusic.musicName : (list.musicsName != null ? list.musicsName : "");
            WIAEngineJNI.updateNowPlayingInfo(name, getCoverPathForNowPlaying(), true);
        }
        */

        final int fl = nextListIdx, fm = nextMusicIdx;
        final long gen = playGeneration;
        playbackExecutor.execute(() -> nativePlayTrackAt(fl, fm, gen));
    }
}
