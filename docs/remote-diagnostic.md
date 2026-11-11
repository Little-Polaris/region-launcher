# ChatGPT 授权连接诊断

此工具用于排查点击远程控制授权后、浏览器尚未打开就出现连接重置的问题。适用于已运行的 RegionLauncher alpha.12 环境，不更换启动器或后台服务。

## 使用

1. 保持启动器及通过它运行的 ChatGPT 打开。
2. 将 `Diagnose-ChatGPT.cmd` 和 `Diagnose-ChatGPT.ps1` 放到启动器目录，与 `app` 文件夹同级。
3. 双击 `Diagnose-ChatGPT.cmd`。无需管理员权限；如果目录不匹配，按提示输入包含 `RegionLauncher.exe` 的 `app` 文件夹路径。
4. 等待 `DONE`。结果在同目录 `diagnostic-results/时间/connection-report.txt`。若连接超时，整轮可能需要约三分钟。
5. 只提供 `connection-report.txt`，无需上传节点、订阅、浏览器资料或整个 data 目录。

脚本只读取相关 hosts 行、当前会话的连接参数和模块版本；不会修改 hosts、DNS、系统代理、账号授权、正在运行的程序或服务。测试只发送不带账号信息的 GET 请求，不读取或发送 cookies、令牌及节点密码。

## 对照方式

对四个目标分别进行两次请求：Cloudflare、ChatGPT trace、ChatGPT models、授权站点。

- `proxy`：Windows 自带 curl 显式使用启动器当前 SOCKS5 端口，域名由代理解析。
- `environment`：同一个 curl 加入现有启动器进程组，不使用 curl 应用代理，由启动器网络接管。

每次请求记录退出码、HTTP 状态、连接地址、TLS 验证结果及耗时；不关闭证书校验、不跟随重定向、不输出响应正文。

`exit=0` 且 HTTP 状态非 000，说明获得了该请求的 HTTP 响应。401/403 在不带账号的请求中可能出现，不代表授权成功或账号被封。若两路均发生重置，优先调查共有代理路径及目标端；若只有 environment 失败，进一步检查运行环境；若两路都成功而 ChatGPT 仍失败，还需定位应用的网络实现和实际授权接口。单次 curl 结果不能替代完整 ChatGPT 授权或 WebSocket 验收。

## 本机验证

2026-11-11，使用 Windows PowerShell 5.1 执行，加入当前运行中的网络会话，完成八项请求；脚本正常退出，ChatGPT models 两路均收到 HTTP 401，未出现连接重置。现有 ChatGPT 会话未停止。验证文件保存在忽略目录 `build/test-results/remote-diagnostic`。
