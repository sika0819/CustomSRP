package com.wanos.app.health;

import android.content.Context;
import android.content.SharedPreferences;
import android.os.SystemClock;
import android.provider.Settings;
import android.util.Log;
import com.unity3d.player.UnityPlayer;
import java.util.HashMap;
import java.util.Map;
import java.util.UUID;

/**
 * 雨声后台回前台卡顿：A/B 开关（默认全关）+ 最小计数。
 * 与宿主 {@code BoluoAudioStutterAb} 共用 prefs 名/键；每个决策点重读，不依赖冷启动。
 */
public final class BoluoAudioStutterProbe {
    public static final String TAG = "BoluoFgStutter";
    public static final String PREFS = "boluo_audio_stutter_ab";
    public static final String KEY_FORCE_LEGACY = "force_legacy_audio_path";
    public static final String KEY_SKIP_RECLAIM = "skip_reclaim_reset_when_keep_playing";
    public static final String KEY_PARK_DISPLAY = "keep_playing_park_display";
    public static final String KEY_DELAY_RESYNC = "delay_foreground_resync";
    public static final String KEY_GRAY_SKIP = "gray_skip_reclaim_percent";
    public static final String KEY_GRAY_PARK = "gray_park_display_percent";
    public static final String KEY_GRAY_DELAY = "gray_delay_resync_percent";

    public static final String REOPEN_NONE = "none";
    public static final String REOPEN_PREEMPT = "preempt";
    public static final String REOPEN_TRANSIENT = "transient";
    public static final String REOPEN_SELF_GONE = "self_gone";
    public static final String REOPEN_DEVICE_ROUTE = "device_route";
    public static final String REOPEN_MANUAL = "manual";

    private static final long MIXER_CACHE_TTL_MS = 500L;

    private static volatile String traceId = "--------";
    private static volatile int resetCount;
    private static volatile int skipResetCount;
    private static volatile int staleMixerCount;
    private static volatile int underrunCount;
    private static volatile long bgEnteredElapsed;
    private static volatile Boolean mixerCache;
    private static volatile long mixerCacheAt;

    private BoluoAudioStutterProbe() {}

    public static String traceId() {
        return traceId;
    }

    public static void onEnterBackground() {
        traceId = UUID.randomUUID().toString().substring(0, 8);
        resetCount = 0;
        skipResetCount = 0;
        staleMixerCount = 0;
        underrunCount = 0;
        bgEnteredElapsed = SystemClock.elapsedRealtime();
        Log.i(TAG, "bg enter trace=" + traceId);
    }

    public static void onEnterForeground() {
        long bgMs = bgEnteredElapsed == 0L ? 0L : SystemClock.elapsedRealtime() - bgEnteredElapsed;
        boolean longBg = bgMs >= 180_000L;
        Log.i(TAG, "fg enter trace=" + traceId
                + " bg_duration_ms=" + bgMs
                + " session_long_bg_resume=" + longBg
                + " process_survived=true"
                + " reset_count=" + resetCount
                + " skip_reset_count=" + skipResetCount
                + " underrun_count=" + underrunCount
                + " stream_health_stale=" + staleMixerCount);
        reportFgSession(bgMs, longBg);
    }

    public static void noteReset(String reason) {
        resetCount++;
        Log.i(TAG, "resetPlaybackDevice trace=" + traceId
                + " n=" + resetCount + " reopen_reason=" + reason);
    }

    public static void noteSkipReset(String reason, String streamHealth) {
        skipResetCount++;
        Log.i(TAG, "skip reset trace=" + traceId
                + " n=" + skipResetCount
                + " reopen_reason=" + reason
                + " stream_health=" + streamHealth);
    }

    public static void noteStaleMixer() {
        staleMixerCount++;
    }

    public static void noteUnderrun(int n) {
        if (n > 0) {
            underrunCount += n;
        }
    }

    public static void cacheMixer(boolean selfActive) {
        mixerCache = selfActive;
        mixerCacheAt = SystemClock.elapsedRealtime();
    }

    public static MixerCache readMixerCache() {
        MixerCache out = new MixerCache();
        Boolean cached = mixerCache;
        if (cached == null || SystemClock.elapsedRealtime() - mixerCacheAt > MIXER_CACHE_TTL_MS) {
            out.stale = true;
            out.active = false;
            return out;
        }
        out.stale = false;
        out.active = cached;
        return out;
    }

    public static final class MixerCache {
        public boolean stale;
        public boolean active;
    }

    public static boolean forceLegacy(Context ctx) {
        return readBool(ctx, KEY_FORCE_LEGACY, false);
    }

    public static boolean skipReclaimReset(Context ctx) {
        return readBool(ctx, KEY_SKIP_RECLAIM, false) || inGray(ctx, KEY_GRAY_SKIP);
    }

    public static boolean parkDisplay(Context ctx) {
        return readBool(ctx, KEY_PARK_DISPLAY, false) || inGray(ctx, KEY_GRAY_PARK);
    }

    public static boolean delayResync(Context ctx) {
        return readBool(ctx, KEY_DELAY_RESYNC, false) || inGray(ctx, KEY_GRAY_DELAY);
    }

    public static boolean isHardReopenReason(String reason) {
        return REOPEN_PREEMPT.equals(reason)
                || REOPEN_TRANSIENT.equals(reason)
                || REOPEN_DEVICE_ROUTE.equals(reason)
                || REOPEN_SELF_GONE.equals(reason);
    }

    /**
     * 续播且引擎仍 Started、reopen 非硬原因时跳过全局 Reset。
     * 雨声无 BGM instance 时旧逻辑会误 Reset。
     */
    public static boolean shouldSkipKeepPlayingReset(
            boolean flagOn,
            boolean forceLegacyPath,
            boolean userPaused,
            boolean shouldStop,
            boolean backgroundPlayDisabled,
            boolean engineStarted,
            String reopenReason) {
        if (!flagOn || forceLegacyPath) {
            return false;
        }
        if (userPaused || shouldStop || backgroundPlayDisabled) {
            return false;
        }
        if (!engineStarted) {
            return false;
        }
        return !isHardReopenReason(reopenReason);
    }

    private static boolean inGray(Context ctx, String percentKey) {
        int percent = readInt(ctx, percentKey, 0);
        if (percent <= 0) {
            return false;
        }
        if (percent >= 100) {
            return true;
        }
        Context app = resolveApp(ctx);
        if (app == null) {
            return false;
        }
        try {
            String id = Settings.Secure.getString(app.getContentResolver(), Settings.Secure.ANDROID_ID);
            if (id == null) {
                id = "boluo";
            }
            int bucket = (id.hashCode() & 0x7fffffff) % 100;
            return bucket < percent;
        } catch (Exception e) {
            return false;
        }
    }

    private static void reportFgSession(long bgMs, boolean longBg) {
        try {
            Class<?> cls = Class.forName("com.wanos.boluo.log.BoluoBlsLogger");
            Object logger = cls.getField("INSTANCE").get(null);
            Map<String, Object> fields = new HashMap<>();
            fields.put("event", "fg_stutter_session");
            fields.put("trace_id", traceId);
            fields.put("reset_count", resetCount);
            fields.put("underrun_count", underrunCount);
            fields.put("stream_health_stale", staleMixerCount);
            fields.put("bg_duration_ms", bgMs);
            fields.put("session_long_bg_resume", longBg);
            fields.put("process_survived", true);
            fields.put("skip_reset_count", skipResetCount);
            fields.put("ab_skip_reclaim", skipReclaimReset(null));
            fields.put("ab_force_legacy", forceLegacy(null));
            cls.getMethod("logEvent", Map.class).invoke(logger, fields);
        } catch (Throwable t) {
            Log.d(TAG, "BLS fg session skipped: " + t.getMessage());
        }
    }

    private static boolean readBool(Context ctx, String key, boolean def) {
        Context app = resolveApp(ctx);
        if (app == null) {
            return def;
        }
        try {
            SharedPreferences p = app.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
            return p.getBoolean(key, def);
        } catch (Exception e) {
            return def;
        }
    }

    private static int readInt(Context ctx, String key, int def) {
        Context app = resolveApp(ctx);
        if (app == null) {
            return def;
        }
        try {
            SharedPreferences p = app.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
            return p.getInt(key, def);
        } catch (Exception e) {
            return def;
        }
    }

    private static Context resolveApp(Context ctx) {
        if (ctx != null) {
            return ctx.getApplicationContext();
        }
        try {
            if (UnityPlayer.currentActivity != null) {
                return UnityPlayer.currentActivity.getApplicationContext();
            }
        } catch (Throwable ignored) {
            // 引擎未挂
        }
        return null;
    }
}
