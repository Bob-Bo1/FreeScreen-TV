package com.gathin.tvhome;
import android.app.*;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.view.*;
import android.widget.*;
import java.util.*;

/** Remote-first picker with a preview of the four dock slots. */
final class DockPicker {
    interface Save { boolean apply(java.util.List<String> keys); }
    static void show(Activity a,java.util.List<AppAdapter.Entry> apps,int limit,Save save,Runnable skip,Runnable dismissed) {
        float den=a.getResources().getDisplayMetrics().density;
        java.util.function.IntUnaryOperator dp=n->Math.round(n*den);
        Dialog dialog=new Dialog(a);
        LinearLayout panel=new LinearLayout(a);panel.setOrientation(1);panel.setPadding(dp.applyAsInt(24),dp.applyAsInt(20),dp.applyAsInt(24),dp.applyAsInt(16));panel.setBackground(shape(0xff17212c,0xff465362,dp.applyAsInt(20),1));
        TextView title=new TextView(a);title.setText("把喜欢的应用放在前面");title.setTextColor(-1);title.setTextSize(26);panel.addView(title);
        TextView hint=new TextView(a);hint.setText("选择最多4个 · 按确定选择，再按一次取消 · 按选择顺序排列");hint.setTextColor(0xffb9c7d6);hint.setTextSize(14);hint.setPadding(0,dp.applyAsInt(6),0,dp.applyAsInt(12));panel.addView(hint);
        LinearLayout slots=new LinearLayout(a);panel.addView(slots);
        TextView[] slotLabels=new TextView[limit];
        for(int i=0;i<limit;i++){TextView t=new TextView(a);t.setGravity(Gravity.CENTER);t.setTextColor(0xffc7d7e6);t.setTextSize(15);t.setBackground(shape(0xff243343,0xff465362,dp.applyAsInt(10),1));LinearLayout.LayoutParams p=new LinearLayout.LayoutParams(0,dp.applyAsInt(44),1);p.setMargins(0,0,dp.applyAsInt(8),0);slots.addView(t,p);slotLabels[i]=t;}
        TextView count=new TextView(a);count.setTextColor(0xff9bd5ff);count.setTextSize(14);count.setPadding(0,dp.applyAsInt(10),0,dp.applyAsInt(6));panel.addView(count);
        ScrollView scroll=new ScrollView(a);scroll.setClipToPadding(false);scroll.setPadding(dp.applyAsInt(6),dp.applyAsInt(6),dp.applyAsInt(6),dp.applyAsInt(6));panel.addView(scroll,new LinearLayout.LayoutParams(-1,0,1));
        GridLayout grid=new GridLayout(a);grid.setColumnCount(4);scroll.addView(grid);
        java.util.List<Integer> selected=new ArrayList<>();java.util.List<TextView> labels=new ArrayList<>();java.util.List<LinearLayout> cards=new ArrayList<>();
        int screenWidth=a.getResources().getDisplayMetrics().widthPixels;int panelWidth=Math.min(screenWidth-dp.applyAsInt(80),dp.applyAsInt(860));int cell=(panelWidth-dp.applyAsInt(72))/4;
        Runnable refresh=()->{count.setText("已选 "+selected.size()+" / "+limit+"　· 少选的位置将自动补齐");for(int i=0;i<limit;i++)slotLabels[i].setText(i<selected.size()?(i+1)+"  "+apps.get(selected.get(i)).label:(i+1)+"  待选择");for(int i=0;i<labels.size();i++){int index=selected.indexOf(i);labels.get(i).setText((index>=0?(index+1)+"  ✓  ":"")+apps.get(i).label);cards.get(i).setActivated(index>=0);cards.get(i).setBackground(shape(index>=0?0xff214b63:0xff243343,cards.get(i).isFocused()?0xffb3e3ff:index>=0?0xff68c6fa:0xff354553,dp.applyAsInt(12),cards.get(i).isFocused()?3:1));}};
        for(int i=0;i<apps.size();i++){final int index=i;AppAdapter.Entry app=apps.get(i);LinearLayout card=new LinearLayout(a);card.setOrientation(1);card.setPadding(dp.applyAsInt(5),dp.applyAsInt(5),dp.applyAsInt(5),dp.applyAsInt(5));card.setFocusable(true);card.setClickable(true);card.setId(View.generateViewId());
            ImageView image=new ImageView(a);image.setScaleType(ImageView.ScaleType.FIT_CENTER);image.setImageDrawable(app.banner!=null?app.banner:app.icon);card.addView(image,new LinearLayout.LayoutParams(-1,(cell-dp.applyAsInt(10))*9/16));
            TextView text=new TextView(a);text.setTextColor(-1);text.setTextSize(14);text.setGravity(Gravity.CENTER);text.setSingleLine(true);text.setEllipsize(android.text.TextUtils.TruncateAt.END);card.addView(text,new LinearLayout.LayoutParams(-1,dp.applyAsInt(28)));labels.add(text);cards.add(card);
            GridLayout.LayoutParams lp=new GridLayout.LayoutParams();lp.width=cell;lp.setMargins(dp.applyAsInt(2),dp.applyAsInt(2),dp.applyAsInt(2),dp.applyAsInt(8));grid.addView(card,lp);
            card.setOnFocusChangeListener((v,focus)->{card.animate().scaleX(focus?1.025f:1).scaleY(focus?1.025f:1).setDuration(120).start();refresh.run();if(focus)scroll.requestChildFocus(card,card);});
            card.setOnClickListener(v->{if(selected.contains(index))selected.remove(Integer.valueOf(index));else if(selected.size()<limit)selected.add(index);else Toast.makeText(a,"已选满，请先取消一个应用",Toast.LENGTH_SHORT).show();refresh.run();});
        }
        LinearLayout actions=new LinearLayout(a);actions.setGravity(Gravity.RIGHT|Gravity.CENTER_VERTICAL);panel.addView(actions);
        Button later=new Button(a);later.setAllCaps(false);later.setText("稍后设置");actions.addView(later);later.setOnClickListener(v->{skip.run();dialog.dismiss();});
        Button done=new Button(a);done.setAllCaps(false);done.setText("完成");actions.addView(done);done.setOnClickListener(v->{if(selected.isEmpty()){Toast.makeText(a,"先选择应用，或稍后设置",Toast.LENGTH_SHORT).show();return;}java.util.List<String> keys=new ArrayList<>();for(int i:selected)keys.add(apps.get(i).component.flattenToShortString());if(save.apply(keys))dialog.dismiss();});
        dialog.setOnKeyListener((d,key,event)->{
            if(event.getAction()!=android.view.KeyEvent.ACTION_DOWN)return false;
            if(key!=android.view.KeyEvent.KEYCODE_DPAD_LEFT && key!=android.view.KeyEvent.KEYCODE_DPAD_RIGHT && key!=android.view.KeyEvent.KEYCODE_DPAD_UP && key!=android.view.KeyEvent.KEYCODE_DPAD_DOWN)return false;
            View focused=dialog.getCurrentFocus();int pos=cards.indexOf(focused);
            if(pos<0 && focused!=later && focused!=done){if(!cards.isEmpty())cards.get(0).requestFocus();return true;}
            if(pos>=0){int target=pos;
                if(key==android.view.KeyEvent.KEYCODE_DPAD_LEFT && pos%4>0)target=pos-1;
                if(key==android.view.KeyEvent.KEYCODE_DPAD_RIGHT && pos%4<3 && pos+1<cards.size())target=pos+1;
                if(key==android.view.KeyEvent.KEYCODE_DPAD_UP && pos>=4)target=pos-4;
                if(key==android.view.KeyEvent.KEYCODE_DPAD_DOWN){if(pos+4<cards.size())target=pos+4;else {done.requestFocus();return true;}}
                cards.get(target).requestFocus();return true;
            }
            if(key==android.view.KeyEvent.KEYCODE_DPAD_UP){cards.get(cards.size()-1).requestFocus();return true;}
            if(key==android.view.KeyEvent.KEYCODE_DPAD_LEFT){later.requestFocus();return true;}
            if(key==android.view.KeyEvent.KEYCODE_DPAD_RIGHT){done.requestFocus();return true;}
            return true;
        });
        dialog.setContentView(panel);dialog.setOnCancelListener(d->skip.run());dialog.setOnDismissListener(d->dismissed.run());dialog.show();Window w=dialog.getWindow();if(w!=null){w.setBackgroundDrawableResource(android.R.color.transparent);w.setLayout(panelWidth,Math.min(a.getResources().getDisplayMetrics().heightPixels-dp.applyAsInt(40),dp.applyAsInt(490)));}refresh.run();if(!cards.isEmpty())cards.get(0).post(()->{cards.get(0).requestFocusFromTouch();cards.get(0).requestFocus();});
    }
    private static GradientDrawable shape(int fill,int line,int radius,int stroke){GradientDrawable d=new GradientDrawable();d.setColor(fill);d.setCornerRadius(radius);d.setStroke(stroke,line);return d;}
}
