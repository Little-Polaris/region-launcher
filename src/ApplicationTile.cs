using System.Drawing.Drawing2D;
using System.Globalization;

static class HomeTheme
{
    public static readonly Color Background = Color.FromArgb(243, 244, 247);
    public static readonly Color Text = Color.FromArgb(28, 32, 40);
    public static readonly Color Muted = Color.FromArgb(94, 101, 114);
    public static readonly Color Accent = Color.FromArgb(37, 99, 235);
    public static readonly Color Border = Color.FromArgb(226, 229, 235);
    public static readonly Color Hover = Color.FromArgb(234, 241, 253);

    public static Button Action(string text)
    {
        var button = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Padding = new(5, 0, 5, 0),
            BackColor = Color.White, ForeColor = Text, Cursor = Cursors.Hand, UseMnemonic = false };
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Hover;
        return button;
    }

    public static GraphicsPath Rounded(RectangleF bounds, int radius)
    {
        var path = new GraphicsPath();
        path.AddRoundedRectangle(bounds, new SizeF(radius, radius));
        return path;
    }
}

sealed class ApplicationTile : Button
{
    static readonly Lazy<Dictionary<int, string>> Regions = new(() => WindowsSettings.Regions()
        .GroupBy(r => r.GeoId).ToDictionary(g => g.Key, g => g.First().Code));
    static readonly Color[] Avatars = [Color.FromArgb(37, 99, 235), Color.FromArgb(4, 120, 87),
        Color.FromArgb(124, 58, 237), Color.FromArgb(161, 27, 70), Color.FromArgb(175, 83, 10)];
    readonly string subtitle;
    bool busy, hover, pressed;

    public ApplicationTile(LaunchApplication? entry)
    {
        Text = entry?.Name ?? "添加程序";
        subtitle = entry?.Environment is { } env
            ? string.Join(" · ", new[] { Regions.Value.GetValueOrDefault(env.GeoId), env.Locale }.Where(s => !string.IsNullOrEmpty(s)))
            : "添加程序";
        AccessibleName = Text;
        AccessibleDescription = entry == null ? "打开添加程序窗口" : $"按保存的设置启动（{subtitle}）。右键可编辑或删除。";
        Tag = entry; Size = new(132, 142); Margin = new(8);
        FlatStyle = FlatStyle.Flat;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = HomeTheme.Background;
        UseMnemonic = false;
        Cursor = Cursors.Hand;
        if (entry != null) Image = LoadIcon(entry.Application);
    }

    public void SetBusy(bool value)
    {
        if (busy == value) return;
        busy = value; Invalidate();
    }

    static Bitmap? LoadIcon(string application)
    {
        try {
            string path = ApplicationLibrary.ResolvePath(application);
            string? root = Path.GetPathRoot(path);
            // Avoid blocking the UI on unavailable network drives.
            if (root is not { Length: 3 } || new DriveInfo(root).DriveType != DriveType.Fixed) return null;
            using var icon = Icon.ExtractIcon(path, 0, 128);
            return icon?.ToBitmap();
        } catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return null; }
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnMouseCaptureChanged(EventArgs e) { pressed = false; Invalidate(); base.OnMouseCaptureChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { if (!Enabled) hover = pressed = false; base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        int S(int n) => LogicalToDeviceUnits(n);
        if (Width < S(32) || Height < S(32)) return;
        var graphics = e.Graphics;
        graphics.Clear(HomeTheme.Background);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var card = new RectangleF(S(2), S(2), Width - S(5), Height - S(5));
        var shadowBounds = card; shadowBounds.Offset(0, S(2));
        using var shadow = HomeTheme.Rounded(shadowBounds, S(12));
        using var shadowBrush = new SolidBrush(Color.FromArgb(hover && Enabled ? 26 : 14, 30, 40, 60));
        graphics.FillPath(shadowBrush, shadow);
        using var path = HomeTheme.Rounded(card, S(12));
        bool focus = Focused && ShowFocusCues;
        bool highlighted = Enabled && (hover || focus);
        using var fill = new SolidBrush(pressed ? Color.FromArgb(222, 233, 252) : highlighted || busy ? HomeTheme.Hover : Color.White);
        graphics.FillPath(fill, path);
        using var border = new Pen(focus || busy ? HomeTheme.Accent : highlighted ? Color.FromArgb(120, 160, 235) : HomeTheme.Border);
        if (Tag == null && !highlighted) border.DashStyle = DashStyle.Dash;
        graphics.DrawPath(border, path);
        var iconBox = new Rectangle((Width - S(56)) / 2, S(20), S(56), S(56));
        const TextFormatFlags centered = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        if (Tag == null) {
            using var pen = new Pen(Enabled ? HomeTheme.Accent : HomeTheme.Muted, S(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            int x = iconBox.X + iconBox.Width / 2, y = iconBox.Y + iconBox.Height / 2;
            graphics.DrawLine(pen, x - S(14), y, x + S(14), y);
            graphics.DrawLine(pen, x, y - S(14), x, y + S(14));
        } else if (Image != null) {
            graphics.DrawImage(Image, iconBox);
        } else {
            using var avatar = HomeTheme.Rounded(iconBox, S(14));
            using var brush = new SolidBrush(Avatars[(uint)Text.GetHashCode(StringComparison.Ordinal) % Avatars.Length]);
            graphics.FillPath(brush, avatar);
            string letter = Text.Length == 0 ? "?" : StringInfo.GetNextTextElement(Text).ToUpperInvariant();
            using var big = new Font(Font.FontFamily, 22, FontStyle.Bold);
            TextRenderer.DrawText(graphics, letter, big, iconBox, Color.White, centered);
        }
        TextRenderer.DrawText(graphics, Tag == null ? "添加" : Text, Font, new Rectangle(S(8), S(84), Width - S(16), S(24)),
            Enabled || busy ? HomeTheme.Text : HomeTheme.Muted, centered);
        using var small = new Font(Font.FontFamily, Math.Max(7f, Font.Size - 2f));
        TextRenderer.DrawText(graphics, busy ? "启动中…" : subtitle, small, new Rectangle(S(8), S(108), Width - S(16), S(20)),
            busy ? HomeTheme.Accent : HomeTheme.Muted, centered);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { ContextMenuStrip?.Dispose(); Image?.Dispose(); }
        base.Dispose(disposing);
    }
}
