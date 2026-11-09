# 使用修复版启动器

1. 将 ZIP 完整解压到可写、路径仅含英文字符的目录；不要在压缩包内直接运行。
2. 双击 app/RegionLauncher.exe。需要 .NET 10 Desktop Runtime x64；当前这台电脑已安装。
3. 本机后台已更新为本次修复版，无须重复安装。其他电脑首次使用、或仍使用旧后台时，运行根目录“安装或更新后台服务.cmd”并完成 Windows 管理员确认。
4. 包内提供代理内核，但不包含节点密码、浏览器登录信息或个人资料。联网前需要 app/data/private/proxy.json 中的独立代理配置；目前界面没有节点导入功能。
5. 本机另行解压使用时，可将原 outputs/region-launcher-unified/app/data/private/proxy.json 复制到新目录的 app/data/private/proxy.json；也可复制原 app/data/profile.json 以保留程序、参数和地区选项。保留原文件，这些私人配置不要发给他人。
6. 原浏览器副本仍留在原启动器目录。其启动参数包含原副本的绝对路径，移动整个项目后需在启动器内调整该路径。
7. 同时只运行一个环境。先选程序、国家、语言、时区和自动冬夏时间，再启动。停止环境前保存其中应用的文件。

0.3.0-alpha.2 修复退出时未排空捕获队列的问题，12 轮启停及完整网络回归通过。范围与原始证据位置见 [启停验证记录](validation-lifecycle-20261109.md)。

包内 app 是客户端，install 是后台安装文件，docs 是说明，SHA256SUMS.txt 是程序和安装文件校验清单。私人节点和个人资料不会进入 ZIP 或源码包。
