sealed class ApplicationEditor : Form
{
    readonly TextBox name = new() { Dock = DockStyle.Fill };
    readonly TextBox executable = new() { Dock = DockStyle.Fill };
    readonly TextBox arguments = new() { Dock = DockStyle.Fill };
    readonly SearchComboBox country = new() { Dock = DockStyle.Fill };
    readonly SearchComboBox language = new() { Dock = DockStyle.Fill };
    readonly SearchComboBox timeZone = new() { Dock = DockStyle.Fill };
    readonly CheckBox dst = new() { Text = "自动切换夏令时与标准时间", AutoSize = true };
    readonly Button save = new() { Text = "保存", AutoSize = true };
    readonly string id;
    public LaunchApplication? Entry { get; private set; }

    public ApplicationEditor(LaunchApplication? entry = null, LaunchEnvironment? defaults = null) : this(entry, defaults, false) { }
    internal ApplicationEditor(LaunchApplication? entry, LaunchEnvironment? defaults, bool adding)
    {
        id = entry?.Id ?? Guid.NewGuid().ToString("N");
        Text = entry == null || adding ? "添加软件" : "编辑软件";
        Font = new("Microsoft YaHei UI", 10); ClientSize = new(740, 440); MinimumSize = new(680, 360);
        StartPosition = FormStartPosition.CenterParent; MinimizeBox = MaximizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(16, 16, 16, 0), ColumnCount = 3, RowCount = 8 };
        grid.ColumnStyles.Add(new(SizeType.AutoSize)); grid.ColumnStyles.Add(new(SizeType.Percent, 100)); grid.ColumnStyles.Add(new(SizeType.AutoSize));
        for (int i = 0; i < 8; ++i) grid.RowStyles.Add(new(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = "软件名称", AutoSize = true }, 0, 0); grid.Controls.Add(name, 1, 0); grid.SetColumnSpan(name, 2);
        grid.Controls.Add(new Label { Text = "程序路径", AutoSize = true }, 0, 1); grid.Controls.Add(executable, 1, 1);
        var browse = new Button { Text = "浏览…", AutoSize = true };
        browse.Click += (_, _) => {
            using var dialog = new OpenFileDialog { Filter = "Windows 程序 (*.exe)|*.exe", CheckFileExists = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            executable.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(name.Text)) name.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        };
        grid.Controls.Add(browse, 2, 1);
        grid.Controls.Add(new Label { Text = "启动参数", AutoSize = true }, 0, 2); grid.Controls.Add(arguments, 1, 2); grid.SetColumnSpan(arguments, 2);
        country.SetOptions(WindowsSettings.Regions().Cast<object>().ToArray());
        language.SetOptions(WindowsSettings.Languages().Cast<object>().ToArray());
        timeZone.SetOptions(WindowsSettings.TimeZones().Cast<object>().ToArray());
        grid.Controls.Add(new Label { Text = "国家或地区", AutoSize = true }, 0, 3); grid.Controls.Add(country, 1, 3); grid.SetColumnSpan(country, 2);
        grid.Controls.Add(new Label { Text = "语言", AutoSize = true }, 0, 4); grid.Controls.Add(language, 1, 4); grid.SetColumnSpan(language, 2);
        grid.Controls.Add(new Label { Text = "时区", AutoSize = true }, 0, 5); grid.Controls.Add(timeZone, 1, 5); grid.SetColumnSpan(timeZone, 2);
        grid.Controls.Add(dst, 1, 6); grid.SetColumnSpan(dst, 2);
        var settings = entry?.Environment ?? defaults ?? new LaunchEnvironment();
        country.SelectedItem = country.Items.Cast<RegionOption>().Single(r => r.GeoId == settings.GeoId);
        language.SelectedItem = language.Items.Cast<LanguageOption>().Single(l => l.Name.Equals(settings.Locale, StringComparison.OrdinalIgnoreCase));
        timeZone.SelectedItem = timeZone.Items.Cast<TimeZoneInfo>().Single(z => z.Id == settings.TimeZone);
        dst.Checked = settings.AutoDst;
        var hint = new Label { Text = "地区设置对本软件及其子进程生效，修改后需重新启动。\n启动参数可留空；带空格的参数路径请用双引号括起。", AutoSize = true, Padding = new(0, 6, 0, 8) };
        grid.Controls.Add(hint, 0, 7); grid.SetColumnSpan(hint, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Padding = new(16, 8, 16, 12), Margin = Padding.Empty };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel); buttons.Controls.Add(save);
        content.Controls.Add(grid); layout.Controls.Add(content, 0, 0); layout.Controls.Add(buttons, 0, 1);
        Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
        name.Text = entry?.Name ?? ""; executable.Text = entry?.Application ?? ""; arguments.Text = entry?.Arguments ?? "";
        save.Click += (_, _) => {
            try {
                var value = new LaunchApplication { Id = id, Name = name.Text.Trim(), Application = executable.Text.Trim(), Arguments = arguments.Text,
                    Environment = new LaunchEnvironment { GeoId = ((RegionOption)country.Value!).GeoId, Locale = ((LanguageOption)language.Value!).Name,
                        TimeZone = ((TimeZoneInfo)timeZone.Value!).Id, AutoDst = dst.Checked } };
                ApplicationLibrary.ValidateEntry(value);
                if (!File.Exists(ApplicationLibrary.ResolvePath(value.Application))) throw new FileNotFoundException("程序不存在，请重新选择程序路径。");
                Entry = value; DialogResult = DialogResult.OK;
            } catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法保存软件", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
    }
}
