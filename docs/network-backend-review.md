# 网络后端核查（2026-11-08）

## 本机事实

- 后台更新已成功；服务、安装文件校验和限定转发规则已核对。
- 两次完整验收均止于 WinDivert 加载失败，Windows 错误 1450。普通用户接口拒绝无效参数和安装目录写保护检查通过；目标应用没有启动，不能据此推断网络可用。
- 火绒截图将安装目录内的 WinDivert64.sys 记为“漏洞驱动拦截 / 已阻止”。Windows 系统日志时间对应，驱动状态 Stopped。
- 尚未取得火绒对该文件的具体漏洞编号或判定依据，不能认定误报，也不能仅由这一分类断言文件是恶意软件。没有更改安全软件配置。

## 来源核验

官方发布页及 API 当前最新版本为 [WinDivert 2.2.2](https://github.com/basil00/WinDivert/releases/tag/v2.2.2)，发布于 2022-09-21；此次没有找到更新的官方发布修复。

从该发布页重新下载 `WinDivert-2.2.2-A.zip`，SHA-256 为 `63CB41763BB4B20F600B6DE04E991A9C2BE73279E317D4D82F237B150C5F3F15`，与仓库已有归档一致。已安装驱动与归档提取文件一致，SHA-256 为 `8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2`。本机 Authenticode 返回 Valid，签名主体为成都密思听科技有限公司。签名和来源核对不能证明没有漏洞。

[ProxyBridge #277](https://github.com/InterceptSuite/ProxyBridge/issues/277) 包含相同文件哈希及错误 1450；维护者指出安全工具可能拒绝驱动，但没有提供修复版。这条反馈不能代替本机安全软件的具体判定。[火绒官方说明](https://www.huorong.cn/document/tech/safety-classroom/1947) 建议在无法确定驱动风险时向官方核实；本任务尚未发送任何对外反馈。

## 替代方案的现有证据

| 候选 | 已确认能力 | 距离本任务要求的缺口 |
| --- | --- | --- |
| ProxyBridge 自研 WFP 驱动测试版 | 按连接重定向 TCP/UDP | [官方 #210](https://github.com/InterceptSuite/ProxyBridge/discussions/210) 明确是未正式签名的测试版本，要求测试模式并限定测试机；不在本机部署。 |
| ProxiFyre + Windows Packet Filter | TCP/UDP；厂商提供 Microsoft-attested NDIS 驱动 | 现有规则按名称/路径，不能区分同一程序的内外实例；源码明确放行无法解析传输头的 IPv6 分片。还需解决本地回环、归属不明数据包、进程组限定和 DNS 全路径，再进行实际驱动兼容性验证。不能直接替换后宣布成功。 |
| Sandboxie Plus 代理 | 通用 Winsock TCP SOCKS5 CONNECT | [官方代理说明](https://github.com/sandboxie-plus/sandboxie-docs/blob/main/docs/PlusContent/ProxySupport.md) 明确不支持 UDP ASSOCIATE/UDP 代理，且代理本身不自动创建 WFP 防泄漏规则。单独启用不能覆盖本任务要求。 |
| Force-Proxy DLL | 通用 Winsock IPv4 TCP/UDP 钩子 | [README](https://github.com/runetfreedom/force-proxy) 只列出部分 API；尚未验证 ConnectEx、重叠 I/O 等路径，也没有网络层防泄漏边界，不能视为等价的通用接管。 |

ProxiFyre 已读取 README 并下载源码到隔离的 `build/research/network-backend/source`，固定提交 `5e3bc845d73a47a0931b1233c745580cb916f4ba`，原始源码保持不变，未运行或安装其完整程序和驱动。关键检查点为 `netlib/src/proxy/process_routing_policy.h` 的 `match_owner_to_app`，以及 `netlib/src/proxy/socks_local_router.h` 的 IPv6 分片放行分支。项目为 AGPL-3.0；底层 Windows Packet Filter 使用单独许可，不能把用户态源码许可视为驱动许可。

参考：[ProxiFyre README](https://github.com/wiresock/proxifyre)、[Windows Packet Filter 官方下载说明](https://www.ntkernel.com/windows-packet-filter/download/)。其他候选没有在此电脑上安装。不能通过更换同一驱动的签名或文件名来绕过已经观察到的拦截。

## 无驱动隔离实验

`experiments/proxifyre/scripts/check_scope.ps1` 编译并运行了单独的 ScopeProbe。它创建同一个可执行文件的两个挂起进程，只把其中一个加入真实 Windows Job；结束时只清理这两个自建进程。没有套接字调用，不打开驱动，不修改网络设置。

实验直接包含上述固定提交的原始路由策略头文件（SHA-256 `6AA9E7DC770643DFEFCD98C598A5F0F3F82DEBEF9810B2D2816F99DB98EFA200`），使用最小进程记录夹具，而不是网络连接表。结果：

- 上游名称规则匹配两个实例；实际 Job 查询可以区分内外。
- 上游缓存端口分支会跳过重新选择代理。因此只在名称匹配回调里添加 Job 判断仍不够，实例归属检查必须位于缓存使用之前。
- 查询失败单独保留为 unknown，不能直接当作“外部进程”放行。尚未决定网络层如何在归属未知时同时保证环境内不泄漏、环境外不误拦。
- IPv6 分片和多播可能在识别进程前放行，属于另一个必须处理的路径。

输出位于 `build/research/network-backend/scope-probe/results.txt`。该实验只证明进程组判定和所审查策略函数的分支行为；不证明进程与数据包的真实关联，不证明内核拦截、DNS、IPv6 或浏览器已经可用。实验尚未接入已安装后台。

## 验收仍需完成

### 独立虚拟网卡路线的边界

[Wintun 官方](https://www.wintun.net/) 提供已签名的三层虚拟网卡组件，但组件本身不提供进程组分流策略。[Mihomo TUN 文档](https://wiki.metacubex.one/en/config/inbound/tun/) 明确 `auto-route` 会设置全局路由；直接开启并不满足外部程序不受影响的要求。尚未安装 Wintun 或启用 TUN 自动路由。

[IP_UNICAST_IF](https://learn.microsoft.com/en-us/windows/win32/winsock/ipproto-ip-socket-options) 是单个 IPv4 套接字的出口接口选项，不是进程组级强制隔离。仅确认这一 API 存在，不能推断所有 TCP/UDP、DNS 服务代发、IPv6、继承套接字和故障回退都已覆盖。没有将接口绑定实验或配置项当作完整替代后端。

[SetCurrentThreadCompartmentId](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-setcurrentthreadcompartmentid) 当前微软文档标为保留、要求不要使用。没有调用它创建或改变本机网络分区；不能将这条未受支持的路线作为已经可交付的无虚拟机隔离实现。

这并不证明替代实现不可能，而是说明本轮候选中没有已核实可直接部署并满足全部要求的方案。已安装后端依赖的驱动仍被现有防护拦截，后续完整验收需要兼容的网络后端或厂商对该拦截的具体处理依据。当前不能靠重复加载、全局 TUN、名称规则、关闭防护或缩减测试范围推进验收。

美国 / en-US / Pacific Standard Time / 自动冬夏令时是既定测试参数。保留按启动进程组隔离、组外不变、文件和硬件访问、IPv6 禁用、DNS 及 TCP/UDP 通用接管的全部要求。当前没有满足这些要求并通过本机完整验收的替代后端。

实际浏览器访问 ip.net.coffee/claude/ 及目标颜色要求仍未验收。网页绿灯不替代网络路径证据；反过来，本地单元测试也不替代实际网页结果。
