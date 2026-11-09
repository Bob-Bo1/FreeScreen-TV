package com.gathin.tvhome;

import android.app.*;
import android.os.Bundle;
import android.content.*;
import android.graphics.*;
import android.net.Uri;
import android.widget.*;
import androidx.core.content.FileProvider;
import com.google.zxing.*;
import com.google.zxing.common.BitMatrix;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;

/** A temporary, token-protected LAN inbox. Closing this screen stops new transfers. */
public final class PhoneTransferActivity extends Activity {
    static final String WALLPAPER_ONLY = "wallpaper_only";
    private ServerSocket server;
    private final ExecutorService workers=new ThreadPoolExecutor(2,2,0L,TimeUnit.MILLISECONDS,new SynchronousQueue<Runnable>());
    private final String token=UUID.randomUUID().toString();
    private volatile boolean closed;
    private TextView status;
    private String link;
    private boolean wallpaperOnly;
    private final java.util.concurrent.atomic.AtomicBoolean busy=new java.util.concurrent.atomic.AtomicBoolean();
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        wallpaperOnly=getIntent().getBooleanExtra(WALLPAPER_ONLY,false);
        LinearLayout box=new LinearLayout(this); box.setOrientation(1); box.setPadding(64,32,64,32); box.setBackgroundColor(0xff18212d);
        TextView title=new TextView(this); title.setText(wallpaperOnly?"手机上传壁纸":"手机传文件 · FreeScreen TV"); title.setTextColor(-1);title.setTextSize(28);box.addView(title);
        status=new TextView(this);status.setTextColor(-1);status.setTextSize(18);box.addView(status);
        ImageView qr=new ImageView(this);box.addView(qr,new LinearLayout.LayoutParams(300,300));
        if(!wallpaperOnly) {
            Button sources=new Button(this);sources.setText("查看已保存的 TiviMate 直播源");box.addView(sources);sources.setOnClickListener(v->showSources());
            Button cleanup=new Button(this);cleanup.setText("清理已上传的安装包");box.addView(cleanup);cleanup.setOnClickListener(v->clearApks());
        }
        Button done=new Button(this);done.setText("关闭传输");box.addView(done);done.setOnClickListener(v->finish());setContentView(box);
        try {
            String ip=localIp();if(ip==null)throw new IOException("请先连接家庭网络");
            server=new ServerSocket(0,2);link="http://"+ip+":"+server.getLocalPort()+"/"+token+"/";
            status.setText("手机与电视连接同一网络，扫码上传。关闭此页后停止接收。\n"+link);
            BitMatrix matrix=new MultiFormatWriter().encode(link,BarcodeFormat.QR_CODE,300,300);
            Bitmap bitmap=Bitmap.createBitmap(300,300,Bitmap.Config.ARGB_8888);
            for(int y=0;y<300;y++)for(int x=0;x<300;x++)bitmap.setPixel(x,y,matrix.get(x,y)?Color.BLACK:Color.WHITE);qr.setImageBitmap(bitmap);
            new Thread(()->{while(!closed)try { Socket s=server.accept();s.setSoTimeout(30000);try{workers.execute(()->receive(s));}catch(RejectedExecutionException e){s.close();} }catch(IOException e){if(!closed)runOnUiThread(()->status.setText("连接中断，请关闭后重新进入"));break;}},"phone-inbox").start();
        } catch(Exception e){status.setText("无法开启传输："+e.getMessage());}
    }
    private String localIp() throws Exception {
        Enumeration<NetworkInterface> interfaces=NetworkInterface.getNetworkInterfaces();
        while(interfaces.hasMoreElements()){NetworkInterface n=interfaces.nextElement();if(!n.isUp()||n.isLoopback())continue;
            Enumeration<InetAddress> addresses=n.getInetAddresses();while(addresses.hasMoreElements()){InetAddress a=addresses.nextElement();if(a instanceof Inet4Address && a.isSiteLocalAddress())return a.getHostAddress();}}
        return null;
    }
    private String line(InputStream in)throws IOException {ByteArrayOutputStream b=new ByteArrayOutputStream();int c;while((c=in.read())!=-1){if(c==10)break;if(c!=13)b.write(c);if(b.size()>8192)throw new IOException("请求过长");}return b.toString("UTF-8");}
    private void reply(OutputStream out,int code,String type,byte[] data)throws IOException {String h="HTTP/1.1 "+code+" Result\r\nContent-Type: "+type+"\r\nContent-Length: "+data.length+"\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";out.write(h.getBytes(StandardCharsets.US_ASCII));out.write(data);out.flush();}
    private void text(OutputStream out,int code,String s)throws IOException{reply(out,code,"text/plain; charset=utf-8",s.getBytes(StandardCharsets.UTF_8));}
    private void receive(Socket socket){File partial=null;boolean owns=false;try(Socket s=socket){InputStream in=s.getInputStream();OutputStream out=s.getOutputStream();String[] request=line(in).split(" ");if(request.length<2)return;
        Map<String,String> headers=new HashMap<>();for(int i=0;i<60;i++){String l=line(in);if(l.isEmpty())break;int p=l.indexOf(':');if(p>0)headers.put(l.substring(0,p).toLowerCase(Locale.ROOT),l.substring(p+1).trim());if(i==59)throw new IOException("请求头过长");}
        String prefix="/"+token+"/";String path=request[1];if(!path.startsWith(prefix)){text(out,403,"请重新扫描电视二维码");return;}path=path.substring(prefix.length());
        if("GET".equals(request[0])&&path.isEmpty()){
            try(InputStream page=getAssets().open("phone.html")){
                byte[] html=readSmall(page,65536);
                if(wallpaperOnly) html=new String(html,StandardCharsets.UTF_8)
                        .replace("<section><h2>安装应用</h2>","<section hidden><h2>安装应用</h2>")
                        .getBytes(StandardCharsets.UTF_8);
                reply(out,200,"text/html; charset=utf-8",html);
            }
            return;
        }
        if(!"POST".equals(request[0])||!(path.equals("wallpaper")||(!wallpaperOnly&&path.equals("apk")))){text(out,404,"找不到该入口");return;}
        String origin=headers.get("origin");if(origin!=null&&!origin.equals(link.substring(0,link.indexOf('/',7)))){text(out,403,"请通过扫码页面上传");return;}
        if(headers.containsKey("transfer-encoding")){text(out,400,"不支持此传输方式");return;}
        long length;try{length=Long.parseLong(headers.getOrDefault("content-length","-1"));}catch(NumberFormatException e){length=-1;}
        long limit=path.equals("apk")?256L*1024*1024:path.equals("wallpaper")?20L*1024*1024:8192;
        if(length<=0||length>limit){text(out,413,"文件为空或过大（壁纸20MB，APK256MB）");return;}
        if(!busy.compareAndSet(false,true)){text(out,409,"电视正在处理上一项，请先在电视上确认");return;}owns=true;
        File dir=new File(getFilesDir(),"phone-inbox");if(!dir.isDirectory()&&!dir.mkdirs())throw new IOException("无法创建目录");
        File imagesDir=new File(getFilesDir(),"phone-wallpapers");
        File[] imagesList=imagesDir.listFiles();File[] inboxList=dir.listFiles();
        if((path.equals("wallpaper")&&imagesList!=null&&imagesList.length>=30)||(path.equals("apk")&&inboxList!=null&&inboxList.length>=10)){text(out,409,"存储数量已达上限，请先删除旧文件");return;}
        String id=UUID.randomUUID().toString();partial=new File(dir,id+".part");
        try(FileOutputStream file=new FileOutputStream(partial)){byte[] buffer=new byte[65536];long remain=length;while(remain>0){int n=in.read(buffer,0,(int)Math.min(buffer.length,remain));if(n<0)throw new IOException("上传中断");file.write(buffer,0,n);remain-=n;}}
        if(path.equals("wallpaper")){BitmapFactory.Options opt=new BitmapFactory.Options();opt.inJustDecodeBounds=true;BitmapFactory.decodeFile(partial.getPath(),opt);
            if(opt.outWidth<1||opt.outHeight<1||((long)opt.outWidth*opt.outHeight)>50000000L){text(out,400,"图片无法读取或尺寸过大，请选择 JPG、PNG 或 WebP");return;}
            File images=new File(getFilesDir(),"phone-wallpapers");images.mkdirs();File image=new File(images,id+".img");if(!partial.renameTo(image))throw new IOException("保存失败");partial=null;
            runOnUiThread(()->confirmWallpaper(image));text(out,200,"壁纸已上传，请在电视上预览并确认。");owns=false;return;}
        File apk=new File(dir,id+".apk");if(apk.exists()&&!apk.delete())throw new IOException("请稍后再试");if(!partial.renameTo(apk))throw new IOException("保存失败");partial=null;
        android.content.pm.PackageInfo info=getPackageManager().getPackageArchiveInfo(apk.getPath(),0);if(info==null){apk.delete();text(out,400,"这不是有效的 APK 安装包");return;}
        info.applicationInfo.sourceDir=apk.getPath();info.applicationInfo.publicSourceDir=apk.getPath();String name=String.valueOf(info.applicationInfo.loadLabel(getPackageManager()));
        runOnUiThread(()->confirmApk(apk,name,info.packageName));text(out,200,"安装包已上传，请在电视确认安装。");owns=false;
    }catch(Exception e){runOnUiThread(()->Toast.makeText(this,"传输未完成，请重试",Toast.LENGTH_SHORT).show());}finally{if(partial!=null)partial.delete();if(owns)busy.set(false);}}
    private byte[] readSmall(InputStream in,int limit)throws IOException{ByteArrayOutputStream b=new ByteArrayOutputStream();byte[] buf=new byte[4096];int n;while((n=in.read(buf))>0){b.write(buf,0,n);if(b.size()>limit)throw new IOException();}return b.toByteArray();}
    private void confirmWallpaper(File file){if(isFinishing()||closed){busy.set(false);return;}BitmapFactory.Options o=new BitmapFactory.Options();o.inSampleSize=4;ImageView preview=new ImageView(this);preview.setAdjustViewBounds(true);preview.setMaxHeight(400);preview.setImageBitmap(BitmapFactory.decodeFile(file.getPath(),o));
        new AlertDialog.Builder(this).setTitle("收到新壁纸").setView(preview).setPositiveButton("设为壁纸",(d,w)->{getSharedPreferences("home_experience",MODE_PRIVATE).edit().putString("wallpaper",file.getName()).apply();}).setNegativeButton("稍后使用",null).setOnDismissListener(d->busy.set(false)).show();}
    private void confirmApk(File apk,String name,String pkg){if(isFinishing()||closed){busy.set(false);return;}new AlertDialog.Builder(this).setTitle("安装 "+name+"？").setMessage("应用包名："+pkg+"\n请按电视系统提示确认安装。").setPositiveButton("继续安装",(d,w)->install(apk)).setNegativeButton("取消",null).setOnDismissListener(d->busy.set(false)).show();}
    private File pendingApk;
    private void install(File apk){try{if(!getPackageManager().canRequestPackageInstalls()){
        pendingApk=apk;
        new AlertDialog.Builder(this).setMessage("请允许此主页安装应用，返回后继续安装。").setPositiveButton("打开权限设置",(d,w)->{try{startActivityForResult(new Intent(android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,Uri.parse("package:"+getPackageName())),41);}catch(Exception e){Toast.makeText(this,"无法打开权限设置，请在电视系统设置中授权",Toast.LENGTH_LONG).show();}}).setNegativeButton("取消",null).show();return;
    }launchInstaller(apk);}catch(Exception e){Toast.makeText(this,"电视不支持打开安装界面",Toast.LENGTH_LONG).show();}}
    @Override protected void onActivityResult(int request,int result,Intent data){super.onActivityResult(request,result,data);if(request==41&&pendingApk!=null){File file=pendingApk;pendingApk=null;if(getPackageManager().canRequestPackageInstalls())confirmApk(file,"已上传应用","权限已开启");else Toast.makeText(this,"未获得安装权限",Toast.LENGTH_LONG).show();}}
    private void launchInstaller(File apk){try{Uri uri=FileProvider.getUriForFile(this,getPackageName()+".phone-files",apk);startActivity(new Intent(Intent.ACTION_VIEW).setDataAndType(uri,"application/vnd.android.package-archive").addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION));}catch(Exception e){Toast.makeText(this,"无法打开安装器，请检查系统权限",Toast.LENGTH_LONG).show();}}
    private void clearApks(){if(busy.get()){Toast.makeText(this,"请先完成当前传输和确认",Toast.LENGTH_SHORT).show();return;}new AlertDialog.Builder(this).setMessage("删除已接收的安装文件？已安装的应用不会受影响。请先退出系统安装界面。").setPositiveButton("清理",(d,w)->{File[] files=new File(getFilesDir(),"phone-inbox").listFiles();if(files!=null)for(File f:files)if(f.getName().endsWith(".apk"))f.delete();Toast.makeText(this,"清理完成",Toast.LENGTH_SHORT).show();}).setNegativeButton("取消",null).show();}
    private void showSources(){Set<String> saved=getSharedPreferences("phone_sources",MODE_PRIVATE).getStringSet("urls",Collections.emptySet());String[] urls=saved.toArray(new String[0]);if(urls.length==0){Toast.makeText(this,"还没有保存直播源",Toast.LENGTH_SHORT).show();return;}new AlertDialog.Builder(this).setTitle("选择直播源").setItems(urls,(d,n)->new AlertDialog.Builder(this).setMessage(urls[n]+"\n请在 TiviMate：添加播放列表 → M3U 播放列表 → 输入地址。跨应用粘贴能力取决于输入法。").setPositiveButton("复制地址",(a,b)->{((android.content.ClipboardManager)getSystemService(CLIPBOARD_SERVICE)).setPrimaryClip(ClipData.newPlainText("M3U",urls[n]));Toast.makeText(this,"已复制地址",Toast.LENGTH_SHORT).show();}).setNeutralButton("打开 TiviMate",(a,b)->{Intent i=getPackageManager().getLaunchIntentForPackage("ar.tvplayer.tv");if(i!=null)startActivity(i);else Toast.makeText(this,"请先安装 TiviMate",Toast.LENGTH_LONG).show();}).setNegativeButton("关闭",null).show()).show();}
    @Override protected void onDestroy(){closed=true;try{if(server!=null)server.close();}catch(IOException ignored){}workers.shutdownNow();super.onDestroy();}
}
