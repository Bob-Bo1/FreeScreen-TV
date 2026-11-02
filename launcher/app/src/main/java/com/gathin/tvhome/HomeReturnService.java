package com.gathin.tvhome;

import android.accessibilityservice.AccessibilityService;
import android.content.Intent;
import android.util.Log;
import android.view.KeyEvent;
import android.view.accessibility.AccessibilityEvent;

/** Xiaomi firmware keeps its own HOME handler. Redirect only its main home window. */
public final class HomeReturnService extends AccessibilityService {
    @Override protected void onServiceConnected() {
        Log.i("HomeReturn", "connected");
    }

    private void openHome(String reason) {
        try {
            startActivity(new Intent(this, MainActivity.class)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_RESET_TASK_IF_NEEDED));
            Log.i("HomeReturn", "open: " + reason);
        } catch (RuntimeException error) {
            Log.e("HomeReturn", "open failed", error);
        }
    }

    @Override protected boolean onKeyEvent(KeyEvent event) {
        int key = event.getKeyCode();
        if (key != KeyEvent.KEYCODE_HOME && key != KeyEvent.KEYCODE_ALL_APPS) return false;
        if (event.getAction() == KeyEvent.ACTION_UP && !event.isCanceled()) openHome("key " + key);
        return true;
    }

    @Override public void onAccessibilityEvent(AccessibilityEvent event) {
        if (event.getEventType() == AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED
                && "com.mitv.tvhome".contentEquals(event.getPackageName() == null ? "" : event.getPackageName())
                && "com.mitv.tvhome.MainActivityUserMode".contentEquals(
                        event.getClassName() == null ? "" : event.getClassName())) {
            openHome("xiaomi home window");
        }
    }

    @Override public void onInterrupt() { }
}
