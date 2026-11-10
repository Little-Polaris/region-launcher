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
- 配置专用 DNS 时，原始 TCP/UDP 53 请求统一交给该本地 DNS 入口，包括程序明确指定其他解析器的情况。专用内核负责经选定节点解析。
- 原生 IPv6 连接和 UDP 发送拒绝；允许承载 IPv4 的映射地址。
- 其他本机连接保留，未注入的程序不经过此连接代理。现有进程组数据包引擎继续提供底层约束。

实现不按浏览器名称设置策略，不添加浏览器代理或 DNS 参数，不改宿主机代理、路由或 DNS，不停止 UU，不更新已安装服务。

## 已通过的检查

- `python tests/test_connect_broker.py`：名为 `chrome.exe` 的五种 TCP/子进程路径；Python 48 次并发 IPv4/映射连接、半关闭、peer 还原；代理不可用时不能连接真实可用的直连目标。
- `python tests/test_datagram_broker.py`：九种原生 UDP 收发/完成路径、原始来源还原、同一套接字多目标、IPv4 映射、关联断开后恢复、IPv6 阻止。
- `python tests/test_uu_live.py <本机私有配置>`：完整服务会话内，名为 `chrome.exe` 的原生已连接/未连接 UDP STUN、.NET HTTPS 及普通 Windows curl 均使用 `15.253.23.247`；原生 Windows DNS API 和原始 TCP/UDP DNS 成功，独立见证返回美国解析器；AAAA/IPv6 阻止。此结果只表示当次节点的实测，不固定节点地址。
- 真实 Chrome 已启动，连接代理日志可见 Chrome 网络子进程的 TCP、UDP、DNS 目标；采样的已建立 TCP 连接均指向本次本地转发端口，没有 `1111`。这不能替代页面最终结果。

验收记录在忽略目录 `build/test-results/uu-live`、`datagram-broker`、`uu-browser` 及 `build/diagnostics/uu-routing`，不包含在发布包内。

## 未通过或尚未完成

三种结束方式（停止事件、自然退出、会话所有者退出）完成了运行检查。外部持续/新建 TCP、UDP 及既有 HTTPS 在这几轮没有失败，但新建 HTTPS 存在失败，完整连续性断言没有通过：

- 百度见证出现 Schannel `CRYPT_E_REVOCATION_OFFLINE`，包括环境启动前；没有关闭证书检查。
- Cloudflare 见证出现间歇 TLS 握手失败，启动前、启动中及退出后均有记录。一次对照在环境启动前就失败而未执行任何环境启动。
- 后续仍需外部网络基线、启动/加入/退出对照，不能把这些失败直接排除或归因于启动器。

浏览器扩展读取连续超时。Windows 窗口工具随后因无法可靠确定当前 URL 而停止本轮 Computer Use，未改用其他 UI 自动化绕过，未取得实际检测页最终结果。测试 Chrome 窗口暂时保留。

当前是未发布的开发修复：尚需真实 Chrome 页面验收、完整连续性检查、退出资源回收、最终版本号及打包验证。此前交付的 `0.3.0-alpha.8` 尚未替换，不将探针通过当作完整应用验收。

当前转发以普通 Windows Winsock 应用为目标；不声称可以隔离故意绕过进程钩子或使用自有内核驱动的程序。UDP 回复须来自请求目标的同一 IP/端口；改变回复端口的协议尚未覆盖。
