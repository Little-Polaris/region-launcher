# Region Launcher Android

目标设备：vivo X300，Android 17，未 Root。开发中，尚未达到交付验收。

Android 工程与 Windows 工程分别构建，不改动 Windows 后台服务。

## 目标与边界

- 使用 Android VpnService 只接管选定应用的 IPv4、TCP、UDP 和 DNS；环境内 IPv6 禁用。
- 支持独立节点和机场订阅，保留所选节点与应用列表。
- 只有手动“检测并修复”和启动应用时检查；不做定时或随机联网检测。
- 优先采用独立工作资料运行应用副本，避免与手机个人资料已有应用和 VPN 混用。工作资料的创建与 VPN 首次连接需要用户在手机确认。
- 未 Root 的 VpnService 按应用 UID/资料工作，不能区分同一应用的不同启动入口。直接在个人资料选择某应用，会接管该应用在该资料内的全部流量。
- 普通 Android 应用没有权限为任意第三方应用注入时区或地区。系统支持的每应用语言设置与强制改变其他应用读到的语言不是同一能力。不能通过显示一个设置开关来声称已实现 Windows 版注入。
- 系统级文件访问遵循 Android 权限与文档选择器，不承诺读取其他应用的私有文件。

完整目标包含网络隔离、运行环境设置、启动和恢复、日志、安装包与真实验证；不能把仅编译出 APK 视为完成。

## 目录

- `app`：Android 界面、VPN 与应用管理。
- `native`：Mihomo 的 JNI 接口。
- `scripts`：可复现的依赖准备、构建与打包。
- `tests`：配置、网络隔离和恢复验证。
- 开发依赖与证据放在仓库根目录 `build/android-tools`、`build/test-results/android`。

参考：[Android VPN](https://developer.android.com/develop/connectivity/vpn)、[每应用语言](https://developer.android.com/guide/topics/resources/app-languages)、[Mihomo](https://github.com/MetaCubeX/mihomo)。

## 构建

依赖：JDK 25、Gradle 9.5.0、Android SDK platform 37、build-tools 36.0.0；AGP 固定为 9.3.1。Go、NDK 的官方下载与校验值见 scripts/toolchain-lock.json，Mihomo 固定为 v1.19.33，完整 Go 依赖校验值见 native/go.sum。

用 Android Studio 打开本目录，Gradle 使用项目的 `gradle/wrapper/gradle-wrapper.properties`。Wrapper 固定 Gradle 9.5.0 并校验官方下载包 SHA-256，构建脚本默认使用相同 Wrapper。AGP 9.3.1 不兼容 Gradle 9.3.0；缺少 Wrapper 时 IDE 自动选择旧版会报 `ProjectTypeBinding` 缺失，反复同步或重启不能解决版本不匹配。

在仓库根目录执行：

```powershell
python android/scripts/prepare-tools.py go ndk
pwsh android/scripts/build-native.ps1
pwsh android/scripts/build-app.ps1 -JavaHome '<JDK目录>' -Sdk '<SDK目录>' -Release
python android/scripts/package.py --java-home '<JDK目录>' --sdk '<SDK目录>'
```

依赖下载可传 `--proxy`（Python）或 `-Proxy`（PowerShell），只对该构建过程生效。不要把手机节点配置写入源码。`cmfa` 编译选项使用上游面向 Android VpnService 的模式，避免读取需要 Root 的系统包信息；`with_gvisor` 提供用户态网络栈。

首次 Release 构建在 `.private/` 创建本项目专用签名密钥，该目录忽略于 Git 和交付包。请安全备份该目录；后续 APK 升级必须使用同一密钥。构建目录可清理，签名密钥不可当作普通缓存删除。

打包复用 `outputs/region-launcher-unified/packages/android/` 中的当前开发 APK 和 ZIP，不修改 Windows 启动器和私人数据。打包脚本检查 Git 状态、测试结果、签名、ABI、16 KiB ELF 对齐与内容；这些检查不代替手机运行验收。

安装与能力边界见 [INSTALL.md](INSTALL.md)。网络核查证据保存在忽略目录 `build/test-results/android`，目前模拟器软件模式崩溃且没有连接真机，运行验收未完成。
