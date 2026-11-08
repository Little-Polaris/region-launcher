# 2026-11-09 验证与待查问题

## 地区选择与语言差异

已安装应用的保存配置为 GeoID 244、en-US、Pacific Standard Time、AutoDst=true。国家、语言和时区目录分别来自 Windows 的 EnumSystemGeoID、EnumSystemLocalesEx、TimeZoneInfo.GetSystemTimeZones。

本轮对 `outputs/region-launcher-unified/app` 运行 `tests/test_locale.py` 通过：父子进程的美国地区/英语、冬夏日期与切换边界、关闭自动切换后的固定标准时间、宿主文件访问、宿主地区设置不变，以及德国、日本和印度的组合。

已复制的 Chrome 资料保存了 zh-CN,zh 的网页语言；前轮网站读取也显示语言不一致。Chromium 的 renderer_preferences_util.cc 从资料中的 kAcceptLanguages 取得该值，因此系统语言接口通过不能证明应用自身语言偏好已改变。官方代码：
https://chromium.googlesource.com/chromium/src/+/refs/heads/main/chrome/browser/renderer_preferences_util.cc

用户已授权仅修改副本语言偏好。工具拒绝访问 chrome://settings/languages，明确禁止换用其他自动化通道达到同一结果；未写入语言设置，已交由用户手动调整。网络设置不涉及这项调整。全绿目标仍未完成。

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
