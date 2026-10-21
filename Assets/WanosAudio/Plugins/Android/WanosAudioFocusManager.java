package com.wanos.app.health;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.media.AudioAttributes;
import android.media.AudioFocusRequest;
import android.media.AudioManager;
import android.media.AudioPlaybackConfiguration;
import android.os.Build;
import android.os.Process;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.os.Trace;
import android.util.Log;

import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.lang.reflect.Method;

/**
 * Android 音频焦点管理器，对齐 iOS BoluoMusicPlayerIOS 的中断恢复语义（JIRA PA-904 声音失焦未恢复）。
 *
 * <p>背景：波萝 Android 侧音频由 libWIAEngine 直接产生，此前完全没有 AudioManager 焦点管理。
 * 当其它 App 抢占音频焦点、随后停止时，系统只会向"持有焦点请求的监听者"回调 AUDIOFOCUS_GAIN；
 * 波萝既没有申请焦点也没有监听者，因此收不到 GAIN，也没有自动恢复路径——只有回到前台
 * （Unity 重新打开输出设备）才恢复播放，这正是该 bug 的表现。</p>
 *
 * <p>处理策略（与 iOS 对齐）：</p>
 * <ul>
 *   <li>播放开始 → requestFocus(GAIN) 并注册 OnAudioFocusChangeListener。</li>
 *   <li>LOSS_TRANSIENT_CAN_DUCK：前台无 competing → duck ~40%；后台或 MEDIA 竞争（B 站 MAY_DUCK）→ 抢占 mute。</li>
 *   <li>LOSS_TRANSIENT：暂停；进后台 800ms 内无 competing 可忽略（CWPA-44 抖动）。
 *       OEM 提示音常不回 GAIN：~1.8s 看门狗在非通话时改走 competing poll 重申焦点。</li>
 *   <li>GAIN → unduck；pendingResume 时，若本轮见过 MEDIA 竞争则先静默确认再 unmute
 *       （前台 2s / 后台 30s），避免 B 站暂停漏一声尾音；系统提示等未见 MEDIA 的 GAIN
 *       后台也立刻恢复（不必回前台）。后台且对方 MEDIA 仍在播 → 禁止 request GAIN 抢占。</li>
 *   <li>LOSS → 暂停并轮询其它音源停止后重新申请焦点（同样走静默确认）。</li>
 *   <li>AudioPlaybackCallback：B 站等不发 LOSS 时，凭其它 UID 的 MEDIA/GAME 流抢占。</li>
 *   <li>回前台 → {@link #reclaimOnForeground} 强制重申 GAIN（对齐 iOS force exclusive reclaim）；
 *       成功清 stale pending 并 resume；失败保持 mute（电话等由系统仲裁拒绝）。</li>
 *   <li>强制起播 → {@link #requestFocusBlocking} 先拿到 GRANTED 再出声，禁止叠播。</li>
 *   <li>停止 → abandonFocus()。</li>
 * </ul>
 */
final class WanosAudioFocusManager {

    private static final String TAG = "WanosAudioFocus";

    /** 竞争音源轮询间隔与看门狗上限（1s × 120 ≈ 120s），避免长期独占时无限轮询。 */
    private static final long COMPETING_AUDIO_POLL_INTERVAL_MS = 1000L;
    private static final int COMPETING_AUDIO_POLL_MAX = 120;
    /** 回前台强抢最小间隔：避免快切前后台重复 request/resume 导致音效卡顿。 */
    private static final long RECLAIM_DEBOUNCE_MS = 400L;
    /** 进后台后仅在此窗口内忽略 LOSS_TRANSIENT（CWPA-44）；超时视为 B 站等真竞争。 */
    private static final long BACKGROUND_TRANSIENT_SUPPRESS_WINDOW_MS = 800L;
    /** 对齐 C# AndroidForegroundCompetingResumeDebounceMs：一起启动 / 切视频间隙防尾音。 */
    private static final long FOREGROUND_COMPETING_RESUME_DEBOUNCE_MS = 2000L;
    /** 对齐 C# AndroidBackgroundSilenceConfirmMs / iOS 30s 后台静默确认。 */
    private static final long BACKGROUND_SILENCE_CONFIRM_MS = 30_000L;
    /** 对齐 C# AndroidSilenceConfirmPollMs。 */
    private static final long SILENCE_CONFIRM_POLL_MS = 1000L;
    /**
     * 提示音 / 音量键反馈走 TRANSIENT 后经常不发 GAIN。
     * 短于电话时长，长过典型通知；通话中 {@link #isInVoiceCallLocked} 会续期而不是抢焦。
     */
    private static final long TRANSIENT_GAIN_WATCHDOG_MS = 1800L;
    /** CAN_DUCK 后 OEM 拆轨且不回 GAIN：超时 unduck 并标记重开设备。 */
    private static final long DUCK_STALE_WATCHDOG_MS = 2000L;

    private long lastReclaimAttemptElapsedMs = 0L;

    /** 焦点变化的回调，由 WanosMusicPlayerNative 实现以驱动 WIAEngine。 */
    interface Callback {
        /** 需要暂停（瞬时/永久丢焦），实现方应静音或压低输出，但记住"之前在播"。 */
        void onFocusPause();

        /**
         * 暂停后的岛屿 UI 提示。
         * @param competingLong true=长期抢占文案；false=短暂中断文案
         */
        void onFocusPauseUi(boolean competingLong);

        /** 需要恢复播放（重新获得焦点），实现方应取消静音/续播。 */
        void onFocusResume();

        /** 短暂可 duck（导航/通知音）：降低音量约 40%，不要 mute。 */
        void onFocusDuck();

        /** duck 结束，恢复音量。 */
        void onFocusUnduck();

        /** 当前引擎是否在播，用于判断丢焦时是否需要记 pendingResume。 */
        boolean isEnginePlaying();

        /**
         * 允许后台播且 App 已进后台时，忽略 {@code LOSS_TRANSIENT} 的 mute/pending，
         * 避免前后台快切把整引擎 mute/unmute 拉锯（音效卡顿）。
         */
        boolean shouldSuppressTransientPauseInBackground();

        /** 申请焦点失败。 */
        void onFocusRequestFailed();

        /** App 是否在后台（CAN_DUCK 升级抢占用）。 */
        boolean isAppInBackground();

        /**
         * 后台续播且自身 MEDIA/GAME 轨已从 mixer 消失：立刻无爬坡重开 AAudio。
         * 创维会把 USAGE_MEDIA 改写成 GAME，约 9–11s 拆轨；不能等 aliveWatch。
         */
        void onKeepPlayingTrackGone();
    }

    private final Callback callback;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final Object focusLock = new Object();

    private AudioManager audioManager;
    private AudioFocusRequest focusRequest; // API 26+ 使用
    private final AudioManager.OnAudioFocusChangeListener focusListener = this::onAudioFocusChange;

    private volatile boolean hasFocus = false;
    /** 丢焦时若之前在播则置位；重新获得焦点后据此自动恢复。 */
    private volatile boolean pendingResume = false;
    /** CAN_DUCK 期间为 true，GAIN 时 unduck。 */
    private volatile boolean ducked = false;
    /**
     * 下一轮 {@link Callback#onFocusResume()} 须 ResetPlaybackDevice：
     * TRANSIENT/LOSS 或 mixer 拆掉本进程轨后，unmute 救不回已 remove 的 AAudio。
     */
    private volatile boolean reopenPlaybackDevice;
    /** 最近一次置 reopen 的原因：none/preempt/transient/self_gone/device_route/manual */
    private volatile String reopenReason = BoluoAudioStutterProbe.REOPEN_NONE;
    /** playbackCallback 连续扫不到本进程轨的次数（判定仍是一次 false 即 gone）。 */
    private int consecutiveSelfGoneMiss;
    private int competingAudioPollCount = 0;
    private final Runnable competingAudioPoll = this::pollCompetingAudioStopped;
    /** poll 已启动：PlaybackCallback 才能在对方停止时踢一脚；CAN_DUCK 未见 competing 时不得 armed。 */
    private boolean competingPollArmed;
    private boolean playbackCallbackRegistered;
    /** 进后台时刻，用于 TRANSIENT 抖动窗口（对齐 policy 800ms）。 */
    private long backgroundEnteredElapsedMs;
    private boolean lastObservedBackground;
    /** 本轮曾见其它 UID 的 MEDIA/GAME；停播后须静默确认再 unmute（防 B 站暂停尾音）。 */
    private boolean competingEpisodeSawMedia;
    private long lastCompetingSeenElapsedMs;
    private boolean silenceConfirmPosted;
    private final Runnable silenceConfirmPoll = this::pollSilenceConfirm;
    private final Runnable transientGainWatchdog = this::onTransientGainWatchdog;
    private final Runnable duckStaleWatchdog = this::onDuckStaleWatchdog;
    private boolean volumeReceiverRegistered;
    private final BroadcastReceiver volumeReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            if (intent == null
                    || !"android.media.VOLUME_CHANGED_ACTION".equals(intent.getAction())) {
                return;
            }
            int stream = intent.getIntExtra("android.media.EXTRA_VOLUME_STREAM_TYPE", -1);
            if (stream != AudioManager.STREAM_MUSIC) {
                return;
            }
            int prev = intent.getIntExtra("android.media.EXTRA_PREV_VOLUME_STREAM_VALUE", -1);
            int nowVol = intent.getIntExtra("android.media.EXTRA_VOLUME_STREAM_VALUE", -1);
            onMusicVolumeChanged(prev, nowVol);
        }
    };
    /** B 站等视频常不发 AUDIOFOCUS_LOSS，靠 PlaybackConfiguration 兜底抢占。 */
    private final AudioManager.AudioPlaybackCallback playbackCallback =
            new AudioManager.AudioPlaybackCallback() {
                @Override
                public void onPlaybackConfigChanged(List<AudioPlaybackConfiguration> configs) {
                    onForeignPlaybackConfigChanged();
                }
            };

    WanosAudioFocusManager(Callback callback) {
        this.callback = callback;
    }

    boolean hasAudioFocus() {
        return hasFocus;
    }

    boolean isPendingResume() {
        return pendingResume;
    }

    /**
     * 焦点恢复时是否必须重开 AAudio。消费即清，避免回前台正常播时重复 Reset 咯噔。
     */
    boolean consumeReopenPlaybackDevice() {
        synchronized (focusLock) {
            boolean reopen = reopenPlaybackDevice;
            reopenPlaybackDevice = false;
            return reopen;
        }
    }

    String consumeReopenReason() {
        synchronized (focusLock) {
            String reason = reopenReason;
            boolean wasSet = reopenPlaybackDevice;
            reopenPlaybackDevice = false;
            reopenReason = BoluoAudioStutterProbe.REOPEN_NONE;
            String out = reason == null ? BoluoAudioStutterProbe.REOPEN_NONE : reason;
            Log.i(TAG, "reopenPlaybackDevice consume reason=" + out + " wasSet=" + wasSet);
            Trace.beginSection("reopen_consume");
            Trace.endSection();
            return out;
        }
    }

    String peekReopenReason() {
        String reason = reopenReason;
        return reason == null ? BoluoAudioStutterProbe.REOPEN_NONE : reason;
    }

    boolean scanSelfMediaPlaybackActive() {
        return isSelfMediaPlaybackActive(getAudioManager());
    }

    private void markReopenLocked(String reason) {
        reopenPlaybackDevice = true;
        reopenReason = reason;
        Trace.beginSection("reopen_set");
        Trace.endSection();
        Log.i(TAG, "reopenPlaybackDevice set reason=" + reason
                + " pendingResume=" + pendingResume
                + " ducked=" + ducked
                + " hasFocus=" + hasFocus);
    }

    boolean needsPlaybackReopen() {
        return reopenPlaybackDevice;
    }

    boolean isDucked() {
        return ducked;
    }

    /** 反射获取 Application Context，后台可用（与 WIAEngineJNI 一致）。 */
    private Context resolveContext() {
        try {
            Object app = Class.forName("android.app.ActivityThread")
                    .getMethod("currentApplication")
                    .invoke(null);
            if (app instanceof Context) {
                return ((Context) app).getApplicationContext();
            }
        } catch (Exception e) {
            Log.d(TAG, "resolveContext failed: " + e.getMessage());
        }
        return null;
    }

    private AudioManager getAudioManager() {
        if (audioManager == null) {
            Context ctx = resolveContext();
            if (ctx != null) {
                audioManager = (AudioManager) ctx.getSystemService(Context.AUDIO_SERVICE);
            }
        }
        return audioManager;
    }

    /**
     * 焦点抢占/恢复上云（反射宿主 BoluoBlsLogger，unityLibrary 无编译依赖）。
     * 未配置 BLS / 宿主未装 logger 时静默跳过，绝不影响音频热路径。
     */
    private void reportFocusBls(String event, String level, String focusChange,
                                boolean pending, long durationMs, String detail) {
        try {
            Class<?> cls = Class.forName("com.wanos.boluo.log.BoluoBlsLogger");
            Object logger = cls.getField("INSTANCE").get(null);
            Map<String, Object> fields = new HashMap<>();
            fields.put("event", event);
            fields.put("level", level);
            fields.put("tag", focusChange);
            fields.put("focusChange", focusChange);
            fields.put("pendingResume", pending);
            fields.put("durationMs", durationMs);
            fields.put("source", "native_android");
            if (detail != null && !detail.isEmpty()) {
                fields.put("detail", detail.length() > 400 ? detail.substring(0, 400) : detail);
            }
            cls.getMethod("logEvent", Map.class).invoke(logger, fields);
        } catch (Throwable t) {
            Log.d(TAG, "BLS report skipped: " + t.getMessage());
        }
    }

    private static String focusChangeName(int focusChange) {
        switch (focusChange) {
            case AudioManager.AUDIOFOCUS_GAIN:
                return "AUDIOFOCUS_GAIN";
            case AudioManager.AUDIOFOCUS_LOSS:
                return "AUDIOFOCUS_LOSS";
            case AudioManager.AUDIOFOCUS_LOSS_TRANSIENT:
                return "AUDIOFOCUS_LOSS_TRANSIENT";
            case AudioManager.AUDIOFOCUS_LOSS_TRANSIENT_CAN_DUCK:
                return "AUDIOFOCUS_LOSS_TRANSIENT_CAN_DUCK";
            default:
                return "AUDIOFOCUS_UNKNOWN_" + focusChange;
        }
    }

    /**
     * 实际发起 {@link AudioManager#requestAudioFocus}（须持 {@link #focusLock}）。
     * @param force 为 true 时即使本地 hasFocus 也重申（假持有 / 起播门禁 / 回前台强抢）
     * @param notifyFailed 失败时是否回调 {@link Callback#onFocusRequestFailed()}（回前台强抢失败不弹 toast）
     */
    private boolean performRequestFocus(boolean force, boolean notifyFailed) {
        AudioManager am = getAudioManager();
        if (am == null) {
            Log.w(TAG, "requestFocus: AudioManager unavailable");
            if (notifyFailed) {
                callback.onFocusRequestFailed();
            }
            return false;
        }
        int result;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            AudioAttributes attributes = new AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                    .build();
            focusRequest = new AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN)
                    .setAudioAttributes(attributes)
                    .setWillPauseWhenDucked(false)
                    .setOnAudioFocusChangeListener(focusListener, mainHandler)
                    .build();
            result = am.requestAudioFocus(focusRequest);
        } else {
            result = am.requestAudioFocus(focusListener,
                    AudioManager.STREAM_MUSIC, AudioManager.AUDIOFOCUS_GAIN);
        }
        hasFocus = (result == AudioManager.AUDIOFOCUS_REQUEST_GRANTED);
        Log.i(TAG, "requestFocus" + (force ? "Forced" : "")
                + " result=" + result + " hasFocus=" + hasFocus);
        ensurePlaybackCallbackLocked(am);
                if (hasFocus) {
            stopCompetingAudioPoll();
            stopTransientGainWatchdog();
            pendingResume = false;
        } else {
            reportFocusBls("boluo_audio_focus_loss", "warn", "REQUEST_FAILED",
                    pendingResume, 0, force ? "requestAudioFocus denied (forced)" : "requestAudioFocus denied");
            if (notifyFailed) {
                callback.onFocusRequestFailed();
            }
        }
        return hasFocus;
    }

    /** 播放开始时申请音频焦点（GAIN）。已持有则跳过，避免无谓重复抢占。 */
    void requestFocus() {
        mainHandler.post(() -> {
            synchronized (focusLock) {
                ensurePlaybackCallbackLocked(getAudioManager());
                if (hasFocus) {
                    return;
                }
                performRequestFocus(false, true);
            }
        });
    }

    /**
     * 强制重申音频焦点（忽略本地 hasFocus 短路）。
     * 用于假持有 / 需系统再次仲裁的场景；异步，不驱动 resume。
     */
    void requestFocusForced() {
        mainHandler.post(() -> {
            synchronized (focusLock) {
                Log.i(TAG, "requestFocusForced: reassert AUDIOFOCUS_GAIN");
                performRequestFocus(true, true);
            }
        });
    }

    /**
     * 同步申请焦点（起播 / 媒体控制续播门禁）。
     * 须在拿到 {@link AudioManager#AUDIOFOCUS_REQUEST_GRANTED} 后再 unmute/PostEvent，否则禁止出声叠播。
     *
     * @param force true=即使本地 hasFocus 也重申（同曲 mute 态强制播放）
     * @return true=GRANTED
     */
    boolean requestFocusBlocking(boolean force) {
        synchronized (focusLock) {
            if (hasFocus && !force) {
                Log.i(TAG, "requestFocusBlocking skip: already hasFocus");
                return true;
            }
            Log.i(TAG, "requestFocusBlocking force=" + force + " hadFocus=" + hasFocus
                    + " pendingResume=" + pendingResume);
            return performRequestFocus(force, true);
        }
    }

    /**
     * 回前台强制抢焦点（对齐 iOS {@code handleAppDidBecomeActive} force exclusive reclaim）。
     * <ul>
     *   <li>发起条件：未 userPaused + 有恢复意图（pendingResume 或丢焦后引擎仍 Started/有实例）。
     *       不靠本地状态猜测电话 vs 音乐 App——以 {@code requestAudioFocus} 仲裁为准。</li>
     *   <li>GRANTED → 清 stale pending、unduck、{@link Callback#onFocusResume()}。</li>
     *   <li>DENIED → 保持 mute/pending，继续竞争轮询；不叠播。</li>
     *   <li>已持焦且无 pending/duck → NOOP，避免每次回前台咯噔。</li>
     * </ul>
     */
    void reclaimOnForeground(boolean userPaused, boolean hasResumeIntent) {
        mainHandler.post(() -> {
            synchronized (focusLock) {
                if (userPaused) {
                    Log.i(TAG, "reclaimOnForeground skip: userPaused");
                    return;
                }
                if (!hasResumeIntent && !pendingResume && !reopenPlaybackDevice && !ducked) {
                    Log.i(TAG, "reclaimOnForeground skip: no resume intent");
                    return;
                }
                // 幂等 NOOP：已持焦且无待恢复/duck/拆轨，勿重复 request 重启 IO
                if (hasFocus && !pendingResume && !ducked && !reopenPlaybackDevice) {
                    Log.i(TAG, "reclaimOnForeground NOOP: already hasFocus");
                    return;
                }
                long now = SystemClock.elapsedRealtime();
                // CWPA-44：无论是否已持焦，短间隔 reclaim 一律 debounce，避免 FG/BG 抖动重复 request
                if (now - lastReclaimAttemptElapsedMs < RECLAIM_DEBOUNCE_MS) {
                    Log.i(TAG, "reclaimOnForeground debounced (interval="
                            + (now - lastReclaimAttemptElapsedMs) + "ms)");
                    Log.i(TAG, "[CWPA-44] reclaimOnForeground debounced");
                    return;
                }
                lastReclaimAttemptElapsedMs = now;
                Log.i(TAG, "reclaimOnForeground: force exclusive reclaim"
                        + " pendingResume=" + pendingResume
                        + " reopen=" + reopenPlaybackDevice
                        + " ducked=" + ducked
                        + " hasFocus=" + hasFocus
                        + " hasResumeIntent=" + hasResumeIntent);
                // 已持焦但轨被提示音拆掉：勿再 request（咯噔），直接 resume/重开设备
                if (hasFocus && (pendingResume || ducked || reopenPlaybackDevice)) {
                    stopCompetingAudioPoll();
                    stopTransientGainWatchdog();
                    stopDuckStaleWatchdog();
                    clearCompetingEpisodeLocked();
                    pendingResume = false;
                    if (ducked) {
                        ducked = false;
                        Log.i(TAG, "reclaimOnForeground hasFocus -> unduck + resume");
                        callback.onFocusUnduck();
                    }
                    Log.i(TAG, "[PA-904] foreground auto-resume hasFocus reopen="
                            + reopenPlaybackDevice);
                    callback.onFocusResume();
                    return;
                }
                boolean granted = performRequestFocus(true, false);
                if (granted) {
                    stopCompetingAudioPoll();
                    clearCompetingEpisodeLocked();
                    pendingResume = false;
                    if (ducked) {
                        ducked = false;
                        Log.i(TAG, "reclaimOnForeground -> unduck");
                        callback.onFocusUnduck();
                    }
                    Log.i(TAG, "forced reclaim OK — clear stale pendingResume, resume");
                    long t0 = SystemClock.elapsedRealtime();
                    callback.onFocusResume();
                    long ms = SystemClock.elapsedRealtime() - t0;
                    reportFocusBls("boluo_audio_focus_gain", "info", "FORCED_FOREGROUND_RECLAIM",
                            false, ms, "forced-reclaim-clear-stale-pending");
                } else {
                    Log.i(TAG, "forced reclaim FAILED — keep mute/pending (system arbitration)");
                    if (!pendingResume) {
                        pendingResume = true;
                    }
                    startCompetingAudioPoll();
                    reportFocusBls("boluo_audio_focus_loss", "warn", "FORCED_FOREGROUND_RECLAIM_FAILED",
                            true, 0, "system denied — defer");
                }
            }
        });
    }

    /** 停止播放时放弃音频焦点，并清理轮询 / pendingResume / duck。 */
    void abandonFocus() {
        mainHandler.post(() -> {
            synchronized (focusLock) {
                pendingResume = false;
                reopenPlaybackDevice = false;
                reopenReason = BoluoAudioStutterProbe.REOPEN_NONE;
                stopCompetingAudioPoll();
                stopTransientGainWatchdog();
                stopDuckStaleWatchdog();
                clearCompetingEpisodeLocked();
                // 停播时系统不一定再发 GAIN；若不在此 unduck，Unity 侧会粘在 ~0.4 音量
                if (ducked) {
                    ducked = false;
                    Log.i(TAG, "abandonFocus -> unduck");
                    callback.onFocusUnduck();
                }
                AudioManager am = getAudioManager();
                if (am == null) {
                    hasFocus = false;
                    return;
                }
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && focusRequest != null) {
                    am.abandonAudioFocusRequest(focusRequest);
                } else {
                    am.abandonAudioFocus(focusListener);
                }
                unregisterPlaybackCallbackLocked(am);
                unregisterVolumeReceiverLocked();
                hasFocus = false;
                Log.i(TAG, "abandonFocus done");
            }
        });
    }

    private void onAudioFocusChange(int focusChange) {
        synchronized (focusLock) {
            String changeName = focusChangeName(focusChange);
            Log.i(TAG, "onAudioFocusChange: " + changeName);
            // 验证 CWPA-44：前后台切换时焦点抖动导致卡顿
            Log.i(TAG, "[CWPA-44] onAudioFocusChange " + changeName
                    + " hasFocus=" + hasFocus + " pendingResume=" + pendingResume
                    + " ducked=" + ducked
                    + " suppressTransientBg=" + callback.shouldSuppressTransientPauseInBackground());
            switch (focusChange) {
                case AudioManager.AUDIOFOCUS_GAIN: {
                    hasFocus = true;
                    stopCompetingAudioPoll();
                    stopTransientGainWatchdog();
                    stopDuckStaleWatchdog();
                    if (ducked) {
                        ducked = false;
                        Log.i(TAG, "GAIN -> unduck");
                        callback.onFocusUnduck();
                        reportFocusBls("boluo_audio_focus_gain", "info", changeName,
                                false, 0, "unduck");
                    }
                    if (pendingResume) {
                        // 回岛后对方还在播：静默确认永远 reset。强抢续播，禁止死等。
                        boolean competingNow = isCompetingMediaPlaybackActive(getAudioManager());
                        boolean inBackground = callback.isAppInBackground();
                        boolean fgExclusive = !inBackground && competingNow;
                        if (fgExclusive) {
                            pendingResume = false;
                            clearCompetingEpisodeLocked();
                            Log.i(TAG, "GAIN -> FG exclusive resume (competitor still active)");
                            Log.i(TAG, "[PA-904] GAIN FG exclusive skip silence-confirm");
                            callback.onFocusResume();
                            break;
                        }
                        // 后台：对方播放器仍在出声 → 等它停，禁止抢占。
                        if (inBackground && competingNow) {
                            Log.i(TAG, "GAIN keep waiting — competing media, do not steal");
                            Log.i(TAG,
                                    "[PA-904] GAIN BG skip reclaim competing media");
                            startCompetingAudioPoll();
                            break;
                        }
                        if (shouldDeferAutoResumeLocked(false)) {
                            Log.i(TAG, "GAIN deferred until silence confirm bg="
                                    + callback.isAppInBackground());
                            Log.i(TAG,
                                    "[CWPA-66] GAIN defer silence-confirm bg="
                                            + callback.isAppInBackground());
                            startSilenceConfirmLocked();
                            break;
                        }
                        pendingResume = false;
                        clearCompetingEpisodeLocked();
                        Log.i(TAG, "GAIN -> auto resume");
                        long t0 = SystemClock.elapsedRealtime();
                        callback.onFocusResume();
                        long ms = SystemClock.elapsedRealtime() - t0;
                        reportFocusBls("boluo_audio_focus_gain", "info", changeName,
                                false, ms, "auto_resume");
                    } else if (reopenPlaybackDevice) {
                        Log.i(TAG, "GAIN -> reopen dead output (no pendingResume)");
                        Log.i(TAG, "[PA-904] GAIN reopen after duck/tear");
                        callback.onFocusResume();
                    }
                    break;
                }
                case AudioManager.AUDIOFOCUS_LOSS_TRANSIENT_CAN_DUCK: {
                    // 前台无 competing media：duck ~40%（导航/短信，PRD P0-2/P0-3）。
                    // 后台或已有 MEDIA/GAME 竞争（B 站常用 MAY_DUCK）→ 抢占静音，禁止叠播。
                    noteBackgroundStateLocked();
                    boolean competing = isCompetingMediaPlaybackActive(getAudioManager());
                    // 后台或对方 MEDIA 出声 → 抢占。前台无 competing 仍 duck（导航/短信）。
                    boolean preempt = callback.isAppInBackground() || competing;
                    long t0 = SystemClock.elapsedRealtime();
                    if (preempt) {
                        Log.i(TAG, "LOSS_TRANSIENT_CAN_DUCK -> preempt bg="
                                + callback.isAppInBackground() + " competing=" + competing);
                        Log.i(TAG, "[CWPA-66] CAN_DUCK preempt bg="
                                + callback.isAppInBackground() + " competing=" + competing);
                        // competingLongUi / startPoll 仅真 MEDIA；系统提示不得记 episode（否则 30s 死等回前台）。
                        applyCompetingPreemptLocked(changeName, competing, competing);
                        if (callback.isAppInBackground() && !competing) {
                            startTransientGainWatchdog();
                        }
                    } else if (callback.isEnginePlaying()) {
                        ducked = true;
                        Log.i(TAG, "LOSS_TRANSIENT_CAN_DUCK -> duck");
                        callback.onFocusDuck();
                        startDuckStaleWatchdog();
                        reportFocusBls("boluo_audio_focus_loss", "info", changeName,
                                false, SystemClock.elapsedRealtime() - t0, "transient_duck");
                    }
                    break;
                }
                case AudioManager.AUDIOFOCUS_LOSS_TRANSIENT: {
                    // 瞬时丢焦（来电等）：暂停并记 pendingResume，不放弃焦点，等待系统回调 GAIN。
                    // CWPA-44：进后台 800ms 内无 competing 可忽略抖动；其后或 B 站 competing 禁止忽略。
                    long t0 = SystemClock.elapsedRealtime();
                    noteBackgroundStateLocked();
                    boolean competing = isCompetingMediaPlaybackActive(getAudioManager());
                    long bgAge = lastObservedBackground
                            ? (t0 - backgroundEnteredElapsedMs) : 0L;
                    // 回前台刚强抢后 B 站仍发 TRANSIENT：禁止再 pause，立刻再申 GAIN。
                    if (competing && !callback.isAppInBackground()) {
                        Log.i(TAG, "LOSS_TRANSIENT fight-back FG competing, re-request exclusive");
                        Log.i(TAG, "[PA-904] LOSS_TRANSIENT FG exclusive fight-back");
                        boolean granted = performRequestFocus(true, false);
                        if (!granted && callback.isEnginePlaying()) {
                            pendingResume = true;
                            markReopenLocked(BoluoAudioStutterProbe.REOPEN_TRANSIENT);
                        }
                        break;
                    }
                    boolean suppress = callback.shouldSuppressTransientPauseInBackground()
                            && !competing
                            && bgAge < BACKGROUND_TRANSIENT_SUPPRESS_WINDOW_MS;
                    if (suppress) {
                        Log.i(TAG, "LOSS_TRANSIENT suppressed (bg flicker window age="
                                + bgAge + "ms)");
                        reportFocusBls("boluo_audio_focus_loss", "info", changeName,
                                false, 0, "transient_suppressed_background_play");
                        break;
                    }
                    Log.i(TAG, "LOSS_TRANSIENT -> pause competing=" + competing
                            + " bgAge=" + bgAge);
                    applyTransientPauseLocked();
                    startTransientGainWatchdog();
                    long ms = SystemClock.elapsedRealtime() - t0;
                    Log.i(TAG, "LOSS_TRANSIENT handleMs=" + ms);
                    reportFocusBls("boluo_audio_focus_loss", "warn", changeName,
                            pendingResume, ms, "transient_pause");
                    break;
                }
                case AudioManager.AUDIOFOCUS_LOSS: {
                    // 永久丢焦（被其它播放器长期独占）：系统不会再回调 GAIN，
                    // 需主动轮询——待其它音源停止后重新申请焦点以自动恢复。
                    long t0 = SystemClock.elapsedRealtime();
                    Log.i(TAG, "LOSS -> pause pendingResume, start competing-audio poll");
                    applyCompetingPreemptLocked(changeName, true, true);
                    long ms = SystemClock.elapsedRealtime() - t0;
                    Log.i(TAG, "LOSS handleMs=" + ms);
                    break;
                }
                default:
                    break;
            }
        }
    }

    private void noteBackgroundStateLocked() {
        boolean bg = callback.isAppInBackground();
        if (bg && !lastObservedBackground) {
            backgroundEnteredElapsedMs = SystemClock.elapsedRealtime();
        }
        lastObservedBackground = bg;
    }

    /**
     * 长期竞争抢占：mute、记 pending。
     * @param startPoll true=对方停止后无 GAIN（LOSS / 已见到 PlaybackConfiguration）；
     *                  false=等系统 GAIN 或回前台 reclaim（CAN_DUCK 未见 competing 时禁止 poll，
     *                  否则 1s 内 requestFocus 仍 GRANTED 会立刻叠播）。
     */
    private void applyCompetingPreemptLocked(String reason, boolean competingLongUi,
                                            boolean startPoll) {
        if (callback.shouldSuppressTransientPauseInBackground()) {
            Log.i(TAG, "[CWPA-audio] " + reason + " skip mute (keep playing bg)");
            return;
        }
        hasFocus = false;
        markReopenLocked(BoluoAudioStutterProbe.REOPEN_PREEMPT);
        if (callback.isEnginePlaying()) {
            pendingResume = true;
        }
        // 只在真 competing / LOSS 时记 episode。系统提示 CAN_DUCK(startPoll=false) 不得记。
        if (startPoll) {
            noteCompetingSeenLocked();
        }
        stopSilenceConfirmLocked();
        stopDuckStaleWatchdog();
        Log.i(TAG, reason + " -> preempt pendingResume=" + pendingResume
                + " startPoll=" + startPoll);
        // 必须先护栏 mute 再清 duck：旧顺序 Unduck→满增益再 mute = 切微信/B 站电流音。
        callback.onFocusPause();
        if (ducked) {
            ducked = false;
        }
        callback.onFocusPauseUi(competingLongUi);
        if (pendingResume && startPoll) {
            startCompetingAudioPoll();
        }
        reportFocusBls("boluo_audio_focus_loss", "warn", reason,
                pendingResume, 0, startPoll ? "competing_preempt_poll" : "competing_preempt_wait_gain");
    }

    /** 瞬时丢焦暂停：不放弃本地焦点记账以外的轮询（等系统 GAIN）。 */
    private void applyTransientPauseLocked() {
        if (callback.shouldSuppressTransientPauseInBackground()) {
            Log.i(TAG, "[CWPA-audio] TRANSIENT skip mute (keep playing bg)");
            return;
        }
        markReopenLocked(BoluoAudioStutterProbe.REOPEN_TRANSIENT);
        if (callback.isEnginePlaying()) {
            pendingResume = true;
        }
        // 先护栏 mute，禁止 Unduck 把增益拉回 1.0 再硬 mute。
        callback.onFocusPause();
        if (ducked) {
            ducked = false;
        }
        callback.onFocusPauseUi(false);
    }

    private void ensurePlaybackCallbackLocked(AudioManager am) {
        ensureVolumeReceiverLocked();
        if (am == null || playbackCallbackRegistered
                || Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
            return;
        }
        try {
            am.registerAudioPlaybackCallback(playbackCallback, mainHandler);
            playbackCallbackRegistered = true;
            Log.i(TAG, "AudioPlaybackCallback registered");
        } catch (Exception e) {
            Log.w(TAG, "registerAudioPlaybackCallback: " + e.getMessage());
        }
    }

    private void unregisterPlaybackCallbackLocked(AudioManager am) {
        if (!playbackCallbackRegistered || am == null
                || Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
            return;
        }
        try {
            am.unregisterAudioPlaybackCallback(playbackCallback);
        } catch (Exception e) {
            Log.w(TAG, "unregisterAudioPlaybackCallback: " + e.getMessage());
        }
        playbackCallbackRegistered = false;
    }

    /**
     * B 站等可能不申请焦点：其它 App 的 MEDIA/GAME 一出声即抢占。
     * 对方停止且 pendingResume 时先静默确认，禁止立刻 unmute 漏尾音。
     */
    private void onForeignPlaybackConfigChanged() {
        synchronized (focusLock) {
            AudioManager am = getAudioManager();
            boolean competing = isCompetingMediaPlaybackActive(am);
            if (competing) {
                // 回前台已抢到 GAIN：对方 MEDIA 可能还在 mixer 里收尾，禁止再 mute 把自己掐死。
                // 进后台见到对方 MEDIA 仍抢占（B 站/其它音乐叠播）。
                if (hasFocus && !callback.isAppInBackground()) {
                    Log.i(TAG,
                            "[PA-904] skip remute FG exclusive hold (reclaim won)");
                    return;
                }
                noteCompetingSeenLocked();
                if (callback.isEnginePlaying()) {
                    Log.i(TAG, "[CWPA-66] playbackCallback preempt competing media");
                    applyCompetingPreemptLocked("PLAYBACK_CONFIG_COMPETING", true, true);
                }
            } else {
                boolean selfGone = scanSelfPlaybackGoneLocked(am);
                Log.i(TAG, "self_gone_scan_result=" + selfGone
                        + " pendingResume=" + pendingResume
                        + " ducked=" + ducked
                        + " consecutive_miss_count=" + consecutiveSelfGoneMiss);
                BoluoAudioStutterProbe.cacheMixer(!selfGone);
                // SKS30：USAGE_GAME 轨在 mixer 常扫不到 → self_gone 假阳性。
                // 线上 3eec80b6：有 pending/duck 才 mark reopen。
                // 现网回归：后台续播干净 HOME 也会 NoAudioTrack，仅 defer 会永久无声。
                // 干净后台 self_gone：交给 keepPlaying L0/L1（L1 可 reset+replay，对齐线上续播）。
                boolean keepPlayingBg = callback.isAppInBackground()
                        && callback.shouldSuppressTransientPauseInBackground();
                if (selfGone && keepPlayingBg && !pendingResume && !ducked) {
                    Log.i(TAG, "[CWPA-audio] keepPlayingBg self_gone → MEDIA/L1 recover (no defer silence)");
                    callback.onKeepPlayingTrackGone();
                } else if (selfGone && (pendingResume || ducked)) {
                    markReopenLocked(BoluoAudioStutterProbe.REOPEN_SELF_GONE);
                    if (!pendingResume && callback.isEnginePlaying()) {
                        pendingResume = true;
                    }
                    Log.i(TAG, "self mixer tracks gone — mark reopen"
                            + " pendingResume=" + pendingResume + " ducked=" + ducked);
                    Log.i(TAG, "[PA-904] self tracks gone ducked=" + ducked
                            + " pendingResume=" + pendingResume);
                }
                if (pendingResume) {
                    if (shouldDeferAutoResumeLocked(false)) {
                        Log.i(TAG,
                                "[CWPA-66] playbackCallback defer silence-confirm bg="
                                        + callback.isAppInBackground());
                        startSilenceConfirmLocked();
                    } else if (competingPollArmed) {
                        mainHandler.removeCallbacks(competingAudioPoll);
                        mainHandler.post(competingAudioPoll);
                    }
                }
            }
        }
    }

    private void startCompetingAudioPoll() {
        stopCompetingAudioPoll();
        competingPollArmed = true;
        competingAudioPollCount = 0;
        mainHandler.postDelayed(competingAudioPoll, COMPETING_AUDIO_POLL_INTERVAL_MS);
    }

    private void stopCompetingAudioPoll() {
        competingPollArmed = false;
        mainHandler.removeCallbacks(competingAudioPoll);
        competingAudioPollCount = 0;
    }

    private void noteCompetingSeenLocked() {
        competingEpisodeSawMedia = true;
        lastCompetingSeenElapsedMs = SystemClock.elapsedRealtime();
    }

    private void clearCompetingEpisodeLocked() {
        competingEpisodeSawMedia = false;
        lastCompetingSeenElapsedMs = 0L;
        stopSilenceConfirmLocked();
    }

    /**
     * 对齐 C# {@code ShouldDeferAndroidAutoResumeAfterCompetingStopped}。
     * 回前台 reclaim 不推迟；B 站暂停后须满静默窗才 unmute，避免一声尾音。
     */
    private boolean shouldDeferAutoResumeLocked(boolean isForegroundReclaim) {
        if (isForegroundReclaim || !competingEpisodeSawMedia) {
            return false;
        }
        long since = lastCompetingSeenElapsedMs > 0
                ? SystemClock.elapsedRealtime() - lastCompetingSeenElapsedMs
                : 0L;
        long required = callback.isAppInBackground()
                ? BACKGROUND_SILENCE_CONFIRM_MS
                : FOREGROUND_COMPETING_RESUME_DEBOUNCE_MS;
        return since < required;
    }

    private void startSilenceConfirmLocked() {
        stopCompetingAudioPoll();
        if (silenceConfirmPosted) {
            return;
        }
        silenceConfirmPosted = true;
        long since = lastCompetingSeenElapsedMs > 0
                ? SystemClock.elapsedRealtime() - lastCompetingSeenElapsedMs
                : 0L;
        long required = callback.isAppInBackground()
                ? BACKGROUND_SILENCE_CONFIRM_MS
                : FOREGROUND_COMPETING_RESUME_DEBOUNCE_MS;
        long remain = Math.max(200L, required - since);
        long delay = Math.min(remain, SILENCE_CONFIRM_POLL_MS);
        mainHandler.postDelayed(silenceConfirmPoll, delay);
        Log.i(TAG, "silence confirm armed remain=" + remain + "ms bg="
                + callback.isAppInBackground());
    }

    private void stopSilenceConfirmLocked() {
        silenceConfirmPosted = false;
        mainHandler.removeCallbacks(silenceConfirmPoll);
    }

    /** 静默确认：窗口内对方再出声则重置；满窗且仍 pending 才 requestFocus/unmute。 */
    private void pollSilenceConfirm() {
        synchronized (focusLock) {
            silenceConfirmPosted = false;
            if (!pendingResume) {
                return;
            }
            if (!callback.isAppInBackground()) {
                boolean granted = hasFocus || performRequestFocus(true, false);
                if (granted) {
                    pendingResume = false;
                    clearCompetingEpisodeLocked();
                    Log.i(TAG, "silence confirmed FG exclusive -> auto resume");
                    Log.i(TAG, "[PA-904] silence-confirm FG exclusive resume");
                    callback.onFocusResume();
                } else {
                    startCompetingAudioPoll();
                }
                return;
            }
            if (isCompetingMediaPlaybackActive(getAudioManager())) {
                Log.i(TAG, "[CWPA-66] silence-confirm reset (competing back)");
                Log.i(TAG, "silence confirm: competing media, do not steal");
                startSilenceConfirmLocked();
                return;
            }
            if (shouldDeferAutoResumeLocked(false)) {
                startSilenceConfirmLocked();
                return;
            }
            boolean granted = hasFocus || performRequestFocus(false, false);
            if (granted) {
                pendingResume = false;
                clearCompetingEpisodeLocked();
                Log.i(TAG, "silence confirmed -> auto resume");
                Log.i(TAG, "[CWPA-66] silence-confirm resume");
                long t0 = SystemClock.elapsedRealtime();
                callback.onFocusResume();
                long ms = SystemClock.elapsedRealtime() - t0;
                reportFocusBls("boluo_audio_focus_gain", "info", "SILENCE_CONFIRMED_RESUME",
                        false, ms, "silence_confirm");
            } else {
                startCompetingAudioPoll();
            }
        }
    }

    /** 轮询其它音源是否已停止：已停止则重新申请焦点，成功即自动恢复。 */
    private void pollCompetingAudioStopped() {
        synchronized (focusLock) {
            if (!pendingResume) {
                return;
            }
            AudioManager am = getAudioManager();
            if (am == null) {
                return;
            }
            // 已回前台：不要死等对方停完（对方可能后台继续播），强抢焦点后续播。
            if (!callback.isAppInBackground()) {
                boolean granted = performRequestFocus(true, false);
                if (granted) {
                    pendingResume = false;
                    competingAudioPollCount = 0;
                    clearCompetingEpisodeLocked();
                    Log.i(TAG, "competing poll: FG exclusive reclaim, auto resume");
                    Log.i(TAG, "[PA-904] poll FG reclaim instead of wait");
                    callback.onFocusResume();
                    return;
                }
                competingAudioPollCount++;
                if (competingAudioPollCount < COMPETING_AUDIO_POLL_MAX) {
                    mainHandler.postDelayed(competingAudioPoll, COMPETING_AUDIO_POLL_INTERVAL_MS);
                }
                return;
            }
            // 仍有其它音源在播 → 继续等待（带看门狗上限）。
            // CWPA-66：isMusicActive 对哔哩/腾讯视频等纯视频流常为 false，须再查活跃 PlaybackConfiguration。
            if (isCompetingPlaybackActive(am)) {
                competingAudioPollCount++;
                if (competingAudioPollCount < COMPETING_AUDIO_POLL_MAX) {
                    mainHandler.postDelayed(competingAudioPoll, COMPETING_AUDIO_POLL_INTERVAL_MS);
                } else {
                    Log.i(TAG, "competing-audio poll watchdog expired");
                    reportFocusBls("boluo_audio_focus_loss", "info", "COMPETING_POLL_WATCHDOG",
                            true, 0, "watchdog_expired");
                }
                return;
            }
            if (shouldDeferAutoResumeLocked(false)) {
                Log.i(TAG, "competing audio gone -> defer resume until silence confirm bg="
                        + callback.isAppInBackground());
                Log.i(TAG,
                        "[CWPA-66] poll defer silence-confirm bg="
                                + callback.isAppInBackground());
                startSilenceConfirmLocked();
                return;
            }
            // 其它音源已停止且静默确认通过 → 重新申请焦点。
            boolean granted = performRequestFocus(false, false);
            if (granted) {
                pendingResume = false;
                competingAudioPollCount = 0;
                clearCompetingEpisodeLocked();
                Log.i(TAG, "competing audio gone -> re-acquired focus, auto resume");
                long t0 = SystemClock.elapsedRealtime();
                callback.onFocusResume();
                long ms = SystemClock.elapsedRealtime() - t0;
                reportFocusBls("boluo_audio_focus_gain", "info", "AUDIOFOCUS_REACQUIRED",
                        false, ms, "poll_reacquire");
            } else {
                // 尚未拿回焦点（系统仍在过渡）→ 继续轮询。
                competingAudioPollCount++;
                if (competingAudioPollCount < COMPETING_AUDIO_POLL_MAX) {
                    mainHandler.postDelayed(competingAudioPoll, COMPETING_AUDIO_POLL_INTERVAL_MS);
                }
            }
        }
    }

    /**
     * CWPA-66：检测其它 App 是否仍在出声（含视频 App 的 USAGE_MEDIA 流）。
     * 禁止用 {@link AudioManager#isMusicActive()} 当主判据：对哔哩纯视频常为 false（漏检叠播），
     * 且会把波萝自己的流算进去（mute 后 poll 永不恢复）。
     */
    private boolean isCompetingPlaybackActive(AudioManager am) {
        return isCompetingMediaPlaybackActive(am);
    }

    private boolean isCompetingMediaPlaybackActive(AudioManager am) {
        if (am == null || Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
            return false;
        }
        try {
            List<AudioPlaybackConfiguration> configs = am.getActivePlaybackConfigurations();
            if (configs == null) {
                return false;
            }
            int myUid = Process.myUid();
            for (AudioPlaybackConfiguration cfg : configs) {
                if (cfg == null || !isPlaybackStarted(cfg)) {
                    continue;
                }
                AudioAttributes attrs = cfg.getAudioAttributes();
                if (attrs == null || !isForeignCompetingUsage(attrs)) {
                    continue;
                }
                // 自身 WIA AAudio 在创维上常是 USAGE_MEDIA + CONTENT_TYPE_UNKNOWN；
                // PID/UID 偶发错报时勿当外来 MEDIA 抢占，否则 HOME 后续播被 mute。
                if (attrs.getUsage() == AudioAttributes.USAGE_MEDIA
                        && attrs.getContentType() == AudioAttributes.CONTENT_TYPE_UNKNOWN) {
                    continue;
                }
                // 本进程 MEDIA 静音锚点（CONTENT_TYPE_MUSIC）：保活轨绝不能当 competing。
                if (attrs.getUsage() == AudioAttributes.USAGE_MEDIA
                        && attrs.getContentType() == AudioAttributes.CONTENT_TYPE_MUSIC
                        && BoluoKeepPlayingMediaAnchor.isRunning()) {
                    Boolean own = isForeignClient(cfg, myUid);
                    if (own == null || !own) {
                        continue;
                    }
                }
                Boolean foreign = isForeignClient(cfg, myUid);
                if (foreign == null) {
                    // 无 UID 时无法与自身 Unity/WIA 多路流区分，交给 CAN_DUCK 后台升级 / TRANSIENT 窗口
                    continue;
                }
                if (foreign) {
                    noteCompetingSeenLocked();
                    int uid = -1;
                    int pid = -1;
                    try {
                        uid = (Integer) cfg.getClass().getMethod("getClientUid").invoke(cfg);
                    } catch (Exception ignored) {
                        // 仅诊断
                    }
                    try {
                        pid = (Integer) cfg.getClass().getMethod("getClientPid").invoke(cfg);
                    } catch (Exception ignored) {
                        // 仅诊断
                    }
                    Log.i(TAG,
                            "[CWPA-66] competing playback active usage=" + attrs.getUsage()
                                    + " content=" + attrs.getContentType()
                                    + " uid=" + uid + " pid=" + pid + " myUid=" + myUid
                                    + " myPid=" + Process.myPid());
                    return true;
                }
            }
        } catch (Exception e) {
            Log.w(TAG, "isCompetingMediaPlaybackActive: " + e.getMessage());
        }
        return false;
    }

    /**
     * 对方音乐/视频 competing：只认 MEDIA/影视。
     * USAGE_GAME 不计入——SKS30 上自身 AAudio 常被标 GAME 且 UID 错报，unmute 后会误抢占自己。
     */
    private static boolean isForeignCompetingUsage(AudioAttributes attrs) {
        int usage = attrs.getUsage();
        int content = attrs.getContentType();
        return usage == AudioAttributes.USAGE_MEDIA
                || content == AudioAttributes.CONTENT_TYPE_MUSIC
                || content == AudioAttributes.CONTENT_TYPE_MOVIE;
    }

    /** 本进程 mixer 检测仍含 GAME（Unity/WIA AAudio）。 */
    private static boolean isMediaLikeUsage(AudioAttributes attrs) {
        return isForeignCompetingUsage(attrs)
                || attrs.getUsage() == AudioAttributes.USAGE_GAME;
    }

    private static boolean isPlaybackStarted(AudioPlaybackConfiguration cfg) {
        try {
            Method isActive = cfg.getClass().getMethod("isActive");
            Object active = isActive.invoke(cfg);
            if (active instanceof Boolean) {
                return (Boolean) active;
            }
        } catch (Exception ignored) {
            // API 28 及以下无 isActive
        }
        try {
            Method getPlayerState = cfg.getClass().getMethod("getPlayerState");
            int state = (Integer) getPlayerState.invoke(cfg);
            return state == 3; // PLAYER_STATE_STARTED
        } catch (Exception e) {
            return false;
        }
    }

    /**
     * @return TRUE=其它进程，FALSE=本进程，null=无法判定
     * <p>创维 SKS30 上 getClientUid 常回 -1。旧逻辑 {@code uid != myUid} 把 -1 当外来，
     * 导致 {@link BoluoKeepPlayingMediaAnchor}（USAGE_MEDIA/MUSIC）一拉起就 CWPA-66
     * preempt → focus_pause 自 mute——Unity 独立包没有这套回调，所以后台能续播。
     */
    private static Boolean isForeignClient(AudioPlaybackConfiguration cfg, int myUid) {
        Integer pid = null;
        try {
            Method getClientPid = cfg.getClass().getMethod("getClientPid");
            pid = (Integer) getClientPid.invoke(cfg);
            if (pid != null && pid > 0 && pid == Process.myPid()) {
                return false;
            }
        } catch (Exception ignored) {
            // API 或 OEM 无 getClientPid
        }
        try {
            Method getClientUid = cfg.getClass().getMethod("getClientUid");
            int uid = (Integer) getClientUid.invoke(cfg);
            if (uid >= 0) {
                return uid != myUid;
            }
            // uid=-1：不可用，禁止当 foreign
        } catch (Exception ignored) {
            // fall through
        }
        if (pid != null && pid > 0 && pid != Process.myPid()) {
            return true;
        }
        return null;
    }

    /** 本进程 MEDIA/GAME 轨是否还在 mixer 里（与 competing 检测对称）。 */
    private boolean isSelfMediaPlaybackActive(AudioManager am) {
        if (am == null || Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
            return false;
        }
        try {
            List<AudioPlaybackConfiguration> configs = am.getActivePlaybackConfigurations();
            if (configs == null) {
                return false;
            }
            int myUid = Process.myUid();
            for (AudioPlaybackConfiguration cfg : configs) {
                if (cfg == null || !isPlaybackStarted(cfg)) {
                    continue;
                }
                AudioAttributes attrs = cfg.getAudioAttributes();
                if (attrs == null || !isMediaLikeUsage(attrs)) {
                    continue;
                }
                Boolean foreign = isForeignClient(cfg, myUid);
                if (foreign != null && !foreign) {
                    return true;
                }
            }
        } catch (Exception e) {
            Log.w(TAG, "isSelfMediaPlaybackActive: " + e.getMessage());
        }
        return false;
    }

    /** @return true 本进程可听轨刚从 mixer 消失 */
    private boolean noteSelfPlaybackGoneLocked(AudioManager am) {
        return !isSelfMediaPlaybackActive(am);
    }

    private boolean scanSelfPlaybackGoneLocked(AudioManager am) {
        boolean gone = noteSelfPlaybackGoneLocked(am);
        if (gone) {
            consecutiveSelfGoneMiss++;
        } else {
            consecutiveSelfGoneMiss = 0;
        }
        return gone;
    }

    private boolean isInVoiceCallLocked(AudioManager am) {
        if (am == null) {
            return false;
        }
        int mode = am.getMode();
        return mode == AudioManager.MODE_IN_CALL
                || mode == AudioManager.MODE_IN_COMMUNICATION
                || mode == AudioManager.MODE_RINGTONE;
    }

    private void startTransientGainWatchdog() {
        stopTransientGainWatchdog();
        if (!pendingResume) {
            return;
        }
        mainHandler.postDelayed(transientGainWatchdog, TRANSIENT_GAIN_WATCHDOG_MS);
        Log.i(TAG, "TRANSIENT GAIN watchdog armed " + TRANSIENT_GAIN_WATCHDOG_MS + "ms");
    }

    private void stopTransientGainWatchdog() {
        mainHandler.removeCallbacks(transientGainWatchdog);
    }

    /**
     * OEM 提示音结束后不发 GAIN：非通话则改走 competing poll 重申焦点。
     * 通话中续期，避免 1.8s 抢焦叠在电话上。
     */
    private void onTransientGainWatchdog() {
        synchronized (focusLock) {
            if (!pendingResume) {
                return;
            }
            AudioManager am = getAudioManager();
            if (isInVoiceCallLocked(am)) {
                Log.i(TAG, "TRANSIENT GAIN watchdog deferred (voice call)");
                startTransientGainWatchdog();
                return;
            }
            if (isCompetingMediaPlaybackActive(am)) {
                Log.i(TAG, "TRANSIENT GAIN watchdog: competing still active, poll (do not steal)");
                Log.i(TAG,
                        "[PA-904] TRANSIENT watchdog skip reclaim competing media");
                startCompetingAudioPoll();
                return;
            }
            Log.i(TAG, "TRANSIENT GAIN watchdog — no competing, reacquire");
            Log.i(TAG, "[PA-904] TRANSIENT GAIN watchdog reacquire");
            markReopenLocked(BoluoAudioStutterProbe.REOPEN_TRANSIENT);
            if (hasFocus) {
                pendingResume = false;
                clearCompetingEpisodeLocked();
                callback.onFocusResume();
                return;
            }
            boolean granted = performRequestFocus(false, false);
            if (granted) {
                pendingResume = false;
                clearCompetingEpisodeLocked();
                callback.onFocusResume();
            } else {
                startCompetingAudioPoll();
            }
        }
    }

    private void startDuckStaleWatchdog() {
        stopDuckStaleWatchdog();
        mainHandler.postDelayed(duckStaleWatchdog, DUCK_STALE_WATCHDOG_MS);
    }

    private void stopDuckStaleWatchdog() {
        mainHandler.removeCallbacks(duckStaleWatchdog);
    }

    /** CAN_DUCK 拆轨且不回 GAIN：超时 unduck，必要时走 resume。 */
    private void onDuckStaleWatchdog() {
        synchronized (focusLock) {
            if (!ducked) {
                return;
            }
            AudioManager am = getAudioManager();
            if (isInVoiceCallLocked(am) || isCompetingMediaPlaybackActive(am)) {
                startDuckStaleWatchdog();
                return;
            }
            boolean selfGone = scanSelfPlaybackGoneLocked(am);
            if (!selfGone) {
                // 导航/短信仍在 duck 且本进程轨还在：等系统 GAIN，禁止 2s 强行 unduck。
                return;
            }
            ducked = false;
            Log.i(TAG, "CAN_DUCK stale watchdog unduck selfGone=" + selfGone);
            Log.i(TAG, "[PA-904] CAN_DUCK stale unduck selfGone=" + selfGone);
            callback.onFocusUnduck();
            if (selfGone || reopenPlaybackDevice) {
                markReopenLocked(BoluoAudioStutterProbe.REOPEN_SELF_GONE);
                if (!pendingResume) {
                    pendingResume = true;
                }
                if (hasFocus) {
                    pendingResume = false;
                    callback.onFocusResume();
                } else {
                    startCompetingAudioPoll();
                }
            }
        }
    }

    private void ensureVolumeReceiverLocked() {
        if (volumeReceiverRegistered) {
            return;
        }
        Context ctx = resolveContext();
        if (ctx == null) {
            return;
        }
        try {
            IntentFilter filter = new IntentFilter("android.media.VOLUME_CHANGED_ACTION");
            if (Build.VERSION.SDK_INT >= 33) {
                ctx.registerReceiver(volumeReceiver, filter, Context.RECEIVER_EXPORTED);
            } else {
                ctx.registerReceiver(volumeReceiver, filter);
            }
            volumeReceiverRegistered = true;
            Log.i(TAG, "VOLUME_CHANGED_ACTION registered");
        } catch (Exception e) {
            Log.w(TAG, "register VOLUME_CHANGED: " + e.getMessage());
        }
    }

    private void unregisterVolumeReceiverLocked() {
        if (!volumeReceiverRegistered) {
            return;
        }
        Context ctx = resolveContext();
        if (ctx != null) {
            try {
                ctx.unregisterReceiver(volumeReceiver);
            } catch (Exception e) {
                Log.w(TAG, "unregister VOLUME_CHANGED: " + e.getMessage());
            }
        }
        volumeReceiverRegistered = false;
    }

    private void onMusicVolumeChanged(int prev, int nowVol) {
        synchronized (focusLock) {
            if (prev > 0 || nowVol <= 0) {
                return;
            }
            if (!callback.isEnginePlaying() && !pendingResume) {
                return;
            }
            Log.i(TAG, "STREAM_MUSIC 0→" + nowVol + " — recover");
            Log.i(TAG, "[PA-904] volume restored " + prev + "→" + nowVol);
            markReopenLocked(BoluoAudioStutterProbe.REOPEN_DEVICE_ROUTE);
            if (!pendingResume) {
                pendingResume = true;
            }
            if (hasFocus) {
                pendingResume = false;
                callback.onFocusResume();
            } else {
                startCompetingAudioPoll();
            }
        }
    }
}
