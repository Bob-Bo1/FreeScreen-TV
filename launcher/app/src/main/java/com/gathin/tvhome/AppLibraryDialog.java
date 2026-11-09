package com.gathin.tvhome;

import android.app.Activity;
import android.content.ActivityNotFoundException;
import android.content.ComponentName;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.graphics.Color;
import android.graphics.Rect;
import android.graphics.drawable.ColorDrawable;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.provider.Settings;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.PopupWindow;
import android.widget.TextView;
import android.widget.Toast;

import androidx.annotation.NonNull;
import androidx.recyclerview.widget.GridLayoutManager;
import androidx.recyclerview.widget.RecyclerView;

import java.text.Collator;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.function.Predicate;

/** On-demand installed-app picker. Uses the launcher's glass resources; no backdrop capture or blur. */
final class AppLibraryDialog {
    private final Activity activity;
    private final Predicate<String> onHome;
    private final Runnable changed;
    private final LinearLayout panel;
    private final boolean rtl;
    private TextView instructions;
    private int totalApps;
    final PopupWindow popup;
    private final float density;
    private List<ResolveInfo> apps = new ArrayList<>();
    private String selectedPackage;
    private int selectedPosition;
    private final Map<String, Long> installTimes = new HashMap<>();
    private final Map<String, String> labels = new HashMap<>();
    private long catalogTime;
    private PopupWindow actionPopup;
    private RecyclerView catalog;
    private final int catalogWidth;
    private final int catalogHeight;
    private final int columns;

    private void stopCatalog() {
        if (catalog != null) catalog.stopScroll();
        catalog = null;
    }

    private GradientDrawable surface(int color, int radius) {
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(color);
        bg.setCornerRadius(dp(radius));
        bg.setStroke(dp(1), 0x18FFFFFF);
        return bg;
    }

    private void resize(int width, int height) {
        popup.setWidth(width);
        popup.setHeight(height);
        if (popup.isShowing()) popup.update(width, height);
    }

    AppLibraryDialog(Activity activity, Predicate<String> onHome, Runnable changed, Runnable dismissed) {
        this.activity = activity;
        this.rtl = activity.getResources().getBoolean(R.bool.rtl_app_grid);
        this.onHome = onHome;
        this.changed = changed;
        density = activity.getResources().getDisplayMetrics().density;
        panel = new LinearLayout(activity);
        panel.setLayoutDirection(rtl ? View.LAYOUT_DIRECTION_RTL : View.LAYOUT_DIRECTION_LTR);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(20), dp(14), dp(20), dp(12));
        panel.setBackground(surface(0xFF101B2A, 22));
        View decor = activity.getWindow().getDecorView();
        catalogWidth = Math.max(dp(200), decor.getWidth() - dp(44));
        catalogHeight = Math.max(dp(180), decor.getHeight() - dp(40));
        columns = Math.max(2, Math.min(6, catalogWidth / dp(162)));
        popup = new PopupWindow(panel, catalogWidth, catalogHeight, true);
        popup.setBackgroundDrawable(new ColorDrawable(Color.TRANSPARENT));
        popup.setOutsideTouchable(true);
        popup.setAnimationStyle(0);
        popup.setOnDismissListener(() -> { dismissActions(); stopCatalog(); dismissed.run(); });
    }

    void close() { dismissActions(); popup.dismiss(); }

    void show() {
        refresh();
        popup.showAtLocation(activity.getWindow().getDecorView(), Gravity.CENTER, 0, 0);
        View container = panel.getRootView();
        if (container.getLayoutParams() instanceof WindowManager.LayoutParams) {
            WindowManager.LayoutParams params = (WindowManager.LayoutParams) container.getLayoutParams();
            params.flags |= WindowManager.LayoutParams.FLAG_DIM_BEHIND;
            params.dimAmount = 0.55f;
            activity.getWindowManager().updateViewLayout(container, params);
        }
        panel.setAlpha(0f);
        panel.animate().alpha(1f).setDuration(180).start();
    }

    /** Called by the activity's existing package receiver; an uninstall cannot leave a stale action. */
    /** Packages with a leanback entry, from the last refresh. */
    private final java.util.Set<String> tvPackages = new java.util.HashSet<>();
    private java.util.Set<String> hiddenFromHome = java.util.Collections.emptySet();

    void refresh() {
        dismissActions();
        PackageManager pm = activity.getPackageManager();
        Map<String, ResolveInfo> unique = new LinkedHashMap<>();
        tvPackages.clear();
        // Prefer the TV entry when an app declares both categories.
        for (String category : new String[]{Intent.CATEGORY_LEANBACK_LAUNCHER, Intent.CATEGORY_LAUNCHER}) {
            for (ResolveInfo info : pm.queryIntentActivities(new Intent(Intent.ACTION_MAIN).addCategory(category), 0)) {
                if (info.activityInfo == null || !info.activityInfo.exported) continue;
                String pkg = info.activityInfo.packageName;
                if (Intent.CATEGORY_LEANBACK_LAUNCHER.equals(category)) tvPackages.add(pkg);
                if (!pkg.equals(activity.getPackageName()) && !unique.containsKey(pkg)) unique.put(pkg, info);
            }
        }
        if (pm.resolveActivity(new Intent().setClassName("com.xiaomi.mitv.settings",
                "com.xiaomi.mitv.settings.entry.MainActivity"), 0) != null) {
            unique.remove("com.android.tv.settings");
        }
        totalApps = unique.size();
        apps = new ArrayList<>(unique.values());
        installTimes.clear();
        labels.clear();
        Map<String, Boolean> home = new HashMap<>();
        for (ResolveInfo app : apps) {
            String pkg = app.activityInfo.packageName;
            home.put(pkg, onHome.test(pkg));
            label(app);
            try { installTimes.put(pkg, pm.getPackageInfo(pkg, 0).firstInstallTime); }
            catch (PackageManager.NameNotFoundException | RuntimeException unavailable) { installTimes.put(pkg, 0L); }
        }
        catalogTime = System.currentTimeMillis();
        Collator collator = Collator.getInstance();
        java.util.Set<String> removed = LibraryApps.loadRemoved(activity);
        hiddenFromHome = removed;
        android.content.SharedPreferences hideTimes = activity.getSharedPreferences("home_organize", android.content.Context.MODE_PRIVATE);
        apps.sort((a, b) -> {
            String pa = a.activityInfo.packageName, pb = b.activityInfo.packageName;
            boolean hiddenA = removed.contains(pa), hiddenB = removed.contains(pb);
            if (hiddenA != hiddenB) return hiddenA ? -1 : 1;
            if (hiddenA) {
                int recentHidden = Long.compare(hideTimes.getLong("hidden:" + pb, 0), hideTimes.getLong("hidden:" + pa, 0));
                if (recentHidden != 0) return recentHidden;
            }
            int priority = LibraryApps.comparePriority(home.get(pa), installTimes.get(pa),
                    home.get(pb), installTimes.get(pb), catalogTime);
            if (priority != 0) return priority;
            int byLabel = collator.compare(label(a), label(b));
            return byLabel != 0 ? byLabel : pa.compareTo(pb);
        });
        showCatalog();
    }

    private String label(ResolveInfo app) {
        String pkg = app.activityInfo.packageName;
        String cached = labels.get(pkg);
        if (cached != null) return cached;
        String value;
        try { value = Cards.cleanLabel(String.valueOf(app.loadLabel(activity.getPackageManager())), pkg); }
        catch (RuntimeException unavailable) { value = pkg; }
        labels.put(pkg, value);
        return value;
    }

    private void heading(String text) {
        TextView title = text(text, 18, 0xFFFFFFFF);
        title.setMaxLines(1);
        title.setEllipsize(TextUtils.TruncateAt.END);
        title.setPadding(dp(12), dp(10), dp(12), dp(8));
        panel.addView(title, new LinearLayout.LayoutParams(-1, -2));
    }

    private void showCatalog() {
        stopCatalog();
        resize(catalogWidth, catalogHeight);
        panel.removeAllViews();
        LinearLayout header = new LinearLayout(activity);
        header.setGravity(Gravity.CENTER_VERTICAL);
        header.setPadding(dp(12), dp(4), dp(8), dp(4));
        TextView title = text("所有应用", 25, Color.WHITE);
        header.addView(title, new LinearLayout.LayoutParams(0, dp(36), 1));
        title.setGravity(Gravity.CENTER_VERTICAL);
        title.setSingleLine(true);
        title.setEllipsize(TextUtils.TruncateAt.END);
        TextView count = text(activity.getResources().getQuantityString(
                R.plurals.library_app_count, totalApps, totalApps), 13, 0xA6FFFFFF);
        count.setSingleLine(true);
        header.addView(count, new LinearLayout.LayoutParams(-2, -2));
        panel.addView(header, new LinearLayout.LayoutParams(-1, dp(44)));
        instructions = text("确定打开   ·   长按管理   ·   返回主页", 12, 0xFFB4C8DF);
        instructions.setSingleLine(true);
        instructions.setEllipsize(TextUtils.TruncateAt.END);
        instructions.setPadding(dp(12), 0, dp(12), dp(6));
        panel.addView(instructions, new LinearLayout.LayoutParams(-1, dp(26)));
        panel.setFocusableInTouchMode(apps.isEmpty());
        if (apps.isEmpty()) {
            instructions.setVisibility(View.GONE);
            heading(activity.getString(R.string.library_empty));
            panel.setFocusableInTouchMode(true);
            panel.requestFocus();
            return;
        }
        RecyclerView list = new RecyclerView(activity);
        list.setLayoutDirection(rtl ? View.LAYOUT_DIRECTION_RTL : View.LAYOUT_DIRECTION_LTR);
        catalog = list;
        list.setLayoutManager(new GridLayoutManager(activity, columns));
        list.setItemAnimator(null);
        list.setOverScrollMode(View.OVER_SCROLL_NEVER);
        list.setClipToPadding(false);
        list.setPadding(dp(4), dp(6), dp(4), dp(6));
        list.setAdapter(new Catalog());
        panel.addView(list, new LinearLayout.LayoutParams(-1, 0, 1));
        int position = Math.min(selectedPosition, apps.size() - 1);
        for (int i = 0; i < apps.size(); i++) {
            if (apps.get(i).activityInfo.packageName.equals(selectedPackage)) { position = i; break; }
        }
        list.scrollToPosition(position);
        final int target = position;
        list.post(() -> {
            if (catalog != list || !popup.isShowing()) return;
            RecyclerView.ViewHolder holder = list.findViewHolderForAdapterPosition(target);
            if (holder != null) holder.itemView.requestFocus();
        });
    }

    private void dismissActions() {
        PopupWindow menu = actionPopup;
        actionPopup = null;
        if (menu != null) menu.dismiss();
    }

    private void restoreCardFocus() {
        if (catalog == null || !popup.isShowing()) return;
        catalog.post(() -> {
            if (catalog == null || !popup.isShowing() || actionPopup != null) return;
            RecyclerView.ViewHolder holder = catalog.findViewHolderForAdapterPosition(selectedPosition);
            if (holder != null) holder.itemView.requestFocus();
        });
    }

    private void changeHome(ResolveInfo app, boolean add) {
        if (!LibraryApps.save(activity, app.activityInfo.packageName, add)) {
            Toast.makeText(activity, R.string.library_save_failed, Toast.LENGTH_LONG).show();
            return;
        }
        selectedPackage = app.activityInfo.packageName;
        dismissActions();
        changed.run();
        refresh();
    }

    private void open(ResolveInfo app) {
        close();
        Intent intent = new Intent(Intent.ACTION_MAIN).setComponent(
                new ComponentName(app.activityInfo.packageName, app.activityInfo.name));
        // TV apps keep the leanback launch intent, so an app already running resumes instead of restarting.
        if (tvPackages.contains(app.activityInfo.packageName)) intent.addCategory(Intent.CATEGORY_LEANBACK_LAUNCHER);
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_RESET_TASK_IF_NEEDED);
        try { activity.startActivity(intent); }
        catch (ActivityNotFoundException | SecurityException e) {
            Toast.makeText(activity, activity.getString(R.string.cant_open, label(app)), Toast.LENGTH_SHORT).show();
        }
    }

    private void showActions(ResolveInfo app, View anchor) {
        dismissActions();
        selectedPackage = app.activityInfo.packageName;
        String pkg = selectedPackage;
        View menuPanel = activity.getLayoutInflater().inflate(R.layout.popup_inputs, panel, false);
        LinearLayout rows = menuPanel.findViewById(R.id.rows);
        PopupWindow menu = new PopupWindow(menuPanel, dp(220), ViewGroup.LayoutParams.WRAP_CONTENT, true);
        actionPopup = menu;
        menu.setBackgroundDrawable(new ColorDrawable(Color.TRANSPARENT));
        menu.setOutsideTouchable(true);
        menu.setAnimationStyle(0);
        menu.setElevation(0f);
        menu.setOnDismissListener(() -> {
            if (actionPopup == menu) actionPopup = null;
            restoreCardFocus();
        });
        addAction(rows, R.string.library_open, () -> open(app));
        boolean home = onHome.test(pkg);
        addAction(rows, home ? R.string.library_remove_shortcut : R.string.library_add_home,
                () -> changeHome(app, !home));
        addAction(rows, R.string.library_app_info, () -> {
            close();
            try {
                activity.startActivity(new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + pkg)));
            } catch (ActivityNotFoundException | SecurityException e) {
                Toast.makeText(activity, activity.getString(R.string.cant_open, label(app)), Toast.LENGTH_SHORT).show();
            }
        });
        int menuWidth = dp(220);
        for (int i = 0; i < rows.getChildCount(); i++) {
            TextView title = rows.getChildAt(i).findViewById(R.id.title);
            menuWidth = Math.max(menuWidth, (int) Math.ceil(title.getPaint().measureText(title.getText().toString())) + dp(58));
            title.setEllipsize(TextUtils.TruncateAt.END);
        }
        menuWidth = Math.min(menuWidth, panel.getWidth() - dp(24));
        menu.setWidth(menuWidth);
        menuPanel.measure(View.MeasureSpec.makeMeasureSpec(menuWidth, View.MeasureSpec.EXACTLY), View.MeasureSpec.UNSPECIFIED);
        int[] location = new int[2];
        anchor.getLocationOnScreen(location);
        int[] panelLocation = new int[2];
        panel.getLocationOnScreen(panelLocation);
        Rect screen = new Rect(panelLocation[0], panelLocation[1],
                panelLocation[0] + panel.getWidth(), panelLocation[1] + panel.getHeight());
        int x = Math.max(screen.left + dp(12), Math.min(location[0] + anchor.getWidth() - dp(12), screen.right - menuWidth - dp(12)));
        int y = Math.max(screen.top + dp(12), Math.min(location[1] + dp(8), screen.bottom - menuPanel.getMeasuredHeight() - dp(12)));
        menuPanel.setAlpha(0f);
        menuPanel.setPivotY(0f);
        menuPanel.setScaleY(0.92f);
        menuPanel.setTranslationY(-dp(8));
        menu.showAtLocation(activity.getWindow().getDecorView(), Gravity.TOP | Gravity.START, x, y);
        menuPanel.animate().alpha(1f).scaleY(1f).translationY(0f).setDuration(220)
                .setInterpolator(new android.view.animation.DecelerateInterpolator(1.6f)).start();
        rows.getChildAt(0).requestFocus();
    }

    private void addAction(LinearLayout rows, int label, Runnable action) {
        View row = activity.getLayoutInflater().inflate(R.layout.item_input, rows, false);
        ((TextView) row.findViewById(R.id.title)).setText(label);
        row.setFocusableInTouchMode(true);
        row.findViewById(R.id.icon).setVisibility(View.GONE);
        row.findViewById(R.id.dot).setVisibility(View.GONE);
        row.findViewById(R.id.status).setVisibility(View.GONE);
        row.setOnClickListener(v -> action.run());
        rows.addView(row);
        row.setOnKeyListener((v, key, event) -> {
            int index = rows.indexOfChild(v);
            return event.getAction() == KeyEvent.ACTION_DOWN && (key == KeyEvent.KEYCODE_DPAD_LEFT
                    || key == KeyEvent.KEYCODE_DPAD_RIGHT || key == KeyEvent.KEYCODE_DPAD_UP && index == 0
                    || key == KeyEvent.KEYCODE_DPAD_DOWN && index == rows.getChildCount() - 1);
        });
    }

    private TextView text(String value, int size, int color) {
        TextView view = new TextView(activity);
        view.setText(value);
        view.setTextSize(size);
        view.setTextColor(color);
        view.setTypeface(activity.getResources().getFont(R.font.outfit_medium));
        view.setLetterSpacing(0.01f);
        view.setIncludeFontPadding(false);
        return view;
    }

    private int dp(int value) { return Math.round(value * density); }

    private final class Catalog extends RecyclerView.Adapter<Row> {
        @NonNull @Override public Row onCreateViewHolder(@NonNull ViewGroup parent, int type) {
            FrameLayout row = new FrameLayout(activity);
            int coverWidth = Math.max(dp(48), (catalogWidth - dp(48)) / columns - dp(24));
            int coverHeight = coverWidth * 9 / 16;
            RecyclerView.LayoutParams params = new RecyclerView.LayoutParams(-1, coverHeight + dp(66));
            params.setMargins(dp(5), dp(5), dp(5), dp(5));
            row.setLayoutParams(params);
            row.setPadding(dp(7), dp(8), dp(7), dp(6));
            row.setFocusableInTouchMode(true);
            row.setClickable(true);
            LinearLayout content = new LinearLayout(activity);
            content.setOrientation(LinearLayout.VERTICAL);
            content.setGravity(Gravity.CENTER_HORIZONTAL);
            row.addView(content, new FrameLayout.LayoutParams(-1, -1));
            ImageView icon = new ImageView(activity);
            icon.setId(R.id.icon);
            icon.setScaleType(ImageView.ScaleType.FIT_CENTER);
            content.addView(icon, new LinearLayout.LayoutParams(coverWidth, coverHeight));
            TextView name = text("", 15, Color.WHITE);
            name.setId(R.id.title);
            name.setGravity(Gravity.CENTER);
            name.setMaxLines(1);
            LinearLayout.LayoutParams nameParams = new LinearLayout.LayoutParams(-1, dp(24));
            nameParams.topMargin = dp(4);
            content.addView(name, nameParams);
            TextView status = text("", 11, 0xBBFFFFFF);
            status.setId(R.id.status);
            status.setGravity(Gravity.CENTER);
            status.setMaxLines(2);
            content.addView(status, new LinearLayout.LayoutParams(-1, -2));
            View check = new View(activity);
            check.setId(R.id.dot);
            FrameLayout.LayoutParams badge = new FrameLayout.LayoutParams(dp(18), dp(18), Gravity.TOP | Gravity.END);
            badge.setMargins(0, dp(2), dp(2), 0);
            row.addView(check, badge);
            return new Row(row);
        }
        @Override public void onBindViewHolder(@NonNull Row holder, int position) {
            ResolveInfo app = apps.get(position);
            String pkg = app.activityInfo.packageName;
            holder.itemView.setContentDescription(label(app));
            ImageView icon = holder.itemView.findViewById(R.id.icon);
            try { icon.setImageDrawable(AppAdapter.coverFor(activity, app, label(app))); }
            catch (RuntimeException unavailable) { icon.setImageResource(R.drawable.ic_apps); }
            TextView name = holder.itemView.findViewById(R.id.title);
            name.setText(label(app));
            name.setEllipsize(TextUtils.TruncateAt.END);
            TextView status = holder.itemView.findViewById(R.id.status);
            boolean home = onHome.test(pkg);
            boolean recent = LibraryApps.recent(installTimes.getOrDefault(pkg, 0L), catalogTime);
            status.setText(home ? R.string.library_on_home : hiddenFromHome.contains(pkg)
                    ? R.string.library_hidden_status : recent ? R.string.library_recent : R.string.library_add_home);
            status.setVisibility(View.VISIBLE);
            View check = holder.itemView.findViewById(R.id.dot);
            check.getLayoutParams().width = dp(home ? 18 : 8);
            check.getLayoutParams().height = dp(home ? 18 : 8);
            if (home) check.setBackgroundResource(R.drawable.library_check);
            else check.setBackground(surface(0xFF70D9A0, 8));
            check.setVisibility(home || recent ? View.VISIBLE : View.GONE);
            holder.itemView.setOnClickListener(v -> open(app));
            holder.itemView.setOnLongClickListener(v -> { showActions(app, v); return true; });
            holder.itemView.setOnFocusChangeListener((v, focused) -> {
                styleTile(v, focused);
                if (focused) {
                    selectedPackage = pkg;
                    int index = holder.getBindingAdapterPosition();
                    if (index != RecyclerView.NO_POSITION) selectedPosition = index;
                }
            });
            styleTile(holder.itemView, holder.itemView.hasFocus());
        }
        private void styleTile(View view, boolean focused) {
            GradientDrawable tile = surface(focused ? 0xFF294565 : 0xFF18283D, 14);
            if (focused) tile.setStroke(dp(2), 0xFFA7D0FF);
            view.setBackground(tile);
        }
        @Override public int getItemCount() { return apps.size(); }
    }

    private static final class Row extends RecyclerView.ViewHolder {
        Row(View view) { super(view); }
    }
}
