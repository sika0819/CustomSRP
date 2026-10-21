package com.wanos.app.health;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.media.session.MediaSession;
import android.os.Build;
import android.os.IBinder;
import android.util.Log;

/**
 * 媒体播放前台服务（FOREGROUND_SERVICE_MEDIA_PLAYBACK）。
 * 后台播音乐时维持进程，满足 Android 8+ / 14 媒体后台规范。
 * 产品口径：蓝牙断开不因此服务暂停；仅随播放 start/stop。
 * 创维负一屏常绑媒体通知：须 MediaStyle + MediaSession token + 上一曲/播放暂停/下一曲。
 */
public class WanosMediaPlaybackForegroundService extends Service {

    private static final String TAG = "WanosMediaFgs";
    private static final String CHANNEL_ID = "boluo_media_playback";
    private static final int NOTIFICATION_ID = 9101;
    public static final String ACTION_START = "com.wanos.app.health.action.MEDIA_FGS_START";
    public static final String ACTION_STOP = "com.wanos.app.health.action.MEDIA_FGS_STOP";
    public static final String ACTION_TRANSPORT_PLAY = "com.wanos.app.health.action.MEDIA_TRANSPORT_PLAY";
    public static final String ACTION_TRANSPORT_PAUSE = "com.wanos.app.health.action.MEDIA_TRANSPORT_PAUSE";
    public static final String ACTION_TRANSPORT_NEXT = "com.wanos.app.health.action.MEDIA_TRANSPORT_NEXT";
    public static final String ACTION_TRANSPORT_PREV = "com.wanos.app.health.action.MEDIA_TRANSPORT_PREV";

    private static volatile boolean sRunning;

    /** 探针只读，不改变 start/stop。 */
    public static boolean isRunning() {
        return sRunning;
    }

    public static void start(Context context) {
        if (context == null) return;
        Intent i = new Intent(context, WanosMediaPlaybackForegroundService.class);
        i.setAction(ACTION_START);
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(i);
            } else {
                context.startService(i);
            }
        } catch (Exception e) {
            Log.w(TAG, "start FGS failed: " + e.getMessage());
        }
    }

    public static void stop(Context context) {
        if (context == null) return;
        Intent i = new Intent(context, WanosMediaPlaybackForegroundService.class);
        i.setAction(ACTION_STOP);
        try {
            context.startService(i);
        } catch (Exception e) {
            Log.w(TAG, "stop FGS failed: " + e.getMessage());
        }
    }

    /** NowPlaying 更新后刷新 MediaStyle 标题/按钮。已在跑用 startService，避免后台再 startForegroundService。 */
    public static void notifyNowPlayingChanged(Context context) {
        if (context == null) return;
        if (!sRunning) {
            start(context);
            return;
        }
        Intent i = new Intent(context, WanosMediaPlaybackForegroundService.class);
        i.setAction(ACTION_START);
        try {
            context.startService(i);
        } catch (Exception e) {
            Log.w(TAG, "notifyNowPlayingChanged startService failed: " + e.getMessage());
        }
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent != null ? intent.getAction() : ACTION_START;
        if (ACTION_STOP.equals(action)) {
            sRunning = false;
            stopForeground(true);
            stopSelf();
            return START_NOT_STICKY;
        }
        sRunning = true;
        dispatchTransportAction(action);
        ensureChannel();
        Notification notification = buildNotification();
        try {
            if (Build.VERSION.SDK_INT >= 29) {
                startForeground(NOTIFICATION_ID, notification,
                        ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK);
            } else {
                startForeground(NOTIFICATION_ID, notification);
            }
        } catch (Exception e) {
            Log.w(TAG, "startForeground failed: " + e.getMessage());
        }
        return START_STICKY;
    }

    private void dispatchTransportAction(String action) {
        if (ACTION_TRANSPORT_PLAY.equals(action)) {
            WIAEngineJNI.dispatchTransportPlay();
        } else if (ACTION_TRANSPORT_PAUSE.equals(action)) {
            WIAEngineJNI.dispatchTransportPause();
        } else if (ACTION_TRANSPORT_NEXT.equals(action)) {
            WIAEngineJNI.dispatchTransportSkipToNext();
        } else if (ACTION_TRANSPORT_PREV.equals(action)) {
            WIAEngineJNI.dispatchTransportSkipToPrevious();
        }
    }

    private void ensureChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return;
        NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        if (nm == null) return;
        NotificationChannel ch = new NotificationChannel(
                CHANNEL_ID, "波萝播放", NotificationManager.IMPORTANCE_LOW);
        ch.setDescription("后台音频播放");
        ch.setShowBadge(false);
        nm.createNotificationChannel(ch);
    }

    private Notification buildNotification() {
        Intent launch = getPackageManager().getLaunchIntentForPackage(getPackageName());
        PendingIntent contentPi = null;
        if (launch != null) {
            contentPi = PendingIntent.getActivity(
                    this, 0, launch,
                    PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        }
        Notification.Builder b;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            b = new Notification.Builder(this, CHANNEL_ID);
        } else {
            b = new Notification.Builder(this);
        }
        String title = WIAEngineJNI.getLastTrackTitle();
        if (title == null || title.isEmpty()) title = "波萝";
        boolean playing = WIAEngineJNI.getLastIsPlaying();
        b.setContentTitle(title)
                .setContentText(playing ? "正在播放" : "已暂停")
                .setSmallIcon(android.R.drawable.ic_media_play)
                .setOngoing(true)
                .setContentIntent(contentPi)
                .setVisibility(Notification.VISIBILITY_PUBLIC)
                .setCategory(Notification.CATEGORY_TRANSPORT)
                .addAction(transportAction(android.R.drawable.ic_media_previous, "上一曲", ACTION_TRANSPORT_PREV))
                .addAction(playing
                        ? transportAction(android.R.drawable.ic_media_pause, "暂停", ACTION_TRANSPORT_PAUSE)
                        : transportAction(android.R.drawable.ic_media_play, "播放", ACTION_TRANSPORT_PLAY))
                .addAction(transportAction(android.R.drawable.ic_media_next, "下一曲", ACTION_TRANSPORT_NEXT));

        Notification.MediaStyle style = new Notification.MediaStyle()
                .setShowActionsInCompactView(0, 1, 2);
        MediaSession.Token token = WIAEngineJNI.getMediaSessionToken();
        if (token != null) {
            style.setMediaSession(token);
        }
        b.setStyle(style);
        return b.build();
    }

    private Notification.Action transportAction(int icon, String title, String action) {
        Intent i = new Intent(this, WanosMediaPlaybackForegroundService.class);
        i.setAction(action);
        PendingIntent pi;
        int flags = PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            pi = PendingIntent.getForegroundService(this, action.hashCode(), i, flags);
        } else {
            pi = PendingIntent.getService(this, action.hashCode(), i, flags);
        }
        return new Notification.Action.Builder(icon, title, pi).build();
    }

    @Override
    public void onDestroy() {
        sRunning = false;
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
