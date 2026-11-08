# 2026-11-09 验证与待查问题

## 地区选择与语言差异

已安装应用的保存配置为 GeoID 244、en-US、Pacific Standard Time、AutoDst=true。国家、语言和时区目录分别来自 Windows 的 EnumSystemGeoID、EnumSystemLocalesEx、TimeZoneInfo.GetSystemTimeZones。

本轮对 `outputs/region-launcher-unified/app` 运行 `tests/test_locale.py` 通过：父子进程的美国地区/英语、冬夏日期与切换边界、关闭自动切换后的固定标准时间、宿主文件访问、宿主地区设置不变，以及德国、日本和印度的组合。

已复制的 Chrome 资料保存了 zh-CN,zh 的网页语言；前轮网站读取也显示语言不一致。Chromium 的 renderer_preferences_util.cc 从资料中的 kAcceptLanguages 取得该值，因此系统语言接口通过不能证明应用自身语言偏好已改变。官方代码：
https://chromium.googlesource.com/chromium/src/+/refs/heads/main/chrome/browser/renderer_preferences_util.cc

用户已授权仅修改副本语言偏好。工具拒绝访问 chrome://settings/languages，明确禁止换用其他自动化通道达到同一结果；当时未写入语言设置，交由用户手动调整。用户随后回复“副本语言已改”，以下为调整后的实际验收。

## 实际页面与双浏览器验收

官方浏览器工具读取环境内标签 119040737（https://ip.net.coffee/claude/）的完整可访问性树，并截图核对颜色：

| 要求 | 实测结果 |
| --- | --- |
| 国家及城市 | United States / Los Angeles；中国、Cloudflare、Claude 三个 IPv4 出口均为 15.253.23.247 |
| 时区 | 绿色“时区一致”，America/Los_Angeles (UTC-8) |
| 网页语言 | 绿色“语言一致”，en-US |
| DNS | 绿色“未检测到泄露”，美国 Cloudflare 172.64.216.83 |
| WebRTC | 绿色“WebRTC 已禁用或无泄露”；仅认可页面状态，不据此宣称所有 UDP 场景都通过 |
| 支持地区及服务 | 绿色“正常”“全部服务正常” |
| IP 风险项 | VPN、代理、Tor 均为绿色“未检测到”；机器人绿色“否”；滥用记录绿色“无记录” |
| Cookie | 绿色“已启用” |
| 允许的黄色项 | 机房 IP；claude.ai 551ms 和 anthropic.com 555ms 均为黄色“较慢” |
| 信任评分 | 79，分数为绿色，“良好”为网站自带蓝色等级标签；其余普通信息文字没有红黄风险标记 |

启动器真实窗口 202732 显示美国 (US)、英语(美国) (en-US)、太平洋时间；说明文字为“按 Windows 时区规则自动切换；当前 UTC−08:00”。当前 session.ini 的 AutoDst=1。运行中选择框禁用，结束当前环境后可为下一次运行选择；不通过运行中改变宿主设置实现选择功能。

Windows API 验证当前 Job `Local\RegionLauncher.a06c6e9ed63f44ae9381d73b6cc0513d`：副本主进程 21544 和联网进程 25564 在组内；原资料主进程 6116 和联网进程 49960 在组外。副本窗口 11142178 属于 21544，外部窗口 1904838 属于 6116。副本使用自己的 `chrome-user-profile` 目录，浏览器自行重启后仍留在环境中。全部成员查询成功。

在两份浏览器同时运行时，原浏览器新建临时测试标签访问 Cloudflare trace 成功，出口为日本 57.183.8.118，与环境内美国出口不同；随后同一标签访问百度成功。临时测试标签已关闭，环境内验收页保留。宿主代理和 DNS 与复制前记录一致。用户报告的此前整体断网原因仍未知；本次成功不能证明此前问题已修复或长期稳定。

当前应用五个受测二进制与 `build/system-proxy-bin` 的 SHA256 相同；已安装服务及 ProxyBridgeCore 与 `build/service-bin` 相同。22:24 的五项网络套件测试使用已安装服务，包含 IPv6 组内阻断、组外不变及 TCP/UDP/DNS 进程隔离，本轮未更新服务。00:11 的地区测试直接针对当前发布目录。没有为正在运行的浏览器再启动冲突的网络测试会话。

当前证据位于 `build/test-results/acceptance-20261109/` 的 browser-scope.json 和 invariants.json。网页文字与截图来自本轮官方工具输出。此验收证明本机当前配置达到所要求的检测页状态，不扩大为 Claude Desktop、任意应用、自定义语言偏好自动跟随、长期稳定或账号安全保证。

## 外部 Chrome 短暂断网

用户报告原 Chrome 无法打开网页，副本可联网；重启外部代理后恢复。此次没有捕获故障发生时的网络状态，不能认定根因或宣称已修复。

只读检查结果：

- Clash Mi 主进程在 00:14:56 启动，服务进程在 00:14:58 启动，后者监听 127.0.0.1:7890。
- 启动器专用 Mihomo 仍为 22:52:49 启动的原进程，监听 127.0.0.1:17890 和专用 DNS 127.0.0.153:53。检查时两个代理端口不同。
- 网络服务日志于 00:14:19 最后写入，结尾为 WinDivertSend error 6 和 ProxyBridge stopped；当时的完整停止原因未记录。不可单凭停止期间的错误 6 判断先前断网原因。
- 检查时原环境的 RegionSession 已不存在，只有外部原资料 Chrome 主进程 6116（00:17:11 启动）。当前通道可用检查不构成两组浏览器并发隔离验收。
- 在宿主用户权限下分别经 7890 和 17890 请求 Cloudflare trace，均返回 HTTP 200，耗时分别约 0.30 秒、0.67 秒。没有改变任何代理设置。
- 宿主系统代理仍启用并指向 127.0.0.1:7890。受限终端的 HKCU 和 TLS 环境不同，不能使用其注册表空值或 SEC_E_NO_CREDENTIALS 错误推断宿主网络状态。
- Clash Mi 当前 service_core.log 和 service_error.log 长度为 0，更新时间均为此次重启；没有从这些文件获得故障前错误。应用事件日志的本次范围查询也没有取得相关错误。

后续需要在环境内外浏览器并行时持续验证；若复现，应在重启任何程序之前采集两条代理连通性、端口归属、进程组归属和日志。没有证据时不对服务或外部代理做猜测性修补。

后续并发检查时，原浏览器访问 my.ip.cn 得到 ERR_CONNECTION_CLOSED。宿主 curl 显式经原 7890 访问同站也遇到 TLS 握手失败，但同一代理访问百度、Cloudflare 均为 HTTP 200；真实原浏览器访问后两者也成功。此单站故障不构成整体断网复现，其根因仍未确认。
