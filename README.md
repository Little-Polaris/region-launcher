<div align="center">

# Region Launcher

**为每个 Windows 程序设置独立的国家和地区、语言与时区。**

[![Release](https://img.shields.io/github/v/release/Little-Polaris/region-launcher?label=release)](https://github.com/Little-Polaris/region-launcher/releases)
[![Platform](https://img.shields.io/badge/platform-Windows_11_x64-0078D4)](#安装)
[![Apps](https://img.shields.io/badge/apps-x86_%7C_x64-526D82)](docs/bitness.md)
[![License](https://img.shields.io/badge/license-MIT-3DA639)](LICENSE)

[下载](https://github.com/Little-Polaris/region-launcher/releases) · [使用指南](docs/quick-start.md) · [更新记录](CHANGELOG.md) · [反馈问题](https://github.com/Little-Polaris/region-launcher/issues)

</div>

Region Launcher 将程序路径、启动参数和地区设置保存为可复用的配置。点击应用图标后，它及受控子进程便使用对应的运行环境；多个程序可以分别使用不同的地区设置。

应用直接运行在 Windows 上，沿用当前用户的文件访问权限。启动器还提供独立代理、DNS 接管和联网检测，适合多语言验证、跨时区测试以及需要独立网络出口的日常应用。

## 功能

| 功能 | 说明 |
| --- | --- |
| 按程序保存设置 | 每个软件分别保存国家和地区、语言、时区、启动参数 |
| 自动冬夏时间 | 根据 Windows 时区规则切换夏令时和标准时间 |
| 应用图标 | 点击启动，右键编辑或删除；支持拖入 EXE 添加，卡片显示地区和语言 |
| 32 / 64 位应用 | 支持 x86、x64 及混合位数子进程 |
| 独立网络出口 | 受控程序的公网 IPv4 连接通过选定节点或自动故障切换组，DNS 使用专用解析通道 |
| 节点与订阅 | 支持配置文件、订阅链接、故障切换组及七种协议的配置表单 |
| 局域网访问 | 私有 IPv4 地址通过本地转发器直连，便于访问 SSH、NAS 等服务 |
| 检测并修复 | 检查实际联网，必要时回收旧监听引用并在原端口重连代理 |
| 托盘运行 | 关闭窗口后继续运行；重复启动唤出已有窗口；启动失败提供提醒 |
| 文件日志 | 记录启动、联网检测、代理恢复和退出清理，便于定位问题 |

## 安装

运行环境：**Windows 11 x64**、[.NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)。

**安装程序（推荐）**

1. 从 [Releases](https://github.com/Little-Polaris/region-launcher/releases) 下载 `RegionLauncher-Setup-版本号-win-x64.exe` 并运行。
2. 阅读并同意[用户协议](docs/EULA.md)，选择安装位置（默认 `%LOCALAPPDATA%\Programs\RegionLauncher`，无需管理员权限）。
3. 安装程序会检查后台网络服务：已是当前版本则跳过，否则请求一次 Windows 管理员确认来安装或更新。
4. 首次打开时，使用向导会引导你完成服务、代理节点和第一个程序的设置。

已经在用便携版的话，直接运行安装程序即可：它会选中正在使用的便携版目录，原地转为安装版并保留 `app\data` 中的数据。安装、升级或卸载时如果启动器正在运行，会先请它退出。

安装位置记录在当前用户注册表中。启动器启动时会检查 GitHub 上的新版本（每天最多一次，可在“更多”菜单中关闭）；确认更新后，会下载安装程序并核对 SHA-256，退出启动器后原位覆盖，节点、订阅和程序列表保留。卸载请使用 Windows“设置 → 应用 → 已安装的应用”，可选择是否同时删除个人数据，并会卸载后台网络服务。

**便携版**

1. 下载 `RegionLauncher-版本号-win-x64.zip`，完整解压到可写目录。
2. 首次使用时运行根目录的 **安装或更新后台服务.cmd**，完成 Windows 管理员确认。
3. 打开 **app/RegionLauncher.exe**。便携版不会写入注册表，发现新版本时会打开发布页供手动下载。

后台服务负责网络接管。服务安装或更新需要管理员权限，日常打开启动器使用普通用户权限即可。安装包附带 WinDivert 驱动及相关许可证；依赖版本和来源见 [SOURCES](docs/SOURCES.md)。

**驱动提示：** 网络驱动可能会被安全软件拦截。遇到拦截时，可将该驱动加入白名单，或临时退出安全软件排查，完成后请重新开启。

## 开始使用

1. **配置代理**：从右上角菜单打开“独立代理配置…”，填写节点，或导入配置后选择固定节点或故障切换组并保存。
2. **添加程序**：点击“添加程序”的加号图标，选择可执行文件，填写启动参数和地区、语言、时区设置。
3. **启动**：点击应用图标。启动器先检测联网，再按该应用已保存的配置启动。右键图标可编辑或删除。

例如，可以让一个程序使用 `美国 / en-US / Pacific Standard Time`，另一个程序使用 `日本 / ja-JP / Tokyo Standard Time`。设置按软件保存，修改后在该软件下次启动时生效。同一次运行中的软件共用所选代理出口。

关闭主窗口会隐藏到托盘，应用继续运行。**从托盘选择“退出（结束全部运行）”会结束受控程序及自有代理，请先保存文件。**

详细步骤见 [使用指南](docs/quick-start.md)。

## 网络与运行范围

- 以本次启动的进程组为范围，同一程序从其他入口启动的实例保持原有环境。
- 公网 IPv4 TCP/UDP 连接使用指定代理；局域网 IPv4 直连；受控组内 IPv6 阻断。具体地址范围见 [局域网访问](docs/lan.md)。
- 网络接管保留宿主机的系统代理、DNS、路由和网卡设置。
- “检测并修复”以及启动新程序会触发应用环境检测。选择故障切换组时，Mihomo 会持续探活并选择节点。详见 [联网检测](docs/connectivity.md)。

地区设置覆盖常见 Windows API、环境变量和部分注册表读取；应用自带的语言偏好、地区数据库或账号设置可能优先于这些值。

已有单实例程序、系统服务、COM 启动、WSL 以及远程主机中的进程有各自的生命周期，不能仅凭调用关系认定它们属于受控进程组。浏览器授权链接可配置专用 Chrome / Edge 资料，见 [授权浏览器](docs/link-browser.md)。

此工具保留用户原有文件权限，适用于可信应用的运行环境配置；受保护进程及额外代码完整性限制可能影响兼容性。

## 文档

| 主题 | 文档 |
| --- | --- |
| 软件配置 | [程序列表与配置文件](docs/applications.md) |
| 地区实现 | [注册表与地区接口](docs/registry-region.md) · [32 / 64 位支持](docs/bitness.md) |
| 代理与订阅 | [节点配置](docs/proxy-settings.md) · [订阅导入](docs/subscriptions.md) |
| 浏览器 | [授权链接与独立资料](docs/link-browser.md) |
| 排查问题 | [联网检测与日志](docs/connectivity.md) · [退出与端口释放](docs/shutdown.md) |
| 开发参考 | [后台服务](docs/service.md) · [依赖来源](docs/SOURCES.md) |

`docs/validation*.md` 保存各轮测试记录，按文档日期和测试范围阅读。仓库另含开发中的 [Android 工程](android/README.md)，与 Windows 版本分别构建。

## 从源码构建

准备 Visual Studio C++ 构建工具（MSVC x86/x64、Windows SDK）、.NET 10 SDK、PowerShell 7、Python 和 Git。

```powershell
git clone https://github.com/Little-Polaris/region-launcher.git
cd region-launcher

pwsh -NoProfile -File scripts/build.ps1
pwsh -NoProfile -File scripts/fetch_proxy.ps1
pwsh -NoProfile -File scripts/package.ps1
```

构建产物位于 `build/service-bin`。`fetch_proxy.ps1` 下载固定版本的 Mihomo 并校验归档；脚本默认使用本机 `127.0.0.1:7890` 下载代理，请根据构建机网络调整下载命令。打包脚本支持通过 `-OutputDirectory` 指定交付目录。

示例检查：

```powershell
dotnet run --project tests/ApplicationLibrary/ApplicationLibrary.csproj -c Release -- ./build/test-results/application-library
./build/ChildEnvironmentTest.exe
./build/JobProcessTest.exe
python -X utf8 tests/test_bitness.py
python -X utf8 tests/test_package.py <生成的ZIP路径>
```

实网测试还需要已安装的后台服务和测试节点，入口与条件见对应功能文档。

## 反馈与贡献

欢迎通过 [Issues](https://github.com/Little-Polaris/region-launcher/issues) 提交复现步骤，附上启动器版本、Windows 版本和目标程序的版本/位数。分享日志前请移除订阅链接、节点密码、令牌及个人路径。

提交修改时请说明实际行为变化，并提供与修改相关的验证结果。

## 许可证

项目代码采用 [MIT License](LICENSE)。随附的第三方组件按各自许可证分发，包括 Detours、ProxyBridge、WinDivert、YamlDotNet、AntdUI 和 Mihomo；版本、源码链接及许可证说明见 [SOURCES](docs/SOURCES.md)，发布包内保留对应许可证。
