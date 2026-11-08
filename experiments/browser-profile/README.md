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
- 启动器界面测试尚未执行成功：窗口最小化后，自动恢复持续报告用户输入状态冲突，正在等待用户恢复显示。
- 备用命令行入口 `RegionSession --join` 返回 `Could not enroll application; it was not resumed (Windows error 5)`。目标程序在恢复运行前已被终止，**不能用这一失败判断 Chrome 的资料复用行为**。尚未确定此访问拒绝的具体原因。
- 失败尝试后的两组原进程归属未变；按字段比较的宿主机代理设置和按网卡/地址族比较的 DNS 设置均未变。不得直接比较无序 JSON 对象的序列化字符串。

## 下一步验收

从现有启动器的“加入所选程序”执行待测参数，记录返回的启动 PID、退出状态、新窗口所属 PID 和 Job 成员身份。仅关闭本轮创建的空白窗口；恢复启动器参数。原浏览器仍运行的情况下，如果新窗口属于外部进程，则不能视为隔离成功。

“原资料未被占用时启动”的情况尚未测试。本轮不关闭用户已有浏览器来制造该条件。即使该情况可用，也不能推出同一份资料能同时服务于独立的内外两个浏览器。

Chromium 的 Windows 单实例实现可作为解释依据，不能替代本机验证：
https://chromium.googlesource.com/chromium/src/+/lkgr/chrome/browser/process_singleton_win.cc
