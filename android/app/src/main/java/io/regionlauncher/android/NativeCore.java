package io.regionlauncher.android;

final class NativeCore {
    static { System.loadLibrary("regioncore"); }
    static native void attach(android.net.VpnService service);
    static native String start(String privateDirectory, String config, int tunFd);
    static native void stop();
    static native String version();
}
