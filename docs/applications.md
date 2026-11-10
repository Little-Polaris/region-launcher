# 软件列表

主界面“运行软件”下拉框读取 `app/data/applications.json`。点击“添加…”填写软件名称、选择 `.exe` 和填写启动参数；保存后立即出现在列表中。“编辑…”修改所选条目，“删除”只移除列表条目，不卸载软件、不删除文件，也不结束正在运行的程序。

选择软件后，下方显示其路径和参数；点击“启动所选程序”启动。首次创建运行环境，后续将所选软件加入已有环境。地区、语言、时区和代理仍由当前环境统一设置。软件可以用不同的名称和启动参数添加多次。

首次更新会将旧 `profile.json` 中的程序路径和参数迁入，保留原文件。列表即时保存并记住上次选择。JSON 损坏时保留原文件并在日志中提示；修正后重新打开启动器。也可以关闭启动器后手动编辑此文件。

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
      "arguments": "--user-data-dir=\"D:\\Browser Profiles\\RegionChrome\" --profile-directory=Default"
    }
  ]
}
```

`id` 必须唯一，界面自动生成；`selectedId` 对应所选条目，无条目时为 `null`。程序路径支持环境变量、绝对路径，以及相对于 `app/RegionLauncher.exe` 所在目录的路径。启动参数原样传入；参数中的路径需要符合目标程序自身规则，移动到其他电脑后请检查并编辑。

软件列表可能包含个人路径或启动参数，因此与浏览器资料、代理凭据一样不进入安装包。迁移设备时可自行复制 `app/data/applications.json`；安装包内说明包含上述示例。
