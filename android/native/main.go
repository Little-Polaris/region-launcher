package main

/*
#include "bridge.h"
*/
import "C"

import (
    "fmt"
    "io"
    "os"
    "path/filepath"
    "sync"
    "syscall"
    "unsafe"

    core "github.com/metacubex/mihomo/constant"
    "github.com/metacubex/mihomo/component/dialer"
    "github.com/metacubex/mihomo/hub/executor"
    "github.com/metacubex/mihomo/listener"
    listenerconfig "github.com/metacubex/mihomo/listener/config"
    "github.com/metacubex/mihomo/tunnel"
    "github.com/metacubex/mihomo/tunnel/statistic"
    "github.com/sirupsen/logrus"
)

var lock sync.Mutex
var running bool

func init() {
    logrus.SetOutput(io.Discard)
    // Install once; changing a global socket hook while dialing would race.
    dialer.DefaultSocketHook = func(network, address string, conn syscall.RawConn) error {
        var protectError error
        err := conn.Control(func(fd uintptr) {
            if C.rl_protect(C.int(fd)) == 0 { protectError = fmt.Errorf("VPN upstream socket protection failed") }
        })
        if err != nil { return err }; return protectError
    }
}

func text(env *C.JNIEnv, value C.jstring) string {
    chars := C.rl_chars(env, value)
    defer C.rl_release(env, value, chars)
    return C.GoString(chars)
}
func result(env *C.JNIEnv, value string) C.jstring {
    chars := C.CString(value); defer C.free(unsafe.Pointer(chars))
    return C.rl_string(env, chars)
}
func stop() {
    if !running { return }
    tunnel.OnSuspend()
    statistic.DefaultManager.Range(func(c statistic.Tracker) bool { _ = c.Close(); return true })
    for _, proxy := range tunnel.Proxies() { _ = proxy.Close() }
    for _, provider := range tunnel.Providers() { if closer, ok := provider.(io.Closer); ok { _ = closer.Close() } }
    // Reset the cached configuration as well as closing the listener: an fd can be reused.
    listener.ReCreateTun(listenerconfig.Tun{}, tunnel.Tunnel)
    executor.Shutdown()
    listener.ReCreateMixed(0, tunnel.Tunnel)
    running = false
}

//export Java_io_regionlauncher_android_NativeCore_start
func Java_io_regionlauncher_android_NativeCore_start(env *C.JNIEnv, klass C.jclass, home C.jstring, config C.jstring, tunFd C.jint) C.jstring {
    lock.Lock(); defer lock.Unlock()
    // Logs exported by Java are deliberately summarized; never copy raw node credentials.
    logrus.SetOutput(io.Discard)
    directory := text(env, home)
    if err := os.MkdirAll(directory, 0700); err != nil { return result(env, err.Error()) }
    core.SetHomeDir(directory); core.SetConfig(filepath.Join(directory, "runtime.json"))
    cfg, err := executor.ParseWithBytes([]byte(text(env, config)))
    if err != nil { return result(env, "代理配置校验失败，请检查节点参数") }
    if cfg.General.AllowLan || cfg.General.IPv6 || cfg.General.Tun.AutoRoute || cfg.General.Tun.AutoRedirect || cfg.Controller.ExternalController != "" {
        return result(env, "拒绝非隔离网络配置")
    }
    if len(cfg.Rules) != 1 || cfg.Rules[0].RuleType() != core.MATCH || cfg.Rules[0].Adapter() != "RegionExit" {
        return result(env, "必须使用固定节点出口")
    }
    fd := 0
    if tunFd >= 0 {
        fd, err = syscall.Dup(int(tunFd))
        if err != nil { return result(env, "无法复制 VPN 接口") }
        cfg.General.Tun.FileDescriptor = fd
        cfg.General.Tun.Enable = true
    } else { cfg.General.Tun.Enable = false }
    stop()
    executor.ApplyConfig(cfg, true)
    running = true
    if tunFd >= 0 && !listener.LastTunConf.Enable {
        stop()
        // The core owns the duplicated descriptor once its TUN constructor runs.
        return result(env, "VPN 数据通道未能建立")
    }
    return result(env, "")
}

//export Java_io_regionlauncher_android_NativeCore_stop
func Java_io_regionlauncher_android_NativeCore_stop(env *C.JNIEnv, klass C.jclass) {
    lock.Lock(); defer lock.Unlock(); stop()
}

//export Java_io_regionlauncher_android_NativeCore_version
func Java_io_regionlauncher_android_NativeCore_version(env *C.JNIEnv, klass C.jclass) C.jstring {
    return result(env, fmt.Sprintf("Mihomo %s / Region Launcher Android", core.Version))
}

func main() {}
