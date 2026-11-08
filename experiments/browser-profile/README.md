# 原有 Chrome 资料复用验证

目标：保留当前 Windows 用户的原有 Chrome 资料，判断启动器打开的新窗口是否真正属于所选进程组。新窗口、渲染进程数量和正常退出码均不能单独证明网络隔离。

`probe.ps1` 只读查询指定 Job 的成员、Chrome 进程角色、官方电脑工具返回的窗口 ID 对应的进程，以及宿主机代理与 DNS。它不会启动、关闭或重新归组任何程序，也不会读取网页、Cookie、密码或书签。

运行时必须传入当前会话 Job 名称及最新窗口 ID，不可沿用旧进程号。示例：

```powershell
./experiments/browser-profile/probe.ps1 -Job 'Local\RegionLauncher.<current-session>' -OutputPath 'build/test-results/profile-reuse/before.json' -WindowIds <observed-window-id>
```

## 当前证据边界

本轮本机记录保存在 `build/test-results/profile-reuse/`，属于被 Git 忽略的运行证据。

- 基线：原 Chrome 主进程 17044、联网进程 20856 不属于当前 Job；独立测试资料 Chrome 主进程 42244、联网进程 50784 属于当前 Job。所有成员查询均成功。
- 原窗口 67034 对应 17044；环境内窗口 1970040 对应 42244。
- 待测启动参数明确指向用户原有 `User Data` 的 `Default` 资料，使用 `--new-window` 打开带测试标记的空白页；没有代理或 DNS 参数。
- 用户恢复启动器后，界面入口测试于 2026-11-08 23:22:30 成功执行。读取可访问性树后点击曾报 `coordinate input geometry is unavailable`；补充一次官方工具截图后，元素点击成功，无需改变应用代码。
- 备用命令行入口 `RegionSession --join` 返回 `Could not enroll application; it was not resumed (Windows error 5)`。目标程序在恢复运行前已被终止，**不能用这一失败判断 Chrome 的资料复用行为**。尚未确定此访问拒绝的具体原因。
- 失败尝试后的两组原进程归属未变；按字段比较的宿主机代理设置和按网卡/地址族比较的 DNS 设置均未变。不得直接比较无序 JSON 对象的序列化字符串。

## 本机实测结果

- “加入所选程序”启动 PID 44980，随后日志为 `Opening in existing browser session.`，加入程序退出码为 0。
- 新窗口 ID 2099876 与原窗口 67034 均归属原主进程 17044。主进程 17044 和联网进程 20856 均不属于当前 Job；独立资料的主进程 42244 和联网进程 50784 仍属于当前 Job。
- 新窗口的标签标题为“搜索 - Microsoft 必应”，官方浏览器枚举返回 `chrome://newtab/`，标签 ID 119039367，创建时间与测试启动时间一致。不要将它描述为已经观察到空白标记页；标记仅存在于请求的启动参数中。
- 已完成断言：新窗口与原窗口同属 17044，该进程成员查询成功且在 Job 外，新启动的 44980 已退出。宿主机代理及 DNS 与基线一致。
- 证据：`after-new-window.json`，启动配置 `outputs/region-launcher-unified/app/data/sessions/bfd588a0a5cf421985b36e71c588f135/session.ini`，以及官方工具取得的启动器日志。
- 启动器持久化参数已恢复至测试前独立资料目录。界面参数恢复操作后工具超时，界面显示未再确认。
- 官方浏览器连接选中本轮测试标签连续超时，未能完成关闭操作。因此新标签可能仍保留；没有终止原浏览器或关闭其其他标签。浏览器读取通道尚未完成验收。

结论：**原有资料已被外部 Chrome 使用时，本机的“新窗口”仍交给外部原进程，未进入所选网络环境。** 此结论基于窗口对应 PID 与实际 Job 查询，不基于窗口外观、渲染进程数量或启动器退出码。

“原资料未被占用时启动”的情况尚未测试。本轮不关闭用户已有浏览器来制造该条件。即使该情况可用，也不能推出同一份资料能同时服务于独立的内外两个浏览器。

Chromium 的 Windows 单实例实现可作为解释依据，不能替代本机验证：
https://chromium.googlesource.com/chromium/src/+/lkgr/chrome/browser/process_singleton_win.cc
