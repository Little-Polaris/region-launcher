# Region Launcher 0.3.0-alpha.7

Windows 11 x64 进程环境原型。按本次启动的进程组接管网络和 DNS，保留当前用户原有文件访问权限，不创建虚拟机。

0.3.0-alpha.7 增加可保存的软件列表：下拉选择软件，界面添加、编辑、删除名称、程序路径和启动参数；重启保留上次选择，旧版程序配置自动迁入。配置格式见 [软件列表](docs/applications.md)。

0.3.0-alpha.6 合并为一个“启动所选程序”按钮：首次创建环境，后续自动加入当前环境。程序日志优先按 UTF-8 解码，遇到无效 UTF-8 时按 Windows 本地代码页解码，修复中文日志乱码。

0.3.0-alpha.5 改为 UTF-16 路径加载运行环境 DLL，支持启动器目录含中文、日文及 emoji；父子进程共用同一加载实现。验证和初始化时机的边界见 [Unicode 路径验证](docs/validation-unicode-20261110.md)。

0.3.0-alpha.4 支持机场订阅链接、多节点列表、筛选和手动更新。入口：“独立代理配置…” → “机场订阅 / 选节点…”。支持 Clash/Mihomo YAML、JSON、Base64 及常见分享链接列表；选中节点后校验并保存。见 [订阅使用说明](docs/subscriptions.md)。

0.3.0-alpha.3 增加“独立代理配置”窗口：Hysteria2、VLESS、VMess、Trojan、Shadowsocks、TUIC、AnyTLS 表单，单节点 JSON 导入、高级节点参数、内核校验及保存前备份。运行期间锁定配置。使用步骤与验证边界见 [代理配置](docs/proxy-settings.md)。本版未更改网络后台或驱动，已安装 alpha.2 后台的本机无须重装。

0.3.0-alpha.2 修复了退出时提前关闭网络句柄、可能丢失环境外数据包的问题。本机后台已更新，12 轮启停期间 6466 次外部通信均成功，宿主网络设置和外部代理进程保持一致，五组通用网络回归通过。见 [启停修复验收](docs/validation-lifecycle-20261109.md) 和 [使用方法](docs/quick-start.md)。

当前重点是验证通用网络机制。完整桌面应用、单实例复用及 Claude Desktop 尚未验收；原型测试不是账号安全或所有程序均兼容的证明。

## 统一运行路径

1. 先准备网络引擎，再暂停创建目标进程并加入 Windows Job Object；通过 UTF-16 路径加载公共接口模块，成功后恢复主线程。
2. TCP、UDP 按进程组匹配。同一可执行文件在组外运行时不匹配这些代理规则。
3. IPv4 连接交给独立 Mihomo/SOCKS5 通道，所有目标经同一个固定节点。
4. Windows DNS 接口明确使用专用解析服务器。异步查询保留回调、取消和 Windows 内存释放接口。组内直接发送的 UDP/TCP 53 数据包统一转交专用解析器。
5. 专用解析器只监听 `127.0.0.153:53`，经固定节点访问加密 DNS。它是普通宿主机进程，不启用全局 TUN。
6. 组内 IPv6 TCP/UDP 由网络引擎阻断，AAAA 地址查询返回无记录，常规 ICMPv6 入口禁用。IPv6 套接字句柄仍允许创建，因为 Windows 的 IPv4 名称查询内部也使用它进行地址排序；创建句柄不代表允许 IPv6 通信。
7. 网络环境内的 WinHTTP 系统代理查询、默认/自动会话和 WinINet 默认会话使用直接连接，由上述网络引擎接管；清除目标进程继承的 HTTP_PROXY、HTTPS_PROXY、ALL_PROXY、NO_PROXY。宿主机代理配置及组外环境变量保持不变。

所有目标程序使用同一套代码；启动器只传入用户填写的程序参数。子进程继承网络进程组与公共接口模块。

系统代理、DNS 服务器、路由和网卡 IPv6 绑定不修改。不清空宿主机 DNS 缓存，后台服务仅为自身 TCP 34010、UDP 34011 转发端口创建规则，卸载时清理。WinDivert 驱动按需加载，仍有网络处理开销，也可能留下驱动服务登记。

## 地区与文件

- 国家、语言、时区可分别选择，选项来自 Windows；默认美国、英语（美国）、洛杉矶时区。开始运行前选择，运行中的环境固定使用启动时的设置。
- 自动冬夏时间：标准时间 UTC−8，夏令时 UTC−7；关闭后全年 UTC−8。
- 通过公共 Windows 接口和环境变量提供地区、语言和时间；直接读取注册表或使用自带地区库的应用可能有不同表现。
- 文件保持原有权限和真实路径；结束环境会终止整个进程组，需先保存文件。

2026-11-09 已在复制原资料的 Chrome 中完成检测页验收：三个出口一致、DNS、时区、语言及安全状态通过，黄色项仅为机房 IP 与延迟。网页语言偏好由用户在副本中设为 en-US；应用保存的自定义语言偏好不一定自动跟随 Windows 默认语言。内外 Chrome 同时联网及进程归属已核对；此前用户报告的外部代理短暂断网尚未查明原因。详见 [本轮验收与证据边界](docs/validation-20261109.md)。

## 后台服务

服务安装、受限接口及当前未完成的验收见 [docs/service.md](docs/service.md)。目前仍为调试原型，安装成功不代表网络已稳定。

## 验证

本机依赖 Visual Studio C++、.NET 10、PowerShell 7 和 Python。测试不安装或登录 Claude。

```powershell
pwsh -NoProfile -File scripts/build.ps1 -BinDirectory ./build/service-bin
$env:REGION_TEST_BIN = (Resolve-Path ./build/service-bin).Path
dotnet run --project tests/ProcessLogs/ProcessLogs.csproj -c Release
dotnet run --project tests/ApplicationLibrary/ApplicationLibrary.csproj -c Release -- ./build/test-results/application-library
dotnet run --project tests/UiEntryPoint/UiEntryPoint.csproj -c Release -- (Resolve-Path ./build/service-bin/RegionLauncher.dll).Path
python -X utf8 tests/test_unified_dns.py --failure
python -X utf8 tests/test_locale.py
python -X utf8 tests/test_unicode_paths.py --network
python -X utf8 tests/test_private_proxy.py
pwsh -NoProfile -File scripts/verify_unified.ps1 -BinDirectory ./build/service-bin
```

首次安装或更新网络后台服务需要管理员确认，安装后完整网络验收从普通权限运行。在线测试还需要构建目录中的 `proxy/mihomo.exe` 和本机私有节点配置 `data/private/proxy.json`；使用独立端口 17891，结束后退出测试内核。不要上传节点配置。

验收覆盖同一程序内外并行、父子进程、同步/异步解析、IPv4/IPv6 联合查询、DNS 缓存隔离、解析拒绝后不回退、TCP/UDP 代理、IPv6 组内阻断与组外正常。`--network-test` 是无注入的包处理层测试入口，界面不提供该模式。

在线测试使用普通 curl、WinHTTP、WinINet、Python 和原生程序，不给它们配置应用级代理。随机 DNS 见证和国内/海外出口分别验证；报告区分已完成项和待确认项。

## 边界

- Windows 11 x64 本机原型；x86、受保护进程、额外代码完整性限制和部分 MSIX 应用尚未覆盖。
- Unicode 加载会触发 Windows 加载器初始化，依赖 DLL 的初始化函数及 TLS 回调可能早于公共接口模块运行。进程已在网络组内，但这些早期代码的 DNS、代理和地区接口接管未验收；主线程保持暂停不代表所有初始化代码尚未执行。
- 外部已有单实例进程、系统服务、COM 激活和默认浏览器回调可能不属于新进程组。完整软件会话隔离仍需继续实现和验证。
- 应用显式指定的代理、自行读取注册表的代理实现和外部本地转发服务不属于“继承系统默认代理”修复的覆盖范围；公共接口检查不能证明所有本地代发路径均已隔离。
- 自带加密 DNS 的应用，其网络连接受统一代理规则处理，但解析器选择与 AAAA 回答不一定经过 Windows DNS 接口。
- IPv6 网络层阻断覆盖 TCP/UDP；常规 ICMPv6 由公共接口模块阻止。私有驱动、原始设备访问和恶意程序不属于此原型的安全保证。
- 驱动异常、网络切换、极端并发和崩溃瞬间的无泄漏保证未完成验证。
- Chrome、Edge、Claude Desktop 的完整操作尚未在这套统一路径下验收。

依赖和许可证见 `docs/SOURCES.md`，验证记录见 `docs/validation.md`。节点凭据、个人资料和私有测试配置不进入 Git 或源码压缩包。
