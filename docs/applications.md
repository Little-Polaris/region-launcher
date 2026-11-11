# 软件列表

主界面“运行软件”下拉框读取 `app/data/applications.json`。点击“添加…”填写软件名称、选择 `.exe`、填写启动参数，并选择国家或地区、语言、时区及是否自动切换冬夏时间；保存后立即出现在列表中。新条目初始沿用当前选择的地区设置，可以独立修改。“编辑…”修改所选条目，“删除”只移除列表条目，不卸载软件、不删除文件，也不结束正在运行的程序。

选择软件后，下方显示其路径、参数及地区设置。直接在主界面修改国家或地区、语言、时区或冬夏时间选项，会立即保存到该条目。点击“启动所选程序”：首次创建运行环境，后续加入已有环境；每个新程序及其子进程使用自己的地区设置，代理节点和 DNS 通道仍由本次运行共用。修改设置对下一次启动生效，已运行的程序保持原来的值，外部系统设置不变。修改地区不会改变代理出口所在国家。

首次更新会将旧 `profile.json` 中的程序路径和参数迁入，保留原文件。已有软件列表中缺少 `environment` 的条目，一次性继承旧配置中的地区、语言、时区和冬夏时间；已配置的条目不会被覆盖。列表即时保存并记住上次选择。JSON 损坏或地区字段无效时保留原文件并在日志中提示；修正后重新打开启动器。也可以关闭启动器后手动编辑此文件。

同一软件可用不同名称、参数和地区设置添加多次。单实例程序可能复用已运行进程，这种情况下新设置不会改变旧进程。例如同时运行两种环境的 Chrome，需要为两个条目分别指定不同的 `--user-data-dir` 资料目录。应用自己保存的网页语言、界面语言偏好也可能优先于 Windows 默认语言。

格式示例（请按本机实际路径填写）：

```json
{
  "version": 1,
  "selectedId": "chrome-work",
  "applications": [
    {
      "id": "chrome-work",
      "name": "Chrome · 工作资料",
      "application": "%ProgramFiles%\\Google\\Chrome\\Application\\chrome.exe",
      "arguments": "--user-data-dir=\"D:\\Browser Profiles\\RegionChrome-US\" --profile-directory=Default",
      "environment": {
        "geoId": 244,
        "locale": "en-US",
        "timeZone": "Pacific Standard Time",
        "autoDst": true
      }
    },
    {
      "id": "chrome-japan",
      "name": "Chrome · 日本资料",
      "application": "%ProgramFiles%\\Google\\Chrome\\Application\\chrome.exe",
      "arguments": "--user-data-dir=\"D:\\Browser Profiles\\RegionChrome-JP\" --profile-directory=Default",
      "environment": {
        "geoId": 122,
        "locale": "ja-JP",
        "timeZone": "Tokyo Standard Time",
        "autoDst": true
      }
    }
  ]
}
```

`id` 必须唯一，界面自动生成；`selectedId` 对应所选条目，无条目时为 `null`。程序路径支持环境变量、绝对路径，以及相对于 `app/RegionLauncher.exe` 所在目录的路径。启动参数原样传入；参数中的路径需要符合目标程序自身规则，移动到其他电脑后请检查并编辑。

`environment.geoId` 是 Windows 国家或地区编号，`locale` 是语言名称，`timeZone` 是 Windows 时区 ID（例如洛杉矶使用 `Pacific Standard Time`，不是 IANA 名称）。`autoDst: true` 按对应时区规则自动切换夏令时与标准时间；`false` 全年使用该时区标准时间。建议通过界面选择以避免填写错误；无效值会被拒绝。配置版本仍为 1，本次为兼容的字段扩展。

软件列表可能包含个人路径或启动参数，因此与浏览器资料、代理凭据一样不进入安装包。迁移设备时可自行复制 `app/data/applications.json`；安装包内说明包含上述示例。

## 本机验证（2026-11-11）

- 配置测试覆盖不同软件分别保存、旧配置迁移一次、编辑后重读及无效设置不覆盖原文件。
- 界面测试覆盖添加/编辑设置、下拉切换、主界面自动保存四项设置、重开恢复，以及已有程序运行时仍可配置下一个软件。
- 原生进程测试保持美国环境根程序运行，依次加入日本与德国环境程序及其子进程；美国程序前后读取一致，德国关闭夏令时后冬夏均使用标准时间，日本保持 UTC+9，组外程序的地区读数前后不变。
- 该原生测试使用本机临时 SOCKS 握手服务，不访问互联网；验证的是地区继承与隔离，不是新增的代理联网验收。没有修改网络后台或驱动。
