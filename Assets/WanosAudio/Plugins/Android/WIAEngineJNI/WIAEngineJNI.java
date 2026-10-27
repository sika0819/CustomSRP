package com.wanos.app.health;

import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.media.MediaMetadata;
import android.media.session.MediaSession;
import android.media.session.PlaybackState;
import android.app.PendingIntent;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Looper;
import android.os.SystemClock;
import android.util.Log;
import android.view.KeyEvent;

import java.io.InputStream;

/**
 * WIAEngine JNI 封装
 * 供 Java 在后台直接调用 WIAEngine，绕过 Unity，实现与 iOS mm 等价的能力。
 * 用于锁屏播放控制、自动下一曲、Bank 加载/卸载等场景。
 * updateNowPlayingInfo 无 Unity 依赖，支持后台调用。
 */
public class WIAEngineJNI {
    private static final String TAG = "WIAEngineJNI";

    static {
        try {
            // 必须先加载 libWIAEngine，libWIAEngineJNI 依赖其符号
            System.loadLibrary("WIAEngine");
            Log.i(TAG, "libWIAEngine loaded");
        } catch (UnsatisfiedLinkError e) {
            Log.w(TAG, "libWIAEngine load: " + e.getMessage() + " (may already loaded by Unity)");
        }
        try {
            System.loadLibrary("WIAEngineJNI");
            Log.i(TAG, "libWIAEngineJNI loaded");
        } catch (UnsatisfiedLinkError e) {
            // MediaSession / updateNowPlayingInfo 是纯 Java；JNI 失败时仍可推负一屏卡片。
            Log.e(TAG, "Failed to load libWIAEngineJNI: " + e.getMessage()
                    + " (NowPlaying MediaSession still available)");
        }
    }

    // ========== Bank ==========
    /** 从路径加载 Bank，对应 WIAEngine.LoadBankFromPath */
    public static native int loadBankFromPath(String bankPath);

    /** 从数据加载 Bank，对应 WIAEngine.LoadBankFromData */
    public static native int loadBankFromData(byte[] data, String bankUnloadTag, boolean async);

    /** 卸载指定 Bank */
    public static native int unloadBank(String bankUnloadTag);

    /** 卸载全部 Bank */
    public static native int unloadAllBanks();

    /** 检查 Bank 是否已解码，0 表示成功 */
    public static native int isBankDecoded(long bankId);

    /** 获取已加载 Bank 列表字符串 */
    public static native String getLoadedBankList();

    // ========== Event ==========
    /** 发送事件，callback 传 null 表示无回调 */
    public static native long postEvent(long objId, String eventName);

    /** 发送事件并注册状态回调（InstanceState_Playing/Ended），用于自动下一曲 */
    public static native long postEventWithCallback(long objId, String eventName);

    /** Native 回调入口，由 JNI 层调用，不可直接调用 */
    public static void dispatchEventCallback(long instanceId, int state) {
        EventCallbackListener l = sEventCallbackListener;
        if (l != null) l.onEventCallback(instanceId, state);
    }

    /** 事件状态回调：InstanceState_Playing=0x03e3, InstanceState_Ended=0x03e5 */
    public interface EventCallbackListener {
        void onEventCallback(long instanceId, int state);
    }

    private static volatile EventCallbackListener sEventCallbackListener;

    /** 设置事件回调监听器（用于 WanosMusicPlayerNative 实现自动下一曲） */
    public static void setEventCallbackListener(EventCallbackListener listener) {
        sEventCallbackListener = listener;
    }

    /** 移除实例状态回调 */
    public static native int removeEventStateCallback(long instanceId);

    // ========== Playback Device & State ==========
    /** 静音/取消静音 */
    public static native void setMute(boolean mute);

    /** 对象衰减增益（护栏静音 / unmute 爬坡，对齐 iOS SetGameObjAttenuationGain）。 */
    public static native void setGameObjAttenuationGain(long objId, float gain);

    /** 开关对象衰减。enable=false 时还原为引擎默认增益。 */
    public static native void setGameObjAttenuationGainEnable(long objId, boolean enable);

    /** 重置播放设备 */
    public static native int resetPlaybackDevice();

    /** 设置播放设备，playbackInfoJson 如 {"play_layout":0,"device_id":"default"} */
    public static native int setPlaybackDevice(String playbackInfoJson);

    /** 获取播放状态：0=Uninitialized, 1=Stopped, 2=Started, 3=Starting, 4=Stopping */
    public static native int getPlaybackState();

    /** 便捷：是否正在播放 */
    public static boolean isPlaying() {
        return getPlaybackState() == 2; // Started
    }

    // ========== NowPlaying（锁屏/通知栏元数据，对齐 iOS UpdateNowPlayingInfo）==========
    /** 同应用内接收：曲名、封面路径、是否播放 */
    public static final String ACTION_NOW_PLAYING_UPDATE = "com.wanos.app.health.action.NOW_PLAYING_UPDATE";
    public static final String EXTRA_NOW_PLAYING_TRACK_NAME = "trackName";
    public static final String EXTRA_NOW_PLAYING_COVER_URL = "coverUrl";
    public static final String EXTRA_NOW_PLAYING_IS_PLAYING = "isPlaying";

    /** 注入的 Context，建议使用 Application 以便后台可用 */
    private static volatile Context sContext;

    /** MediaSession 用于更新锁屏/通知栏 NowPlaying 元数据，并接收播放/暂停/停止传输控制 */
    private static MediaSession sMediaSession;

    /** 保护 sMediaSession 判空→使用的跨线程竞态（Unity render / main / 清卡）。 */
    private static final Object sSessionLock = new Object();

    /**
     * 播放会话闸：登出 / 切 Login 拆除期置 false，拒绝一切 NowPlaying 推送，
     * 避免残留曲名在主线程拥堵时误拉起 Media FGS → ForegroundServiceDidNotStartInTimeException。
     */
    private static volatile boolean sPlaybackActive = true;

    /** 最近一次推到 MediaSession 的曲名 / 播放态，供 FGS MediaStyle 通知读取。 */
    private static volatile String sLastTrackTitle = "";
    private static volatile boolean sLastIsPlaying;
    /** 降采样后的封面，供 MediaStyle setLargeIcon（部分 OEM 负一屏读通知大图而非 Metadata）。 */
    private static volatile Bitmap sLastArtBitmap;
    private static final int ARTWORK_MAX_EDGE = 512;
    private static final String DEFAULT_ARTWORK_ASSET = "WanosAudio/Artwork/boluo.png";

    private static final Handler sMainHandler = new Handler(Looper.getMainLooper());

    /**
     * MediaSession 回调专用线程。禁止用主线程：华为等 OEM 后台会 throttle 主 Looper，
     * 负一屏/媒体键 post 到 main 后迟迟不跑，表现为「点了没反应」。
     */
    private static final Handler sSessionCallbackHandler;

    static {
        HandlerThread t = new HandlerThread("BoluoMediaSessionCb");
        t.start();
        sSessionCallbackHandler = new Handler(t.getLooper());
    }

    /**
     * 通知栏/锁屏/负一屏媒体传输控制回调（play/pause/stop/skip）。
     * 与 iOS MPRemoteCommandCenter（含 next/previous）语义对齐：由 WanosMusicPlayerNative
     * 实现并驱动 WIAEngine 静音/续播/切歌。
     */
    public interface TransportControlListener {
        void onTransportPlay();
        void onTransportPause();
        void onTransportStop();
        void onTransportSkipToNext();
        void onTransportSkipToPrevious();
    }

    private static volatile TransportControlListener sTransportControlListener;

    /** 设置传输控制监听器（用于 WanosMusicPlayerNative 响应通知栏/锁屏的播放/暂停/停止）。 */
    public static void setTransportControlListener(TransportControlListener listener) {
        sTransportControlListener = listener;
    }

    /**
     * FGS MediaStyle 绑定用。Session 未建时返回 null（通知仍可走显式 ACTION）。
     */
    public static MediaSession.Token getMediaSessionToken() {
        MediaSession session = sMediaSession;
        if (session == null) return null;
        try {
            return session.getSessionToken();
        } catch (Exception e) {
            Log.w(TAG, "getMediaSessionToken failed: " + e.getMessage());
            return null;
        }
    }

    public static String getLastTrackTitle() {
        return sLastTrackTitle != null ? sLastTrackTitle : "";
    }

    public static boolean getLastIsPlaying() {
        return sLastIsPlaying;
    }

    /** 播放会话闸是否打开（登出拆除期为 false）。 */
    public static boolean isPlaybackActive() {
        return sPlaybackActive;
    }

    /**
     * 开关播放会话闸。false：释放 MediaSession、停 FGS，后续 updateNowPlayingInfo 直接 return。
     * 进岛 Arm / InitializeAfterLogin 再置 true。
     */
    public static void setPlaybackActive(boolean active) {
        long t0 = SystemClock.elapsedRealtime();
        if (active) {
            sPlaybackActive = true;
            Log.i(TAG, "setPlaybackActive: true");
            Log.i(TAG, "[Hitch] setPlaybackActive active=1 ms="
                    + (SystemClock.elapsedRealtime() - t0)
                    + " thread=" + Thread.currentThread().getName());
            return;
        }
        sPlaybackActive = false;
        sArtKey = null;
        sArtBitmap = null;
        synchronized (sSessionLock) {
            sLastTrackTitle = "";
            sLastIsPlaying = false;
            sLastArtBitmap = null;
            if (sMediaSession != null) {
                try {
                    sMediaSession.setActive(false);
                    sMediaSession.release();
                } catch (Exception e) {
                    Log.w(TAG, "setPlaybackActive release session: " + e.getMessage());
                }
                sMediaSession = null;
            }
        }
        Context ctx = getContext();
        if (ctx != null) {
            try {
                WanosMediaPlaybackForegroundService.stop(ctx);
            } catch (Throwable t) {
                Log.w(TAG, "setPlaybackActive stop FGS: " + t.getMessage());
            }
            sendNowPlayingBroadcast(ctx, "", "", false);
        }
        Log.i(TAG, "setPlaybackActive: false — session cleared, FGS stopped");
        Log.i(TAG, "[Hitch] setPlaybackActive active=0 ms="
                + (SystemClock.elapsedRealtime() - t0)
                + " thread=" + Thread.currentThread().getName());
    }

    /** FGS / 负一屏大图；可能为 null（解码失败时系统会回退 App 图标）。 */
    public static Bitmap getLastArtBitmap() {
        return sLastArtBitmap;
    }

    /**
     * 解析 ACTION_MEDIA_BUTTON / MediaSession onMediaButtonEvent 的 KeyEvent。
     * @return true 表示已消费。
     */
    public static boolean dispatchFromMediaButtonIntent(Intent mediaButtonIntent) {
        if (mediaButtonIntent == null) return false;
        KeyEvent ke;
        try {
            if (Build.VERSION.SDK_INT >= 33) {
                ke = mediaButtonIntent.getParcelableExtra(Intent.EXTRA_KEY_EVENT, KeyEvent.class);
            } else {
                ke = mediaButtonIntent.getParcelableExtra(Intent.EXTRA_KEY_EVENT);
            }
        } catch (Exception e) {
            Log.w(TAG, "dispatchFromMediaButtonIntent extra failed: " + e.getMessage());
            return false;
        }
        if (ke == null || ke.getAction() != KeyEvent.ACTION_DOWN) return false;
        int code = ke.getKeyCode();
        Log.i(TAG, "MediaButton keyCode=" + KeyEvent.keyCodeToString(code));
        switch (code) {
            case KeyEvent.KEYCODE_MEDIA_PLAY:
                dispatchTransportPlay();
                return true;
            case KeyEvent.KEYCODE_MEDIA_PAUSE:
            case KeyEvent.KEYCODE_MEDIA_STOP:
                dispatchTransportPause();
                return true;
            case KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE:
            case KeyEvent.KEYCODE_HEADSETHOOK:
                if (sLastIsPlaying) dispatchTransportPause();
                else dispatchTransportPlay();
                return true;
            case KeyEvent.KEYCODE_MEDIA_NEXT:
                dispatchTransportSkipToNext();
                return true;
            case KeyEvent.KEYCODE_MEDIA_PREVIOUS:
                dispatchTransportSkipToPrevious();
                return true;
            default:
                return false;
        }
    }

    /** Session 未 active / OEM 丢 Callback 时的兜底：媒体键进 FGS 再回传。 */
    private static void installMediaButtonReceiver(Context ctx) {
        try {
            Intent i = new Intent(ctx, WanosMediaPlaybackForegroundService.class);
            i.setAction(Intent.ACTION_MEDIA_BUTTON);
            int flags = PendingIntent.FLAG_UPDATE_CURRENT;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                flags |= PendingIntent.FLAG_IMMUTABLE;
            }
            PendingIntent pi;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                pi = PendingIntent.getForegroundService(ctx, /*requestCode*/ 9102, i, flags);
            } else {
                pi = PendingIntent.getService(ctx, 9102, i, flags);
            }
            sMediaSession.setMediaButtonReceiver(pi);
            Log.i(TAG, "MediaSession mediaButtonReceiver installed → FGS");
        } catch (Exception e) {
            Log.w(TAG, "installMediaButtonReceiver failed: " + e.getMessage());
        }
    }

    public static void dispatchTransportPlay() {
        TransportControlListener l = sTransportControlListener;
        Log.i(TAG, "dispatchTransportPlay listener=" + (l != null));
        if (l != null) l.onTransportPlay();
    }

    public static void dispatchTransportPause() {
        TransportControlListener l = sTransportControlListener;
        Log.i(TAG, "dispatchTransportPause listener=" + (l != null));
        if (l != null) l.onTransportPause();
    }

    public static void dispatchTransportStop() {
        TransportControlListener l = sTransportControlListener;
        Log.i(TAG, "dispatchTransportStop listener=" + (l != null));
        if (l != null) l.onTransportStop();
    }

    public static void dispatchTransportSkipToNext() {
        TransportControlListener l = sTransportControlListener;
        Log.i(TAG, "dispatchTransportSkipToNext listener=" + (l != null));
        if (l != null) l.onTransportSkipToNext();
    }

    public static void dispatchTransportSkipToPrevious() {
        TransportControlListener l = sTransportControlListener;
        Log.i(TAG, "dispatchTransportSkipToPrevious listener=" + (l != null));
        if (l != null) l.onTransportSkipToPrevious();
    }

    /** 反射获取的 Context 缓存，避免重复反射 */
    private static volatile Context sContextCache;

    /**
     * 注入 Context（可选）。未注入时自动通过反射获取，无需修改 BoluoPlugin。
     */
    public static void setContext(Context ctx) {
        sContext = (ctx != null) ? ctx.getApplicationContext() : null;
        sContextCache = sContext;
    }

    /**
     * 更新锁屏/通知栏 NowPlaying 元数据（曲名、封面、播放状态）
     * 无 Unity 编译依赖，支持后台调用。Context 自动获取，无需 BoluoPlugin 配合。
     * 后台场景：直接在调用线程执行，避免 post 到主线程被系统延迟调度。
     *
     * @param trackName 曲名，空则用默认
     * @param coverUrl  封面路径（本地文件），可为 null
     * @param isPlaying 是否正在播放，用于更新播放/暂停图标
     */
    public static void updateNowPlayingInfo(String trackName, String coverUrl, boolean isPlaying) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.LOLLIPOP) return;
        if (!sPlaybackActive) {
            Log.d(TAG, "updateNowPlayingInfo skipped: playback inactive");
            return;
        }
        Context ctx = getContext();
        if (ctx == null) {
            Log.w(TAG, "updateNowPlayingInfo: no Context, call setContext() first or ensure in app process");
            return;
        }
        // 直接在调用线程执行：app 后台时主线程可能被 throttle，post 会严重延迟
        // MediaSession 支持从任意线程调用，适合后台媒体播放
        updateNowPlayingInfoImpl(ctx, trackName, coverUrl, isPlaying);
    }

    /** 获取 Context：注入优先，否则反射获取并缓存。无需修改 BoluoPlugin 即可工作。 */
    private static Context getContext() {
        if (sContext != null) return sContext;
        if (sContextCache != null) return sContextCache;
        Context ctx = getContextFromActivityThread();
        if (ctx == null) ctx = getContextFromUnityPlayer();
        if (ctx != null) sContextCache = ctx;
        return ctx;
    }

    private static Context getContextFromActivityThread() {
        try {
            Object app = Class.forName("android.app.ActivityThread")
                    .getMethod("currentApplication")
                    .invoke(null);
            if (app instanceof Context) {
                return ((Context) app).getApplicationContext();
            }
        } catch (Exception e) {
            Log.d(TAG, "ActivityThread fallback failed", e);
        }
        return null;
    }

    /** Unity 环境备用：反射获取 UnityPlayer.currentActivity，无编译依赖 */
    private static Context getContextFromUnityPlayer() {
        try {
            Object activity = Class.forName("com.unity3d.player.UnityPlayer")
                    .getField("currentActivity")
                    .get(null);
            if (activity != null && activity instanceof Context) {
                return ((Context) activity).getApplicationContext();
            }
        } catch (Exception e) {
            Log.d(TAG, "UnityPlayer fallback failed (non-Unity?)", e);
        }
        return null;
    }

    private static void sendNowPlayingBroadcast(Context ctx, String trackName, String coverUrl, boolean isPlaying) {
        Intent intent = new Intent(ACTION_NOW_PLAYING_UPDATE);
        intent.setPackage(ctx.getPackageName());
        intent.putExtra(EXTRA_NOW_PLAYING_TRACK_NAME, trackName != null ? trackName : "");
        intent.putExtra(EXTRA_NOW_PLAYING_COVER_URL, coverUrl != null ? coverUrl : "");
        intent.putExtra(EXTRA_NOW_PLAYING_IS_PLAYING, isPlaying);
        ctx.sendBroadcast(intent);
    }

    private static void updateNowPlayingInfoImpl(Context ctx, String trackName, String coverUrl, boolean isPlaying) {
        synchronized (sSessionLock) {
            if (!sPlaybackActive) {
                Log.d(TAG, "updateNowPlayingInfoImpl skipped: playback inactive");
                return;
            }
            try {
                boolean allEmpty = (trackName == null || trackName.isEmpty())
                        && (coverUrl == null || coverUrl.isEmpty());
                if (allEmpty) {
                    sLastTrackTitle = "";
                    sLastIsPlaying = false;
                    sLastArtBitmap = null;
                    if (sMediaSession != null) {
                        sMediaSession.setActive(false);
                        sMediaSession.release();
                        sMediaSession = null;
                    }
                    sendNowPlayingBroadcast(ctx, "", "", false);
                    Log.i(TAG, "updateNowPlayingInfo: all empty, cleared");
                    return;
                }

                MediaSession session = sMediaSession;
                if (session == null) {
                    session = new MediaSession(ctx, TAG);
                    session.setFlags(MediaSession.FLAG_HANDLES_MEDIA_BUTTONS | MediaSession.FLAG_HANDLES_TRANSPORT_CONTROLS);
                    // 注册传输控制回调。没有 Callback 时通知栏/锁屏/负一屏按钮会被系统丢弃。
                    // 必须用独立 Handler：后台主线程被 OEM throttle 时 media key 会「送到 session 名但业务不跑」。
                    session.setCallback(new MediaSession.Callback() {
                        @Override
                        public void onPlay() {
                            Log.i(TAG, "MediaSession onPlay");
                            dispatchTransportPlay();
                        }

                        @Override
                        public void onPause() {
                            Log.i(TAG, "MediaSession onPause");
                            dispatchTransportPause();
                        }

                        @Override
                        public void onStop() {
                            Log.i(TAG, "MediaSession onStop");
                            dispatchTransportStop();
                        }

                        @Override
                        public void onSkipToNext() {
                            Log.i(TAG, "MediaSession onSkipToNext");
                            dispatchTransportSkipToNext();
                        }

                        @Override
                        public void onSkipToPrevious() {
                            Log.i(TAG, "MediaSession onSkipToPrevious");
                            dispatchTransportSkipToPrevious();
                        }

                        @Override
                        public boolean onMediaButtonEvent(Intent mediaButtonIntent) {
                            // OEM 负一屏常只走 KEYCODE_MEDIA_*，不保证拆成 onPlay/onSkipToNext。
                            if (dispatchFromMediaButtonIntent(mediaButtonIntent)) {
                                return true;
                            }
                            return super.onMediaButtonEvent(mediaButtonIntent);
                        }
                    }, sSessionCallbackHandler);
                    installMediaButtonReceiver(ctx);
                    sMediaSession = session;
                }

                String title = (trackName == null || trackName.isEmpty()) ? "波萝" : trackName;
                sLastTrackTitle = title;
                sLastIsPlaying = isPlaying;
                Log.d(TAG, "updateNowPlayingInfoImpl: title=" + title + " isPlaying=" + isPlaying);
                MediaMetadata.Builder metaBuilder = new MediaMetadata.Builder()
                        .putString(MediaMetadata.METADATA_KEY_TITLE, title)
                        .putString(MediaMetadata.METADATA_KEY_DISPLAY_TITLE, title)
                        .putLong(MediaMetadata.METADATA_KEY_DURATION, -1);

                Bitmap art = artworkForCover(ctx, coverUrl);
                sLastArtBitmap = art;
                if (art != null) {
                    // 部分 OEM 负一屏读 ART，部分读 ALBUM_ART；两份都写避免默认系统图标。
                    // 必须降采样：2000² ARGB 约 16MB，跨进程 Binder 会被丢弃，负一屏就回退 App 图标。
                    metaBuilder.putBitmap(MediaMetadata.METADATA_KEY_ART, art);
                    metaBuilder.putBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART, art);
                    metaBuilder.putBitmap(MediaMetadata.METADATA_KEY_DISPLAY_ICON, art);
                } else {
                    Log.w(TAG, "updateNowPlayingInfo: artwork decode failed coverUrl=" + coverUrl);
                }
                session.setMetadata(metaBuilder.build());

                // 播放/暂停 + 上一曲/下一曲；paused 时 rate=0，避免 OEM 把播放态图标画反。
                int state = isPlaying ? PlaybackState.STATE_PLAYING : PlaybackState.STATE_PAUSED;
                float rate = isPlaying ? 1.0f : 0.0f;
                long actions = PlaybackState.ACTION_PLAY | PlaybackState.ACTION_PAUSE
                        | PlaybackState.ACTION_PLAY_PAUSE | PlaybackState.ACTION_STOP
                        | PlaybackState.ACTION_SKIP_TO_NEXT | PlaybackState.ACTION_SKIP_TO_PREVIOUS;
                PlaybackState.Builder stateBuilder = new PlaybackState.Builder()
                        .setActions(actions)
                        .setState(state, PlaybackState.PLAYBACK_POSITION_UNKNOWN, rate);
                session.setPlaybackState(stateBuilder.build());
                session.setActive(true);

                sendNowPlayingBroadcast(ctx, trackName, coverUrl, isPlaying);
                // 仅正在播放时拉/刷 FGS。暂停态 startForegroundService 会在登出切 Login
                // 主线程拥堵时触发 ForegroundServiceDidNotStartInTimeException。
                if (isPlaying) {
                    try {
                        WanosMediaPlaybackForegroundService.notifyNowPlayingChanged(ctx);
                    } catch (Throwable t) {
                        Log.d(TAG, "notifyNowPlayingChanged skipped: " + t.getMessage());
                    }
                }
            } catch (Exception e) {
                Log.e(TAG, "updateNowPlayingInfo failed", e);
            }
        }
    }

    private static String sArtKey;
    private static Bitmap sArtBitmap;

    /** 同一 coverUrl 只解码+缩放一次。Arm 与 Publish 会连打三次 NowPlaying。 */
    private static Bitmap artworkForCover(Context ctx, String coverUrl) {
        String key = coverUrl == null ? "" : coverUrl;
        if (sArtBitmap != null && !sArtBitmap.isRecycled() && key.equals(sArtKey)) {
            Log.i(TAG, "[Hitch] decodeCoverBitmap cacheHit thread="
                    + Thread.currentThread().getName());
            return sArtBitmap;
        }

        long t0 = SystemClock.elapsedRealtime();
        Bitmap scaled = scaleArtwork(decodeCoverBitmapBody(ctx, coverUrl));
        long ms = SystemClock.elapsedRealtime() - t0;
        Log.i(TAG, "[Hitch] decodeCoverBitmap cacheMiss ms=" + ms
                + " thread=" + Thread.currentThread().getName());
        sArtKey = key;
        sArtBitmap = scaled;
        return scaled;
    }

    /**
     * 解码 NowPlaying 封面。Android StreamingAssets 常为 jar:file://...!/assets/...，
     * BitmapFactory.decodeFile 会失败；此时从 AssetManager 读默认/相对路径。
     */
    private static Bitmap decodeCoverBitmapBody(Context ctx, String coverUrl) {
        if (coverUrl != null && !coverUrl.isEmpty()) {
            // 直接相对 asset 路径（Unity 侧推荐传这个，避开 jar Path.Combine）
            if (!coverUrl.contains("://") && !coverUrl.startsWith("/")
                    && coverUrl.contains("WanosAudio/")) {
                Bitmap direct = decodeAssetBitmap(ctx, coverUrl.replace('\\', '/'));
                if (direct != null) return direct;
            }
            if (!coverUrl.contains("jar:") && !coverUrl.contains("!/")) {
                String filePath = coverUrl.startsWith("file://") ? coverUrl.substring(7) : coverUrl;
                Bitmap fromFile = BitmapFactory.decodeFile(filePath);
                if (fromFile != null) return fromFile;
            }
            String assetRel = assetRelativePathFromCoverUrl(coverUrl);
            if (assetRel != null) {
                Bitmap fromAsset = decodeAssetBitmap(ctx, assetRel);
                if (fromAsset != null) return fromAsset;
            }
        }
        return decodeAssetBitmap(ctx, DEFAULT_ARTWORK_ASSET);
    }

    /** jar:...!/assets/WanosAudio/... → WanosAudio/...；已是相对路径则原样返回。 */
    private static String assetRelativePathFromCoverUrl(String coverUrl) {
        if (coverUrl == null || coverUrl.isEmpty()) return null;
        String normalized = coverUrl.replace('\\', '/');
        int bang = normalized.indexOf("!/");
        if (bang >= 0) {
            String after = normalized.substring(bang + 2);
            if (after.startsWith("assets/")) after = after.substring("assets/".length());
            return after.isEmpty() ? null : after;
        }
        if (normalized.contains("WanosAudio/Artwork/")) {
            int idx = normalized.indexOf("WanosAudio/Artwork/");
            return normalized.substring(idx);
        }
        if (normalized.equals(DEFAULT_ARTWORK_ASSET) || normalized.endsWith("/" + DEFAULT_ARTWORK_ASSET)) {
            return DEFAULT_ARTWORK_ASSET;
        }
        return null;
    }

    private static Bitmap decodeAssetBitmap(Context ctx, String assetPath) {
        if (ctx == null || assetPath == null || assetPath.isEmpty()) return null;
        try (InputStream is = ctx.getAssets().open(assetPath)) {
            return BitmapFactory.decodeStream(is);
        } catch (Exception e) {
            Log.d(TAG, "decodeAssetBitmap failed path=" + assetPath + " msg=" + e.getMessage());
            return null;
        }
    }

    /** 锁屏 / MediaSession 封面边长上限，避免 Binder 丢图。 */
    private static Bitmap scaleArtwork(Bitmap src) {
        if (src == null) return null;
        int w = src.getWidth();
        int h = src.getHeight();
        int edge = Math.max(w, h);
        if (edge <= ARTWORK_MAX_EDGE) return src;
        float scale = ARTWORK_MAX_EDGE / (float) edge;
        int nw = Math.max(1, Math.round(w * scale));
        int nh = Math.max(1, Math.round(h * scale));
        try {
            Bitmap scaled = Bitmap.createScaledBitmap(src, nw, nh, true);
            if (scaled != src) src.recycle();
            return scaled;
        } catch (Exception e) {
            Log.w(TAG, "scaleArtwork failed: " + e.getMessage());
            return src;
        }
    }
}
