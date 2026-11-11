package io.regionlauncher.android;

import android.app.admin.DevicePolicyManager;
import android.content.*;
import android.os.UserManager;
import java.io.IOException;

/** Lockdown is limited to the work profile this application itself manages. */
final class ProfilePolicy {
    static boolean ownsWorkProfile(Context c){
        return c.getSystemService(UserManager.class).isManagedProfile()
            &&c.getSystemService(DevicePolicyManager.class).isProfileOwnerApp(c.getPackageName());
    }
    static void lock(Context c)throws IOException{
        if(!ownsWorkProfile(c))return;
        DevicePolicyManager policy=c.getSystemService(DevicePolicyManager.class);
        ComponentName admin=new ComponentName(c,ProfileAdmin.class);
        try{
            String existing=policy.getAlwaysOnVpnPackage(admin);
            if(existing!=null&&!existing.equals(c.getPackageName()))throw new IOException("工作资料已有其他始终开启的 VPN，未替换");
            policy.setAlwaysOnVpnPackage(admin,c.getPackageName(),true);
            if(!policy.isAlwaysOnVpnLockdownEnabled(admin))throw new IOException("工作资料的断网保护没有启用");
        }catch(IOException ex){throw ex;}catch(Exception ex){throw new IOException("无法启用工作资料断网保护，应用没有启动",ex);}
    }
    static void unlock(Context c)throws IOException{
        if(!ownsWorkProfile(c))return;
        DevicePolicyManager policy=c.getSystemService(DevicePolicyManager.class);
        ComponentName admin=new ComponentName(c,ProfileAdmin.class);
        try{if(c.getPackageName().equals(policy.getAlwaysOnVpnPackage(admin)))policy.setAlwaysOnVpnPackage(admin,null,false);}
        catch(Exception ex){throw new IOException("无法解除工作资料断网保护，已保留当前连接",ex);}
    }
}
