# Region Launcher 0.3.0-alpha.26

Windows 11 x64 进程环境原型。按本次启动的进程组接管网络和 DNS，保留当前用户原有文件访问权限，不创建虚拟机。

0.3.0-alpha.26 支持局域网 IPv4 的 TCP/UDP 直连：`10.0.0.0/8`、`172.16.0.0/12`、`192.168.0.0/16` 和 `169.254.0.0/16`。公网仍走指定代理，53 端口 DNS 优先使用专用解析器，IPv6 仍禁用。无需更新后台服务；需退出旧运行环境并重新启动，新规则才会生效。详见 [局域网访问](docs/lan.md)。

0.3.0-alpha.25 支持 x86（32 位）和 x64（64 位）目标程序及混合位数子进程树。按实际进程位数加载同一套网络、DNS 和地区模块；跨位数加载使用匹配的辅助程序，初始化失败时不运行目标主线程。仍要求宿主 Windows 为 x64，无需更新后台服务。详见 [32 位支持与验收](docs/bitness.md)。

0.3.0-alpha.24 添加按需“检测并修复”：点击按钮或启动新程序前验证实际 HTTPS 联网，连续两轮均失败才重启本启动器持有的独立代理，并再次验证。网络正常或复查恢复时不重启；正常运行期间不做定时或随机检测。代理退出后保留已有软件和运行组，下一次上述操作会重新启动代理；不再连带关闭软件。重连可能中断旧连接，由软件重新建立；节点本身不可用时仍会失败并阻止新程序启动。无需更新后台服务。详见 [联网检测与日志](docs/connectivity.md)。

0.3.0-alpha.23 修复已有运行环境中启动 Claude MSIX 版时出现 Windows error 5 的问题。创建进程时就加入本次运行组，再检查归属、加载地区与连接模块、恢复主线程；加入失败时不运行目标程序。本机 Claude 5.1231.2 已打开欢迎页面并验证实际 TCP、UDP 和 DNS 请求经过启动器代理；未登录账户，登录后的功能未验收。无需更新后台服务。详见 [Claude 启动验证](docs/claude-startup.md)。

0.3.0-alpha.22 扩展地区设置的注册表读取覆盖：环境内通过常用 Win32 查询、枚举以及 ntdll 查询/枚举读取当前地区、默认语言、日期数字格式和时区时，返回该软件的启动配置。覆盖仅发生在注入进程内，不写系统注册表；父子进程及并行的不同地区环境已验证，外部读取在启动前、运行期间、退出后保持一致。无需更新后台服务。具体覆盖项和限制见 [注册表地区覆盖](docs/registry-region.md)。

0.3.0-alpha.21 修复添加/编辑软件窗口底部按钮被裁切的问题。“保存”和“取消”固定在底部，上方设置区域在空间不足时滚动；国家或地区标签按文字宽度布局，避免换行挤占高度。无需更新后台服务。

0.3.0-alpha.20 添加启动失败提醒：窗口显示时弹出错误提示，隐藏到托盘或最小化时发送 Windows 托盘通知，点击通知恢复启动器查看日志。覆盖配置、代理检测及后台启动程序未能启动目标软件等失败；主动退出取消启动时不提醒，目标程序成功启动后的正常结束不误报。通知展示遵循 Windows 通知和勿扰设置。无需更新后台服务。

0.3.0-alpha.19 改为单实例运行：同一 Windows 用户和桌面会话只保留一个启动器，即使从不同目录打开也是如此。再次运行会唤出已有窗口，包括恢复托盘隐藏或最小化的窗口；重复进程随即退出，不创建第二套代理或运行组。正常退出或异常终止后可以重新启动。无需更新后台服务；首次升级请先从托盘退出旧版启动器。

0.3.0-alpha.18 为托盘、窗口和程序文件统一使用专用图标：深青色圆角底、白色地球与浅青色启动箭头。内置 16～256 像素的九种尺寸，适配小尺寸托盘与不同缩放比例，无需外置图片文件。无需更新后台服务。

同时修复搜索下拉框逐字删除后旧选项回填、重新输入后匹配异常，以及鼠标选择结果后自动出现蓝色全选的问题。搜索会保留实际输入与光标位置，支持逐字退格或 Delete 删除后继续输入。

使用托盘菜单统一退出：点窗口叉号或 Alt+F4 只隐藏窗口，运行程序与代理保持工作；点击托盘图标或菜单“打开启动器”恢复窗口。从托盘选择“退出（结束全部运行）”会取消检测与启动准备、结束本启动器的全部运行组及独立代理，最后退出启动器。退出前请先保存环境内程序的文件；不会结束其他启动器实例或外部程序。

国家或地区、语言、时区下拉框可直接输入显示名称、语言代码和时区标识筛选，例如 `美国`、`en-US`、`Pacific`。清空显示全部，回车选择结果，Esc 或未选择就离开时恢复已有设置。滚轮逐行浏览列表，不直接改变保存的选项。

授权链接浏览器仍支持复用当前环境内的 Chrome / Edge 独立资料，见 [授权浏览器](docs/link-browser.md)。

0.3.0-alpha.11 修复程序为子进程指定自定义环境变量时，丢失启动器参数并报 DLL 初始化失败（1114）的问题。保留本次运行的地区、DNS 和连接通道，支持 ANSI/Unicode 环境块；不向自定义环境复制其他父进程变量。ChatGPT 辅助组件出现了同类错误，真实远程连接仍待重启应用复查。当前通道已通过加密 WebSocket 握手、回显及空闲后续传测试。见 [子进程与 WebSocket 验证](docs/child-environment-websocket.md)。无需更新后台服务。

0.3.0-alpha.10 支持为软件列表中的每个条目分别保存国家或地区、语言、时区和自动冬夏时间。选择软件即加载对应设置，主界面修改后自动保存；添加、编辑窗口也可配置。同一运行会话可以启动不同地区设置的程序及其子进程，代理节点仍共用。旧列表自动继承原地区配置。本版无需更新后台服务。格式与验证见 [软件列表](docs/applications.md)。

0.3.0-alpha.9 修复 UU 并行运行时接管 Chrome 等程序连接的问题：在进程内保存原始目标，经本次会话的专用连接代理处理 TCP、UDP 和 DNS；补充异步收发、网卡绑定与代理关联恢复支持。没有浏览器专用代理参数，不修改 UU、Clash 或宿主机网络设置。本机已安装的后台无须重新安装。验证范围见 [UU 并行运行](docs/uu-coexistence.md)。

0.3.0-alpha.8 将握手检测升级为指定代理与运行环境内两层 HTTPS 联网及出口校验，启动软件前自动检测；增加 UTF-8 文件日志和“打开日志”按钮，并修复保存新节点后继续使用旧内核的问题。见 [联网检测与日志](docs/connectivity.md)。

0.3.0-alpha.7 增加可保存的软件列表：下拉选择软件，界面添加、编辑、删除名称、程序路径和启动参数；重启保留上次选择，旧版程序配置自动迁入。配置格式见 [软件列表](docs/applications.md)。

0.3.0-alpha.6 合并为一个“启动所选程序”按钮：首次创建环境，后续自动加入当前环境。程序日志优先按 UTF-8 解码，遇到无效 UTF-8 时按 Windows 本地代码页解码，修复中文日志乱码。

0.3.0-alpha.5 改为 UTF-16 路径加载运行环境 DLL，支持启动器目录含中文、日文及 emoji；父子进程共用同一加载实现。验证和初始化时机的边界见 [Unicode 路径验证](docs/validation-unicode-20261110.md)。

0.3.0-alpha.4 支持机场订阅链接、多节点列表、筛选和手动更新。入口：“独立代理配置…” → “机场订阅 / 选节点…”。支持 Clash/Mihomo YAML、JSON、Base64 及常见分享链接列表；选中节点后校验并保存。见 [订阅使用说明](docs/subscriptions.md)。

0.3.0-alpha.3 增加“独立代理配置”窗口：Hysteria2、VLESS、VMess、Trojan、Shadowsocks、TUIC、AnyTLS 表单，单节点 JSON 导入、高级节点参数、内核校验及保存前备份。运行期间锁定配置。使用步骤与验证边界见 [代理配置](docs/proxy-settings.md)。本版未更改网络后台或驱动，已安装 alpha.2 后台的本机无须重装。

0.3.0-alpha.2 修复了退出时提前关闭网络句柄、可能丢失环境外数据包的问题。本机后台已更新，12 轮启停期间 6466 次外部通信均成功，宿主网络设置和外部代理进程保持一致，五组通用网络回归通过。见 [启停修复验收](docs/validation-lifecycle-20261109.md) 和 [使用方法](docs/quick-start.md)。

当前重点是验证通用网络机制。Claude Desktop 已完成启动和启动阶段网络验证，完整桌面应用功能及全部单实例复用路径仍未验收；原型测试不是账号安全或所有程序均兼容的证明。

## 统一运行路径

1. 先准备网络引擎，再暂停创建目标进程并加入 Windows Job Object；通过 UTF-16 路径加载公共接口模块，成功后恢复主线程。
2. TCP、UDP 按进程组匹配。同一可执行文件在组外运行时不匹配这些代理规则。
3. 公网 IPv4 连接交给独立 Mihomo/SOCKS5 通道，经过同一个固定节点；局域网 IPv4 由本地转发器直连。
4. Windows DNS 接口明确使用专用解析服务器。异步查询保留回调、取消和 Windows 内存释放接口。组内直接发送的 UDP/TCP 53 数据包统一转交专用解析器。
5. 专用解析器只监听 `127.0.0.153:53`，经固定节点访问加密 DNS。它是普通宿主机进程，不启用全局 TUN。
6. 组内 IPv6 TCP/UDP 由网络引擎阻断，AAAA 地址查询返回无记录，常规 ICMPv6 入口禁用。IPv6 套接字句柄仍允许创建，因为 Windows 的 IPv4 名称查询内部也使用它进行地址排序；创建句柄不代表允许 IPv6 通信。
7. 网络环境内的 WinHTTP 系统代理查询、默认/自动会话和 WinINet 默认会话使用直接连接，由上述网络引擎接管；清除目标进程继承的 HTTP_PROXY、HTTPS_PROXY、ALL_PROXY、NO_PROXY。宿主机代理配置及组外环境变量保持不变。

所有目标程序使用同一套代码；启动器只传入用户填写的程序参数。子进程继承网络进程组与公共接口模块。

系统代理、DNS 服务器、路由和网卡 IPv6 绑定不修改。不清空宿主机 DNS 缓存，后台服务仅为自身 TCP 34010、UDP 34011 转发端口创建规则，卸载时清理。WinDivert 驱动按需加载，仍有网络处理开销，也可能留下驱动服务登记。

## 地区与文件

- 国家、语言、时区按软件条目分别保存，选项来自 Windows；初始默认美国、英语（美国）、洛杉矶时区。修改对该软件下次启动生效，已经运行的程序保持其启动时的设置。
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
dotnet run --project tests/Connectivity/Connectivity.csproj -c Release -- ./build/test-results/connectivity
dotnet run --project tests/ApplicationLibrary/ApplicationLibrary.csproj -c Release -- ./build/test-results/application-library
dotnet run --project tests/UiEntryPoint/UiEntryPoint.csproj -c Release -- (Resolve-Path ./build/service-bin/RegionLauncher.dll).Path
python -X utf8 tests/test_unified_dns.py --failure
python -X utf8 tests/test_locale.py
python -X utf8 tests/test_app_environments.py
./build/ChildEnvironmentTest.exe
./build/JobProcessTest.exe
python -X utf8 tests/test_child_environment.py
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
