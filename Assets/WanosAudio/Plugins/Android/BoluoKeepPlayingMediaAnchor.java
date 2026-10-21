package com.wanos.app.health;

import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioTrack;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;

/**
 * 后台续播 MEDIA 锚点：创维 UsecaseValidator 会把 WIA AAudio 的 USAGE_MEDIA 改成 GAME，
 * 约 9–11s 拆 GAME 轨。本类维持一条真正的 USAGE_MEDIA 近静音循环轨（与媒体 FGS 并列），
 * 目标是让 OEM 按「媒体播放」保活 WIA，而不是靠周期 ResetPlaybackDevice。
 *
 * 纯 0 PCM 会被 MTK 标 NoAudioTrack 拆轨（头不动但 PLAYSTATE 仍 PLAYING）。
 * 写入极低振幅 dither，听感仍静音，但 HAL 认为有有效 PCM。
 *
 * stop 防抖：快切前台再 HOME 时立刻 stop 会撕轨（见 NoAudioTrack 毛刺）。
 */
public final class BoluoKeepPlayingMediaAnchor {

    private static final String TAG = "BoluoMediaAnchor";
    private static final int SAMPLE_RATE = 16_000;
    private static final int CHANNEL_MASK = AudioFormat.CHANNEL_OUT_MONO;
    private static final int ENCODING = AudioFormat.ENCODING_PCM_16BIT;
    /** 回前台防抖。1.5s 盖不住创维上滑约 2–4s 的来回切，stop 会落在下一次 HOME 之前，锚点被拆掉再建（刺啦）。 */
    private static final long STOP_DEBOUNCE_MS = 8_000L;
    /** ±1 LSB dither：听感静音，避免 OEM 对全 0 缓冲发 NoAudioTrack。 */
    private static final short DITHER_AMP = 1;

    private static final Object LOCK = new Object();
    private static final Handler MAIN = new Handler(Looper.getMainLooper());
    private static AudioTrack track;
    private static Thread writer;
    private static volatile boolean running;
    /** 最近一次 write 成功的 elapsedRealtime；HAL 拆轨后 getPlaybackHeadPosition 会卡死，只能靠写心跳判僵尸。 */
    private static volatile long lastWriteElapsedMs;
    private static volatile boolean writeFailed;
    private static volatile Runnable onWriteDead;
    private static final Runnable STOP_RUNNABLE = BoluoKeepPlayingMediaAnchor::stopNow;

    /**
     * 默认关：对齐线上 3eec80b6（无 MediaAnchor）。
     * SKS30 上额外 USAGE_MEDIA dither 轨会连带触发 NoAudioTrack，进程随后被 OEM 冻住，L1 永不执行。
     */
    private static volatile boolean enabled = false;

    /** 写循环失败/停写时回调（observe 线程禁止调 getPlaybackHeadPosition）。 */
    public static void setOnWriteDead(Runnable cb) {
        onWriteDead = cb;
    }

    /**
     * 锚点写心跳停了：HAL/OEM 已拆 MEDIA 保活轨。
     * 禁止用 getPlaybackHeadPosition 判定——拆轨后该 API 会挂死调用线程。
     */
    public static boolean isWriteStalled(long staleMs) {
        if (!running) {
            return false;
        }
        if (writeFailed) {
            return true;
        }
        long last = lastWriteElapsedMs;
        if (last <= 0L) {
            return false;
        }
        return android.os.SystemClock.elapsedRealtime() - last >= staleMs;
    }

    private BoluoKeepPlayingMediaAnchor() {}

    /** 同包对照：关掉后 ensureResident 不再建轨，并立刻拆掉已有轨。 */
    public static void setEnabled(boolean on) {
        enabled = on;
        Log.i(TAG, "enabled=" + on);
        if (!on) {
            releaseResident();
        }
    }

    public static boolean isEnabled() {
        return enabled;
    }

    /** 进岛后拉起并留下。已在播且头在走则不重建。 */
    public static void ensureResident() {
        if (!enabled) {
            Log.i(TAG, "ensureResident skip (disabled)");
            return;
        }
        start();
    }

    /** 退岛/退登才拆。HOME / 回前台不要调。 */
    public static void releaseResident() {
        MAIN.removeCallbacks(STOP_RUNNABLE);
        synchronized (LOCK) {
            stopLocked();
        }
    }

    public static boolean isRunning() {
        synchronized (LOCK) {
            return running && track != null
                    && track.getPlayState() == AudioTrack.PLAYSTATE_PLAYING;
        }
    }

    /** @deprecated 拆轨后 getPlaybackHeadPosition 可能永久阻塞；改用 {@link #isWriteStalled(long)}。 */
    @Deprecated
    public static int playbackHead() {
        // 不碰 AudioTrack API：SKS30 NoAudioTrack 后这里会挂死 keepPlaying HandlerThread。
        return -1;
    }

    /**
     * 头不动但 PLAYSTATE 仍 PLAYING = 僵尸轨。强制拆再建，禁止走 already playing。
     * 只动 MEDIA 锚点，不 Reset WIA 设备。
     */
    public static void restartIfZombieOrStopped() {
        if (!enabled) {
            return;
        }
        MAIN.removeCallbacks(STOP_RUNNABLE);
        synchronized (LOCK) {
            boolean zombie = running && track != null
                    && track.getPlayState() == AudioTrack.PLAYSTATE_PLAYING;
            if (zombie) {
                Log.i(TAG, "restart zombie MEDIA anchor (head stalled / force)");
            }
            stopLocked();
        }
        start();
    }

    public static void restart() {
        MAIN.removeCallbacks(STOP_RUNNABLE);
        synchronized (LOCK) {
            stopLocked();
        }
        start();
    }

    public static void start() {
        if (!enabled) {
            Log.i(TAG, "start skip (disabled)");
            return;
        }
        MAIN.removeCallbacks(STOP_RUNNABLE);
        synchronized (LOCK) {
            if (running && track != null
                    && track.getPlayState() == AudioTrack.PLAYSTATE_PLAYING) {
                Log.i(TAG, "already playing");
                return;
            }
            stopLocked();
            try {
                int minBuf = AudioTrack.getMinBufferSize(SAMPLE_RATE, CHANNEL_MASK, ENCODING);
                if (minBuf <= 0) {
                    Log.w(TAG, "getMinBufferSize failed: " + minBuf);
                    return;
                }
                int bufSize = Math.max(minBuf, SAMPLE_RATE / 10 * 2);
                AudioAttributes attrs = new AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_MEDIA)
                        .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                        .build();
                AudioFormat format = new AudioFormat.Builder()
                        .setSampleRate(SAMPLE_RATE)
                        .setEncoding(ENCODING)
                        .setChannelMask(CHANNEL_MASK)
                        .build();
                AudioTrack t = new AudioTrack.Builder()
                        .setAudioAttributes(attrs)
                        .setAudioFormat(format)
                        .setBufferSizeInBytes(bufSize)
                        .setTransferMode(AudioTrack.MODE_STREAM)
                        .build();
                if (t.getState() != AudioTrack.STATE_INITIALIZED) {
                    Log.w(TAG, "AudioTrack not initialized state=" + t.getState());
                    t.release();
                    return;
                }
                track = t;
                running = true;
                writeFailed = false;
                lastWriteElapsedMs = android.os.SystemClock.elapsedRealtime();
                t.play();
                writer = new Thread(BoluoKeepPlayingMediaAnchor::writeKeepAliveLoop, "BoluoMediaAnchor");
                writer.setDaemon(true);
                writer.start();
                Log.i(TAG, "started USAGE_MEDIA keep-alive dither anchor buf=" + bufSize);
            } catch (Exception e) {
                Log.w(TAG, "start failed: " + e.getMessage());
                stopLocked();
            }
        }
    }

    /** 回前台：防抖后再停，避免快切 HOME 撕锚点。 */
    public static void stop() {
        MAIN.removeCallbacks(STOP_RUNNABLE);
        MAIN.postDelayed(STOP_RUNNABLE, STOP_DEBOUNCE_MS);
        Log.i(TAG, "stop scheduled in " + STOP_DEBOUNCE_MS + "ms");
    }

    private static void stopNow() {
        synchronized (LOCK) {
            stopLocked();
        }
    }

    private static void stopLocked() {
        running = false;
        Thread w = writer;
        writer = null;
        if (w != null) {
            try {
                w.interrupt();
            } catch (Exception ignored) {
            }
        }
        AudioTrack t = track;
        track = null;
        if (t != null) {
            try {
                t.pause();
            } catch (Exception ignored) {
            }
            try {
                t.flush();
            } catch (Exception ignored) {
            }
            try {
                t.stop();
            } catch (Exception ignored) {
            }
            try {
                t.release();
            } catch (Exception ignored) {
            }
            Log.i(TAG, "stopped");
        }
    }

    private static void writeKeepAliveLoop() {
        // ~40ms 帧；交错 ±1 样本，避免全 0 被 OEM 当无轨。
        int samples = SAMPLE_RATE / 25;
        short[] pcm = new short[samples];
        byte[] bytes = new byte[samples * 2];
        int phase = 0;
        while (running) {
            AudioTrack t = track;
            if (t == null) {
                break;
            }
            for (int i = 0; i < samples; i++) {
                short s = ((phase + i) & 1) == 0 ? DITHER_AMP : (short) -DITHER_AMP;
                pcm[i] = s;
            }
            phase += samples;
            for (int i = 0; i < samples; i++) {
                short s = pcm[i];
                bytes[i * 2] = (byte) (s & 0xff);
                bytes[i * 2 + 1] = (byte) ((s >> 8) & 0xff);
            }
            try {
                int written = t.write(bytes, 0, bytes.length);
                if (written < 0) {
                    Log.w(TAG, "write error=" + written);
                    markWriteDead("error=" + written);
                    break;
                }
                lastWriteElapsedMs = android.os.SystemClock.elapsedRealtime();
            } catch (Exception e) {
                Log.w(TAG, "write failed: " + e.getMessage());
                markWriteDead("ex=" + e.getMessage());
                break;
            }
        }
    }

    private static void markWriteDead(String why) {
        writeFailed = true;
        Log.w(TAG, "write dead → notify recover why=" + why);
        Runnable cb = onWriteDead;
        if (cb != null) {
            try {
                MAIN.post(cb);
            } catch (Exception e) {
                Log.w(TAG, "onWriteDead post: " + e.getMessage());
            }
        }
    }
}
