sealed class ApplicationEditor : Form
{
    readonly TextBox name = new() { Dock = DockStyle.Fill };
    readonly TextBox executable = new() { Dock = DockStyle.Fill };
    readonly TextBox arguments = new() { Dock = DockStyle.Fill };
    readonly Button save = new() { Text = "保存", AutoSize = true };
    readonly string id;
    public LaunchApplication? Entry { get; private set; }

    public ApplicationEditor(LaunchApplication? entry = null)
    {
        id = entry?.Id ?? Guid.NewGuid().ToString("N");
        Text = entry == null ? "添加软件" : "编辑软件";
        Font = new("Microsoft YaHei UI", 10); ClientSize = new(700, 235); MinimumSize = new(600, 275);
        StartPosition = FormStartPosition.CenterParent; MinimizeBox = MaximizeBox = false;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 3, RowCount = 5 };
        grid.ColumnStyles.Add(new(SizeType.Absolute, 90)); grid.ColumnStyles.Add(new(SizeType.Percent, 100)); grid.ColumnStyles.Add(new(SizeType.AutoSize));
        for (int i = 0; i < 5; ++i) grid.RowStyles.Add(new(SizeType.AutoSize));
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
        var hint = new Label { Text = "启动参数可留空；带空格的参数路径请用双引号括起。", AutoSize = true, Padding = new(0, 6, 0, 8) };
        grid.Controls.Add(hint, 0, 3); grid.SetColumnSpan(hint, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel); buttons.Controls.Add(save); grid.Controls.Add(buttons, 0, 4); grid.SetColumnSpan(buttons, 3);
        Controls.Add(grid); AcceptButton = save; CancelButton = cancel;
        name.Text = entry?.Name ?? ""; executable.Text = entry?.Application ?? ""; arguments.Text = entry?.Arguments ?? "";
        save.Click += (_, _) => {
            try {
                var value = new LaunchApplication { Id = id, Name = name.Text.Trim(), Application = executable.Text.Trim(), Arguments = arguments.Text };
                ApplicationLibrary.ValidateEntry(value);
                if (!File.Exists(ApplicationLibrary.ResolvePath(value.Application))) throw new FileNotFoundException("程序不存在，请重新选择程序路径。");
                Entry = value; DialogResult = DialogResult.OK;
            } catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法保存软件", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
    }
}
