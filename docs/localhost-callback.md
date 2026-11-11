# localhost 授权回调修复

## 故障与 hosts 核查

2026-11-11，已确认运行目录中的 RegionHook 与 alpha.11 构建哈希一致。ChatGPT 仍提示“此聊天的远程连接已不再配置”。本次日志提供了更具体的失败步骤：

```text
remote_control_client_enrollment_start_response
remote_control_client_enrollment_key_created ... protectionClass=hardware_tpm
remote_control_client_enrollment_step_up_requested
remote_control_authorize_failed errorCode=ENOTFOUND errorMessage="getaddrinfo ENOTFOUND localhost"
```

这说明该次远程控制客户端注册已收到服务端响应，并在进一步授权时失败；不能将截图本身等同于 WebSocket 超时，也不能据此认定所有连接失败都由同一问题导致。

只读核查 hosts：`127.0.0.1 localhost`、`::1 localhost`、`127.0.0.2 chatgpt.com` 均被注释，没有生效的旧转发映射。旧 CodexChatGPTRelay 服务仍运行，采样活动连接为 0，累计接受数保持 1379。没有更改 hosts、服务、系统代理、DNS 或浏览器设置。

旧版本在启动器环境中运行 Python `socket.getaddrinfo('localhost', 443, AF_INET)` 返回 11001。独立回归夹具也复现 Windows DNS/Winsock 查询失败；外部相同查询正常。

## 实现

统一 DNS 钩子此前给所有名称强制附加 WIRE_ONLY，绕过本地名称处理，将 localhost 送到专用 DNS 上游，得到不存在的结果。

现在在 Windows 名称解析 API 共用的 DnsQueryEx 处理入口，识别大小写不敏感的 `localhost`、尾点形式及 `.localhost` 子域，本地构造 A 记录 `127.0.0.1`。AAAA 查询继续返回无记录。缓存专用查询和双地址查询也使用这一规则，不读取或写入宿主 DNS 缓存/hosts，不依赖上游 DNS 可用性。

记录通过 DnsRecordSetCopyEx 分配，调用方可正常使用 DnsRecordListFree。同步完成时不调用异步回调，符合 [Microsoft DnsQueryEx 的同步完成约定](https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsqueryex)。这项更改适用于所有注入环境内的软件，不增加应用专用代理参数。直接构造 DNS 报文的流量仍按原规则送往专用 DNS；本次修复针对 Windows 名称解析接口。

## 验证

- 旧版先失败，新版通过：W/A/UTF8 查询、GetAddrInfoW、AF_UNSPEC、getaddrinfo、gethostbyname、DnsQueryEx 同步/带回调/缓存专用/双地址。
- 使用一个拒绝所有查询的独立 DNS 夹具：localhost 三种形式的解析全部成功，夹具未收到这些名称；相似公网名称不被误判为回环。
- Node 24 执行 dns.lookup、以 localhost 绑定随机端口、通过 localhost 完成 HTTP 回调往返；明确指定 IPv6 的查询失败。
- 普通域名仍交给专用 DNS。专用 DNS 拒绝时，不回退宿主已缓存地址；外部相同 localhost 查询结果在测试前后不变。
- 子进程自定义环境、TCP 五种连接方式、48 路并发、IPv4 映射、半关闭、IPv6 阻断及失败不直连回归通过。
- 将新模块诊断程序加入正在运行的完整网络环境：Node 本地回调通过；加密 WebSocket 连接、首次回显、空闲 20 秒后回显及正常关闭通过。

脚本：`tests/test_localhost.py`、`tests/localhost_callback.cjs`、`tests/test_unified_dns.py`。详细输出存于忽略目录 `build/test-results/localhost`。测试启动独立诊断进程，未终止当前 ChatGPT 或切换代理节点。

## 应用验收边界

旧进程不会热加载 DLL。完全退出 ChatGPT 和启动器，使用 alpha.12 客户端重新启动后，需要再次操作远程连接授权，并核对授权日志及跨设备实际连接。无需重装后台服务。此包修复了已复现的本地回调错误，但不把测试回调成功当成 ChatGPT 完整授权或跨设备连接已成功。
