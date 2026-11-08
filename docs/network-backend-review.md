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

ProxiFyre 已读取 README 并下载源码到隔离的 `build/research/network-backend/source`，固定提交 `5e3bc845d73a47a0931b1233c745580cb916f4ba`，只读审查，未构建、运行或安装。关键检查点为 `netlib/src/proxy/process_routing_policy.h` 的 `match_owner_to_app`，以及 `netlib/src/proxy/socks_local_router.h` 的 IPv6 分片放行分支。项目为 AGPL-3.0；底层 Windows Packet Filter 使用单独许可，不能把用户态源码许可视为驱动许可。

参考：[ProxiFyre README](https://github.com/wiresock/proxifyre)、[Windows Packet Filter 官方下载说明](https://www.ntkernel.com/windows-packet-filter/download/)。其他候选没有在此电脑上安装。不能通过更换同一驱动的签名或文件名来绕过已经观察到的拦截。

## 验收仍需完成

美国 / en-US / Pacific Standard Time / 自动冬夏令时是既定测试参数。保留按启动进程组隔离、组外不变、文件和硬件访问、IPv6 禁用、DNS 及 TCP/UDP 通用接管的全部要求。当前没有满足这些要求并通过本机完整验收的替代后端。

实际浏览器访问 ip.net.coffee/claude/ 及目标颜色要求仍未验收。网页绿灯不替代网络路径证据；反过来，本地单元测试也不替代实际网页结果。
