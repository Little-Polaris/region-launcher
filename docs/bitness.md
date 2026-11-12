# 32 位与 64 位程序

启动器运行于 x64 Windows，支持 x86 和 x64 应用。依据实际进程位数选择模块，而非安装包文件名；64 位产品的安装外壳可以是 32 位。

## 接管方式

- 两种位数共用网络、DNS、地区、语言、时区、注册表和子进程处理源码。
- x64 进程使用 `RegionHook.dll`，x86 进程使用 `RegionHook32.dll`。
- 跨位数时使用 `RegionInject32.exe` / `RegionInject64.exe`，仅继承已打开的目标进程句柄；不根据进程号重新打开目标。
- 主线程始终保持暂停，成功加入运行组并加载模块后才恢复。模块或辅助程序缺失、损坏、超时均按失败处理，不允许自动退回直接联网。
- 辅助程序不请求管理员权限、不改系统配置；后台网络服务及驱动不变。

## 验证入口

```powershell
python -X utf8 tests/test_bitness.py
python -X utf8 tests/test_locale.py
python -X utf8 tests/test_unicode_paths.py
$env:REGION_PROBE_BINARY='RegionProbe32.exe'
python -X utf8 tests/test_connect_broker.py
$env:REGION_DATAGRAM_BINARY='DatagramProbe32.exe'
python -X utf8 tests/test_datagram_broker.py
$env:REGION_DNS_PROBE='DnsProbe32.exe'
$env:REGION_TEST_DNS_ADDRESS='127.0.0.154'
python -X utf8 tests/test_unified_dns.py
# 需要已经运行的独立代理及专用 DNS；默认代理端口 17890。
python -X utf8 tests/test_bitness_live.py
```

覆盖混合三代进程树、暂停启动、ANSI/Unicode 子进程创建、中文/emoji 模块路径、地区及注册表读取、自动冬夏时间、原权限文件访问、TCP/UDP/DNS、IPv6 阻断及接管失败时阻止运行。独立进程的地区和 DNS 对照在启动前后保持原状。

2026-11-12 本机上述两种位数测试均通过。两种位数的真实 HTTPS 请求访问 Cloudflare 和国内检测站均返回 HTTP 200，四次检测为同一代理出口。实际 Claude 安装程序已确认是 x86 外壳；此次尚未执行安装操作，因此这些结果不能代替安装完成后的应用验收。

## 边界

增加 x86 支持不等于接管由 Windows 服务、COM 或外部已存在进程代为启动的全部程序，也不会给应用额外权限。安装器如主动请求管理员权限，仍需用户确认。ARM64 与 16 位程序不支持。应用保存的自定义语言偏好仍可能覆盖系统接口返回值。
