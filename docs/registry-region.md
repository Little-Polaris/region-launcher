# 进程内地区注册表读取覆盖

RegionHook 在目标进程中拦截 ntdll 的 `NtQueryValueKey` 和 `NtEnumerateValueKey`。常见的 `RegQueryValueExA/W`、`RegGetValueA/W` 和 `RegEnumValueA/W` 经过这一层后读取到本次启动的设置。支持 Basic、Full、Partial 及查询接口的 Align64 记录格式；原接口负责权限、有效句柄及参数检查，覆盖层保持长度查询、缓冲区不足和数据类型检查语义。

每个进程在加载时建立配置快照，后续子进程沿用已有注入流程。键名通过真实句柄解析为完整路径，不依赖句柄数值缓存；复制句柄、相对打开和当前 ControlSet 别名仍能匹配。当前用户的键仅匹配该用户的实际路径。

## 覆盖范围

只替换以下键中已有的指定值，不创建或修改注册表键和值：

| 键 | 覆盖内容 |
|---|---|
| HKCU\Control Panel\International | Locale、LocaleName，以及常用日期、时间、数字、货币和度量格式；格式取所选语言的 Windows 默认值 |
| 上述键下的 Geo | Nation、Name，取所选国家/地区 |
| 上述键下的 User Profile；HKCU\Control Panel\Desktop 及 MuiCached | 已有的 Languages、PreferredUILanguages、MachinePreferredUILanguages 多字符串值 |
| HKLM\SYSTEM\CurrentControlSet\Control\Nls\Language | Default；不替换 InstallLanguage 或安装的语言包列表 |
| HKLM\SYSTEM\CurrentControlSet\Control\TimeZoneInformation | 时区标识、标准/夏令时名称、偏移、转换日期和自动冬夏时间开关 |

`ActiveTimeBias` 在每次读取时按当前日期计算。关闭自动冬夏时间时使用标准偏移，清空转换日期并返回禁用状态。所有时区数据库项仍保持真实内容，软件显式查询其他时区时不会被改成当前时区。

## 隔离边界

- 不执行注册表写入，不修改宿主机系统设置，不向外部进程注入。
- 原先不存在的值、无权限的读取及无关注册表项保持原接口结果。
- 这一层是读取结果覆盖，不是完整的注册表沙盒；写入接口保持原行为。
- 未覆盖 `NtQueryMultipleValueKey` 等其他原生接口、直接系统调用、远程注册表及由外部服务代查的结果。
- 软件自身保存的语言偏好、注入前初始化时已缓存的数据仍可能优先；不会安装额外语言包或改变系统 ANSI 代码页。

## 验证

`tests/test_registry_region.py` 使用真实 Windows 注册表与独立进程：并行运行美国自动冬夏时间、美国固定标准时间、德国三套环境，并验证各自子进程；覆盖 ANSI/Unicode 查询及枚举、直接原生查询的五种记录格式、复制句柄、长度/类型错误。外部进程持续读取原值，比较启动前、运行中和退出后的快照；同时检查无关软件信息未被替换。

现有 `tests/test_locale.py` 与 `tests/test_child_environment.py` 继续验证 API、冬夏时间转换边界、多地区以及自定义子进程环境继承。验证证据位于忽略目录 `build/test-results/registry-region`。

更新需退出旧启动器及其运行组后替换客户端，让目标程序重新加载新 DLL；无需重装网络后台服务。
