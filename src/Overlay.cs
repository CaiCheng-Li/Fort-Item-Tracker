using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace FortItemTracker;

/// <summary>
/// Always-on-top, click-through overlay: session timer, time since the last find, and recent finds.
/// A layered window that is only ever shown with SW_SHOWNOACTIVATE, so it never takes focus from the game.
/// </summary>
sealed class Overlay : NativeWindow, IDisposable
{
    const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    const uint SWP_KEEP = 0x0001 | 0x0002 | 0x0010;  // NOSIZE NOMOVE NOACTIVATE

    static readonly Color Panel = Color.FromArgb(18, 22, 28);
    static readonly Color Fg = Color.White;
    static readonly Color Dim = Color.FromArgb(205, 213, 224);
    static readonly Color Accent = Color.FromArgb(255, 211, 77);

    readonly Func<OverlayState> source;
    readonly double scale, opacity;
    readonly int maxItems;
    readonly bool showNames;
    readonly Font timerFont, sinceFont, rowFont, countFont;
    readonly StringFormat format;
    readonly System.Windows.Forms.Timer timer;
    readonly Dictionary<string, Bitmap?> iconCache = [];
    OverlayState? drawn;
    long drawnSecond = -1;
    bool shown;
    double nextRaise;

    public Overlay(Config cfg, Func<OverlayState> source)
    {
        this.source = source;
        scale = Math.Max(0.5, cfg.OverlayScale);
        opacity = Math.Clamp(cfg.OverlayOpacity, 0.2, 1.0);
        maxItems = Math.Max(0, cfg.OverlayItems);
        showNames = cfg.OverlayShowNames;
        timerFont = new Font("Segoe UI Semibold", Px(28), GraphicsUnit.Pixel);
        sinceFont = new Font("Segoe UI", Px(15), GraphicsUnit.Pixel);
        rowFont = new Font("Segoe UI", Px(16), GraphicsUnit.Pixel);
        countFont = new Font("Segoe UI Semibold", Px(17), GraphicsUnit.Pixel);
        format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.LineAlignment = StringAlignment.Center;
        format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces;
        CreateHandle(new CreateParams
        {
            Caption = "Fort Item Tracker overlay",
            Style = unchecked((int)0x80000000),  // WS_POPUP
            ExStyle = WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE,
            Width = 1,
            Height = 1,
        });
        timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    int Px(double v) => Math.Max(1, (int)Math.Round(v * scale));

    void Tick()
    {
        try
        {
            var st = source();
            double now = Clock.Mono;
            if (!st.Visible)
            {
                if (shown) Native.ShowWindow(Handle, SW_HIDE);
                shown = false;
                return;
            }
            long second = (long)Math.Floor(st.Timer(now));  // redraw when the state or the shown second changes
            if (!ReferenceEquals(st, drawn) || second != drawnSecond)
            {
                Present(st, now);
                (drawn, drawnSecond) = (st, second);
            }
            if (!shown)
            {
                Native.ShowWindow(Handle, SW_SHOWNOACTIVATE);
                shown = true;
            }
            if (now >= nextRaise)  // stay above the game without taking focus
            {
                Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, SWP_KEEP);
                nextRaise = now + 2;
            }
        }
        catch (Exception e)  // never take the tracker down with the overlay
        {
            timer.Stop();
            Activity.Write($"overlay stopped: {e.Message}");
        }
    }

    Bitmap? Icon(string? path)
    {
        if (path == null) return null;
        if (iconCache.TryGetValue(path, out var cached)) return cached;
        Bitmap? icon = null;
        var img = ImageIO.Load(path);
        if (img != null && img.Width > 0 && img.Height > 0)
        {
            using var full = ImageIO.ToBitmap(img, PixelFormat.Format32bppPArgb);
            int box = Px(30);
            double f = Math.Min(1.0, box / (double)Math.Max(img.Width, img.Height));
            int w = Math.Max(1, (int)Math.Round(img.Width * f)), h = Math.Max(1, (int)Math.Round(img.Height * f));
            icon = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(icon);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(full, new Rectangle(0, 0, w, h));
        }
        iconCache[path] = icon;
        return icon;
    }

    void Present(OverlayState st, double now)
    {
        int pad = Px(12), top = Px(6), iconBox = Px(30), rowH = Px(34);
        double timerValue = st.Timer(now);
        var lines = new List<(Font Font, string Text, Color Color, int H)> { (timerFont, Fmt.Duration(timerValue), Fg, Px(38)) };
        if (st.LastItemT is double last)
            lines.Add((sinceFont, $"Since last item  {Fmt.Duration(timerValue - last)}", Accent, Px(22)));
        var rows = st.Rows.Take(maxItems).Select(r =>
        {
            var icon = Icon(r.Icon);
            return (Icon: icon, Count: $"x{r.Count}", Text: icon != null && !showNames ? r.Time : $"{r.Time}   {r.Name}");
        }).ToList();

        float width, countW = 0;
        using (var probe = new Bitmap(1, 1))
        using (var mg = Graphics.FromImage(probe))
        {
            mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float Measure(string t, Font f) => mg.MeasureString(t, f, PointF.Empty, format).Width;
            width = lines.Max(l => Measure(l.Text, l.Font));
            foreach (var r in rows) countW = Math.Max(countW, Measure(r.Count, countFont));
            float textX0 = iconBox + Px(4) + countW + Px(10);
            foreach (var r in rows) width = Math.Max(width, textX0 + Measure(r.Text, rowFont));
        }
        int textX = iconBox + Px(4) + (int)Math.Ceiling(countW) + Px(10);  // icon | x3 | time, in fixed columns
        int height = lines.Sum(l => l.H) + rows.Count * rowH + (rows.Count > 0 ? Px(4) : 0);
        int w = (int)Math.Ceiling(width) + 2 * pad, h = height + 2 * top;

        IntPtr screen = Native.GetDC(IntPtr.Zero);
        IntPtr mem = Native.CreateCompatibleDC(screen);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        IntPtr dib = Native.CreateDIBSection(screen, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = Native.SelectObject(mem, dib);
        try
        {
            // Draw straight into the window bitmap: premultiplied BGRA is what UpdateLayeredWindow wants.
            using (var canvas = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits))
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                using (var path = RoundedRect(new RectangleF(0, 0, w - 1, h - 1), Px(8)))
                using (var fill = new SolidBrush(Color.FromArgb((int)(255 * opacity), Panel)))
                    g.FillPath(fill, path);
                float y = top;
                foreach (var (font, text, color, lh) in lines)
                {
                    using var brush = new SolidBrush(color);
                    g.DrawString(text, font, brush, new RectangleF(pad, y, w, lh), format);
                    y += lh;
                }
                if (rows.Count > 0) y += Px(4);
                using var fg = new SolidBrush(Fg);
                using var dim = new SolidBrush(Dim);
                foreach (var (icon, count, text) in rows)  // the icon column is always reserved so everything lines up
                {
                    if (icon != null)
                        g.DrawImage(icon, pad + (iconBox - icon.Width) / 2, (int)y + (rowH - icon.Height) / 2, icon.Width, icon.Height);
                    g.DrawString(count, countFont, fg, new RectangleF(pad + iconBox + Px(4), y, w, rowH), format);
                    g.DrawString(text, rowFont, dim, new RectangleF(pad + textX, y, w, rowH), format);
                    y += rowH;
                }
            }
            var dst = new POINT(st.X, st.Y);
            var size = new SIZE(w, h);
            var src = new POINT(0, 0);
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };  // per-pixel alpha
            Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            Native.SelectObject(mem, old);
            Native.DeleteObject(dib);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        timer.Dispose();
        DestroyHandle();
        foreach (var icon in iconCache.Values) icon?.Dispose();
        timerFont.Dispose(); sinceFont.Dispose(); rowFont.Dispose(); countFont.Dispose();
        format.Dispose();
    }
}
