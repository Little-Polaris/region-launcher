package io.regionlauncher.android;

import android.app.admin.DeviceAdminReceiver;
import android.app.admin.DevicePolicyManager;
import android.content.*;

public final class ProfileAdmin extends DeviceAdminReceiver {
    @Override public void onProfileProvisioningComplete(Context context,Intent intent) {
        DevicePolicyManager policy=context.getSystemService(DevicePolicyManager.class);
        ComponentName admin=new ComponentName(context,ProfileAdmin.class);
        policy.setProfileName(admin,"Region Launcher");policy.setProfileEnabled(admin);
        context.startActivity(new Intent(context,MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
    }
}
