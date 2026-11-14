using System.Drawing.Drawing2D;

namespace FortItemTracker;

/// <summary>Colours, fonts and shapes shared by the overlay, the tray menu and the activity log: the app icon's slate and gold.</summary>
static class Theme
{
    public static readonly Color Ink = Color.FromArgb(15, 18, 24);
    public static readonly Color Panel = Color.FromArgb(24, 28, 37);
    public static readonly Color PanelTop = Color.FromArgb(34, 39, 52);
    public static readonly Color Raised = Color.FromArgb(40, 46, 61);
    public static readonly Color Line = Color.FromArgb(52, 59, 76);
    public static readonly Color Text = Color.FromArgb(238, 241, 246);
    public static readonly Color Dim = Color.FromArgb(160, 169, 186);
    public static readonly Color Faint = Color.FromArgb(104, 113, 132);
    public static readonly Color Gold = Color.FromArgb(255, 199, 56);
    public static readonly Color GoldLight = Color.FromArgb(255, 226, 138);
    public static readonly Color GoldDeep = Color.FromArgb(243, 152, 18);
    public static readonly Color Good = Color.FromArgb(98, 216, 164);
    public static readonly Color Info = Color.FromArgb(128, 176, 255);
    public static readonly Color Warn = Color.FromArgb(255, 178, 92);
    public static readonly Color Bad = Color.FromArgb(255, 116, 106);

    /// <summary>A font in pixels, or <paramref name="fallback"/> when the family isn't installed.</summary>
    public static Font Font(string family, float px, string fallback, FontStyle style = FontStyle.Regular)
    {
        var f = new Font(family, px, style, GraphicsUnit.Pixel);
        if (string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase)) return f;
        f.Dispose();
        return new Font(fallback, px, style, GraphicsUnit.Pixel);
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            p.AddRectangle(r);
            return p;
        }
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>The four-point sparkle from the app icon, with curved sides.</summary>
    public static void Sparkle(Graphics g, Brush brush, float cx, float cy, float r, float degrees = 0)
    {
        if (r <= 0.2f) return;
        using var path = new GraphicsPath();
        double a0 = degrees * Math.PI / 180;
        PointF At(double angle, float dist) => new(cx + (float)(Math.Sin(angle) * dist), cy - (float)(Math.Cos(angle) * dist));
        for (int i = 0; i < 4; i++)
        {
            double a = a0 + i * Math.PI / 2;
            path.AddBezier(At(a, r), At(a + Math.PI / 4, r * 0.16f), At(a + Math.PI / 4, r * 0.16f), At(a + Math.PI / 2, r));
        }
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    /// <summary>A push pin pointing down, in a 16x16 box.</summary>
    public static GraphicsPath PinHead()
    {
        var p = new GraphicsPath(FillMode.Winding);
        p.AddPath(RoundedRect(new RectangleF(4.6f, 0.8f, 6.8f, 2.2f), 0.9f), false);
        p.AddPolygon([new PointF(6.1f, 2.6f), new PointF(9.9f, 2.6f), new PointF(10.7f, 7.4f), new PointF(5.3f, 7.4f)]);
        p.AddPath(RoundedRect(new RectangleF(2.6f, 7.0f, 10.8f, 2.4f), 1.1f), false);
        return p;
    }

    // ---- window chrome (Windows 11; harmless no-ops on Windows 10) -------------------------------

    public static void DarkTitleBar(IntPtr hwnd, Color caption)
    {
        int on = 1;
        Native.DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, 4);
        int color = ColorRef(caption);
        Native.DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref color, 4);
    }

    public static void RoundCorners(IntPtr hwnd, Color border)
    {
        int round = 3;  // DWMWCP_ROUNDSMALL
        Native.DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, 4);
        int color = ColorRef(border);
        Native.DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref color, 4);
    }

    static int ColorRef(Color c) => c.R | c.G << 8 | c.B << 16;
}

/// <summary>
/// Dark, gold-accented look for the tray menu. Menus fade in, items slide into place, and the hover
/// highlight eases in and out instead of snapping.
/// </summary>
sealed class MenuStyle : ToolStripRenderer, IDisposable
{
    /// <summary>Tag for disabled items that are information, not unavailable actions: drawn readable, not greyed.</summary>
    public static readonly object Info = new();
    /// <summary>Tag for the status line at the top.</summary>
    public static readonly object Header = new();

    readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    readonly Dictionary<ToolStripItem, Spring> glow = [];
    readonly Dictionary<ToolStripDropDown, double> opened = [];
    readonly bool dwmCorners = Environment.OSVersion.Version.Build >= 22000;
    double last;

    public MenuStyle()
    {
        timer.Tick += (_, _) => Animate();
    }

    public void Attach(ToolStripDropDown menu)
    {
        menu.Renderer = this;
        menu.Opening += (_, _) =>
        {
            menu.Opacity = 0;
            if (dwmCorners && menu.IsHandleCreated) Theme.RoundCorners(menu.Handle, Theme.Line);
        };
        menu.Opened += (_, _) =>
        {
            if (dwmCorners) Theme.RoundCorners(menu.Handle, Theme.Line);
            opened[menu] = Clock.Mono;
            Start();
        };
        menu.Closed += (_, _) =>
        {
            opened.Remove(menu);
            foreach (ToolStripItem item in menu.Items) glow.Remove(item);
        };
    }

    void Start()
    {
        last = Clock.Mono;
        timer.Start();
    }

    void Animate()
    {
        double now = Clock.Mono, dt = Math.Min(0.05, now - last);
        last = now;
        bool open = false;
        foreach (var (menu, at) in opened.ToList())
        {
            if (menu.IsDisposed || !menu.Visible) continue;
            open = true;
            try
            {
                double opacity = Ease.OutCubic(Ease.Since(now, at, 0.16));
                if (Math.Abs(menu.Opacity - opacity) > 0.004) menu.Opacity = opacity;
                bool entering = now - at < 0.25 + menu.Items.Count * 0.012;
                foreach (ToolStripItem item in menu.Items)
                {
                    if (!glow.TryGetValue(item, out var s)) glow[item] = s = new Spring(0, 7, 0.9);
                    s.Target = item.Selected && item.Enabled ? 1 : 0;  // follows the mouse and the arrow keys
                    if (s.Step(dt) || entering) item.Invalidate();
                }
            }
            catch (Exception)  // a menu that can't animate must still be usable
            {
                menu.Opacity = 1;
                opened.Remove(menu);
            }
        }
        if (!open) timer.Stop();
    }

    double Glow(ToolStripItem item) => glow.TryGetValue(item, out var s) ? Ease.Clamp01(s.Value) : item.Selected ? 1 : 0;

    /// <summary>Items slide in from the right, one after another, as the menu opens.</summary>
    double Enter(ToolStripItem item)
    {
        if (item.Owner is not ToolStripDropDown menu || !opened.TryGetValue(menu, out double at)) return 1;
        int index = menu.Items.IndexOf(item);
        return Ease.OutCubic(Ease.Since(Clock.Mono, at + index * 0.012, 0.2));
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Theme.Panel);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (dwmCorners) return;  // DWM draws a rounded border
        using var pen = new Pen(Theme.Line);
        var r = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        double t = Glow(e.Item);
        if (t <= 0.003) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = e.Item.Owner?.DeviceDpi / 96f ?? 1;
        var r = new RectangleF(4 * s, 1, e.Item.Width - 8 * s, e.Item.Height - 2);
        using (var path = Theme.RoundedRect(r, 5 * s))
        using (var fill = new LinearGradientBrush(r, Ease.Fade(Theme.Gold, 0.20 * t), Ease.Fade(Theme.Gold, 0.06 * t), 0f))
            g.FillPath(fill, path);
        float barH = (float)(r.Height * 0.55 * Ease.OutCubic(t));  // the accent bar grows from the middle
        using (var bar = new SolidBrush(Ease.Fade(Theme.Gold, t)))
        using (var path = Theme.RoundedRect(new RectangleF(r.X + 2 * s, r.Y + (r.Height - barH) / 2, 3 * s, barH), 1.5f * s))
            g.FillPath(bar, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var item = e.Item;
        float s = item.Owner?.DeviceDpi / 96f ?? 1;
        double t = Glow(item);
        var color = item.Tag == Header ? Theme.Gold
            : item.Tag == Info ? Theme.Dim
            : !item.Enabled ? Theme.Faint
            : Ease.Mix(Theme.Text, Color.White, t);
        var r = e.TextRectangle;
        int dx = (int)Math.Round(3 * s * t + 10 * s * (1 - Enter(item)));  // nudges right on hover
        if (e.TextFormat.HasFlag(TextFormatFlags.Right)) dx = 0;  // the shortcut column stays put
        // Drawn here rather than by the base renderer, which greys out disabled items whatever the colour.
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, new Rectangle(r.X + dx, r.Y, r.Width, r.Height), color, e.TextFormat);
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Image == null) return;
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        float pop = (float)(1 + 0.08 * Glow(e.Item));
        var r = e.ImageRectangle;
        float w = r.Width * pop, h = r.Height * pop;
        g.DrawImage(e.Image, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = e.Item.Owner?.DeviceDpi / 96f ?? 1;
        var r = e.ImageRectangle;
        float size = 14 * s;
        var box = new RectangleF(r.X + (r.Width - size) / 2, r.Y + (r.Height - size) / 2, size, size);
        using (var path = Theme.RoundedRect(box, 3.5f * s))
        using (var fill = new LinearGradientBrush(box, Theme.GoldLight, Theme.GoldDeep, 90f))
            g.FillPath(fill, path);
        using var pen = new Pen(Theme.Ink, 2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, [
            new PointF(box.X + size * 0.24f, box.Y + size * 0.52f),
            new PointF(box.X + size * 0.43f, box.Y + size * 0.70f),
            new PointF(box.X + size * 0.77f, box.Y + size * 0.32f),
        ]);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        float s = e.Item.Owner?.DeviceDpi / 96f ?? 1;
        int y = e.Item.Height / 2;
        float x0 = 10 * s, x1 = e.Item.Width - 10 * s;
        using var brush = new LinearGradientBrush(new PointF(x0 - 1, 0), new PointF(x1 + 2, 0), Theme.Line, Ease.Fade(Theme.Line, 0.15));
        e.Graphics.FillRectangle(brush, x0, y, x1 - x0, Math.Max(1, s));
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Ease.Mix(Theme.Dim, Theme.Gold, e.Item == null ? 0 : Glow(e.Item));
        base.OnRenderArrow(e);
    }

    public void Dispose() => timer.Dispose();
}
