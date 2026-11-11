# Claude MSIX 启动验证

版本：0.3.0-alpha.23。验证日期：2026-11-12，Windows 11 x64，Claude 5.1231.2。

## 原因与修复

原日志在代理及环境出口校验通过后，报 `Could not enroll application; it was not resumed (Windows error 5)`。错误发生在 `AssignProcessToJobObject`，早于 RegionHook 加载和主线程恢复。

独立的暂停进程实验复现了以下结果：旧方法在空组中加入 Claude 成功；组中已有普通程序时，创建 Claude 成功，但追加到目标组失败，错误 5。Claude 的 Windows 包进程已有自己的 Job 层级，追加到已使用的另一层级会受到嵌套规则限制。

统一启动入口改为通过 `PROC_THREAD_ATTRIBUTE_JOB_LIST`，在 `CreateProcessW` 创建进程时指定所属运行组。保留 `CREATE_SUSPENDED`，成功后再核对 `IsProcessInJob`、注入公共接口模块、恢复主线程。归属或注入失败时终止测试目标，不放行到外部环境。首次启动和加入已有环境共用此逻辑；没有为 Claude 添加代理参数。

Windows API 说明：[创建属性中的 Job 列表](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)、[嵌套 Job 的顺序要求](https://learn.microsoft.com/en-us/windows/win32/procthread/nested-jobs)。

## 本机实测

- 多次通过修复后的 RegionSession 加入已有运行组，Claude 打开到 `Claude for Windows / Get started` 欢迎页面。未登录账户。
- 采样的 Claude 主进程和全部子进程均属于目标 Job。主进程、崩溃处理进程和网络进程加载了 RegionHook；受限 GPU/渲染进程未加载该模块，但仍在网络进程组内。此结果不等于所有子进程的地区 API 均已覆盖。
- Claude 网络进程的实际 TCP/UDP 连接经 SOCKS5 `127.0.0.1:17890`，DNS 经独立解析服务 `127.0.0.153:53`。代理内核记录 Anthropic 资源域名使用 `RegionExit`。
- 同一路径启动的通用联网探针，在没有应用代理配置的情况下，国内/海外 HTTPS 见证返回相同美国出口；域名解析成功，真实 IPv6 连接被拒绝，错误 10047。
- 外部直连出口在 Claude 启动前、运行中和退出后相同；已有启动器、代理内核和 ChatGPT 进程继续运行。

持续外部监测与一次 Claude 启动、退出重叠，约 55 秒内结果如下：

| 检查 | 次数 | 失败 |
| --- | ---: | ---: |
| TCP 长连接 | 3678 | 0 |
| TCP 新连接 | 1459 | 0 |
| UDP 复用套接字 | 3679 | 0 |
| UDP 新套接字 | 1840 | 0 |
| HTTPS keep-alive | 133 | 0 |
| HTTPS 新连接 | 83 | 0 |

当前宿主没有 7890 监听，外部监测使用显式直连见证；没有启动或修改外部代理软件。测试命令为 `python -X utf8 tests/test_lifecycle.py --selftest --online --direct-https --observe-seconds 55`，同时由独立测试控制器启动、检查并清理本轮 Claude 进程。

## 回归与证据

`tests/JobProcess.cpp` 通过真实 Windows API 检查空组/已有程序的组、主线程暂停、恢复执行、关闭最后 Job 句柄和主动终止的清理行为、外部进程存活，以及缺少加入权限时创建失败。可给 `build/JobProcessTest.exe` 传入本机包程序的完整路径进行附加检查；附加目标始终暂停，测试不运行其业务代码。

地区/时区、注册表覆盖、子进程环境变量、Unicode 路径及缺失/无效 DLL 拒绝运行的回归已通过。原始诊断和在线验收记录保留在忽略目录 `build/test-results/claude-start`，含 `probe.log`、`live-acceptance.json`、`claude-routes.log`、`network-results.json`、`outside-monitor.log`；不随发布包分发私人日志。

## 验证边界与升级

未测试 Claude 登录后聊天、Cowork、MCP、系统服务代发及长期负载。外部已有 Claude 单实例可能接走请求，测试前本机没有外部 Claude 实例。此次没有退出当前正在承载 ChatGPT 的整个网络环境；验证了 Claude 加入/退出、独立测试组终止，以及外部持续联网。网络后台和驱动实现未修改。

本次只需更新启动器客户端，无须重装后台服务。退出旧启动器后覆盖解压新版，保留 `app/data` 中的软件、代理和浏览器配置。退出启动器会结束它管理的程序，请先保存工作。
