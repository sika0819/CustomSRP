package com.wanos.app.health;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.media.AudioAttributes;
import android.media.AudioFocusRequest;
import android.media.AudioManager;
import android.media.AudioPlaybackConfiguration;
import android.media.AudioRecordingConfiguration;
import android.os.Build;
import android.os.Process;
import android.os.Handler;
import android.os.HandlerThread;
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
 *   <li>LOSS_TRANSIENT_CAN_DUCK：已离开且允许后台播、又没有对方媒体/录音 → 保持音频图。
 *       已认出 QQ/视频/微信录音，或已离开且禁止后台播 → 焦点线程同步增益 0 再走护栏，禁止先降到四成。
 *       人还在前台、没有对方 → 先增益 0，短确认后才降到四成（导航/短信）。</li>
 *   <li>LOSS_TRANSIENT（微信语音）：暂停并保留播放资源，不放弃焦点请求。
 *       禁止再发 onSystemDialogCover，否则 Unity 消息积压，回前台 CompetingMuted 盖掉 Resume。</li>
 *   <li>GAIN：用户暂停不恢复。语音这种暂时丢焦在后台也恢复。
 *       音乐/视频（LOSS 或 PlaybackConfiguration）后台不恢复，等回前台或负一屏播放。</li>
 *   <li>LOSS（另一首音乐/视频）：暂停并记住待恢复，不轮询、不在后台重申焦点。</li>
 *   <li>微信录音：同样暂停。不 abandon 焦点请求，否则系统不会再把 GAIN 送回来，回前台会没声。
 *       录音结束且不是音乐视频抢占时，恢复一次。</li>
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
    /**
     * 前后台边沿：回前台会先把 appInBackground 清掉，紧接着的焦点抖动不再满足「已在后台」。
     * 边沿内无 competing、非通话则不 mute，避免切回来停一下。
     * （曾用「进后台 800ms」吞 TRANSIENT，会误伤微信听语音，已去掉。）
     */
    private static final long LIFECYCLE_EDGE_SUPPRESS_MS = 1500L;
    /**
     * UnityPlayer.onPause 后 AAudio 常瞬时从 mixer 消失；边沿内禁止立刻 self_gone→L0/L1，
     * 避免快切与回前台 requestStart 叠打。真死机交给 keepPlayingObserve（首拍 8s）。
     */
    private static final long UNITY_PAUSE_EDGE_SUPPRESS_MS = 1000L;
    /** 对齐 C# AndroidForegroundCompetingResumeDebounceMs：一起启动 / 切视频间隙防尾音。 */
    private static final long FOREGROUND_COMPETING_RESUME_DEBOUNCE_MS = 2000L;
    /**
     * QQ / B 站关掉或停播后，后台尽快续上。
     * 以前默认 30s，人把播放器关掉还要干等，体感是坏的。
     * 与微信语音同一短窗：丢掉焦点后再安静约 1.8s 就恢复。
     * 对方还在出声时由 {@link #isExternalMusicActiveLocked} 拦住，不会提前叠回去。
     */
    private static final long BACKGROUND_SILENCE_CONFIRM_MS = 1_800L;
    /** 微信 VOICE / 录音结束后台短静默窗（skill 约 1.5–2s）。 */
    private static final long VOICE_BACKGROUND_SILENCE_CONFIRM_MS = 1800L;
    /** 误恢复加倍上限约 5min。 */
    private static final long BACKGROUND_SILENCE_CONFIRM_MAX_MS = 300_000L;
    /** 后台自续后多久内再被打断视为误恢复。 */
    private static final long BACKGROUND_RESUME_MISJUDGE_WINDOW_MS = 30_000L;
    /** 对齐 C# AndroidSilenceConfirmPollMs。 */
    private static final long SILENCE_CONFIRM_POLL_MS = 1000L;
    /**
     * 提示音 / 音量键反馈走 TRANSIENT 后经常不发 GAIN。
     * 短于电话时长，长过典型通知；通话中 {@link #isInVoiceCallLocked} 会续期而不是抢焦。
     */
    private static final long TRANSIENT_GAIN_WATCHDOG_MS = 1800L;
    /** CAN_DUCK 后 OEM 拆轨且不回 GAIN：超时 unduck 并标记重开设备。 */
    private static final long DUCK_STALE_WATCHDOG_MS = 2000L;
    /**
     * 前台 CAN_DUCK 还分不清导航和 QQ：先把增益打到 0，这段时间后再决定。
     * 立刻写 0.4 再被 LOSS 护栏盖掉，就是切 QQ 的「卡一下」。
     */
    private static final long DUCK_CONFIRM_MS = 200L;
    /**
     * 这台荣耀上切进微信会挂出录音配置，但 voiceCall 一直是 false。
     * 12:22 日志：没说话的闪一下是 116–298ms；按住说话是 1.7s 起。
     * 超过这一窗仍在录，就按微信语音马上暂停。不要再等 2.5 秒。
     */
    private static final long VOICE_REC_CONFIRM_MS = 400L;
    /**
     * 荣耀上报的播放 uid 是 -1，QQ 音乐抢焦时 competing 一直是 false。
     * 12:32 日志：QQ 的 LOSS_TRANSIENT 被当成没人抢，音乐和负一屏都没暂停。
     * 切应用的抖动在这一窗内会把焦点还回来；窗后仍丢焦且没有录音，按音乐打断暂停。
     * 回前台保持暂停，滑动再把音乐和音效一起拉起。
     */
    private static final long MEDIA_TRANSIENT_HOLD_MS = 800L;
    /** 蓝牙/耳机断开后等路由落到扬声器再续（仅有对手时才 pause）。 */
    private static final long BECOMING_NOISY_RESUME_DELAY_MS = 700L;

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

        /** 短暂可 duck（导航/通知音）：降低音量约 40%，不要 mute。须在焦点线程同步写。 */
        void onFocusDuck();

        /**
         * 前台 CAN_DUCK 还分不清对手：焦点线程立刻把增益写成 0，不 Pause。
         * 确认是导航后再 {@link #onFocusDuck()}；确认要让出则走 {@link #onFocusPause()}。
         */
        void onAmbiguousDuckDip();

        /** 纯前后台、允许续播：撤销 dip，增益回到 1，不爬坡、不 Pause。 */
        void onKeepGraphRestoreGain();

        /** duck 结束，恢复音量。 */
        void onFocusUnduck();

        /** 当前引擎是否在播，用于判断丢焦时是否需要记 pendingResume。 */
        boolean isEnginePlaying();

        /**
         * 允许后台播且 App 已进后台时，忽略 {@code LOSS_TRANSIENT} 的 mute/pending，
         * 避免前后台快切把整引擎 mute/unmute 拉锯（音效卡顿）。
         */
        boolean shouldSuppressTransientPauseInBackground();

        /**
         * 允许后台续播且用户未暂停。与是否已经记成后台无关，
         * 供前后台边沿（回前台标志已清、焦点抖动还在）跳过 mute。
         */
        boolean allowsUninterruptedBackgroundPlayback();

        /** 申请焦点失败。 */
        void onFocusRequestFailed();

        /**
         * 系统更新一类弹窗：瞬时丢焦但没有外来媒体/语音。
         * 实现方不得 Pause，也不得立刻再 requestAudioFocus。
         */
        void onSystemDialogCover();

        /** App 是否在后台（CAN_DUCK 升级抢占用）。 */
        boolean isAppInBackground();

        /**
         * 宿主已标后台，或进程 importance 已不是前台。
         * 不用 Unity「仍声称在前台」短路，否则切走后的 CAN_DUCK 会被当成导航降到四成。
         */
        boolean hasLeftForeground();

        /** 引擎播放态仍是 Started：后台续播时禁止为此 ResetPlaybackDevice。 */
        boolean isPlaybackGraphStarted();

        /** 进岛占位卡还没真正出声时，对方媒体已在播：收起播放态。 */
        void onStartupCardYield();

        /**
         * 后台续播且自身 MEDIA/GAME 轨已从 mixer 消失：立刻无爬坡重开 AAudio。
         * 创维会把 USAGE_MEDIA 改写成 GAME，约 9–11s 拆轨；不能等 aliveWatch。
         */
        void onKeepPlayingTrackGone();
    }

    private final Callback callback;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    /**
     * 焦点回调不挂主线程 Handler。主线程上 {@code removeCallbacksAndMessages(null)}
     * 会把已经派发的 LOSS 吃掉，切到 QQ / 微信时增益还是满的，听感就是卡一下。
     */
    private final Handler focusCallbackHandler;
    private final Object focusLock = new Object();

    {
        HandlerThread thread = new HandlerThread("BoluoAudioFocus");
        thread.start();
        focusCallbackHandler = new Handler(thread.getLooper());
    }

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
    /** 最近一次前后台标志翻转，用于回前台边沿的焦点抖动。 */
    private long lastLifecycleEdgeElapsedMs;
    /** Unity pause 边沿：抑制瞬时 self_gone 立刻 recover。 */
    private long unityPauseEdgeElapsedMs;
    private boolean lastObservedBackground;
    /** 本轮曾见其它 UID 的 MEDIA/GAME；停播后须静默确认再 unmute（防 B 站暂停尾音）。 */
    private boolean competingEpisodeSawMedia;
    private long lastCompetingSeenElapsedMs;
    /** 任意让出/抢占时刻，供 VOICE 短静默窗计时（可不记 MEDIA episode）。 */
    private long lastYieldElapsedMs;
    /**
     * 当前后台静默确认窗（默认 1.8s；误恢复加倍，封顶 5min）。
     * 回前台 reclaim 重置。
     */
    private long silenceConfirmWindowMs = BACKGROUND_SILENCE_CONFIRM_MS;
    /** 最近一次后台自动续播时刻；用于误恢复加倍。 */
    private long lastBackgroundAutoResumeElapsedMs;
    private boolean silenceConfirmPosted;
    private final Runnable silenceConfirmPoll = this::pollSilenceConfirm;
    private final Runnable transientGainWatchdog = this::onTransientGainWatchdog;
    private final Runnable duckStaleWatchdog = this::onDuckStaleWatchdog;
    private final Runnable duckConfirm = this::onDuckConfirm;
    private final Runnable voiceRecConfirm = this::onVoiceRecConfirm;
    private long voiceRecConfirmGeneration;
    private long voiceRecConfirmScheduledGen = -1L;
    private final Runnable mediaTransientHold = this::onMediaTransientHold;
    private long mediaTransientHoldGeneration;
    private long mediaTransientHoldScheduledGen = -1L;
    /** 新的 CAN_DUCK / 抢占使上一次「先 dip 再确认」作废。 */
    private long duckConfirmGeneration;
    /** 当前这一次确认的代号；取消时置为 -1。 */
    private long duckConfirmScheduledGen = -1L;
    private final Runnable becomingNoisyResume = this::onBecomingNoisyResume;
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
    /**
     * 微信按住说话常先占麦（Recording）再出 VOICE，焦点可能晚到或根本不发。
     * 证据：5 次卡顿会话里 voiceLikely 全程 false、0 CAN_DUCK。
     */
    private boolean recordingCallbackRegistered;
    private final AudioManager.AudioRecordingCallback recordingCallback =
            Build.VERSION.SDK_INT >= Build.VERSION_CODES.N
                    ? new AudioManager.AudioRecordingCallback() {
                        @Override
                        public void onRecordingConfigChanged(
                                List<AudioRecordingConfiguration> configs) {
                            onForeignRecordingConfigChanged();
                        }
                    }
                    : null;

    WanosAudioFocusManager(Callback callback) {
        this.callback = callback;
    }

    boolean hasAudioFocus() {
        return hasFocus;
    }

    /** 其它 App 的媒体正在出声（抖音 / QQ 音乐 / B 站）。 */
    boolean hasForeignMedia() {
        synchronized (focusLock) {
            return isCompetingMediaPlaybackActive(getAudioManager());
        }
    }

    boolean isPendingResume() {
        return pendingResume;
    }

    /**
     * 微信按住说话还没结束：通话模式，或外来 VOICE/SPEECH，或 mute 后 isMusicActive 仍真。
     * QQ / B 站等 MEDIA 不算。
     */
    boolean isWeChatVoiceSessionActive() {
        synchronized (focusLock) {
            AudioManager am = getAudioManager();
            // 只认通话模式和外来录音。isMusicActive 在自己还占着流时会误报，
            // 语音结束后会把恢复一直挡住，回前台没声。
            return isInVoiceCallLocked(am) || isForeignRecordingActiveLocked(am);
        }
    }

    /** 本轮见过 QQ / B 站等 MEDIA。微信 VOICE 不记。 */
    boolean sawCompetingMediaEpisode() {
        synchronized (focusLock) {
            return competingEpisodeSawMedia;
        }
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
                    .setOnAudioFocusChangeListener(focusListener, focusCallbackHandler)
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
            Log.i(TAG, "[BoluoAudioFg] exclusive activate failed result=" + result
                    + " force=" + force);
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
                resetSilenceConfirmWindowLocked();
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
                    Log.i(TAG, "[BoluoAudioFg] reclaim success");
                    long t0 = SystemClock.elapsedRealtime();
                    callback.onFocusResume();
                    long ms = SystemClock.elapsedRealtime() - t0;
                    reportFocusBls("boluo_audio_focus_gain", "info", "FORCED_FOREGROUND_RECLAIM",
                            false, ms, "forced-reclaim-clear-stale-pending");
                } else {
                    Log.i(TAG, "forced reclaim FAILED — keep mute/pending (system arbitration)");
                    Log.i(TAG, "[BoluoAudioFg] exclusive activate failed keep mute no card");
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
                mainHandler.removeCallbacks(becomingNoisyResume);
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
        Log.i(TAG, "onAudioFocusChange enter change=" + focusChange
                + " thread=" + Thread.currentThread().getName());
        synchronized (focusLock) {
            String changeName = focusChangeName(focusChange);
            Log.i(TAG, "onAudioFocusChange: " + changeName);
            // 验证 CWPA-44：前后台切换时焦点抖动导致卡顿
            Log.i(TAG, "[CWPA-44] onAudioFocusChange " + changeName
                    + " hasFocus=" + hasFocus + " pendingResume=" + pendingResume
                    + " ducked=" + ducked
                    + " suppressTransientBg=" + callback.shouldSuppressTransientPauseInBackground());
            dumpStutterSnapshotLocked("focus:" + changeName);
            switch (focusChange) {
                case AudioManager.AUDIOFOCUS_GAIN: {
                    boolean dipPending = duckConfirmScheduledGen != -1L
                            && duckConfirmScheduledGen == duckConfirmGeneration;
                    boolean voiceRecPending = isVoiceRecConfirmPendingLocked();
                    boolean mediaHoldPending = isMediaTransientHoldPendingLocked();
                    cancelDuckConfirmLocked();
                    cancelVoiceRecConfirmLocked();
                    cancelMediaTransientHoldLocked();
                    if (voiceRecPending && !pendingResume) {
                        Log.i(TAG, "[BoluoAudioFg] GAIN ends voice confirm — keep playing, no ramp");
                    }
                    if (mediaHoldPending && !pendingResume) {
                        Log.i(TAG, "[BoluoAudioFg] GAIN ends media hold — keep playing, no pause");
                    }
                    hasFocus = true;
                    stopCompetingAudioPoll();
                    stopTransientGainWatchdog();
                    stopDuckStaleWatchdog();
                    if (dipPending && !ducked && !pendingResume) {
                        Log.i(TAG, "[BoluoAudioFg] GAIN restores gain after ambiguous dip");
                        callback.onKeepGraphRestoreGain();
                    }
                    if (ducked) {
                        ducked = false;
                        Log.i(TAG, "GAIN -> unduck");
                        callback.onFocusUnduck();
                        reportFocusBls("boluo_audio_focus_gain", "info", changeName,
                                false, 0, "unduck");
                    }
                    if (pendingResume) {
                        boolean inBackground = callback.isAppInBackground();
                        // 音乐/视频：系统把焦点还回来也不在后台续。回前台 reclaim 或负一屏播放才出声。
                        if (inBackground && competingEpisodeSawMedia) {
                            stopCompetingAudioPoll();
                            stopSilenceConfirmLocked();
                            Log.i(TAG, "GAIN hold — media/video waits for foreground or now-playing");
                            Log.i(TAG, "[PA-904] GAIN BG media hold");
                            break;
                        }
                        boolean competingNow = isCompetingMediaPlaybackActive(getAudioManager())
                                || isExternalMusicActiveLocked(getAudioManager());
                        boolean fgExclusive = !inBackground && competingNow;
                        if (fgExclusive) {
                            pendingResume = false;
                            clearCompetingEpisodeLocked();
                            Log.i(TAG, "GAIN -> FG exclusive resume (competitor still active)");
                            Log.i(TAG, "[PA-904] GAIN FG exclusive skip silence-confirm");
                            callback.onFocusResume();
                            break;
                        }
                        if (inBackground && competingNow && competingEpisodeSawMedia) {
                            Log.i(TAG, "GAIN keep waiting — competing media, do not steal");
                            Log.i(TAG, "[PA-904] GAIN BG skip reclaim competing media");
                            break;
                        }
                        // 微信语音结束：系统送回 GAIN，马上续。用户暂停由播放器拦住。
                        pendingResume = false;
                        clearCompetingEpisodeLocked();
                        Log.i(TAG, "GAIN -> auto resume (transient voice)");
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
                    // 已离开且允许续播、没有对手：保持图。
                    // 已认出 QQ/视频/录音，或已离开且禁止后台播：同步增益 0 + 护栏，禁止 0.4。
                    // 人还在前台、没有对手：先 dip 到 0，确认后再四成，避免 QQ 先降再静。
                    noteBackgroundStateLocked();
                    AudioManager amDuck = getAudioManager();
                    boolean competing = isCompetingMediaPlaybackActive(amDuck);
                    boolean voiceOrRec = isInVoiceCallLocked(amDuck)
                            || isForeignRecordingActiveLocked(amDuck);
                    boolean leftApp = callback.hasLeftForeground();
                    boolean keepPlaying = callback.allowsUninterruptedBackgroundPlayback();
                    long t0 = SystemClock.elapsedRealtime();
                    if (leftApp && keepPlaying && !competing && !voiceOrRec) {
                        cancelDuckConfirmLocked();
                        Log.i(TAG, "[BoluoAudioFg] CAN_DUCK keep_graph left=true no opponent");
                        BoluoAudioStutterProbe.noteSwitchClass(
                                "keep_graph", "can_duck_left_no_opponent");
                        reportFocusBls("boluo_audio_focus_loss", "info", changeName,
                                false, SystemClock.elapsedRealtime() - t0,
                                "can_duck_keep_graph");
                        break;
                    }
                    boolean yieldNow = competing || voiceOrRec || (leftApp && !keepPlaying);
                    if (yieldNow) {
                        Log.i(TAG, "[BoluoAudioFg] CAN_DUCK preempt left=" + leftApp
                                + " competing=" + competing
                                + " voiceOrRec=" + voiceOrRec);
                        Log.i(TAG, "[CWPA-66] CAN_DUCK preempt bg="
                                + callback.isAppInBackground() + " competing=" + competing);
                        // 微信录音/VOICE：让出会话；MEDIA 只 mute 不 abandon。
                        applyCompetingPreemptLocked(changeName, competing, competing,
                                voiceOrRec && !competing);
                        if ((leftApp || voiceOrRec) && !competing) {
                            startTransientGainWatchdog();
                        }
                        BoluoAudioStutterProbe.noteSwitchClass(
                                "guard_mute", "can_duck_preempt");
                        reportFocusBls("boluo_audio_focus_loss", "warn", changeName,
                                pendingResume, SystemClock.elapsedRealtime() - t0,
                                "can_duck_preempt");
                    } else if (callback.isEnginePlaying()) {
                        Log.i(TAG, "[BoluoAudioFg] CAN_DUCK ambiguous dip (FG no opponent yet)");
                        scheduleAmbiguousDuckConfirmLocked();
                        reportFocusBls("boluo_audio_focus_loss", "info", changeName,
                                false, SystemClock.elapsedRealtime() - t0,
                                "can_duck_confirm");
                    }
                    break;
                }
                case AudioManager.AUDIOFOCUS_LOSS_TRANSIENT: {
                    // 真语音 / 已看见对方媒体：马上暂停，不 abandon（放弃后 GAIN 不会回来）。
                    // 允许后台播、又看不见媒体/通话/录音：先等 800ms。
                    // 焦点先回来是切应用；窗后仍丢焦就是 QQ 音乐这类抢占，暂停并等滑动恢复。
                    // 按住说话由录音回调确认，不在这里猜。
                    long t0 = SystemClock.elapsedRealtime();
                    noteBackgroundStateLocked();
                    AudioManager amTransient = getAudioManager();
                    boolean competing = isCompetingMediaPlaybackActive(amTransient);
                    boolean voice = isInVoiceCallLocked(amTransient);
                    boolean foreignRec = isForeignRecordingActiveLocked(amTransient);
                    boolean keepPlaying = callback.allowsUninterruptedBackgroundPlayback();
                    long bgAge = lastObservedBackground
                            ? (t0 - backgroundEnteredElapsedMs) : 0L;
                    Log.i(TAG, "[BoluoAudioFg] LOSS_TRANSIENT competing="
                            + competing + " voice=" + voice
                            + " foreignRec=" + foreignRec
                            + " keepPlaying=" + keepPlaying + " bgAge=" + bgAge);
                    if (isCompetingMediaAppPlaybackActive(amTransient)) {
                        noteCompetingSeenLocked();
                    }
                    if (keepPlaying && !competing && !voice && !foreignRec) {
                        scheduleMediaTransientHoldLocked();
                        BoluoAudioStutterProbe.noteSwitchClass(
                                "keep_graph", "transient_media_hold");
                        reportFocusBls("boluo_audio_focus_loss", "info", changeName,
                                false, SystemClock.elapsedRealtime() - t0,
                                "transient_media_hold");
                        break;
                    }
                    cancelVoiceRecConfirmLocked();
                    applyTransientPauseLocked(false);
                    if (!competingEpisodeSawMedia) {
                        startTransientGainWatchdog();
                    }
                    long ms = SystemClock.elapsedRealtime() - t0;
                    Log.i(TAG, "LOSS_TRANSIENT handleMs=" + ms);
                    reportFocusBls("boluo_audio_focus_loss", "warn", changeName,
                            pendingResume, ms, "transient_pause");
                    break;
                }
                case AudioManager.AUDIOFOCUS_LOSS: {
                    // 另一首音乐/视频：暂停并记住。不轮询、不在后台重申焦点。
                    long t0 = SystemClock.elapsedRealtime();
                    Log.i(TAG, "LOSS -> pause, hold until foreground or now-playing");
                    cancelVoiceRecConfirmLocked();
                    applyCompetingPreemptLocked(changeName, true, false);
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
        if (bg != lastObservedBackground) {
            long now = SystemClock.elapsedRealtime();
            lastLifecycleEdgeElapsedMs = now;
            if (bg) {
                backgroundEnteredElapsedMs = now;
            }
        }
        lastObservedBackground = bg;
    }

    /** 前后台标志刚翻转。调用前须已 {@link #noteBackgroundStateLocked()}。电话另判。 */
    private boolean isLifecycleEdgeLocked() {
        if (lastLifecycleEdgeElapsedMs == 0L) {
            return false;
        }
        return SystemClock.elapsedRealtime() - lastLifecycleEdgeElapsedMs < LIFECYCLE_EDGE_SUPPRESS_MS;
    }

    /**
     * UnityPlayer.onPause / notifyAppLifecycle(background) 后调用：武装 self_gone 抑制窗。
     */
    void noteUnityPauseEdge() {
        synchronized (focusLock) {
            unityPauseEdgeElapsedMs = SystemClock.elapsedRealtime();
            boolean started = callback.isPlaybackGraphStarted();
            Log.i(TAG, "[CWPA-44] noteUnityPauseEdge suppressMs=" + UNITY_PAUSE_EDGE_SUPPRESS_MS
                    + " graphStarted=" + started);
            BoluoAudioStutterProbe.noteSwitchClass(
                    "unity_pause_edge", started ? "mixer_started" : "mixer_not_started");
            dumpStutterSnapshotLocked("unity_pause_edge");
        }
    }

    private boolean isUnityPauseEdgeLocked() {
        if (unityPauseEdgeElapsedMs == 0L) {
            return false;
        }
        return SystemClock.elapsedRealtime() - unityPauseEdgeElapsedMs < UNITY_PAUSE_EDGE_SUPPRESS_MS;
    }

    /**
     * 长期竞争抢占：mute、记 pending。
     * @param startPoll true=对方停止后无 GAIN（LOSS / 已见到 PlaybackConfiguration）；
     *                  false=等系统 GAIN 或回前台 reclaim（CAN_DUCK 未见 competing 时禁止 poll，
     *                  否则 1s 内 requestFocus 仍 GRANTED 会立刻叠播）。
     * @param yieldSession true=微信录音/VOICE：mute 后让出 AudioFocus 会话（保留 pending）。
     */
    private void applyCompetingPreemptLocked(String reason, boolean competingLongUi,
                                            boolean startPoll) {
        applyCompetingPreemptLocked(reason, competingLongUi, startPoll, false);
    }

    private void applyCompetingPreemptLocked(String reason, boolean competingLongUi,
                                            boolean startPoll, boolean yieldSession) {
        // 后台续播只忽略「没有外来音源」的自扰（PlaybackConfiguration 误报等）。
        // 系统焦点回调（LOSS / TRANSIENT / CAN_DUCK）已确认丢焦——微信语音常在 foreign
        // 快照之前到达，禁止再 skip mute。
        cancelVoiceRecConfirmLocked();
        cancelMediaTransientHoldLocked();
        boolean systemFocusLoss = "AUDIOFOCUS_LOSS".equals(reason)
                || "AUDIOFOCUS_LOSS_TRANSIENT".equals(reason)
                || "AUDIOFOCUS_LOSS_TRANSIENT_CAN_DUCK".equals(reason);
        // 微信录音回调已经确认有外来录音。不能再因为「看不见 MEDIA uid」跳过静音，
        // 否则切到微信时系统把满音量掐断，听感就是卡一下。
        boolean voiceYield = "RECORDING_OR_VOICE".equals(reason) || yieldSession;
        boolean foreign = isCompetingMediaPlaybackActive(getAudioManager());
        if (!systemFocusLoss && !voiceYield && !foreign
                && callback.shouldSuppressTransientPauseInBackground()) {
            Log.i(TAG, "[CWPA-audio] " + reason + " skip mute (keep playing bg, no foreign)");
            return;
        }
        cancelDuckConfirmLocked();
        penalizeSilenceConfirmIfMisjudgedLocked();
        hasFocus = false;
        markReopenLocked(BoluoAudioStutterProbe.REOPEN_PREEMPT);
        markYieldElapsedLocked();
        if (callback.isEnginePlaying()) {
            pendingResume = true;
        }
        // LOSS / 播放配置里的音乐视频记 episode：后台不自动续。
        // 微信录音 yield 不记。CAN_DUCK 提示音（startPoll=false 且不是 LOSS）也不记。
        boolean markMedia = "AUDIOFOCUS_LOSS".equals(reason)
                || "AUDIOFOCUS_LOSS_TRANSIENT".equals(reason)
                || "PLAYBACK_CONFIG_COMPETING".equals(reason)
                || (startPoll && !yieldSession);
        if (markMedia) {
            noteCompetingSeenLocked();
        }
        stopSilenceConfirmLocked();
        stopDuckStaleWatchdog();
        stopCompetingAudioPoll();
        Log.i(TAG, reason + " -> preempt pendingResume=" + pendingResume
                + " startPoll=" + startPoll + " yieldSession=" + yieldSession
                + " markMedia=" + markMedia);
        dumpStutterSnapshotLocked("preempt:" + reason);
        // 必须先护栏 mute 再清 duck：旧顺序 Unduck→满增益再 mute = 切微信/B 站电流音。
        callback.onFocusPause();
        if (ducked) {
            ducked = false;
        }
        callback.onFocusPauseUi(competingLongUi);
        if (yieldSession) {
            yieldSessionKeepPendingLocked();
        }
        reportFocusBls("boluo_audio_focus_loss", "warn", reason,
                pendingResume, 0, markMedia ? "media_hold" : "competing_preempt_wait_gain");
    }

    /**
     * 微信语音结束且没有音乐视频抢占：申请一次焦点并恢复。
     * 失败就保持暂停，等系统 GAIN、回前台或负一屏播放。不再开轮询。
     */
    private void resumeTransientVoiceOnceLocked(String reason) {
        if (!pendingResume || competingEpisodeSawMedia) {
            return;
        }
        if (isInVoiceCallLocked(getAudioManager())
                || isForeignRecordingActiveLocked(getAudioManager())) {
            Log.i(TAG, reason + " deferred (voice still active)");
            return;
        }
        boolean granted = hasFocus || performRequestFocus(false, false);
        if (!granted) {
            Log.i(TAG, reason + " focus denied — hold until GAIN or foreground");
            Log.i(TAG, "[PA-904] " + reason + " hold");
            return;
        }
        pendingResume = false;
        clearCompetingEpisodeLocked();
        Log.i(TAG, reason + " -> resume transient voice");
        Log.i(TAG, "[PA-904] " + reason + " resume");
        callback.onFocusResume();
    }

    /** 瞬时丢焦暂停：不放弃焦点请求，等系统 GAIN。 */
    private void applyTransientPauseLocked() {
        applyTransientPauseLocked(false);
    }

    private void applyTransientPauseLocked(boolean yieldSession) {
        cancelVoiceRecConfirmLocked();
        cancelMediaTransientHoldLocked();
        // 能进到这里的是真 TRANSIENT（来电 / 微信语音），后台续播也要停。
        // 必须清 hasFocus：旧逻辑保留 true → 1.8s TRANSIENT watchdog 走
        // 「hasFocus → onFocusResume」把 Pause_ALL 的音效又 Resume_ALL 拉起来（音乐常仍 Pause）。
        cancelDuckConfirmLocked();
        penalizeSilenceConfirmIfMisjudgedLocked();
        hasFocus = false;
        markReopenLocked(BoluoAudioStutterProbe.REOPEN_TRANSIENT);
        markYieldElapsedLocked();
        if (callback.isEnginePlaying()) {
            pendingResume = true;
        }
        // 先护栏 mute，禁止 Unduck 把增益拉回 1.0 再硬 mute。
        callback.onFocusPause();
        if (ducked) {
            ducked = false;
        }
        callback.onFocusPauseUi(false);
        if (yieldSession) {
            yieldSessionKeepPendingLocked();
        }
    }

    /**
     * 让出 AudioFocus 会话但保留 pendingResume（微信录音需要对方拿到麦）。
     * 与 {@link #abandonFocus()} 不同：不停播意图、不清静默 episode。
     */
    private void yieldSessionKeepPendingLocked() {
        AudioManager am = getAudioManager();
        if (am == null) {
            return;
        }
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && focusRequest != null) {
                am.abandonAudioFocusRequest(focusRequest);
            } else {
                am.abandonAudioFocus(focusListener);
            }
            hasFocus = false;
            Log.i(TAG, "yieldSessionKeepPending: abandoned focus request, pending kept="
                    + pendingResume);
        } catch (Exception e) {
            Log.w(TAG, "yieldSessionKeepPending failed: " + e.getMessage());
        }
    }

    /**
     * 蓝牙/耳机断开（ACTION_AUDIO_BECOMING_NOISY）。
     * 无录音、无外来 MEDIA：喇叭继续，不 pause。
     * 有 VOICE/微信或外来 MEDIA：pause；MEDIA 仍在则等静默确认/回前台，不立刻抢。
     */
    void onBecomingNoisy() {
        synchronized (focusLock) {
            AudioManager am = getAudioManager();
            boolean voice = isInVoiceCallLocked(am)
                    || isForeignRecordingActiveLocked(am)
                    || isCompetingVoicePlaybackActive(am);
            boolean media = isCompetingMediaAppPlaybackActive(am);
            if (!voice && !media) {
                Log.i(TAG, "BECOMING_NOISY keep playing on speaker (no voice/media competitor)");
                reportFocusBls("boluo_audio_focus_loss", "info", "BECOMING_NOISY",
                        pendingResume, 0, "route_disconnect_keep_playing");
                return;
            }
            if (voice && isForeignRecordingActiveLocked(am)) {
                Log.i(TAG, "BECOMING_NOISY during recording — pause, no route-restore resume");
                applyTransientPauseLocked(true);
                reportFocusBls("boluo_audio_focus_loss", "info", "BECOMING_NOISY",
                        pendingResume, 0, "route_disconnect_recording");
                return;
            }
            Log.i(TAG, "BECOMING_NOISY -> pause + schedule BG resume voice="
                    + voice + " media=" + media);
            markReopenLocked(BoluoAudioStutterProbe.REOPEN_DEVICE_ROUTE);
            markYieldElapsedLocked();
            if (callback.isEnginePlaying()) {
                pendingResume = true;
            }
            if (media) {
                noteCompetingSeenLocked();
            }
            callback.onFocusPause();
            if (ducked) {
                ducked = false;
            }
            callback.onFocusPauseUi(false);
            mainHandler.removeCallbacks(becomingNoisyResume);
            mainHandler.postDelayed(becomingNoisyResume, BECOMING_NOISY_RESUME_DELAY_MS);
            reportFocusBls("boluo_audio_focus_loss", "info", "BECOMING_NOISY",
                    pendingResume, 0, "route_disconnect_pause");
        }
    }

    private void onBecomingNoisyResume() {
        synchronized (focusLock) {
            if (!pendingResume) {
                return;
            }
            if (isInVoiceCallLocked(getAudioManager())
                    || isForeignRecordingActiveLocked(getAudioManager())) {
                Log.i(TAG, "BECOMING_NOISY resume deferred (voice/recording)");
                mainHandler.postDelayed(becomingNoisyResume, BECOMING_NOISY_RESUME_DELAY_MS);
                return;
            }
            // QQ/B 站仍在播：禁止立刻抢，走静默确认 / 竞争 poll。
            if (isCompetingMediaAppPlaybackActive(getAudioManager())) {
                Log.i(TAG, "BECOMING_NOISY resume skip — competing media, silence confirm");
                noteCompetingSeenLocked();
                startSilenceConfirmLocked();
                return;
            }
            if (shouldDeferAutoResumeLocked(false)) {
                startSilenceConfirmLocked();
                return;
            }
            Log.i(TAG, "BECOMING_NOISY resume after route settle");
            boolean granted = hasFocus || performRequestFocus(false, false);
            if (granted) {
                pendingResume = false;
                noteBackgroundAutoResumeLocked();
                clearCompetingEpisodeLocked();
                callback.onFocusResume();
            } else {
                startCompetingAudioPoll();
            }
        }
    }

    /** 尚未持焦时也注册 PlaybackCallback，启动期切到其它播放器能被看到。 */
    void ensurePlaybackMonitor() {
        synchronized (focusLock) {
            ensurePlaybackCallbackLocked(getAudioManager());
        }
    }

    private void ensurePlaybackCallbackLocked(AudioManager am) {
        ensureVolumeReceiverLocked();
        if (am == null) {
            return;
        }
        if (!playbackCallbackRegistered
                && Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            try {
                am.registerAudioPlaybackCallback(playbackCallback, mainHandler);
                playbackCallbackRegistered = true;
                Log.i(TAG, "AudioPlaybackCallback registered");
            } catch (Exception e) {
                Log.w(TAG, "registerAudioPlaybackCallback: " + e.getMessage());
            }
        }
        if (!recordingCallbackRegistered
                && recordingCallback != null
                && Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            try {
                am.registerAudioRecordingCallback(recordingCallback, mainHandler);
                recordingCallbackRegistered = true;
                Log.i(TAG, "[BoluoAudioFg] AudioRecordingCallback registered (WeChat mic)");
            } catch (Exception e) {
                Log.w(TAG, "registerAudioRecordingCallback: " + e.getMessage());
            }
        }
    }

    private void unregisterPlaybackCallbackLocked(AudioManager am) {
        if (playbackCallbackRegistered && am != null
                && Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            try {
                am.unregisterAudioPlaybackCallback(playbackCallback);
            } catch (Exception e) {
                Log.w(TAG, "unregisterAudioPlaybackCallback: " + e.getMessage());
            }
            playbackCallbackRegistered = false;
        }
        if (recordingCallbackRegistered && am != null && recordingCallback != null
                && Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            try {
                am.unregisterAudioRecordingCallback(recordingCallback);
            } catch (Exception e) {
                Log.w(TAG, "unregisterAudioRecordingCallback: " + e.getMessage());
            }
            recordingCallbackRegistered = false;
        }
    }

    /** 微信按住说话：外来录音会话出现即抢占（不等 VOICE 轨进 mixer）。 */
    private void onForeignRecordingConfigChanged() {
        synchronized (focusLock) {
            AudioManager am = getAudioManager();
            boolean foreignRec = isForeignRecordingActiveLocked(am);
            boolean voiceCall = isInVoiceCallLocked(am);
            Log.i(TAG, "[BoluoAudioFg] recordingCb foreignRec=" + foreignRec
                    + " voiceCall=" + voiceCall
                    + " bg=" + callback.isAppInBackground()
                    + " playing=" + callback.isEnginePlaying());
            dumpStutterSnapshotLocked("recordingCb foreignRec=" + foreignRec);
            if (!foreignRec && !voiceCall) {
                boolean confirmPending = isVoiceRecConfirmPendingLocked();
                cancelVoiceRecConfirmLocked();
                // 还没暂停的短录音：禁止当成「语音结束」去申请焦点再爬坡。
                if (confirmPending) {
                    Log.i(TAG, "[BoluoAudioFg] recording blip cleared before voice confirm");
                    return;
                }
                // 语音结束。音乐视频抢占不在这里续；暂时丢焦等这一下把声音还回来。
                if (pendingResume && !competingEpisodeSawMedia) {
                    resumeTransientVoiceOnceLocked("recording_ended");
                }
                return;
            }
            if (voiceCall || !callback.allowsUninterruptedBackgroundPlayback()) {
                cancelVoiceRecConfirmLocked();
            } else {
                scheduleVoiceRecConfirmLocked();
                Log.i(TAG, "[BoluoAudioFg] foreignRec confirm in " + VOICE_REC_CONFIRM_MS + "ms");
                return;
            }
            if (hasFocus && !callback.isAppInBackground() && !foreignRec) {
                return;
            }
            if (callback.isEnginePlaying() || pendingResume) {
                Log.i(TAG, "[BoluoAudioFg] recording/voice pause (keep focus request)");
                applyTransientPauseLocked(false);
                startTransientGainWatchdog();
            }
        }
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
                dumpStutterSnapshotLocked("playbackCb:competing");
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
                    applyCompetingPreemptLocked("PLAYBACK_CONFIG_COMPETING", true, false);
                } else {
                    callback.onStartupCardYield();
                }
            } else {
                boolean selfGone = scanSelfPlaybackGoneLocked(am);
                Log.i(TAG, "self_gone_scan_result=" + selfGone
                        + " pendingResume=" + pendingResume
                        + " ducked=" + ducked
                        + " consecutive_miss_count=" + consecutiveSelfGoneMiss);
                if (selfGone) {
                    dumpStutterSnapshotLocked("playbackCb:self_gone miss="
                            + consecutiveSelfGoneMiss);
                }
                BoluoAudioStutterProbe.cacheMixer(!selfGone);
                // SKS30：USAGE_GAME 轨在 mixer 常扫不到 → self_gone 假阳性。
                // 线上 3eec80b6：有 pending/duck 才 mark reopen。
                // 现网回归：后台续播干净 HOME 也会 NoAudioTrack，仅 defer 会永久无声。
                // 干净后台 self_gone：交给 keepPlaying L0/L1（L1 可 reset+replay，对齐线上续播）。
                boolean keepPlayingBg = callback.isAppInBackground()
                        && callback.shouldSuppressTransientPauseInBackground();
                if (selfGone && keepPlayingBg && !pendingResume && !ducked) {
                    // USAGE_GAME / Unity pause 后 mixer 常假阳性 self_gone。
                    // 仍持焦、或引擎仍 Started：禁止 L1 Reset——那就是切搜索栏「抖一下」。
                    // 真死机交给 keepPlayingObserve（muted / not Started / anchor stall）。
                    if (hasFocus || callback.isPlaybackGraphStarted()) {
                        Log.i(TAG, "[BoluoAudioFg] keepPlayingBg self_gone skip L1"
                                + " (graph alive hasFocus=" + hasFocus + ")");
                    } else if (isUnityPauseEdgeLocked()) {
                        Log.i(TAG, "[CWPA-44] keepPlayingBg self_gone suppressed (unity pause edge)");
                    } else {
                        Log.i(TAG, "[CWPA-audio] keepPlayingBg self_gone → MEDIA/L1 recover (no defer silence)");
                        callback.onKeepPlayingTrackGone();
                    }
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
                if (pendingResume && competingEpisodeSawMedia && callback.isAppInBackground()) {
                    stopSilenceConfirmLocked();
                    stopCompetingAudioPoll();
                    Log.i(TAG, "[PA-904] playbackCallback media hold (no bg reclaim)");
                } else if (pendingResume && !competingEpisodeSawMedia) {
                    if (shouldDeferAutoResumeLocked(false)) {
                        Log.i(TAG,
                                "[CWPA-66] playbackCallback defer silence-confirm bg="
                                        + callback.isAppInBackground());
                        startSilenceConfirmLocked();
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
        long now = SystemClock.elapsedRealtime();
        lastCompetingSeenElapsedMs = now;
        lastYieldElapsedMs = now;
    }

    private void markYieldElapsedLocked() {
        lastYieldElapsedMs = SystemClock.elapsedRealtime();
    }

    private void clearCompetingEpisodeLocked() {
        competingEpisodeSawMedia = false;
        lastCompetingSeenElapsedMs = 0L;
        lastYieldElapsedMs = 0L;
        stopSilenceConfirmLocked();
    }

    private void resetSilenceConfirmWindowLocked() {
        silenceConfirmWindowMs = BACKGROUND_SILENCE_CONFIRM_MS;
        lastBackgroundAutoResumeElapsedMs = 0L;
    }

    private void noteBackgroundAutoResumeLocked() {
        if (callback.isAppInBackground()) {
            lastBackgroundAutoResumeElapsedMs = SystemClock.elapsedRealtime();
        }
    }

    /**
     * 后台刚自续后又被打断 → 上次「对方已退出」是误判。确认窗 ×2，封顶约 5min。
     */
    private void penalizeSilenceConfirmIfMisjudgedLocked() {
        if (lastBackgroundAutoResumeElapsedMs <= 0L) {
            return;
        }
        long elapsed = SystemClock.elapsedRealtime() - lastBackgroundAutoResumeElapsedMs;
        lastBackgroundAutoResumeElapsedMs = 0L;
        if (elapsed < 0L || elapsed >= BACKGROUND_RESUME_MISJUDGE_WINDOW_MS) {
            return;
        }
        long base = Math.max(silenceConfirmWindowMs, BACKGROUND_SILENCE_CONFIRM_MS);
        silenceConfirmWindowMs = Math.min(base * 2L, BACKGROUND_SILENCE_CONFIRM_MAX_MS);
        Log.i(TAG, "Background resume misjudged (re-interrupted after " + elapsed
                + "ms) — confirm window now " + silenceConfirmWindowMs + "ms");
    }

    /** 后台 QQ/微信停播后约 1.8s；前台见过对手再安静约 2s。对方还在出声不算静默。 */
    private long requiredSilenceConfirmMsLocked() {
        if (callback.isAppInBackground()) {
            return competingEpisodeSawMedia
                    ? silenceConfirmWindowMs
                    : VOICE_BACKGROUND_SILENCE_CONFIRM_MS;
        }
        return competingEpisodeSawMedia
                ? FOREGROUND_COMPETING_RESUME_DEBOUNCE_MS
                : 0L;
    }

    private long silenceSinceElapsedLocked() {
        long anchor = competingEpisodeSawMedia && lastCompetingSeenElapsedMs > 0L
                ? lastCompetingSeenElapsedMs
                : lastYieldElapsedMs;
        if (anchor <= 0L) {
            return Long.MAX_VALUE;
        }
        return SystemClock.elapsedRealtime() - anchor;
    }

    /**
     * 回前台 reclaim 不推迟；MEDIA/VOICE 停后须满静默窗才 unmute。
     */
    private boolean shouldDeferAutoResumeLocked(boolean isForegroundReclaim) {
        if (isForegroundReclaim) {
            return false;
        }
        long required = requiredSilenceConfirmMsLocked();
        if (required <= 0L) {
            return false;
        }
        return silenceSinceElapsedLocked() < required;
    }

    private void startSilenceConfirmLocked() {
        stopCompetingAudioPoll();
        if (silenceConfirmPosted) {
            return;
        }
        silenceConfirmPosted = true;
        long required = requiredSilenceConfirmMsLocked();
        if (required <= 0L) {
            required = VOICE_BACKGROUND_SILENCE_CONFIRM_MS;
        }
        long remain = Math.max(200L, required - silenceSinceElapsedLocked());
        long delay = Math.min(remain, SILENCE_CONFIRM_POLL_MS);
        mainHandler.postDelayed(silenceConfirmPoll, delay);
        Log.i(TAG, "silence confirm armed remain=" + remain + "ms required=" + required
                + "ms sawMedia=" + competingEpisodeSawMedia
                + " bg=" + callback.isAppInBackground());
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
            AudioManager am = getAudioManager();
            if (isCompetingMediaPlaybackActive(am) || isExternalMusicActiveLocked(am)) {
                Log.i(TAG, "[CWPA-66] silence-confirm reset (competing back)");
                Log.i(TAG, "silence confirm: competing media, do not steal");
                noteCompetingSeenLocked();
                startSilenceConfirmLocked();
                return;
            }
            if (competingEpisodeSawMedia) {
                stopCompetingAudioPoll();
                Log.i(TAG, "silence confirm hold — media/video waits for foreground or now-playing");
                Log.i(TAG, "[PA-904] silence-confirm media hold");
                return;
            }
            if (shouldDeferAutoResumeLocked(false)) {
                startSilenceConfirmLocked();
                return;
            }
            // 微信语音：短静默窗过后还没收到 GAIN，恢复一次。
            boolean granted = hasFocus || performRequestFocus(false, false);
            if (granted) {
                boolean sawMedia = competingEpisodeSawMedia;
                pendingResume = false;
                noteBackgroundAutoResumeLocked();
                clearCompetingEpisodeLocked();
                Log.i(TAG, "silence confirmed -> auto resume sawMediaWas=" + sawMedia);
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
            // CWPA-66：PlaybackConfiguration 对视频常漏检；微信语音常无 UID → 再靠 isMusicActive。
            if (isCompetingPlaybackActive(am) || isExternalMusicActiveLocked(am)) {
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
            // 其它音源已停止且短静默通过 → 重新申请焦点。
            boolean granted = performRequestFocus(false, false);
            if (granted) {
                pendingResume = false;
                competingAudioPollCount = 0;
                noteBackgroundAutoResumeLocked();
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
                // 自身 WIA 在创维上常是 USAGE_MEDIA + CONTENT_TYPE_UNKNOWN，且 UID 会错报。
                // 只有 pid 明确属于别的进程才算外来（抖音）。pid 不明仍跳过，避免 HOME 自 mute。
                if (attrs.getUsage() == AudioAttributes.USAGE_MEDIA
                        && attrs.getContentType() == AudioAttributes.CONTENT_TYPE_UNKNOWN
                        && !isOtherAppUnknownMedia(cfg, myUid)) {
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
                    // 微信语音常无 UID；VOICE/SPEECH 本进程不会发 → 当外来，禁止 1.8s 误 resume。
                    if (isVoiceOrSpeechUsage(attrs)) {
                        foreign = Boolean.TRUE;
                        Log.i(TAG, "[CWPA-66] uid-less VOICE/SPEECH treated as foreign"
                                + " usage=" + attrs.getUsage()
                                + " content=" + attrs.getContentType());
                    } else {
                        // 无 UID 的 MEDIA 无法与自身 WIA 区分，交给 isMusicActive 兜底
                        continue;
                    }
                }
                if (foreign) {
                    // 仅 QQ/B 站等 MEDIA 记 episode（回前台才续）；微信 VOICE 不记，允许后台 GAIN 续。
                    if (isForeignMediaAppUsage(attrs)) {
                        noteCompetingSeenLocked();
                    }
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

    /** 仅 QQ 音乐 / B 站等 MEDIA 出声（不含微信 VOICE）——用于后台禁止抢播。 */
    private boolean isCompetingMediaAppPlaybackActive(AudioManager am) {
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
                if (attrs == null || !isForeignMediaAppUsage(attrs)) {
                    continue;
                }
                if (attrs.getUsage() == AudioAttributes.USAGE_MEDIA
                        && attrs.getContentType() == AudioAttributes.CONTENT_TYPE_UNKNOWN
                        && !isOtherAppUnknownMedia(cfg, myUid)) {
                    continue;
                }
                if (attrs.getUsage() == AudioAttributes.USAGE_MEDIA
                        && attrs.getContentType() == AudioAttributes.CONTENT_TYPE_MUSIC
                        && BoluoKeepPlayingMediaAnchor.isRunning()) {
                    Boolean own = isForeignClient(cfg, myUid);
                    if (own == null || !own) {
                        continue;
                    }
                }
                Boolean foreign = isForeignClient(cfg, myUid);
                if (foreign != null && foreign) {
                    return true;
                }
            }
        } catch (Exception e) {
            Log.w(TAG, "isCompetingMediaAppPlaybackActive: " + e.getMessage());
        }
        return false;
    }

    /**
     * 对方 competing：MEDIA/影视，以及微信语音等 VOICE/SPEECH。
     * USAGE_GAME 不计入——SKS30 上自身 AAudio 常被标 GAME 且 UID 错报，unmute 后会误抢占自己。
     */
    private static boolean isForeignCompetingUsage(AudioAttributes attrs) {
        return isForeignMediaAppUsage(attrs) || isVoiceOrSpeechUsage(attrs);
    }

    /**
     * 微信听/发语音常见 usage；本进程 WIA 不会发这些。
     * 导航 {@code USAGE_ASSISTANCE_NAVIGATION_GUIDANCE} 不计入——应 duck 而非 preempt。
     */
    private static boolean isVoiceOrSpeechUsage(AudioAttributes attrs) {
        int usage = attrs.getUsage();
        return usage == AudioAttributes.USAGE_VOICE_COMMUNICATION
                || usage == AudioAttributes.USAGE_VOICE_COMMUNICATION_SIGNALLING
                || usage == AudioAttributes.USAGE_ASSISTANT
                || attrs.getContentType() == AudioAttributes.CONTENT_TYPE_SPEECH;
    }

    /** 外来 VOICE/SPEECH 轨（不含 MEDIA、不含导航）。 */
    private boolean isCompetingVoicePlaybackActive(AudioManager am) {
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
                if (attrs == null || !isVoiceOrSpeechUsage(attrs)) {
                    continue;
                }
                Boolean foreign = isForeignClient(cfg, myUid);
                if (foreign == null || foreign) {
                    return true;
                }
            }
        } catch (Exception e) {
            Log.w(TAG, "isCompetingVoicePlaybackActive: " + e.getMessage());
        }
        return false;
    }

    /**
     * 已 mute/Pause 后自身可听轨常空：此时 {@link AudioManager#isMusicActive()} 真即外来
     * （微信语音消息常无 UID）。若保活 MEDIA 锚点仍占 mixer，不能单靠此 API——交给 GAIN。
     */
    private boolean isExternalMusicActiveLocked(AudioManager am) {
        if (am == null) {
            return false;
        }
        try {
            if (!am.isMusicActive()) {
                return false;
            }
            // 清晰外来 config 优先
            if (isCompetingMediaPlaybackActive(am)) {
                return true;
            }
            // 自身 mixer 已空仍 isMusicActive → 外来（常见微信）
            return !isSelfMediaPlaybackActive(am);
        } catch (Exception e) {
            Log.w(TAG, "isExternalMusicActiveLocked: " + e.getMessage());
            return false;
        }
    }

    /**
     * 抖音常见 USAGE_MEDIA + CONTENT_TYPE_UNKNOWN。
     * pid 是别的进程，或 pid 不可用但 uid 是别的正数用户，才算外来。
     * uid=-1（创维错报）且 pid 不是别的进程：仍当自己，避免 HOME 自 mute。
     */
    static boolean isOtherAppUnknownMedia(AudioPlaybackConfiguration cfg, int myUid) {
        int pid = clientPidOf(cfg);
        if (pid > 0) {
            return pid != Process.myPid();
        }
        int uid = clientUidOf(cfg);
        return uid > 0 && uid != myUid;
    }

    private static int clientPidOf(AudioPlaybackConfiguration cfg) {
        return invokeClientId(cfg, "getClientPid");
    }

    private static int clientUidOf(AudioPlaybackConfiguration cfg) {
        return invokeClientId(cfg, "getClientUid");
    }

    private static int invokeClientId(AudioPlaybackConfiguration cfg, String method) {
        try {
            Object value = cfg.getClass().getMethod(method).invoke(cfg);
            if (value instanceof Integer) {
                return (Integer) value;
            }
        } catch (Exception ignored) {
            // OEM 无该方法
        }
        return -1;
    }

    private static int invokeRecordingClientId(AudioRecordingConfiguration cfg, String method) {
        try {
            Object value = cfg.getClass().getMethod(method).invoke(cfg);
            if (value instanceof Integer) {
                return (Integer) value;
            }
        } catch (Exception ignored) {
            // API < 29 或 OEM 无该方法
        }
        return -1;
    }

    /** QQ / B 站等长期 MEDIA competing（回前台才续）；不含微信 VOICE。 */
    private static boolean isForeignMediaAppUsage(AudioAttributes attrs) {
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

    /** API 29+ 的录音源。读不到时返回 -1，不当成麦克风。 */
    private static int recordingAudioSource(AudioRecordingConfiguration cfg) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
            return -1;
        }
        try {
            return cfg.getClientAudioSource();
        } catch (Exception e) {
            return -1;
        }
    }

    /**
     * 其它 App 正在录音。只认还能对上进程的配置。
     * 身份被抹掉的配置不能当微信：AOSP {@code anonymizedCopy} 把 uid 写成 -1、包名清空，
     * source 却原样留下。本机 14:34:36–42 连续四次就是 uid=-1 source=1 mode=0，
     * 400ms 后 Pause_ALL，录音一结束又 Resume_ALL。
     */
    private boolean isForeignRecordingActiveLocked(AudioManager am) {
        if (am == null || Build.VERSION.SDK_INT < Build.VERSION_CODES.N) {
            return false;
        }
        try {
            List<AudioRecordingConfiguration> configs = am.getActiveRecordingConfigurations();
            if (configs == null || configs.isEmpty()) {
                return false;
            }
            int myUid = Process.myUid();
            int myPid = Process.myPid();
            for (AudioRecordingConfiguration cfg : configs) {
                if (cfg == null) {
                    continue;
                }
                // getClientUid/Pid 部分 API/OEM 无公开方法，反射兜底。
                int uid = invokeRecordingClientId(cfg, "getClientUid");
                int pid = invokeRecordingClientId(cfg, "getClientPid");
                int source = recordingAudioSource(cfg);
                boolean foreign;
                if (pid > 0) {
                    foreign = pid != myPid;
                } else if (uid >= 0) {
                    foreign = uid != myUid;
                } else {
                    foreign = false;
                }
                Log.i(TAG, "[BoluoAudioFg] recording cfg uid=" + uid
                        + " pid=" + pid + " source=" + source
                        + " foreign=" + foreign);
                if (foreign) {
                    return true;
                }
            }
        } catch (Exception e) {
            Log.w(TAG, "isForeignRecordingActive: " + e.getMessage());
        }
        return false;
    }

    /**
     * 卡顿 / 微信语音 RCA：一次打出焦点闩 + AudioManager.mode + 全 mixer 轨。
     * 过滤 tag {@code BoluoAudioStutter}。
     */
    private void dumpStutterSnapshotLocked(String reason) {
        final String ST = "BoluoAudioStutter";
        AudioManager am = getAudioManager();
        int mode = -1;
        boolean musicActive = false;
        try {
            if (am != null) {
                mode = am.getMode();
                musicActive = am.isMusicActive();
            }
        } catch (Exception e) {
            Log.w(ST, "mode/musicActive: " + e.getMessage());
        }
        boolean voiceCall = isInVoiceCallLocked(am);
        boolean foreignRec = false;
        boolean competing = false;
        boolean voiceCfg = false;
        boolean mediaApp = false;
        try {
            competing = isCompetingMediaPlaybackActive(am);
            mediaApp = isCompetingMediaAppPlaybackActive(am);
            voiceCfg = competing && !mediaApp;
            foreignRec = isForeignRecordingActiveLocked(am);
        } catch (Exception ignored) {
            // dump only
        }
        Log.i(ST, "snap reason=" + reason
                + " bg=" + callback.isAppInBackground()
                + " hasFocus=" + hasFocus
                + " pending=" + pendingResume
                + " ducked=" + ducked
                + " enginePlaying=" + callback.isEnginePlaying()
                + " keepPlaying=" + callback.shouldSuppressTransientPauseInBackground()
                + " mode=" + mode
                + " voiceCall=" + voiceCall
                + " foreignRec=" + foreignRec
                + " musicActive=" + musicActive
                + " competing=" + competing
                + " mediaApp=" + mediaApp
                + " voiceLikely=" + (voiceCall || voiceCfg || foreignRec)
                + " reopen=" + reopenReason
                + " unityEdge=" + isUnityPauseEdgeLocked()
                + " selfGoneMiss=" + consecutiveSelfGoneMiss);
        if (am == null || Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
            return;
        }
        try {
            List<AudioPlaybackConfiguration> configs = am.getActivePlaybackConfigurations();
            if (configs == null || configs.isEmpty()) {
                Log.i(ST, "mixer empty");
                return;
            }
            int myUid = Process.myUid();
            int myPid = Process.myPid();
            int i = 0;
            for (AudioPlaybackConfiguration cfg : configs) {
                if (cfg == null) {
                    continue;
                }
                AudioAttributes attrs = cfg.getAudioAttributes();
                int usage = attrs != null ? attrs.getUsage() : -1;
                int content = attrs != null ? attrs.getContentType() : -1;
                int uid = clientUidOf(cfg);
                int pid = clientPidOf(cfg);
                boolean started = isPlaybackStarted(cfg);
                Boolean foreign = isForeignClient(cfg, myUid);
                boolean voice = attrs != null && isVoiceOrSpeechUsage(attrs);
                boolean media = attrs != null && isForeignMediaAppUsage(attrs);
                Log.i(ST, "mixer[" + i + "] started=" + started
                        + " usage=" + usage
                        + " content=" + content
                        + " uid=" + uid
                        + " pid=" + pid
                        + " myUid=" + myUid
                        + " myPid=" + myPid
                        + " foreign=" + foreign
                        + " voice=" + voice
                        + " media=" + media);
                i++;
                if (i >= 24) {
                    Log.i(ST, "mixer truncated n=" + configs.size());
                    break;
                }
            }
            Log.i(ST, "mixer total=" + configs.size() + " dumped=" + i);
        } catch (Exception e) {
            Log.w(ST, "mixer dump: " + e.getMessage());
        }
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
     * 微信语音结束后 OEM 不发 GAIN：只恢复一次。
     * 音乐/视频不走这里硬抢。通话还在就再等一轮。
     */
    private void onTransientGainWatchdog() {
        synchronized (focusLock) {
            if (!pendingResume || competingEpisodeSawMedia) {
                Log.i(TAG, "TRANSIENT GAIN watchdog stop"
                        + " pending=" + pendingResume
                        + " media=" + competingEpisodeSawMedia);
                return;
            }
            AudioManager am = getAudioManager();
            if (isInVoiceCallLocked(am) || isForeignRecordingActiveLocked(am)) {
                Log.i(TAG, "TRANSIENT GAIN watchdog deferred (voice still active)");
                startTransientGainWatchdog();
                return;
            }
            if (isCompetingMediaPlaybackActive(am)) {
                noteCompetingSeenLocked();
                Log.i(TAG, "TRANSIENT GAIN watchdog: external media, hold");
                Log.i(TAG, "[PA-904] TRANSIENT watchdog media hold");
                return;
            }
            resumeTransientVoiceOnceLocked("transient_watchdog");
        }
    }

    private void cancelDuckConfirmLocked() {
        duckConfirmGeneration++;
        duckConfirmScheduledGen = -1L;
        focusCallbackHandler.removeCallbacks(duckConfirm);
    }

    private boolean isVoiceRecConfirmPendingLocked() {
        return voiceRecConfirmScheduledGen != -1L
                && voiceRecConfirmScheduledGen == voiceRecConfirmGeneration;
    }

    private void cancelVoiceRecConfirmLocked() {
        voiceRecConfirmGeneration++;
        voiceRecConfirmScheduledGen = -1L;
        focusCallbackHandler.removeCallbacks(voiceRecConfirm);
    }

    private boolean isMediaTransientHoldPendingLocked() {
        return mediaTransientHoldScheduledGen != -1L
                && mediaTransientHoldScheduledGen == mediaTransientHoldGeneration;
    }

    private void cancelMediaTransientHoldLocked() {
        mediaTransientHoldGeneration++;
        mediaTransientHoldScheduledGen = -1L;
        focusCallbackHandler.removeCallbacks(mediaTransientHold);
    }

    /** 看不见对方播放轨时，短暂丢焦先等一窗。重复进入不重置，避免 QQ 连发把窗拉长。 */
    private void scheduleMediaTransientHoldLocked() {
        if (isMediaTransientHoldPendingLocked()) {
            return;
        }
        final long gen = ++mediaTransientHoldGeneration;
        mediaTransientHoldScheduledGen = gen;
        focusCallbackHandler.removeCallbacks(mediaTransientHold);
        focusCallbackHandler.postDelayed(mediaTransientHold, MEDIA_TRANSIENT_HOLD_MS);
        Log.i(TAG, "[BoluoAudioFg] media hold arm gen=" + gen
                + " delayMs=" + MEDIA_TRANSIENT_HOLD_MS);
    }

    private void onMediaTransientHold() {
        synchronized (focusLock) {
            if (mediaTransientHoldScheduledGen != mediaTransientHoldGeneration) {
                return;
            }
            mediaTransientHoldScheduledGen = -1L;
            if (hasFocus) {
                Log.i(TAG, "[BoluoAudioFg] media hold expired, focus back, keep playing");
                return;
            }
            AudioManager am = getAudioManager();
            boolean voice = isInVoiceCallLocked(am);
            boolean foreignRec = isForeignRecordingActiveLocked(am);
            if (voice || foreignRec) {
                Log.i(TAG, "[BoluoAudioFg] media hold defer to voice voice=" + voice
                        + " foreignRec=" + foreignRec);
                return;
            }
            Log.i(TAG, "[BoluoAudioFg] media hold pause (transient still lost, treat as music)");
            // 不轮询：QQ 停了也不在后台续。回前台显示暂停，滑动再恢复。
            applyCompetingPreemptLocked("AUDIOFOCUS_LOSS_TRANSIENT", true, false, false);
        }
    }

    /** 录音要连续站住才当按住说话。重复进入不重置计时。 */
    private void scheduleVoiceRecConfirmLocked() {
        if (isVoiceRecConfirmPendingLocked()) {
            return;
        }
        final long gen = ++voiceRecConfirmGeneration;
        voiceRecConfirmScheduledGen = gen;
        focusCallbackHandler.removeCallbacks(voiceRecConfirm);
        focusCallbackHandler.postDelayed(voiceRecConfirm, VOICE_REC_CONFIRM_MS);
        Log.i(TAG, "[BoluoAudioFg] voice confirm arm gen=" + gen
                + " delayMs=" + VOICE_REC_CONFIRM_MS);
    }

    private void onVoiceRecConfirm() {
        synchronized (focusLock) {
            if (voiceRecConfirmScheduledGen != voiceRecConfirmGeneration) {
                return;
            }
            voiceRecConfirmScheduledGen = -1L;
            AudioManager am = getAudioManager();
            boolean voice = isInVoiceCallLocked(am);
            boolean foreignRec = isForeignRecordingActiveLocked(am);
            if (!voice && !foreignRec) {
                Log.i(TAG, "[BoluoAudioFg] voice confirm expired, keep playing");
                BoluoAudioStutterProbe.noteSwitchClass("keep_graph", "voice_confirm_expired");
                return;
            }
            if (isCompetingMediaPlaybackActive(am)) {
                Log.i(TAG, "[BoluoAudioFg] voice confirm pause competing");
                applyCompetingPreemptLocked("RECORDING_OR_VOICE", true, true, false);
                return;
            }
            Log.i(TAG, "[BoluoAudioFg] voice confirm pause voice=" + voice
                    + " foreignRec=" + foreignRec);
            applyTransientPauseLocked(false);
            if (!competingEpisodeSawMedia) {
                startTransientGainWatchdog();
            }
        }
    }

    /** 焦点线程同步 dip 到 0，稍后再决定四成还是护栏。 */
    private void scheduleAmbiguousDuckConfirmLocked() {
        final long gen = ++duckConfirmGeneration;
        duckConfirmScheduledGen = gen;
        ducked = false;
        stopDuckStaleWatchdog();
        callback.onAmbiguousDuckDip();
        focusCallbackHandler.removeCallbacks(duckConfirm);
        focusCallbackHandler.postDelayed(duckConfirm, DUCK_CONFIRM_MS);
        Log.i(TAG, "[BoluoAudioFg] CAN_DUCK confirm scheduled gen=" + gen
                + " delayMs=" + DUCK_CONFIRM_MS);
    }

    private void onDuckConfirm() {
        synchronized (focusLock) {
            if (duckConfirmScheduledGen != duckConfirmGeneration) {
                Log.i(TAG, "[BoluoAudioFg] CAN_DUCK confirm superseded");
                return;
            }
            AudioManager am = getAudioManager();
            boolean competing = isCompetingMediaPlaybackActive(am);
            boolean voiceOrRec = isInVoiceCallLocked(am)
                    || isForeignRecordingActiveLocked(am);
            boolean leftApp = callback.hasLeftForeground();
            boolean keepPlaying = callback.allowsUninterruptedBackgroundPlayback();
            if (leftApp && keepPlaying && !competing && !voiceOrRec) {
                Log.i(TAG, "[BoluoAudioFg] CAN_DUCK confirm keep_graph");
                BoluoAudioStutterProbe.noteSwitchClass(
                        "keep_graph", "duck_confirm_left_no_opponent");
                callback.onKeepGraphRestoreGain();
                return;
            }
            if (competing || voiceOrRec || (leftApp && !keepPlaying)) {
                Log.i(TAG, "[BoluoAudioFg] CAN_DUCK confirm guard_mute left="
                        + leftApp + " competing=" + competing
                        + " voiceOrRec=" + voiceOrRec);
                applyCompetingPreemptLocked("AUDIOFOCUS_LOSS_TRANSIENT_CAN_DUCK",
                        competing, competing, voiceOrRec && !competing);
                BoluoAudioStutterProbe.noteSwitchClass(
                        "guard_mute", "duck_confirm_yield");
                return;
            }
            if (!callback.isEnginePlaying()) {
                Log.i(TAG, "[BoluoAudioFg] CAN_DUCK confirm skip (not playing)");
                callback.onKeepGraphRestoreGain();
                return;
            }
            ducked = true;
            Log.i(TAG, "[BoluoAudioFg] CAN_DUCK confirm duck≈0.4 (nav)");
            callback.onFocusDuck();
            startDuckStaleWatchdog();
            BoluoAudioStutterProbe.noteSwitchClass("duck", "duck_confirm_nav");
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
        // 系统音量键只改 STREAM_MUSIC。重开播放设备或 Resume 会在出声时卡一下。
        Log.i(TAG, "[BoluoAudioFg] STREAM_MUSIC " + prev + "→" + nowVol
                + " ignore (no reopen)");
    }
}
