package com.wanos.app.health;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.util.Log;
import android.view.KeyEvent;

/**
 * MediaSession.setMediaButtonReceiver 必须指向 BroadcastReceiver。
 * 旧实现用 getForegroundService → dumpsys 里 mediaButtonReceiver=null，
 * Vivo OriginOS 负一屏 / Last VivoMediaButtonReceiver 不认，媒体栏空白。
 */
public class BoluoMediaButtonReceiver extends BroadcastReceiver {
    private static final String TAG = "BoluoMediaButton";

    @Override
    public void onReceive(Context context, Intent intent) {
        if (intent == null) return;
        if (!Intent.ACTION_MEDIA_BUTTON.equals(intent.getAction())) return;
        KeyEvent ke = null;
        try {
            if (android.os.Build.VERSION.SDK_INT >= 33) {
                ke = intent.getParcelableExtra(Intent.EXTRA_KEY_EVENT, KeyEvent.class);
            } else {
                ke = intent.getParcelableExtra(Intent.EXTRA_KEY_EVENT);
            }
        } catch (Exception e) {
            Log.w(TAG, "EXTRA_KEY_EVENT failed: " + e.getMessage());
        }
        if (ke == null || ke.getAction() != KeyEvent.ACTION_DOWN) return;
        Log.i(TAG, "media button " + KeyEvent.keyCodeToString(ke.getKeyCode()));
        Intent service = new Intent(context, WanosMediaPlaybackForegroundService.class);
        service.setAction(Intent.ACTION_MEDIA_BUTTON);
        service.putExtra(Intent.EXTRA_KEY_EVENT, ke);
        try {
            if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.O) {
                context.startForegroundService(service);
            } else {
                context.startService(service);
            }
        } catch (Exception e) {
            // FGS 起不来时仍走 JNI 直派，避免键事件全丢。
            Log.w(TAG, "start FGS failed, dispatch direct: " + e.getMessage());
            WIAEngineJNI.dispatchFromMediaButtonIntent(intent);
        }
    }
}
