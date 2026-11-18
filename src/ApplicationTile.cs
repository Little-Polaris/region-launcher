using System.Drawing.Drawing2D;

sealed class ApplicationTile : Button
{
    static readonly Color Border = Color.FromArgb(222, 228, 238);

    public ApplicationTile(LaunchApplication? entry)
    {
        Text = entry?.Name ?? "添加程序";
        AccessibleName = Text;
        AccessibleDescription = entry == null ? "打开添加程序窗口" : "按保存的设置启动。右键可以编辑或删除。";
        Tag = entry;
        Size = new(148, 124);
        Margin = new(6);
        Padding = new(8, 12, 8, 8);
        FlatStyle = FlatStyle.Flat;
        BackColor = entry == null ? Color.FromArgb(239, 245, 255) : Color.White;
        ForeColor = Color.FromArgb(33, 46, 68);
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = Color.FromArgb(231, 240, 255);
        FlatAppearance.MouseDownBackColor = Color.FromArgb(220, 233, 253);
        TextImageRelation = TextImageRelation.ImageAboveText;
        ImageAlign = ContentAlignment.MiddleCenter;
        TextAlign = ContentAlignment.MiddleCenter;
        UseMnemonic = false;
        AutoEllipsis = true;
        Cursor = Cursors.Hand;
        Image = CreateImage(entry);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        using var outline = Outline(0);
        var previous = Region;
        Region = new Region(outline);
        previous?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var outline = Outline(0.8f);
        using var pen = new Pen(Focused && ShowFocusCues ? SystemColors.Highlight : Border);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, outline);
    }

    GraphicsPath Outline(float inset)
    {
        var path = new GraphicsPath();
        var bounds = new RectangleF(inset, inset, Math.Max(1, Width - 2 * inset), Math.Max(1, Height - 2 * inset));
        path.AddRoundedRectangle(bounds, new SizeF(12, 12));
        return path;
    }

    static Bitmap CreateImage(LaunchApplication? entry)
    {
        const int size = 52;
        var image = new Bitmap(size, size);
        using var paint = Graphics.FromImage(image);
        paint.SmoothingMode = SmoothingMode.AntiAlias;
        Icon? icon = null;
        if (entry != null) {
            try {
                string path = ApplicationLibrary.ResolvePath(entry.Application);
                string? root = Path.GetPathRoot(path);
                if (root is { Length: 3 } && new DriveInfo(root).DriveType == DriveType.Fixed)
                    icon = Icon.ExtractIcon(path, 0, size);
            } catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) {}
        }
        using (icon) {
            if (icon != null) {
                paint.DrawIcon(icon, new Rectangle(0, 0, size, size));
                return image;
            }
        }
        using var background = new SolidBrush(Color.FromArgb(230, 239, 253));
        using var stroke = new Pen(Color.FromArgb(38, 89, 178), 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        paint.FillEllipse(background, 1, 1, size - 2, size - 2);
        if (entry == null) {
            paint.DrawLine(stroke, 17, 26, 35, 26);
            paint.DrawLine(stroke, 26, 17, 26, 35);
        } else {
            paint.DrawRectangle(stroke, 15, 17, 22, 18);
            paint.DrawLine(stroke, 15, 22, 37, 22);
        }
        return image;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { ContextMenuStrip?.Dispose(); Image?.Dispose(); }
        base.Dispose(disposing);
    }
}
