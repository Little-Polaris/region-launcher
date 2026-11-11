package io.regionlauncher.android;

import android.app.*;
import android.content.*;
import android.net.VpnService;
import android.os.*;
import org.json.*;
import java.io.*;
import java.net.*;
import java.util.*;
import java.util.concurrent.*;

public final class RegionVpnService extends VpnService {
    static volatile boolean alive;
    private static volatile RegionVpnService current;
    private volatile boolean checking;
    static final String CHECK="io.regionlauncher.CHECK",STOP="io.regionlauncher.STOP",EVENT="io.regionlauncher.EVENT";
    // One native core exists per process; serialize cleanup across service recreation too.
    private static final ExecutorService commands=Executors.newSingleThreadExecutor();
    static boolean isChecking(){RegionVpnService value=current;return value!=null&&value.checking;}
    private ParcelFileDescriptor tun;
    private Set<String> activeApps=Set.of();
    private String config;
    private int port;
    private final Recovery recovery=new Recovery();
    private volatile boolean stopping;
    private Journal journal;
    private NotificationManager notifications;
    @Override public void onCreate() {
        super.onCreate();current=this;alive=true;journal=new Journal(this);notifications=getSystemService(NotificationManager.class);
        notifications.createNotificationChannel(new NotificationChannel("connection","连接状态",NotificationManager.IMPORTANCE_LOW));
        notifications.createNotificationChannel(new NotificationChannel("result","启动和修复结果",NotificationManager.IMPORTANCE_DEFAULT));
        try { NativeCore.attach(this); } catch(Throwable ex) {journal.write("内核加载失败："+ex.getClass().getSimpleName());}
    }
    @Override public int onStartCommand(Intent intent,int flags,int startId) {
        if(intent==null)intent=new Intent(CHECK);
        startForeground(1,notice("准备连接",false,null));
        if(STOP.equals(intent.getAction())) {stopConnection();return START_NOT_STICKY;}
        if(checking||stopping)return restartMode();
        checking=true;
        String target=intent.getStringExtra("target");
        commands.execute(()->{
            try {status("正在检测并修复",false,null);check();if(stopping)return;status("已连接 · IPv6 已禁用",true,target);}
            catch(Throwable ex){if(!stopping){String error=ex instanceof IOException?ex.getMessage():"操作失败："+ex.getClass().getSimpleName();journal.write(error);status(error,false,null);notifications.notify(2,notice(error,true,null));}}
            finally {checking=false;sendBroadcast(new Intent(EVENT).setPackage(getPackageName()));}
        });
        return restartMode();
    }
    private int restartMode(){return ProfilePolicy.ownsWorkProfile(this)?START_STICKY:START_NOT_STICKY;}
    private void check() throws Exception {
        JSONObject settings=new Store(this).read();Set<String> selected=Store.apps(settings);
        if(selected.isEmpty())throw new IOException("请先选择要通过此环境联网的应用");
        if(selected.contains(getPackageName()))throw new IOException("不能把启动器本身作为目标应用");
        if(stopping)throw new IOException("操作已取消");
        JSONObject node=Store.node(settings);
        if(tun==null||!selected.equals(activeApps)) {
            int replacementPort;
            try(ServerSocket free=new ServerSocket(0,1,InetAddress.getLoopbackAddress())){replacementPort=free.getLocalPort();}
            String replacementConfig=ProxyConfig.build(node,replacementPort);
            if(VpnService.prepare(this)!=null)throw new IOException("VPN 授权已失效，请重新授权");
            if(tun!=null)NativeCore.stop();
            Builder builder=new Builder().setSession("Region Launcher").setMtu(1500).addAddress(ProxyConfig.ADDRESS,30)
                .addRoute("0.0.0.0",0).addDnsServer(ProxyConfig.DNS);
            builder.addAllowedApplication(getPackageName());
            for(String pkg:selected)builder.addAllowedApplication(pkg);
            // No IPv6 address, route, DNS or allowFamily: Android blocks IPv6 for these UIDs.
            ParcelFileDescriptor replacement=builder.establish();
            if(replacement==null)throw new IOException("VPN 授权已失效，请重新授权");
            if(tun!=null)tun.close();tun=replacement;activeApps=Set.copyOf(selected);
            port=replacementPort;config=replacementConfig;
            ProfilePolicy.lock(this);
            startCore();journal.write("已创建按应用 VPN；应用数量="+selected.size()+"；IPv6 禁用；DNS 通过指定节点");
        } else {
            if(!config.equals(ProxyConfig.build(node,port))) throw new IOException("运行期间节点发生变化；请先停止连接再切换节点");
            ProfilePolicy.lock(this);
        }
        recovery.check(this::verifyConnection,()->{
            if(stopping)throw new InterruptedException("操作已取消");
            journal.write("连续两轮实际联网验证失败，重建代理和转发通道；保留 VPN 接口及应用");
            NativeCore.stop();startCore();
        });
        journal.write("指定代理与环境内 HTTPS 出口验证通过");
    }
    private void verifyConnection()throws Exception{
        if(stopping)throw new InterruptedException("操作已取消");
        List<Health.Result> proxy=Health.check(port,journal);
        if(!Health.usable(proxy))throw new IOException("指定代理无法完成 HTTPS 请求");
        if(stopping)throw new InterruptedException("操作已取消");
        Health.match(proxy,Health.check(null,journal));
    }
    private void startCore() throws IOException {
        if(stopping)throw new IOException("操作已取消");
        String error=NativeCore.start(new File(getFilesDir(),"core").getAbsolutePath(),config,tun.getFd());
        if(!error.isEmpty())throw new IOException(error);
    }
    private void status(String text,boolean ready,String target) {
        if(current!=this)return;
        getSharedPreferences("status",MODE_PRIVATE).edit().putString("message",text).putBoolean("active",tun!=null).putBoolean("ready",ready).apply();
        notifications.notify(1,notice(text,false,ready?target:null));
        sendBroadcast(new Intent(EVENT).setPackage(getPackageName()).putExtra("ready",ready).putExtra("target",target));
    }
    private Notification notice(String text,boolean error,String target) {
        Intent open=target==null?null:getPackageManager().getLaunchIntentForPackage(target);
        if(open==null)open=new Intent(this,MainActivity.class);
        PendingIntent pending=PendingIntent.getActivity(this,target==null?0:target.hashCode(),open,PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this,error?"result":"connection").setSmallIcon(android.R.drawable.ic_lock_lock)
            .setContentTitle("Region Launcher").setContentText(text).setContentIntent(pending).setOngoing(!error).build();
    }
    private void close() {
        try{NativeCore.stop();}catch(Throwable ignored){}
        try{if(tun!=null)tun.close();}catch(IOException ignored){}tun=null;activeApps=Set.of();
        config=null;
        journal.write("VPN 接口已关闭；应用本身未被强制结束");
        if(current!=this)return;
        getSharedPreferences("status",MODE_PRIVATE).edit().putString("message","未连接").putBoolean("active",false).putBoolean("ready",false).apply();
        notifications.cancel(1);notifications.cancel(2);
        sendBroadcast(new Intent(EVENT).setPackage(getPackageName()));
    }
    private void stopConnection(){if(stopping)return;stopping=true;commands.execute(()->{
        try{ProfilePolicy.unlock(this);close();stopForeground(STOP_FOREGROUND_REMOVE);stopSelf();}
        catch(IOException ex){stopping=false;journal.write(ex.getMessage());status(ex.getMessage(),false,null);notifications.notify(2,notice(ex.getMessage(),true,null));}
    });}
    @Override public void onRevoke(){stopping=true;commands.execute(()->{close();stopSelf();});}
    @Override public void onDestroy(){if(current==this)alive=false;stopping=true;commands.execute(()->{if(tun!=null)close();checking=false;});super.onDestroy();}
}
