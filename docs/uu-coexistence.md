# UU 并行运行：连接接管验证

## 已复现的问题

2026-11-11，UU 加速器保持运行。相同 SHA-256 的联网探针使用 `UseProxy=false`：

- 命名为 `RouteCheck.exe` 时，IPv4 连接目标为网站的公网地址。
- 只改名为 `chrome.exe`，IPv4 连接目标变成 `127.0.0.1:1111`，监听者为 `uu.exe`。
- 系统代理仍为 `127.0.0.1:7890`；启动器独立内核监听 `17890`。
- `uunetfilter` 驱动正在运行。具体 UU 规则名称未取得；日志内容不是明文，WFP 状态导出需要管理员。

这证明触发因素包含程序名，不能通过普通名称的检测程序推断 Chrome 的真实路径。

## 当前实现

`RegionHook` 在进程内接管 `connect`、`WSAConnect` 和通过 `WSAIoctl` 获取的 `ConnectEx`。先把原始 IPv4 目标交给本次会话的命名管道，然后让原生 Winsock 连接到一次性的本地监听端口。

`RegionSession` 内的连接代理验证请求者属于本次 Job，并验证实际连入者的 PID。代理通过启动器指定的 SOCKS5 端口连接原始目标，再双向传递字节。不会将网站域名、TLS 或 HTTP 交给特殊浏览器适配代码。

- 保留原生非阻塞、事件和 IOCP 的本地连接完成机制。
- `getpeername` 返回原始目标；支持 IPv4 映射地址、半关闭和子进程注入。
- SOCKS 失败关闭连接，不回退到目标直连。
- 原始本机连接继续保留，UDP 仍由现有进程组数据包引擎处理。
- 原生 IPv6 `connect` 在环境内拒绝；完整 UDP/IPv6 约束仍需数据包引擎。
- 不修改已安装服务，不停止 UU，不修改宿主机代理或浏览器资料。

## 当前验证证据

`python tests/test_connect_broker.py` 使用独立 Job 和 `ConnectHarness`，无需停止正在运行的旧环境：

- 名为 `chrome.exe` 的探针：普通 connect、WSAConnect、非阻塞 connect、ConnectEx、注入子进程五条路径成功。
- Python：48 次并发 IPv4 / IPv4 映射连接，原始 peer 地址、半关闭和数据校验成功。
- 原生 IPv6 connect 拒绝。
- 代理端口不可用时，另一个真实可用的直连监听器未收到连接。

独立连接代理实网验证：名为 `chrome.exe` 的 .NET 探针请求 Cloudflare 和国内检测站，均 HTTP 200、出口 `15.253.23.247`。原始结果保存在忽略目录 `build/diagnostics/uu-routing/broker-live.json`。

## 尚未完成的验收

此实现还未作为新版本发布。上述探针验证不能替代实际 Chrome：

- 完整启动器会话下的 Chrome 页面、连接归属、DNS 和 UDP。
- UU 持续运行时，完整会话的启动、加入、退出与外部 TCP/UDP/HTTPS 连续性。
- 代理故障及恢复、并发压力、会话退出资源回收。
- 更新本机启动器、版本号、打包及包内容验证。

当前旧版启动器和浏览器仍在运行，尚未替换。Windows 浏览器读取工具在本轮因无法确认 URL 而停止，未改用其他 UI 自动化绕过。

尝试将短暂探针加入现存 GUI 会话时，Windows 的 Job 分配返回 `ERROR_ACCESS_DENIED`。该尝试没有产生有效的 UDP/DNS 验收结果，也没有停止原会话；临时探针已清理。因此完整会话测试需要正常结束旧测试会话后再启动新版。

新增原生探针 `RegionProbe --stun <IPv4> 3478 [connected]` 可分别检查已连接和未连接 UDP 的公网出口，后续会话验证使用 [Cloudflare 公共 STUN 服务](https://developers.cloudflare.com/realtime/turn/)。目前只运行了外部对照，得到国内出口，不能据此声称内部 UDP 已通过。
