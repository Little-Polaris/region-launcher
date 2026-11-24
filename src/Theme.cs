using System.Drawing.Drawing2D;
using AntdUI;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using Message = System.Windows.Forms.Message;
using DrawItemEventArgs = System.Windows.Forms.DrawItemEventArgs;

// Shared look for every window: Ant Design tokens, AntdUI controls and the few native lists that keep their selection logic.
static class HomeTheme
{
    public static readonly Color Background = Color.FromArgb(245, 247, 250);
    public static readonly Color Surface = Color.White;
    public static readonly Color Text = Color.FromArgb(31, 35, 41);
    public static readonly Color Muted = Color.FromArgb(100, 106, 115);
    public static readonly Color Subtle = Color.FromArgb(143, 149, 158);
    public static readonly Color Accent = Color.FromArgb(22, 119, 255);
    public static readonly Color AccentSoft = Color.FromArgb(232, 243, 255);
    public static readonly Color Border = Color.FromArgb(229, 230, 235);
    public static readonly Color Hover = Color.FromArgb(242, 246, 252);
    public static readonly Color Success = Color.FromArgb(82, 196, 26);
    public static readonly Color Warning = Color.FromArgb(250, 173, 20);
    public static readonly Color Danger = Color.FromArgb(255, 77, 79);
    public const string FontName = "Microsoft YaHei UI";

    public static void Initialize()
    {
        Config.TextRenderingHighQuality = true;
        Config.ShowInWindow = true;
    }

    public static AntdUI.Button Action(string text, TTypeMini type = TTypeMini.Default, string? icon = null)
    {
        var button = new AntdUI.Button { Text = text, AutoSizeMode = TAutoSize.Auto, Type = type, Radius = 8, WaveSize = 3,
            Cursor = Cursors.Hand, UseMnemonic = false, Margin = new(4, 2, 4, 2) };
        if (type == TTypeMini.Default) button.BorderWidth = 1;
        if (icon != null) button.IconSvg = icon;
        return button;
    }

    public static AntdUI.Button Link(string text, string? icon = null)
    {
        var button = Action(text, TTypeMini.Default, icon);
        button.Ghost = true; button.BorderWidth = 0; button.ForeColor = Muted;
        return button;
    }

    public static AntdUI.Input Input(string placeholder = "") =>
        new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, AutoSizeMode = TAutoSize.Height, PlaceholderText = placeholder, Radius = 8, WaveSize = 3, Margin = new(3, 4, 3, 4) };

    public static AntdUI.Select Select() =>
        new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, AutoSizeMode = TAutoSize.Height, List = true, Radius = 8, WaveSize = 3, Margin = new(3, 4, 3, 4), DropDownRadius = 8 };

    public static System.Windows.Forms.Label Caption(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = Muted, Anchor = AnchorStyles.Left, Margin = new(3, 4, 14, 4) };

    public static System.Windows.Forms.Label Hint(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = Subtle, Margin = new(3, 4, 3, 8) };

    // Footer bar used by every dialog: buttons right-aligned on a light band with a hairline above.
    public static FlowLayoutPanel Footer(params Control[] buttons)
    {
        var bar = new FooterBar { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Padding = new(16, 10, 16, 12), Margin = Padding.Empty };
        bar.Controls.AddRange(buttons);
        return bar;
    }

    public static void Dialog(Form form, string title, Size client, Size minimum)
    {
        form.Text = title; form.Font = new(FontName, 10); form.ClientSize = client; form.MinimumSize = minimum;
        form.StartPosition = FormStartPosition.CenterParent; form.BackColor = Surface; form.ShowInTaskbar = false;
        form.AutoScaleDimensions = new SizeF(96, 96); form.AutoScaleMode = AutoScaleMode.Dpi;
    }

    public static Bitmap? Icon(string name, int size, Color color)
    {
        try {
            string svg = SvgDb.Custom.TryGetValue(name, out var value) ? value : name;
            return SvgExtend.SvgToBmp(svg, size, size, color);
        } catch { return null; }
    }

    public static GraphicsPath Rounded(RectangleF bounds, int radius)
    {
        var path = new GraphicsPath();
        path.AddRoundedRectangle(bounds, new SizeF(radius, radius));
        return path;
    }

    public static void Card(Graphics graphics, RectangleF bounds, int radius, Color fill, Color border, int shadow)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (shadow > 0) {
            var drop = bounds; drop.Offset(0, Math.Max(1, shadow / 2));
            using var shadowPath = Rounded(drop, radius);
            using var shadowBrush = new SolidBrush(Color.FromArgb(shadow * 4, 30, 40, 60));
            graphics.FillPath(shadowBrush, shadowPath);
        }
        using var path = Rounded(bounds, radius);
        using var brush = new SolidBrush(fill);
        graphics.FillPath(brush, path);
        using var pen = new Pen(border);
        graphics.DrawPath(pen, path);
    }

    public static void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = new MenuRenderer(); menu.ShowImageMargin = true;
        menu.Padding = new(4); menu.BackColor = Surface;
    }

    sealed class FooterBar : FlowLayoutPanel
    {
        public FooterBar() { BackColor = Background; SetStyle(ControlStyles.ResizeRedraw, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Border);
            e.Graphics.DrawLine(pen, 0, 0, Width, 0);
        }
    }
}

// Rounded, airy menus with an accent hover instead of the default ToolStrip chrome.
sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    public MenuRenderer() : base(new MenuColors()) { RoundedEdges = true; }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(HomeTheme.Surface);

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(HomeTheme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var bounds = new RectangleF(2, 1, e.Item.Width - 4, e.Item.Height - 2);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = HomeTheme.Rounded(bounds, 6);
        using var brush = new SolidBrush(HomeTheme.AccentSoft);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? HomeTheme.Subtle : e.Item.Selected ? HomeTheme.Accent : HomeTheme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(HomeTheme.Border);
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color MenuBorder => HomeTheme.Border;
        public override Color ToolStripDropDownBackground => HomeTheme.Surface;
        public override Color ImageMarginGradientBegin => HomeTheme.Surface;
        public override Color ImageMarginGradientMiddle => HomeTheme.Surface;
        public override Color ImageMarginGradientEnd => HomeTheme.Surface;
        public override Color MenuItemSelected => HomeTheme.AccentSoft;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color SeparatorDark => HomeTheme.Border;
        public override Color SeparatorLight => HomeTheme.Surface;
    }
}

// Owner-drawn list with roomy rows, rounded selection and an empty-state message; keeps ListBox semantics.
class ModernList : ListBox
{
    int hoverIndex = -1;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public string EmptyText { get; set; } = "";
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public string EmptyHint { get; set; } = "";
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public Func<object, string?>? Detail { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public Func<object, Image?>? ItemIcon { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public Func<object, Color?>? Dot { get; set; }

    public ModernList()
    {
        DrawMode = DrawMode.OwnerDrawFixed; BorderStyle = BorderStyle.None; IntegralHeight = false;
        BackColor = HomeTheme.Surface; ForeColor = HomeTheme.Text;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        UpdateItemHeight();
    }

    void UpdateItemHeight()
    {
        int line = (int)Math.Ceiling(Font.GetHeight(DeviceDpi));
        int rows = Detail == null ? 1 : 2;
        ItemHeight = Math.Min(255, line * rows + LogicalToDeviceUnits(rows == 1 ? 14 : 16));
    }

    public void Refit() { UpdateItemHeight(); Invalidate(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); UpdateItemHeight(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateItemHeight(); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = IndexFromPoint(e.Location);
        if (index >= 0 && !GetItemRectangle(index).Contains(e.Location)) index = -1;
        if (index != hoverIndex) { hoverIndex = index; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverIndex = -1; Invalidate(); }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // WM_PAINT: ListBox never raises DrawItem for an empty list, so draw the empty state ourselves.
        if ((m.Msg == 0x000F || m.Msg == 0x0318) && Items.Count == 0 && (EmptyText != "" || EmptyHint != "")) {
            // WM_PAINT draws on screen; WM_PRINTCLIENT draws into DrawToBitmap snapshots.
            using var graphics = m.Msg == 0x0318 ? Graphics.FromHdc(m.WParam) : CreateGraphics();
            graphics.Clear(BackColor);
            int S(int n) => LogicalToDeviceUnits(n);
            using var hintFont = new Font(Font.FontFamily, Math.Max(8f, Font.SizeInPoints - 1.5f));
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
            int top = Math.Max(S(12), ClientSize.Height / 3 - S(36));
            using (var icon = HomeTheme.Icon("AppstoreOutlined", S(28), HomeTheme.Border)) {
                if (icon != null) graphics.DrawImage(icon, (ClientSize.Width - icon.Width) / 2, top);
            }
            var text = new Rectangle(S(8), top + S(36), ClientSize.Width - S(16), S(60));
            TextRenderer.DrawText(graphics, EmptyText, Font, text, HomeTheme.Muted, flags);
            text.Y += (int)Math.Ceiling(Font.GetHeight(DeviceDpi)) + S(4);
            TextRenderer.DrawText(graphics, EmptyHint, hintFont, text, HomeTheme.Subtle, flags);
        }
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        int S(int n) => LogicalToDeviceUnits(n);
        var graphics = e.Graphics;
        using (var back = new SolidBrush(BackColor)) graphics.FillRectangle(back, e.Bounds);
        bool selected = (e.State & DrawItemState.Selected) != 0;
        bool hover = e.Index == hoverIndex;
        var row = new RectangleF(e.Bounds.X + S(4), e.Bounds.Y + S(2), e.Bounds.Width - S(8), e.Bounds.Height - S(4));
        if (selected || hover) {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = HomeTheme.Rounded(row, S(8));
            using var fill = new SolidBrush(selected ? HomeTheme.AccentSoft : HomeTheme.Hover);
            graphics.FillPath(fill, path);
        }
        object item = Items[e.Index];
        int x = (int)row.X + S(10);
        if (ItemIcon?.Invoke(item) is { } icon) {
            int size = Math.Min(S(28), (int)row.Height - S(8));
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(icon, new Rectangle(x, (int)(row.Y + (row.Height - size) / 2), size, size));
            x += size + S(10);
        }
        int right = (int)row.Right - S(10);
        if (Dot?.Invoke(item) is Color dot) {
            int size = S(8);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(dot);
            graphics.FillEllipse(brush, right - size, row.Y + (row.Height - size) / 2, size, size);
            right -= size + S(8);
        }
        string text = item.ToString() ?? "";
        string? detail = Detail?.Invoke(item);
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        var textColor = selected ? HomeTheme.Accent : HomeTheme.Text;
        if (string.IsNullOrEmpty(detail)) {
            TextRenderer.DrawText(graphics, text, Font, new Rectangle(x, (int)row.Y, right - x, (int)row.Height), textColor, flags);
            return;
        }
        int half = (int)(row.Height / 2);
        using var small = new Font(Font.FontFamily, Math.Max(8f, Font.SizeInPoints - 1.5f));
        TextRenderer.DrawText(graphics, text, Font, new Rectangle(x, (int)row.Y + S(3), right - x, half - S(1)), textColor, flags);
        TextRenderer.DrawText(graphics, detail, small, new Rectangle(x, (int)row.Y + half, right - x, half - S(3)), HomeTheme.Subtle, flags);
    }
}

// A styled, real message window (keeps its title so callers and tests can find and close it).
sealed class MessageDialog : Form
{
    MessageDialog(string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        HomeTheme.Dialog(this, title, new(440, 10), new(360, 0));
        FormBorderStyle = FormBorderStyle.FixedDialog; MinimizeBox = MaximizeBox = false; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var (svg, color) = icon switch {
            MessageBoxIcon.Error => ("CloseCircleFilled", HomeTheme.Danger),
            MessageBoxIcon.Warning => ("ExclamationCircleFilled", HomeTheme.Warning),
            MessageBoxIcon.Question => ("QuestionCircleFilled", HomeTheme.Accent),
            MessageBoxIcon.Information => ("InfoCircleFilled", HomeTheme.Accent),
            _ => ("", HomeTheme.Accent) };
        if (title.StartsWith("检测并修复：") && icon == MessageBoxIcon.Information) (svg, color) = ("CheckCircleFilled", HomeTheme.Success);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new(24, 22, 24, 18) };
        body.ColumnStyles.Add(new(SizeType.AutoSize)); body.ColumnStyles.Add(new(SizeType.Percent, 100));
        if (svg != "") body.Controls.Add(new PictureBox { Image = HomeTheme.Icon(svg, LogicalToDeviceUnits(24), color),
            SizeMode = PictureBoxSizeMode.AutoSize, Margin = new(0, 2, 14, 0) }, 0, 0);
        var heading = new System.Windows.Forms.Label { Text = title.Contains('：') ? title[(title.IndexOf('：') + 1)..] : title, AutoSize = true,
            Font = new(HomeTheme.FontName, 11, FontStyle.Bold), ForeColor = HomeTheme.Text, Margin = new(0, 0, 0, 8), UseMnemonic = false };
        var message = new System.Windows.Forms.Label { Text = text, AutoSize = true, MaximumSize = new(LogicalToDeviceUnits(420), 0),
            ForeColor = HomeTheme.Muted, Margin = Padding.Empty, UseMnemonic = false };
        body.Controls.Add(heading, 1, 0); body.Controls.Add(message, 1, 1);
        var choices = buttons switch {
            MessageBoxButtons.OKCancel => new[] { ("确定", DialogResult.OK), ("取消", DialogResult.Cancel) },
            MessageBoxButtons.YesNo => new[] { ("是", DialogResult.Yes), ("否", DialogResult.No) },
            _ => new[] { ("知道了", DialogResult.OK) } };
        var controls = choices.Select((c, i) => {
            var button = HomeTheme.Action(c.Item1, i == 0 ? (icon == MessageBoxIcon.Error ? TTypeMini.Error : TTypeMini.Primary) : TTypeMini.Default);
            button.DialogResult = c.Item2; return (Control)button; }).Reverse().ToArray();
        var footer = HomeTheme.Footer(controls);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        layout.Controls.Add(body, 0, 0); layout.Controls.Add(footer, 0, 1);
        Controls.Add(layout);
        AcceptButton = (IButtonControl)controls[^1];
        CancelButton = (IButtonControl)controls[0];
    }

    public static DialogResult Show(IWin32Window? owner, string text, string title,
        MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
    {
        using var dialog = new MessageDialog(text, title, buttons, icon);
        if (owner is not Form { Visible: true }) dialog.StartPosition = FormStartPosition.CenterScreen;
        return dialog.ShowDialog(owner);
    }
}

// Status text with a leading state dot (green ready, blue busy, amber needs setup, red error).
sealed class StatusLabel : System.Windows.Forms.Label
{
    Color dot = HomeTheme.Success;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public Color Dot { get => dot; set { dot = value; Invalidate(); } }
    int Lead => LogicalToDeviceUnits(16);
    public StatusLabel() { UseMnemonic = false; }
    public override Size GetPreferredSize(Size proposedSize)
    {
        var size = base.GetPreferredSize(proposedSize);
        return new(size.Width + Lead, size.Height);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        int size = LogicalToDeviceUnits(8);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var halo = new SolidBrush(Color.FromArgb(48, dot)))
            e.Graphics.FillEllipse(halo, 0, (Height - size * 2) / 2f, size * 2, size * 2);
        using (var brush = new SolidBrush(dot))
            e.Graphics.FillEllipse(brush, size / 2f, (Height - size) / 2f, size, size);
        var bounds = new Rectangle(Lead, 0, Math.Max(0, Width - Lead), Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, bounds, ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }
}

sealed class FooterStrip : System.Windows.Forms.Panel
{
    public FooterStrip() { BackColor = HomeTheme.Surface; SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(HomeTheme.Border);
        e.Graphics.DrawLine(pen, 0, 0, Width, 0);
    }
}

// Rounded frame around a borderless native editor or list.
sealed class LogFrame : System.Windows.Forms.Panel
{
    readonly int outer, inner;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public Color Fill { get; set; } = Color.FromArgb(250, 251, 252);
    public LogFrame(int outer = 14, int inner = 10)
    {
        this.outer = outer; this.inner = inner; Padding = new(outer + inner);
        BackColor = HomeTheme.Surface;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
    protected override void OnControlAdded(ControlEventArgs e) { base.OnControlAdded(e); e.Control!.BackColor = Fill; }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        int o = LogicalToDeviceUnits(outer);
        HomeTheme.Card(e.Graphics, new RectangleF(o + 0.5f, o + 0.5f, ClientSize.Width - 2 * o - 1f, ClientSize.Height - 2 * o - 1f),
            LogicalToDeviceUnits(10), Fill, HomeTheme.Border, 0);
    }
}

// Rounded input-style frame for native controls (e.g. the searchable ComboBox) so they match AntdUI inputs.
sealed class FieldFrame : System.Windows.Forms.Panel
{
    bool focused, hover;
    public FieldFrame(Control field)
    {
        BackColor = HomeTheme.Surface; Padding = new(9, 6, 6, 5); Margin = new(3, 4, 3, 4);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        field.Dock = DockStyle.Fill; field.Margin = Padding.Empty; field.BackColor = HomeTheme.Surface;
        Controls.Add(field);
        field.Enter += (_, _) => { focused = true; Invalidate(); };
        field.Leave += (_, _) => { focused = false; Invalidate(); };
        field.MouseEnter += (_, _) => { hover = true; Invalidate(); };
        field.MouseLeave += (_, _) => { hover = false; Invalidate(); };
        Height = field.PreferredSize.Height + Padding.Vertical;
        field.SizeChanged += (_, _) => Height = field.Height + Padding.Vertical;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? HomeTheme.Surface);
        var border = focused ? HomeTheme.Accent : hover ? Color.FromArgb(105, 177, 255) : Color.FromArgb(217, 217, 217);
        if (focused) {
            using var glow = HomeTheme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), LogicalToDeviceUnits(9));
            using var pen = new Pen(Color.FromArgb(40, HomeTheme.Accent), LogicalToDeviceUnits(3));
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(pen, glow);
        }
        HomeTheme.Card(e.Graphics, new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), LogicalToDeviceUnits(8), HomeTheme.Surface, border, 0);
    }
}
