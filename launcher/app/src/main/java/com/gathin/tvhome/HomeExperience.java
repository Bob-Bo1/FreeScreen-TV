package com.gathin.tvhome;

import android.app.Activity;
import android.content.SharedPreferences;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Color;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.TextClock;
import java.io.InputStream;
import java.util.Random;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Local wallpaper and home-only idle screen; no global screen or key service changes. */
final class HomeExperience {
    static final String[] FILES = {"01-mountain.jpg", "02-dunes.jpg", "03-aurora.jpg", "04-sunset.jpg"};
    static final String[] NAMES = {"雪山秋湖", "金色沙丘", "冰湖极光", "海边落日"};
    private final Activity activity;
    private final FrameLayout root;
    private final View ambient, sky;
    private final SharedPreferences prefs;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService decoder = Executors.newSingleThreadExecutor();
    private final java.util.function.BooleanSupplier canIdle;
    private ImageView background;
    private FrameLayout screen;
    private TextClock clock;
    private boolean active, running, consumeUp;
    private int imageToken, photoIndex;
    private final Random random = new Random();
    private final Runnable idle = new Runnable() { public void run() {
        if (!active) return;
        if (activity.hasWindowFocus() && canIdle.getAsBoolean()) showScreen();
        else handler.postDelayed(this, 1000);
    }};
    private final Runnable photoTick = new Runnable() { public void run() {
        if (screen == null) return;
        ImageView image = screen.findViewWithTag("photo");
        if (image != null) load(image, FILES[photoIndex++ % FILES.length]);
        handler.postDelayed(this, 30000);
    }};
    private final Runnable clockTick = new Runnable() { public void run() {
        if (screen == null || clock == null) return;
        int w = root.getWidth(), h = root.getHeight();
        if (w > 0 && h > 0) {
            int margin = (int)(50 * activity.getResources().getDisplayMetrics().density);
            FrameLayout.LayoutParams lp = (FrameLayout.LayoutParams) clock.getLayoutParams();
            lp.gravity = Gravity.TOP | Gravity.LEFT;
            lp.leftMargin = margin + random.nextInt(Math.max(1, w - clock.getWidth() - margin * 2));
            lp.topMargin = margin + random.nextInt(Math.max(1, h - clock.getHeight() - margin * 2));
            clock.setLayoutParams(lp);
        }
        handler.postDelayed(this, 60000);
    }};
    HomeExperience(Activity activity, FrameLayout root, View ambient, View sky, java.util.function.BooleanSupplier canIdle) {
        this.activity=activity; this.root=root; this.ambient=ambient; this.sky=sky; this.canIdle=canIdle;
        prefs=activity.getSharedPreferences("home_experience",Activity.MODE_PRIVATE);
    }
    void start() { active=true; applyWallpaper(); reset(); }
    void stop() { active=false; handler.removeCallbacks(idle); hideScreen(); }
    void close() { stop(); imageToken++; decoder.shutdownNow(); }
    void reset() { handler.removeCallbacks(idle); if (active && screen == null) handler.postDelayed(idle,600000); }
    String wallpaper() { return prefs.getString("wallpaper", "brand"); }
    String screenMode() { return prefs.getString("screensaver", "clock"); }
    void selectWallpaper(String file) { prefs.edit().putString("wallpaper",file).apply(); applyWallpaper(); reset(); }
    void selectScreen(String mode) { prefs.edit().putString("screensaver",mode).apply(); reset(); }
    void applyWallpaper() {
        String name=wallpaper(); boolean use=!"brand".equals(name);
        boolean found=false; for(String f:FILES) if(f.equals(name)) found=true;
        use &= found;
        if (!use) { imageToken++; if(background!=null) background.setVisibility(View.GONE); ambient.setVisibility(View.VISIBLE); sky.setVisibility(View.VISIBLE); return; }
        if (background==null) {
            background=new ImageView(activity); background.setScaleType(ImageView.ScaleType.CENTER_CROP);
            background.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
            int position=root.indexOfChild(sky); root.addView(background,Math.max(0,position),new FrameLayout.LayoutParams(-1,-1));
        }
        ambient.animate().cancel(); ambient.setVisibility(View.GONE); sky.setVisibility(View.GONE);
        background.setVisibility(View.VISIBLE); load(background,name);
    }
    private void load(ImageView target, String name) {
        int token=++imageToken;
        decoder.execute(() -> {
            Bitmap result=null;
            try (InputStream input=activity.getAssets().open("wallpapers/"+name)) {
                BitmapFactory.Options opt=new BitmapFactory.Options();
                int max=Math.max(activity.getResources().getDisplayMetrics().widthPixels,1920);
                opt.inSampleSize=max<=1920?2:1; result=BitmapFactory.decodeStream(input,null,opt);
            } catch (Exception ignored) { }
            final Bitmap loaded=result;
            handler.post(() -> { if (token!=imageToken || activity.isDestroyed()) { if(loaded!=null) loaded.recycle(); return; }
                if (loaded!=null) target.setImageBitmap(loaded);
            });
        });
    }
    void preview() { showScreen(); }
    private void showScreen() {
        if (screen!=null || !active) return;
        handler.removeCallbacks(idle);
        screen=new FrameLayout(activity); screen.setBackgroundColor(Color.BLACK);
        screen.setClickable(true); root.addView(screen,new FrameLayout.LayoutParams(-1,-1));
        if ("photo".equals(screenMode())) {
            ImageView image=new ImageView(activity); image.setTag("photo"); image.setScaleType(ImageView.ScaleType.CENTER_CROP);
            screen.addView(image,new FrameLayout.LayoutParams(-1,-1)); photoIndex=0; photoTick.run();
        } else {
            clock=new TextClock(activity); clock.setTextColor(0xFFBBBBBB); clock.setTextSize(64); clock.setFormat24Hour("HH:mm"); clock.setFormat12Hour("h:mm");
            FrameLayout.LayoutParams lp=new FrameLayout.LayoutParams(-2,-2,Gravity.CENTER); screen.addView(clock,lp);
            clock.post(() -> { if(screen!=null) clockTick.run(); });
        }
        running=true;
    }
    boolean key(KeyEvent event) {
        if (consumeUp && event.getAction()==KeyEvent.ACTION_UP) { consumeUp=false; return true; }
        if (screen==null) { if(event.getAction()==KeyEvent.ACTION_DOWN) reset(); return false; }
        if(event.getAction()!=KeyEvent.ACTION_DOWN) return true;
        int code=event.getKeyCode(); hideScreen(); reset();
        if (code==KeyEvent.KEYCODE_VOLUME_UP || code==KeyEvent.KEYCODE_VOLUME_DOWN || code==KeyEvent.KEYCODE_VOLUME_MUTE || code==KeyEvent.KEYCODE_POWER || code==KeyEvent.KEYCODE_HOME) return false;
        consumeUp=true; return true;
    }
    void hideScreen() {
        handler.removeCallbacks(photoTick); handler.removeCallbacks(clockTick);
        if(screen!=null) root.removeView(screen); screen=null; clock=null; running=false;
        applyWallpaper();
    }
}
