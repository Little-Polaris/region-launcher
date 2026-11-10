# UU 并行运行：连接接管验证

## 根因证据

2026-11-11，UU 加速器保持运行。相同 SHA-256 的 .NET 联网探针使用 `UseProxy=false`：命名为 `RouteCheck.exe` 时，连接目标是网站公网地址；只改名为 `chrome.exe`，连接目标变成 `127.0.0.1:1111`，监听者为 `uu.exe`。系统代理仍为 `127.0.0.1:7890`；启动器独立内核监听 `17890`。

`uunetfilter` 驱动正在运行。具体 UU 规则名称未取得；日志不是明文，WFP 状态导出需要管理员。现有证据证明接管与程序名有关，不能用普通名称的检测程序推断 Chrome 的路径。原始对照记录：`build/diagnostics/uu-routing/comparison.json`。

TCP 初步修复后，名为 `chrome.exe` 的原生 UDP STUN 探针仍返回国内出口 `117.136.38.112`。因此必须同时处理 TCP、UDP 和 DNS。

## 当前实现

`RegionHook` 在进程内把原始 IPv4 目标交给本次会话的命名管道，然后让原生 Winsock 连接或发送到专用本地端口。`RegionSession` 验证请求者属于本次 Job，并验证实际连入者的 PID，再经指定 SOCKS5 转发。

- TCP 覆盖 `connect`、`WSAConnect`、`ConnectEx`，保留非阻塞、半关闭和原始 `getpeername`。
- UDP 覆盖已连接套接字及 `sendto`、`WSASendTo`、通过扩展指针取得的 `WSASendMsg`；支持同步、事件、回调、IOCP 接收的来源地址还原。
- UDP 使用 SOCKS5 UDP ASSOCIATE，控制连接断开后保留应用侧端口并重建关联。重连期间丢包由应用重试，不回退到公网直连。
- 显式绑定本机 IPv4 网卡的套接字使用本地回环传输，`getsockname` 保留应用请求的地址及实际端口。这解决了 Windows 拒绝从网卡绑定地址发送到回环代理的问题，也避免在物理网卡上增加转发监听端口。
- 配置专用 DNS 时，原始 TCP/UDP 53 请求统一交给该本地 DNS 入口，包括程序明确指定其他解析器的情况。专用内核负责经选定节点解析。
- 原生 IPv6 连接和 UDP 发送拒绝；允许承载 IPv4 的映射地址。
- 其他本机连接保留，未注入的程序不经过此连接代理。现有进程组数据包引擎继续提供底层约束。

实现不按浏览器名称设置策略，不添加浏览器代理或 DNS 参数，不改宿主机代理、路由或 DNS，不停止 UU，不更新已安装服务。

## 已通过的检查

- `python tests/test_connect_broker.py`：名为 `chrome.exe` 的五种 TCP/子进程路径；Python 48 次并发 IPv4/映射连接、半关闭、peer 还原；代理不可用时不能连接真实可用的直连目标。
- `python tests/test_datagram_broker.py`：九种原生 UDP 收发/完成路径、原始来源还原、同一套接字多目标、IPv4 映射、显式网卡绑定、关联断开后恢复、IPv6 阻止；代理不可用时，真实可用的直连目标收不到数据。
- `python tests/test_uu_live.py <本机私有配置>`：完整服务会话内，名为 `chrome.exe` 的原生已连接/未连接 UDP STUN、.NET HTTPS 及普通 Windows curl 均使用 `15.253.23.247`；原生 Windows DNS API 和原始 TCP/UDP DNS 成功，独立见证返回美国解析器；AAAA/IPv6 阻止。此结果只表示当次节点的实测，不固定节点地址。
- 真实 Chrome 检测页已通过浏览器工具读取：中国出口、Cloudflare 和 Claude 出口均为 `15.253.23.247`，DNS 为美国 Cloudflare 解析器 `104.22.194.84`，页面显示未检测到 DNS 泄露；时区为洛杉矶，首选语言为 en-US。采样的已建立 TCP 连接指向本次本地转发端口，没有 `1111`。
- 实网原生 UDP 四种模式（已连接、未连接，以及两种显式网卡绑定方式）均为同一美国出口。探针还覆盖原生 Windows DNS 接口、原始 UDP/TCP 53、普通 Windows curl 和 .NET HTTPS。
- 最终客户端的 `Connectivity --live` 检查通过：创建环境及加入已有环境的 HTTPS 出口一致，加入检测不会结束原有程序。地区/语言、自动冬夏时间、固定标准时间、父子进程、Unicode DLL 路径及宿主文件访问回归通过。

验收记录在忽略目录 `build/test-results/uu-live`、`datagram-broker`、`uu-browser` 及 `build/diagnostics/uu-routing`，不包含在发布包内。

## 外部程序连续性

最终三种结束方式（停止事件、自然退出、会话所有者退出）的连续性测试通过，记录为 `build/test-results/lifecycle-20261111-022316/results.json`：持续/新建 TCP、UDP，以及既有/新建 HTTPS 共 2014 次通信，失败数为 0。

另外在完整实网代理探针运行期间，连续监测外部程序 60 秒：`build/test-results/lifecycle-20261111-023007/results.json` 记录 12228 次外部通信，失败数为 0（4037 次持续 TCP、1926 次新建 TCP、4037 次持续 UDP、2019 次新建 UDP、146 次已有 HTTPS、63 次新建 HTTPS）。HTTPS 继续使用外部 Clash 的 7890 入口，未关闭证书校验。UU 进程在测试全过程保留。

早期失败记录仍保留：

- 百度见证出现 Schannel `CRYPT_E_REVOCATION_OFFLINE`，包括环境启动前；没有关闭证书检查。
- Cloudflare 见证出现间歇 TLS 握手失败，启动前、启动中及退出后均有记录。一次对照在环境启动前就失败而未执行任何环境启动。
- 这些失败在启动前也有出现，不能全部归因于启动器；后续通过结果也不能证明外部网络永不波动。

## 浏览器复查与范围

旧标签页的读取连接失效，后来通过同一浏览器的新检测标签页完成实际结果读取。网站 WebRTC 深度页面存在脚本错误；本地原生 WebRTC 测试页也不产生 ICE 候选。只读检查确认资料副本已有 WebRTC Control 扩展，策略为 `disable_non_proxied_udp`。没有修改该设置，也没有把“WebRTC 已禁用或无泄露”当作浏览器 UDP 成功的证明；UDP 出口由独立原生探针验证。

窗口关闭阶段，Windows UI 工具再次因无法确认 URL 而停止；未使用替代 UI 自动化绕过。测试会话随后通过已有停止事件接口结束，网络子进程和专用内核退出；外部原浏览器及 UU 保留。

当前转发以普通 Windows Winsock 客户端应用为目标；不声称可以隔离故意绕过进程钩子或使用自有内核驱动的程序。UDP 回复须来自请求目标的同一 IP/端口；改变回复端口的协议尚未覆盖。显式网卡绑定转换为回环传输，环境内程序不适合用作需要从局域网接收入站连接的服务器。Claude Desktop 仍未验收。
