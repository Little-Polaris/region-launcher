sealed class ApplicationEditor : Form
{
    readonly AntdUI.Input name = HomeTheme.Input("例如：Chrome 美区");
    readonly AntdUI.Input executable = HomeTheme.Input("选择或粘贴程序或脚本路径");
    readonly AntdUI.Input arguments = HomeTheme.Input("可留空");
    readonly SearchComboBox country = new() { Dock = DockStyle.Fill };
    readonly SearchComboBox language = new() { Dock = DockStyle.Fill };
    readonly SearchComboBox timeZone = new() { Dock = DockStyle.Fill };
    readonly AntdUI.Checkbox dst = new() { Text = "自动切换夏令时与标准时间", AutoSizeMode = AntdUI.TAutoSize.Auto, Margin = new(3, 6, 3, 6) };
    readonly AntdUI.Button save = HomeTheme.Action("保存", AntdUI.TTypeMini.Primary, "CheckOutlined");
    readonly string id;
    public LaunchApplication? Entry { get; private set; }

    public ApplicationEditor(LaunchApplication? entry = null, LaunchEnvironment? defaults = null) : this(entry, defaults, false) { }
    internal ApplicationEditor(LaunchApplication? entry, LaunchEnvironment? defaults, bool adding)
    {
        id = entry?.Id ?? Guid.NewGuid().ToString("N");
        HomeTheme.Dialog(this, entry == null || adding ? "添加软件" : "编辑软件", new(740, 540), new(680, 360));
        MinimizeBox = MaximizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(24, 20, 24, 0), ColumnCount = 3, RowCount = 9 };
        grid.ColumnStyles.Add(new(SizeType.AutoSize)); grid.ColumnStyles.Add(new(SizeType.Percent, 100)); grid.ColumnStyles.Add(new(SizeType.AutoSize));
        for (int i = 0; i < 9; ++i) grid.RowStyles.Add(new(SizeType.AutoSize));
        grid.Controls.Add(HomeTheme.Caption("软件名称"), 0, 0); grid.Controls.Add(name, 1, 0); grid.SetColumnSpan(name, 2);
        grid.Controls.Add(HomeTheme.Caption("程序路径"), 0, 1); grid.Controls.Add(executable, 1, 1);
        var browse = HomeTheme.Action("浏览…", AntdUI.TTypeMini.Default, "FolderOpenOutlined"); browse.Anchor = AnchorStyles.Left;
        browse.Click += (_, _) => {
            using var dialog = new OpenFileDialog { Filter = ApplicationLibrary.TargetFilter, CheckFileExists = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            executable.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(name.Text)) name.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        };
        grid.Controls.Add(browse, 2, 1);
        grid.Controls.Add(HomeTheme.Caption("启动参数"), 0, 2); grid.Controls.Add(arguments, 1, 2); grid.SetColumnSpan(arguments, 2);
        country.SetOptions(WindowsSettings.Regions().Cast<object>().ToArray());
        language.SetOptions(WindowsSettings.Languages().Cast<object>().ToArray());
        timeZone.SetOptions(WindowsSettings.TimeZones().Cast<object>().ToArray());
        var region = new AntdUI.Divider { Text = "地区环境", Orientation = AntdUI.TOrientation.Left, Dock = DockStyle.Fill,
            Height = LogicalToDeviceUnits(34), ColorSplit = HomeTheme.Border, ForeColor = HomeTheme.Subtle, Margin = new(3, 10, 3, 2) };
        grid.Controls.Add(region, 0, 3); grid.SetColumnSpan(region, 3);
        foreach (var box in new[] { country, language, timeZone }) box.FlatStyle = FlatStyle.Flat;
        Control Framed(Control box) => new FieldFrame(box) { Anchor = AnchorStyles.Left | AnchorStyles.Right };
        var countryField = Framed(country); var languageField = Framed(language); var zoneField = Framed(timeZone);
        grid.Controls.Add(HomeTheme.Caption("国家或地区"), 0, 4); grid.Controls.Add(countryField, 1, 4); grid.SetColumnSpan(countryField, 2);
        grid.Controls.Add(HomeTheme.Caption("语言"), 0, 5); grid.Controls.Add(languageField, 1, 5); grid.SetColumnSpan(languageField, 2);
        grid.Controls.Add(HomeTheme.Caption("时区"), 0, 6); grid.Controls.Add(zoneField, 1, 6); grid.SetColumnSpan(zoneField, 2);
        grid.Controls.Add(dst, 1, 7); grid.SetColumnSpan(dst, 2);
        var settings = entry?.Environment ?? defaults ?? new LaunchEnvironment();
        country.SelectedItem = country.Items.Cast<RegionOption>().Single(r => r.GeoId == settings.GeoId);
        language.SelectedItem = language.Items.Cast<LanguageOption>().Single(l => l.Name.Equals(settings.Locale, StringComparison.OrdinalIgnoreCase));
        timeZone.SelectedItem = timeZone.Items.Cast<TimeZoneInfo>().Single(z => z.Id == settings.TimeZone);
        dst.Checked = settings.AutoDst;
        var hint = HomeTheme.Hint("地区设置对本软件及其子进程生效，修改后需重新启动。\n启动参数可留空；带空格的参数路径请用双引号括起。");
        hint.Margin = new(3, 8, 3, 14);
        grid.Controls.Add(hint, 0, 8); grid.SetColumnSpan(hint, 3);
        var cancel = HomeTheme.Action("取消"); cancel.DialogResult = DialogResult.Cancel;
        var buttons = HomeTheme.Footer(cancel, save);
        content.Controls.Add(grid); layout.Controls.Add(content, 0, 0); layout.Controls.Add(buttons, 0, 1);
        Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
        name.Text = entry?.Name ?? ""; executable.Text = entry?.Application ?? ""; arguments.Text = entry?.Arguments ?? "";
        save.Click += (_, _) => {
            try {
                string target = executable.Text.Trim();
                var value = new LaunchApplication { Id = id, Name = name.Text.Trim(), Application = target, Arguments = arguments.Text,
                    WorkingDirectory = string.Equals(entry?.Application, target, StringComparison.OrdinalIgnoreCase) ? entry?.WorkingDirectory : null,
                    Environment = new LaunchEnvironment { GeoId = ((RegionOption)country.Value!).GeoId, Locale = ((LanguageOption)language.Value!).Name,
                        TimeZone = ((TimeZoneInfo)timeZone.Value!).Id, AutoDst = dst.Checked } };
                ApplicationLibrary.ValidateEntry(value);
                if (!File.Exists(ApplicationLibrary.ResolvePath(value.Application))) throw new FileNotFoundException("程序不存在，请重新选择程序路径。");
                Entry = value; DialogResult = DialogResult.OK;
            } catch (Exception ex) { MessageDialog.Show(this, ex.Message, "无法保存软件", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
    }
}
