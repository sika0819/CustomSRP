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
    private long objectId = 0;
    private String stopEventName = "Stop_Music";
    private String breakEventName = "Break_Music";
    private String unityObjectName = "WanosMusicPlayer";

    private volatile boolean shouldStop = false;
    /** 用户经通知栏/锁屏主动暂停：置位后焦点回归等自动路径不得擅自续播，与 iOS userManuallyPaused 对齐。 */
    private volatile boolean userPaused = false;
    private long currentInstanceId = 0;
    /** forceStop 刚拆掉的 instance：Ended 回调在 sleep 窗口内不得再自动下一曲盖掉负一屏 skip。 */
    private volatile long lastStoppedInstanceId = 0;
    /** 与 iOS g_playGeneration 对齐：切歌/停止时自增，decode 等待期间若已变则放弃 Post。 */
    private volatile long playGeneration = 0;
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
    /** 负一屏 MediaStyle ACTION 与 MediaSession.Callback 可能连发同一方向切歌。 */
    private volatile long lastSkipElapsedMs = 0L;
    private volatile int lastSkipDirection = 0;
    private volatile boolean skipInFlight = false;
    /** 对齐 BoluoNowPlayingRemoteCommandPolicy.SkipDebounceMs */
    private static final long SKIP_DEBOUNCE_MS = 400L;
    /** 对齐 BoluoNowPlayingRemoteCommandPolicy.SkipInFlightTimeoutMs */
    private static final long SKIP_IN_FLIGHT_TIMEOUT_MS = 2000L;
    /** 对齐 BoluoNowPlayingRemoteCommandPolicy.PlayPauseCoalesceMs：连点只落地最终播放/暂停意图 */
    private static final long PLAY_PAUSE_COALESCE_MS = 250L;
    /** null=无 pending；Boolean.TRUE=暂停；FALSE=播放 */
    private Boolean pendingMediaPlayPauseIsPause = null;
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
                    // 调用方已确认是真丢焦 / 外来音源。后台续播不能再吞掉：
                    // 切到抖音、QQ 音乐时必须停，否则启动期出声会一直叠播。
                    applyGuardedSystemMute("focus_pause");
                }

                @Override
                public void onFocusPauseUi(boolean competingLong) {
                    mainHandler.post(() -> {
                        try {
                            String method = competingLong ? "OnCompetingAudioMuted" : "OnSystemAudioMuted";
                            UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid", method, "");
                        } catch (Exception e) {
                            Log.w(TAG, "onFocusPauseUi failed: " + e.getMessage());
                        }
                    });
                }

                @Override
                public void onFocusResume() {
                    resumeAfterFocusRegained();
                    if (userPaused || shouldStop) {
                        // 负一屏/通知栏用户暂停：焦点回归不得把岛屿 UI 刷成「播放中」。
                        Log.i(TAG, "onFocusResume skip Unity play notify userPaused="
                                + userPaused + " shouldStop=" + shouldStop);
                        return;
                    }
                    mainHandler.post(() -> {
                        try {
                            UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid",
                                    "OnAudioFocusUnduck", "");
                            UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid",
                                    "OnNowPlayingPlay", "");
                        } catch (Exception e) {
                            Log.w(TAG, "focus resume UI notify failed: " + e.getMessage());
                        }
                    });
                }

                @Override
                public void onFocusDuck() {
                    mainHandler.post(() -> {
                        try {
                            UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid",
                                    "OnAudioFocusDuck", "0.4");
                        } catch (Exception e) {
                            Log.w(TAG, "OnAudioFocusDuck failed: " + e.getMessage());
                        }
                    });
                }

                @Override
                public void onFocusUnduck() {
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
     * 产品口径（对齐 iOS）：蓝牙/耳机断开有扬声器等剩余输出时继续播，不因
     * ACTION_AUDIO_BECOMING_NOISY 强制暂停防外放。仅记录日志便于排查。
     */
    private void registerBecomingNoisyReceiver() {
        if (becomingNoisyRegistered) return;
        becomingNoisyReceiver = new BroadcastReceiver() {
            @Override
            public void onReceive(Context context, Intent intent) {
                if (intent == null || !AudioManager.ACTION_AUDIO_BECOMING_NOISY.equals(intent.getAction())) {
                    return;
                }
                Log.i(TAG, "AUDIO_BECOMING_NOISY: keep playing (product auto-resume on BT disconnect)");
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
            foregroundResumeGeneration++;
            yieldStartupPlaceholderCard();
            BoluoAudioStutterProbe.onEnterBackground();
            Log.i(TAG, "notifyAppLifecycle background gen=" + foregroundResumeGeneration
                    + " backgroundPlayDisabled=" + backgroundPlayDisabled
                    + " trace=" + BoluoAudioStutterProbe.traceId());
            if (!backgroundPlayDisabled && !userPaused && !shouldStop) {
                armKeepPlayingAliveWatch();
            }
        } else {
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
        if (userPaused || shouldStop || backgroundPlayDisabled) {
            Log.i(TAG, "reassertNowPlaying skip userPaused=" + userPaused
                    + " shouldStop=" + shouldStop
                    + " bgDisable=" + backgroundPlayDisabled);
            return;
        }
        if (!WIAEngineJNI.isPlaybackActive()) {
            Log.i(TAG, "reassertNowPlaying skip: playback inactive");
            return;
        }
        Log.i(TAG, "reassertNowPlaying foreground");
        updateTransportNowPlaying(true);
        foregroundCardPublished = true;
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
     * Unity {@code OnApplicationFocus(true)} → 回前台强制抢焦点（对齐 iOS becomeActive reclaim）。
     * 未 userPaused 且有恢复意图时发起；成败以系统 {@code requestAudioFocus} 为准。
     */
    public void reclaimFocusOnForeground() {
        if (userPaused || shouldStop) {
            Log.i(TAG, "reclaimFocusOnForeground skip: userPaused=" + userPaused
                    + " shouldStop=" + shouldStop);
            return;
        }
        // 抢焦点可能因已持焦 / debounce 直接返回，卡片仍要重推。
        reassertNowPlayingOnForeground();
        // 后台续播：禁止 reclaim→Reset。HOME 期间误调会把自己拆静音。
        if (shouldSuppressTransientPauseInBackground()) {
            Log.i(TAG, "reclaimFocusOnForeground skip: keep playing in background");
            return;
        }
        boolean engineStarted = WIAEngineJNI.getPlaybackState() == PlaybackState_Started;
        boolean hasInstance = currentInstanceId != 0;
        // 标准版不能滑动续播：系统打断后回前台必须自动续。
        // pending / 拆轨 / duck / 未持焦 / 引擎未 Started 都算恢复意图。
        boolean hasResumeIntent = audioFocusManager.isPendingResume()
                || audioFocusManager.needsPlaybackReopen()
                || audioFocusManager.isDucked()
                || (!audioFocusManager.hasAudioFocus() && (engineStarted || hasInstance))
                || (hasInstance && !engineStarted);
        long now = SystemClock.elapsedRealtime();
        // 已持焦但 duck/拆轨/pending 时禁止 debounce 跳过，否则标准版回前台无法自动续
        if (audioFocusManager.hasAudioFocus()
                && !audioFocusManager.isPendingResume()
                && !audioFocusManager.needsPlaybackReopen()
                && !audioFocusManager.isDucked()
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
                + " reopen_reason=" + audioFocusManager.peekReopenReason()
                + " trace=" + BoluoAudioStutterProbe.traceId());
        audioFocusManager.reclaimOnForeground(userPaused, hasResumeIntent);
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

    /**
     * 用户明确续播：负一屏「播放」、岛内点按。自动路径（焦点 GAIN / 回前台 reclaim / 曲终下一曲）禁止调用。
     */
    public void resumeFromUserAction() {
        resumeFromMediaControl();
    }

    /**
     * 定时结束弹窗「取消/重复」专用：只清 userPaused + unmute。
     * <p>
     * 禁止在此发 Resume_ALL / Resume_Music——Resume_ALL 可能把已 Pause_Music 的 BGM 打成 Ended，
     * 随后 Unity {@code PlayIndex(restart)} 硬起播才是 BGM 权威路径；音效由 Unity ContentAudio 闸恢复。
     */
    public void resumeAfterTimerEnd() {
        playbackExecutor.execute(() -> {
            userPaused = false;
            shouldStop = false;
            if (!ensureFocusBeforeAudible("timer_end_resume")) {
                Log.w(TAG, "resumeAfterTimerEnd: focus failed");
                return;
            }
            Log.i(TAG, "resumeAfterTimerEnd: unmute only (Unity PlayIndex owns BGM)");
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
     */
    public void noteForegroundCardPublished() {
        foregroundCardPublished = true;
        audioFocusManager.ensurePlaybackMonitor();
        Log.i(TAG, "noteForegroundCardPublished");
    }

    /** 暂停或清卡：占位旗标放下，避免之后切后台又把卡片建回来。 */
    public void noteForegroundCardCleared() {
        foregroundCardPublished = false;
    }

    /**
     * 占位卡还没在前台出过声：人已经不在前台，或对方媒体已经在播，就改成暂停并停 FGS。
     * 真正出过声之后的后台续播不走这里。
     */
    private void yieldStartupPlaceholderCard() {
        if (!foregroundCardPublished || startupAudibleCommitted || userPaused) {
            return;
        }
        if (!effectivelyInBackground() && isProcessInForeground()) {
            return;
        }
        foregroundCardPublished = false;
        Log.i(TAG, "yieldStartupPlaceholderCard");
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
        userPaused = paused;
        Log.i(TAG, "markUserPausedFromUnity: userPaused=" + paused);
        if (paused) {
            mainHandler.post(() -> updateNowPlayingPlayingState(false));
        }
    }

    /**
     * 定时到点停播（前后台均可）。不依赖 Unity Update 消费 Push。
     * Pause_Music 停 BGM（保留实例便于弹窗「取消/重复」续播）；
     * Pause_ALL + mute 停环境/天气等音效（与负一屏 bg transport pause 同一套，产品「到点停音乐和音效」）；
     * userPaused 防焦点回归/后台续播擅自出声。
     */
    public void pauseForTimerEnd() {
        playbackExecutor.execute(() -> {
            userPaused = true;
            musicEngineMuted = true;
            probeSetMute(true);
            postMusicControlEvent("Pause_Music");
            postMusicControlEvent("Pause_ALL");
            Log.i(TAG, "pauseForTimerEnd: Pause_Music+Pause_ALL userPaused=true");
            mainHandler.post(() -> updateNowPlayingPlayingState(false));
            notifyUnityPlaybackState(false);
        });
    }

    /**
     * 系统丢焦护栏静音：先同步压对象增益到 0，短缓冲后再 SetMute。
     * 禁止满音量硬 mute（切微信/QQ/B 站电流音）。mute 后保持衰减，由 unmute 爬坡回满。
     */
    private void applyGuardedSystemMute(String reason) {
        final long gen = ++guardedMuteGeneration;
        final long obj = objectId;
        playbackExecutor.execute(() -> {
            applyMusicGainSafe(obj, 0f, true);
            Log.i(TAG, "guarded mute dip reason=" + reason + " obj=" + obj + " gen=" + gen);
            try {
                Thread.sleep(GUARDED_MUTE_DELAY_MS);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return;
            }
            if (gen != guardedMuteGeneration) {
                Log.i(TAG, "guarded mute superseded reason=" + reason + " gen=" + gen);
                return;
            }
            musicEngineMuted = true;
            probeSetMute(true);
            Log.i(TAG, "guarded mute landed reason=" + reason);
        });
    }

    /** 焦点恢复：先 unmute（增益仍近 0），再两步爬满，消解静音爆音。须在 playbackExecutor 上调用。 */
    private void runGuardedUnmuteOnCurrentThread(String reason) {
        crackleProbe("guardedUnmute.enter", "reason=" + reason, true);
        final long gen = ++guardedMuteGeneration;
        final long obj = objectId;
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
        if (shouldStop || !WIAEngineJNI.isPlaybackActive()) {
            Log.d(TAG, "updateNowPlayingForCurrentTrack skipped: stopped/inactive");
            return;
        }
        if (playlist == null || playlist.musicLists.isEmpty()) return;
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

        // Notify Unity（lambda 只能捕获 final /  effectively final，故用拷贝）
        final int notifyListIdx = listIdx;
        final int notifyMusicIdx = musicIdx;
        mainHandler.post(() -> {
            try {
                UnityPlayer.UnitySendMessage(unityObjectName, "OnNativeTrackChanged", notifyListIdx + "," + notifyMusicIdx);
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
     */
    private boolean hasLiveMusicInstance() {
        return currentInstanceId != 0;
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
                applyKeepPlayingRecoverL1(why);
                keepPlayingRecoverStage = 2;
                keepPlayingL1Used = true;
            } else {
                applyKeepPlayingRecoverL0(why);
                keepPlayingRecoverStage = 1;
            }
            keepPlayingLastRecoverElapsed = now;
            return;
        }
        if (keepPlayingRecoverStage == 1) {
            applyKeepPlayingRecoverL1(why);
            keepPlayingRecoverStage = 2;
            keepPlayingL1Used = true;
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
    private void applyKeepPlayingRecoverL1(String why) {
        Log.i(TAG, "[CWPA-audio] keepPlayingRecover L1 reset+replay why=" + why
                + " (match online 3eec80b6)");
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
                WIAEngineJNI.postEvent(0, "Resume_ALL");
                runGuardedUnmuteOnCurrentThread("keep_playing_l1");
                Log.i(TAG, "[CWPA-audio] keepPlayingRecover L1 Resume_ALL after reset");
            } else {
                nativePlayTrackAt(playlist.currentListIndex, playlist.currentMusicIndex);
                Log.i(TAG, "[CWPA-audio] keepPlayingRecover L1 replay after reset");
            }
        } catch (Exception e) {
            Log.w(TAG, "keepPlayingRecover L1 replay: " + e.getMessage());
            try {
                nativePlayTrackAt(playlist.currentListIndex, playlist.currentMusicIndex);
            } catch (Exception e2) {
                Log.w(TAG, "keepPlayingRecover L1 nativePlay: " + e2.getMessage());
            }
        }
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
        Log.i(TAG, reason + ": Resume_Music + unmute liveInstance=" + hasLiveMusicInstance());
        // 对齐 handleTransportInBackground：先内容声再 BGM，再 unmute 爬坡。
        postMusicControlEvent("Resume_ALL");
        postMusicControlEvent("Resume_Music");
        runGuardedUnmuteOnCurrentThread(reason);
        mainHandler.post(this::updateNowPlayingForCurrentTrack);
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
            if (shouldStop || userPaused) {
                // 用户已在通知栏/锁屏主动暂停，焦点回归不得擅自续播。
                return;
            }
            boolean reopenDevice = audioFocusManager.needsPlaybackReopen();
            String reopenReason = audioFocusManager.consumeReopenReason();
            int state = WIAEngineJNI.getPlaybackState();
            boolean started = state == PlaybackState_Started;
            if (trySkipKeepPlayingReset(reopenReason, started)) {
                if (hasLiveMusicInstance()) {
                    runGuardedUnmuteOnCurrentThread("focus_resume_skip_reset");
                    mainHandler.post(this::updateNowPlayingForCurrentTrack);
                    notifyUnityTrackChanged(playlist.currentListIndex, playlist.currentMusicIndex);
                }
                return;
            }
            if (hasLiveMusicInstance() && started && !reopenDevice) {
                runGuardedUnmuteOnCurrentThread("focus_resume");
                Log.i(TAG, "resumeAfterFocusRegained: music instance live, unmute only");
                mainHandler.post(this::updateNowPlayingForCurrentTrack);
                notifyUnityTrackChanged(playlist.currentListIndex, playlist.currentMusicIndex);
                return;
            }
            if (hasLiveMusicInstance() && !reopenDevice) {
                runGuardedUnmuteOnCurrentThread("focus_resume");
                int after = WIAEngineJNI.getPlaybackState();
                if (after == PlaybackState_Started) {
                    Log.i(TAG, "resumeAfterFocusRegained: music instance live, unmute only");
                    mainHandler.post(this::updateNowPlayingForCurrentTrack);
                    notifyUnityTrackChanged(playlist.currentListIndex, playlist.currentMusicIndex);
                    return;
                }
                // Unity pause/resume 间 getPlaybackState 可能短暂非 Started；先 unmute 再确认，避免 Reset 音效卡顿
                try {
                    Thread.sleep(RESUME_STATE_FLICKER_WAIT_MS);
                } catch (InterruptedException ignored) {
                    Thread.currentThread().interrupt();
                    return;
                }
                if (scheduleGen != foregroundResumeGeneration || shouldStop || userPaused) {
                    Log.i(TAG, "resumeAfterFocusRegained aborted after flicker wait");
                    return;
                }
                if (hasLiveMusicInstance()
                        && WIAEngineJNI.getPlaybackState() == PlaybackState_Started) {
                    Log.i(TAG, "resumeAfterFocusRegained: unmute-only after state flicker");
                    mainHandler.post(this::updateNowPlayingForCurrentTrack);
                    notifyUnityTrackChanged(playlist.currentListIndex, playlist.currentMusicIndex);
                    return;
                }
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
                runGuardedUnmuteOnCurrentThread("focus_resume_reopen");
                mainHandler.post(this::updateNowPlayingForCurrentTrack);
                notifyUnityTrackChanged(playlist.currentListIndex, playlist.currentMusicIndex);
            } else {
                nativePlayTrackAt(playlist.currentListIndex, playlist.currentMusicIndex);
            }
        });
    }

    /** 通知 Unity 当前曲目索引，驱动 C# 同步播放状态与 UI（复用既有 OnNativeTrackChanged 通道）。 */
    private void notifyUnityTrackChanged(int listIdx, int musicIdx) {
        mainHandler.post(() -> {
            try {
                UnityPlayer.UnitySendMessage(unityObjectName, "OnNativeTrackChanged", listIdx + "," + musicIdx);
            } catch (Exception e) {
                Log.w(TAG, "notifyUnityTrackChanged failed: " + e.getMessage());
            }
        });
    }

    /**
     * 通知栏/锁屏/负一屏「暂停」：用户暂停意图。
     * 停掉当前曲实例并作废在途自动下一曲，负一屏保持暂停；续播只走 {@link #resumeFromUserAction()}。
     */
    private void pauseFromMediaControl() {
        playbackExecutor.execute(() -> {
            userPaused = true;
            final long gen = ++playGeneration;
            musicEngineMuted = true;
            probeSetMute(true);
            forceStopCurrentPlayback();
            Log.i(TAG, "pauseFromMediaControl: userPaused=true stopped gen=" + gen);
            mainHandler.post(() -> updateNowPlayingPlayingState(false));
            notifyUnityPlaybackState(false);
        });
    }

    /** 对齐 BoluoNowPlayingRemoteCommandPolicy.ShouldIgnoreSkip */
    private boolean shouldIgnoreRemoteSkip(int direction, long nowElapsedMs) {
        long elapsed = lastSkipElapsedMs == 0L
                ? Long.MAX_VALUE
                : nowElapsedMs - lastSkipElapsedMs;
        boolean sameDir = lastSkipDirection != 0 && lastSkipDirection == direction;
        if (skipInFlight && elapsed < SKIP_IN_FLIGHT_TIMEOUT_MS) return true;
        return sameDir && elapsed < SKIP_DEBOUNCE_MS;
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
     * JSON: {@code {"event":"Play_Music1_2","paused":false,"index":1,"valid":true}}
     */
    public String getBackgroundTransportState() {
        JSONObject o = new JSONObject();
        TransportTrack cur = currentTransportTrack();
        try {
            o.put("event", cur != null ? cur.eventName : "");
            o.put("name", cur != null ? cur.name : "");
            o.put("paused", userPaused);
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
        TransportTrack cur = currentTransportTrack();
        if (cur == null) {
            Log.w(TAG, "bg transport " + (pause ? "pause" : "play")
                    + " ignored: no transport plan");
            return false;                              // 没有计划：仍转发 Unity
        }
        if (pause == userPaused) {
            // 状态已一致，只刷新卡片，避免重复发控制事件。
            updateTransportNowPlaying(!pause);
            return true;
        }
        userPaused = pause;
        if (pause) {
            // 对齐 pauseForTimerEnd / 前台 MusicPlaylist.Pause：
            // Pause_Music 停 BGM，Pause_ALL 停环境/天气；再 mute 兜住 Pause_ALL 盖不住的 3D voice。
            // 后台 Unity 已 pause，调不到 SetContentAudioPaused，只能原生直接控引擎。
            musicEngineMuted = true;
            probeSetMute(true);
            postMusicControlEvent("Pause_Music");
            postMusicControlEvent("Pause_ALL");
        } else {
            // 先 Resume_ALL 恢复内容声，再 Resume_Music；此时曲已 Pause_Music，非 Started，不会踩
            // restoreAudible「活实例 + Started 禁 Resume_ALL」那条 Ended 切歌坑。
            postMusicControlEvent("Resume_ALL");
            postMusicControlEvent("Resume_Music");
            if (musicEngineMuted) {
                playbackExecutor.execute(() -> runGuardedUnmuteOnCurrentThread("bg_resume"));
            }
        }
        updateTransportNowPlaying(!pause);
        notifyUnityPlaybackState(!pause);
        Log.i(TAG, "bg transport " + (pause ? "pause" : "resume")
                + " event=" + cur.eventName + " muted=" + musicEngineMuted);
        return true;
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
        if (shouldIgnoreRemoteSkip(direction, now)) {
            Log.i(TAG, "bg skip debounce dir=" + direction);
            return true;
        }
        lastSkipElapsedMs = now;
        lastSkipDirection = direction;

        int target = resolveTransportTargetIndex(direction);
        if (target < 0) {
            Log.w(TAG, "bg skip no target dir=" + direction + " current=" + transportCurrent);
            return true;
        }
        TransportTrack t = transportTracks.get(target);
        if (!ensureTransportBankLoaded(t)) {
            Log.w(TAG, "bg skip bank not ready, keep current dir=" + direction
                    + " bank=" + t.bankName);
            return true;
        }

        userPaused = false;
        postMusicControlEvent("Stop_Music");
        long instance = WIAEngineJNI.postEvent(0, t.eventName);
        transportCurrent = target;
        updateTransportNowPlaying(true);
        notifyUnityPlaybackState(true);
        notifyUnityTrackChanged(-1, target);
        Log.i(TAG, "bg skip dir=" + direction + " -> index=" + target
                + " event=" + t.eventName + " instance=" + instance);
        return true;
    }

    /**
     * 下一曲/上一曲下标，规则与 C# MusicPlaylist.Next()/Prev() 对齐：
     * 单曲循环(1) 到边界停在边界，列表循环(2) 环绕，随机(3) 随机取另一首。
     */
    private int resolveTransportTargetIndex(int direction) {
        List<TransportTrack> list = transportTracks;
        int n = list == null ? 0 : list.size();
        if (n == 0) return -1;
        int cur = transportCurrent;
        if (cur < 0 || cur >= n) return direction > 0 ? 0 : n - 1;
        if (transportMode == 3) {
            if (n == 1) return cur;
            int pick = cur;
            while (pick == cur) {
                pick = (int) (Math.random() * n);
                if (pick >= n) pick = n - 1;
            }
            return pick;
        }
        if (transportMode == 2) {
            return ((cur + direction) % n + n) % n;
        }
        // 单曲循环：与 C# 一致，越界时停在边界曲。
        int next = cur + direction;
        if (next < 0) next = 0;
        if (next >= n) next = n - 1;
        return next;
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

    /** 音乐控制事件统一走 objId 0，与 C# WanosNative.PostEvent(0, ...) 一致。 */
    private void postMusicControlEvent(String eventName) {
        try {
            WIAEngineJNI.postEvent(0, eventName);
            Log.i(TAG, "post control event=" + eventName);
        } catch (Throwable e) {
            Log.w(TAG, "post control event failed " + eventName + ": " + e.getMessage());
        }
    }

    /** 用传输计划里的曲名刷新负一屏卡片（Java 侧 playlist 在 CustomSRP 是空的）。 */
    private void updateTransportNowPlaying(boolean playing) {
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
        pendingMediaPlayPauseIsPause = pause;
        mainHandler.removeCallbacks(flushMediaPlayPauseRunnable);
        mainHandler.postDelayed(flushMediaPlayPauseRunnable, PLAY_PAUSE_COALESCE_MS);
    }

    private void flushPendingMediaPlayPause() {
        Boolean pause = pendingMediaPlayPauseIsPause;
        pendingMediaPlayPauseIsPause = null;
        if (pause == null) return;
        // Unity MusicPlaylist 权威：只转发，避免 native 与 GF Sound 双开播。
        final boolean wantPause = pause;
        mainHandler.post(() -> {
            try {
                if (wantPause) {
                    userPaused = true;
                    UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid", "OnNowPlayingPause", "");
                } else {
                    userPaused = false;
                    UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid", "OnNowPlayingPlay", "");
                }
            } catch (Exception e) {
                Log.w(TAG, "flushPendingMediaPlayPause UnitySendMessage failed: " + e.getMessage());
            }
        });
    }

    /** 负一屏切歌：debounce 后交给 Unity Next/Prev。 */
    private void skipToUnity(int direction) {
        if (Looper.myLooper() != Looper.getMainLooper()) {
            mainHandler.post(() -> skipToUnity(direction));
            return;
        }
        long now = SystemClock.elapsedRealtime();
        if (shouldIgnoreRemoteSkip(direction, now)) {
            Log.i(TAG, "skipToUnity debounce dir=" + direction);
            return;
        }
        lastSkipElapsedMs = now;
        lastSkipDirection = direction;
        cancelPendingMediaPlayPauseForSkip();
        final String method = direction > 0 ? "OnMediaSkipNext" : "OnMediaSkipPrev";
        try {
            UnityPlayer.UnitySendMessage("BoluoMusicPlayerAndroid", method, "");
        } catch (Exception e) {
            Log.w(TAG, "skipToUnity failed: " + e.getMessage());
        }
    }

    private void cancelPendingMediaPlayPauseForSkip() {
        pendingMediaPlayPauseIsPause = null;
        mainHandler.removeCallbacks(flushMediaPlayPauseRunnable);
    }

    /**
     * 通知栏/锁屏上一曲/下一曲：对齐 iOS MPRemoteCommand next/previous。
     * mode 0/1/2 均按 indexes 顺序（仅 ready 曲）；indexes 空则线性环绕。
     * 先 bump playGeneration 再起播，作废曲终 Ended 自动下一曲，避免盖掉负一屏切歌。
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
        if (shouldIgnoreRemoteSkip(direction, now)) {
            Log.i(TAG, "skipFromMediaControl debounce dir=" + direction
                    + " inFlight=" + skipInFlight + " gen=" + playGeneration);
            return;
        }
        lastSkipElapsedMs = now;
        lastSkipDirection = direction;
        skipInFlight = true;
        cancelPendingMediaPlayPauseForSkip();
        playbackExecutor.execute(() -> {
            try {
                skipFromMediaControlLocked(direction);
            } finally {
                skipInFlight = false;
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

        // 暂停态切歌：先清暂停再起播（nativePlayTrackAt 内会 unmute）
        userPaused = false;
        nativePlayTrackAt(listIdx, nextMusicIdx, gen);
    }

    /**
     * 通知栏/锁屏点击「播放」：清除 userPaused 后续播当前曲。
     * 以 BGM 实例是否还在决定 unmute 还是重播，不用全局引擎 Started。
     */
    private void resumeFromMediaControl() {
        playbackExecutor.execute(() -> {
            userPaused = false;
            shouldStop = false;
            if (!resumeAudibleMusic("resumeFromMediaControl")) {
                return;
            }
            startMediaPlaybackForegroundService();
            notifyUnityPlaybackState(true);
        });
    }

    /** 按当前曲目刷新锁屏/通知栏元数据，并指定播放/暂停状态（用于媒体控制暂停/续播时同步图标）。 */
    private void updateNowPlayingPlayingState(boolean playing) {
        if (shouldStop || !WIAEngineJNI.isPlaybackActive()) {
            Log.d(TAG, "updateNowPlayingPlayingState skipped: stopped/inactive");
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
            if (userPaused) {
                Log.i(TAG, "EventCallback Playing keep paused NowPlaying userPaused=true");
                updateNowPlayingPlayingState(false);
            } else {
                updateNowPlayingForCurrentTrack();
            }
            if ((playlist.mode == 1 || playlist.mode == 2) && !shouldStop) {
                Log.d(TAG, "EventCallback Playing -> Sending Break (mode=" + playlist.mode + ")");
                playbackExecutor.execute(() -> WIAEngineJNI.postEvent(objectId, breakEventName));
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
            mainHandler.post(() -> updateNowPlayingPlayingState(false));
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
