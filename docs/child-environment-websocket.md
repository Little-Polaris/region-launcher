# 子进程环境与 WebSocket 验证

## 2026-11-11 故障证据

ChatGPT 从启动器运行后，用户报告本机可被其他机器连接，但不能主动连接其他机器。检查确认主程序及后台由 RegionSession 启动；主程序和后台已加载 RegionHook，采样连接通过本次连接代理。

旧 CodexChatGPTRelay 服务仍运行，但 hosts 中的 `127.0.0.2 chatgpt.com` 已注释。采样时旧服务活动连接为 0，累计接受数未增长。没有停止该服务或修改 hosts、系统代理、DNS。服务仍运行不等于 ChatGPT 当前仍经由它联网。

ChatGPT 日志同时出现多个辅助组件（codex_app、node_repl、cua_repl 等）启动失败：Windows 错误 1114，DLL 初始化失败。最小对照复现：

- 继承父进程环境：子进程启动成功。
- 显式传入精简环境：启动失败，错误 1114。
- 同一精简环境补回 REGION_LAUNCHER 参数：启动成功。

旧实现将调用方的环境块原样传给 CreateProcess，再加载 RegionHook；模块初始化要求的地区与网络参数可能不存在。该缺陷已复现，但尚不能证明它解释了远程连接失败的全部原因。

## 修复

进程初始化时保存其启动器环境参数。创建子进程时，只补入/覆盖本次运行的地区、语言、时区、冬夏时间、DNS 和连接通道，以及对应 TZ/LANG/LC_ALL。自定义环境中的其他变量保留，不额外复制父进程的凭据或业务变量。网络环境内继续移除 HTTP_PROXY/HTTPS_PROXY/ALL_PROXY/NO_PROXY，防止子进程改走外部代理。

CreateProcessW 和 CreateProcessA 共用转换；接受 Unicode 或 ANSI 环境块，保留 Windows 特殊的每盘工作目录项，变量名按大小写不敏感处理。主线程仍在模块准备好后才恢复，初始化失败仍阻止子进程启动。

这项修复对全部软件使用同一实现。没有为 ChatGPT、浏览器添加代理参数或例外规则。

## 验证与边界

- 原生环境块测试通过：ANSI/Unicode、大小写覆盖、代理变量移除、特殊盘符项、空环境。
- 实际父子进程测试通过：精简环境能启动；地区设置保留；中文与 emoji 保留；无关父进程变量未被复制。
- 地区、冬夏时间、宿主文件访问，以及既有 TCP/非阻塞/ConnectEx/并发/半关闭/失败不直连测试通过。
- 现有运行会话中加入未设置应用代理的 PowerShell WebSocket 探针，经公共接口接管，访问 `wss://ws.postman-echo.com/raw`。真实 TLS 验证、WebSocket 升级、消息回显、空闲 20 秒后的第二次回显及正常关闭均通过。
- 测试只发送固定诊断文字，不使用 ChatGPT 认证信息。脚本为 `tests/websocket_probe.ps1`，结果保存在忽略目录 `build/test-results/child-environment`。

WebSocket 探针通过说明当前通道支持该测试路径，不代表 ChatGPT 远程控制、所有 WebSocket 站点或数小时长连接已通过验收。旧会话不会热加载修复，需完全退出其中的程序和启动器、更新客户端后重启，再复查远程连接与辅助组件日志。已安装的网络后台无需重装。

测试端点：[Postman Echo 官方说明](https://learning.postman.com/docs/developer/echo-api)。客户端：[Microsoft .NET WebSocket 文档](https://learn.microsoft.com/dotnet/fundamentals/networking/websockets)。
