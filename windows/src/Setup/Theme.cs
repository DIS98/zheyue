using System.Drawing.Drawing2D;

namespace ClashDeckSetup;

internal static class Theme
{
    public static readonly Color Brand1 = Color.FromArgb(0x3B, 0x6E, 0xF5);
    public static readonly Color Brand2 = Color.FromArgb(0x7C, 0x5C, 0xF5);
    public static readonly Color Bg = Color.FromArgb(0xF7, 0xF8, 0xFB);
    public static readonly Color Card = Color.White;
    public static readonly Color Text = Color.FromArgb(0x1D, 0x23, 0x31);
    public static readonly Color Text2 = Color.FromArgb(0x5A, 0x63, 0x74);
    public static readonly Color Text3 = Color.FromArgb(0x8A, 0x92, 0xA1);
    public static readonly Color Border = Color.FromArgb(0xDD, 0xE2, 0xEA);
    public static readonly Color Ok = Color.FromArgb(0x18, 0x9B, 0x5E);
    public static readonly Color Bad = Color.FromArgb(0xD9, 0x3B, 0x3B);

    public static readonly Font FTitle = new("Microsoft YaHei UI", 19f, FontStyle.Bold);
    public static readonly Font FHead = new("Microsoft YaHei UI", 12.5f, FontStyle.Bold);
    public static readonly Font FBody = new("Microsoft YaHei UI", 9.5f);
    public static readonly Font FSmall = new("Microsoft YaHei UI", 8.5f);
    public static readonly Font FMono = new("Consolas", 8.5f);

    public static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        if (d <= 0 || d > r.Width || d > r.Height)
        {
            p.AddRectangle(r);
            return p;
        }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>扁平圆角按钮：主按钮实心渐变，次按钮描边。</summary>
internal sealed class FlatBtn : Button
{
    public bool Primary { get; set; }

    public FlatBtn(string text, bool primary = false)
    {
        Text = text;
        Primary = primary;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.FBody;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Size = new Size(primary ? 108 : 88, 34);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        bool hover = ClientRectangle.Contains(PointToClient(Cursor.Position));
        bool down = hover && Control.MouseButtons == MouseButtons.Left;

        using var path = Theme.Round(r, 7);
        if (Primary)
        {
            var c1 = Enabled ? Theme.Brand1 : Color.FromArgb(0xC9, 0xCF, 0xDA);
            var c2 = Enabled ? Theme.Brand2 : Color.FromArgb(0xC9, 0xCF, 0xDA);
            if (down) { c1 = ControlPaint.Dark(c1, .12f); c2 = ControlPaint.Dark(c2, .12f); }
            else if (hover) { c1 = ControlPaint.Light(c1, .08f); c2 = ControlPaint.Light(c2, .08f); }
            using var br = new LinearGradientBrush(r, c1, c2, 0f);
            g.FillPath(br, path);
        }
        else
        {
            using var br = new SolidBrush(hover ? Color.FromArgb(0xF0, 0xF3, 0xF9) : Color.White);
            g.FillPath(br, path);
            using var pen = new Pen(Theme.Border);
            g.DrawPath(pen, path);
        }

        var fc = Primary ? Color.White : Theme.Text;
        TextRenderer.DrawText(g, Text, Font, r, fc,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>顶部渐变横幅 + 图标。</summary>
internal sealed class Banner : Panel
{
    public string Title { get; set; } = SetupMain.AppName;
    public string Subtitle { get; set; } = "";
    public Image Icon { get; set; }

    public Banner()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Height = 96;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Width > 0 && Height > 0)
        {
            using var br = new LinearGradientBrush(new Rectangle(0, 0, Width + 1, Height + 1),
                Theme.Brand1, Theme.Brand2, 12f);
            g.FillRectangle(br, ClientRectangle);

            // 右侧淡光环，避免大色块太平
            using var soft = new SolidBrush(Color.FromArgb(26, 255, 255, 255));
            g.FillEllipse(soft, Width - 210, -70, 300, 220);
            g.FillEllipse(soft, Width - 120, 34, 170, 120);
        }

        int x = 26;
        if (Icon != null)
        {
            g.DrawImage(Icon, new Rectangle(x, 26, 44, 44));
            x += 58;
        }
        TextRenderer.DrawText(g, Title, new Font(Theme.FTitle.FontFamily, 17f, FontStyle.Bold),
            new Point(x, 24), Color.White, TextFormatFlags.NoPadding);
        if (!string.IsNullOrEmpty(Subtitle))
            TextRenderer.DrawText(g, Subtitle, Theme.FSmall, new Point(x + 1, 56),
                Color.FromArgb(230, 255, 255, 255), TextFormatFlags.NoPadding);
    }
}
