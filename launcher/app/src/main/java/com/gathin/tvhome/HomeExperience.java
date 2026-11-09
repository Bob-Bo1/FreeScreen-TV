package com.gathin.tvhome;

import android.app.Activity;
import android.content.SharedPreferences;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.os.Handler;
import android.os.Looper;
import android.view.View;
import android.widget.FrameLayout;
import android.widget.ImageView;
import java.io.InputStream;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Local wallpaper for the launcher. */
final class HomeExperience {
    static final String[] FILES = {"01-mountain.jpg", "02-dunes.jpg", "03-aurora.jpg", "04-sunset.jpg"};
    static final String[] NAMES = {"雪山秋湖", "金色沙丘", "冰湖极光", "海边落日"};
    private final Activity activity;
    private final FrameLayout root;
    private final View ambient, sky;
    private final SharedPreferences prefs;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService decoder = Executors.newSingleThreadExecutor();
    private ImageView background;
    private int imageToken;

    HomeExperience(Activity activity, FrameLayout root, View ambient, View sky) {
        this.activity=activity; this.root=root; this.ambient=ambient; this.sky=sky;
        prefs=activity.getSharedPreferences("home_experience",Activity.MODE_PRIVATE);
    }
    void start() { applyWallpaper(); }
    void close() { imageToken++; decoder.shutdownNow(); }
    String wallpaper() { return prefs.getString("wallpaper", FILES[0]); }
    void selectWallpaper(String file) { prefs.edit().putString("wallpaper",file).apply(); applyWallpaper(); }
    void applyWallpaper() {
        String name=wallpaper(); boolean use=!"brand".equals(name);
        boolean found=false; for(String f:FILES) if(f.equals(name)) found=true;
        found |= name.matches("[a-f0-9-]+\\.img") && new java.io.File(new java.io.File(activity.getFilesDir(),"phone-wallpapers"),name).isFile();
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
            try (InputStream input=name.endsWith(".img") ? new java.io.FileInputStream(new java.io.File(new java.io.File(activity.getFilesDir(),"phone-wallpapers"),name)) : activity.getAssets().open("wallpapers/"+name)) {
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
}
