package com.gathin.tvhome;

import android.app.Activity;
import android.content.Intent;
import android.media.tv.TvContract;
import android.media.tv.TvInputInfo;
import android.media.tv.TvInputManager;
import android.media.tv.TvView;
import android.net.Uri;
import android.os.Bundle;
import android.view.KeyEvent;
import android.view.ViewGroup;
import android.widget.FrameLayout;
import android.widget.Toast;

/**
 * Plays a passthrough input (HDMI / AV) with {@link TvView} when the TV has no Live TV app
 * that answers ACTION_VIEW on the channel URI (Xiaomi PatchWall / MiTV is the usual case).
 */
public class PassthroughActivity extends Activity {
    public static final String EXTRA_INPUT_ID = "input_id";

    private TvView tvView;
    private String inputId;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        inputId = resolveHardwareInputId(inputIdFrom(getIntent()));
        if (inputId == null || inputId.isEmpty()) {
            Toast.makeText(this, R.string.not_available, Toast.LENGTH_SHORT).show();
            finish();
            return;
        }

        FrameLayout root = new FrameLayout(this);
        root.setLayoutParams(new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        root.setBackgroundColor(0xFF000000);
        tvView = new TvView(this);
        root.addView(tvView, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        setContentView(root);

        Uri channel = TvContract.buildChannelUriForPassthroughInput(inputId);
        try {
            tvView.tune(inputId, channel);
        } catch (Exception e) {
            Toast.makeText(this, R.string.not_available, Toast.LENGTH_SHORT).show();
            finish();
        }
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        String next = resolveHardwareInputId(inputIdFrom(intent));
        if (next == null || next.isEmpty() || tvView == null) return;
        inputId = next;
        try {
            tvView.tune(inputId, TvContract.buildChannelUriForPassthroughInput(inputId));
        } catch (Exception ignored) {
        }
    }

    @Override
    protected void onStop() {
        if (tvView != null) {
            try {
                tvView.reset();
            } catch (Exception ignored) {
            }
        }
        super.onStop();
    }

    @Override
    public boolean onKeyDown(int keyCode, KeyEvent event) {
        if (keyCode == KeyEvent.KEYCODE_BACK) {
            finish();
            return true;
        }
        return super.onKeyDown(keyCode, event);
    }

    private static String inputIdFrom(Intent intent) {
        if (intent == null) return null;
        String id = intent.getStringExtra(EXTRA_INPUT_ID);
        if (id != null && !id.isEmpty()) return id;
        Uri data = intent.getData();
        if (data == null) return null;
        // content://android.media.tv/passthrough/<urlencoded-input-id>
        String path = data.getLastPathSegment();
        return path == null ? null : Uri.decode(path);
    }

    /** Walk up CEC device rows to the HDMI/AV port the TvView must tune. */
    private String resolveHardwareInputId(String inputId) {
        if (inputId == null) return null;
        TvInputManager tim = (TvInputManager) getSystemService(TV_INPUT_SERVICE);
        if (tim == null) return inputId;
        java.util.HashMap<String, TvInputInfo> byId = new java.util.HashMap<>();
        for (TvInputInfo info : tim.getTvInputList()) byId.put(info.getId(), info);
        String id = inputId;
        for (int i = 0; i < 5; i++) {
            TvInputInfo info = byId.get(id);
            if (info == null || info.getParentId() == null) return id;
            id = info.getParentId();
        }
        return id;
    }
}
