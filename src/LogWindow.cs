using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text;
using System.Text.RegularExpressions;

namespace FortItemTracker;

/// <summary>Shows what the tracker reports: sessions, finds, test results and problems, under a live session header.</summary>
sealed class LogWindow : Form
{
    static readonly Color[] Palette = [Theme.Text, Theme.Dim, Theme.Gold, Theme.Good, Theme.Info, Theme.Warn, Theme.Bad];
    static readonly Regex FindLine = new(@"^\s+\d{1,2}:\d{2}:\d{2} [AP]M\s+\+\d", RegexOptions.Compiled);
    static readonly Regex Problem = new(@"error|can't|couldn't|failed|stopped|isn't working|not available|unreadable", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    readonly RichTextBox box;
    readonly LogHeader header;
    readonly System.Windows.Forms.Timer fade = new() { Interval = 15 };
    readonly string fontName;
    double fadeAt;
    Point fadeFrom;

    public LogWindow(Icon icon, Func<TrackerStatus> status)
    {
        Text = "Fort Item Tracker - Activity";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Ink;
        ClientSize = new Size(LogicalToDeviceUnits(860), LogicalToDeviceUnits(520));
        MinimumSize = new Size(LogicalToDeviceUnits(480), LogicalToDeviceUnits(260));

        using (var probe = new Font("Cascadia Mono", 10f)) fontName = probe.Name == "Cascadia Mono" ? "Cascadia Mono" : "Consolas";
        box = new RichTextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Ink,
            ForeColor = Theme.Text,
            Font = new Font(fontName, 10f),
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
            DetectUrls = false,
            HideSelection = false,
            Dock = DockStyle.Fill,
        };
        box.HandleCreated += (_, _) => Native.SetWindowTheme(box.Handle, "DarkMode_Explorer", null);  // dark scroll bars
        var body = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Ink,
            Padding = new Padding(LogicalToDeviceUnits(16), LogicalToDeviceUnits(10), 0, 0),
        };
        body.Controls.Add(box);
        header = new LogHeader(status) { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(88) };
        Controls.Add(body);
        Controls.Add(header);  // docks first, so it takes the top

        Append(Activity.Snapshot());
        Activity.Line += OnLine;
        fade.Tick += (_, _) => FadeStep();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle, Theme.PanelTop);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ScrollToEnd();
        // rises into place as it fades in
        fadeFrom = Location;
        Opacity = 0;
        Location = new Point(fadeFrom.X, fadeFrom.Y + LogicalToDeviceUnits(14));
        fadeAt = Clock.Mono;
        fade.Start();
    }

    void FadeStep()
    {
        double t = Ease.OutCubic(Ease.Since(Clock.Mono, fadeAt, 0.22));
        Opacity = t;
        Location = new Point(fadeFrom.X, fadeFrom.Y + (int)Math.Round(LogicalToDeviceUnits(14) * (1 - t)));
        if (t >= 1) fade.Stop();
    }

    void OnLine(string line)
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke(() => Append([line]));
    }

    static int ColorFor(string line)
    {
        if (FindLine.IsMatch(line)) return line.Contains("icon unsure") ? 5 : 2;
        if (line.TrimStart().StartsWith("FOUND:")) return 2;
        if (line.StartsWith("Session ")) return line.Contains(" started ") || line.Contains(" resumed ") ? 3 : 4;
        if (line.StartsWith("Warning:")) return 5;
        if (Problem.IsMatch(line)) return 6;
        if (line.StartsWith("config:") || line.Contains("not recognised") || line.StartsWith("Hotkey")) return 5;
        if (line.StartsWith("   ")) return 1;
        return 0;
    }

    /// <summary>Adds lines in their colours, following the end unless you've scrolled up to read.</summary>
    void Append(IReadOnlyCollection<string> lines)
    {
        if (lines.Count == 0) return;
        bool follow = AtEnd();
        int start = box.SelectionStart, length = box.SelectionLength;
        var rtf = new StringBuilder(@"{\rtf1\ansi\deff0{\fonttbl{\f0 ").Append(fontName).Append(";}}{\\colortbl;");
        foreach (var c in Palette) rtf.Append($"\\red{c.R}\\green{c.G}\\blue{c.B};");
        rtf.Append(@"}\f0\fs20 ");
        foreach (var line in lines)
        {
            rtf.Append($"\\cf{ColorFor(line) + 1} ");
            foreach (char ch in line)
            {
                if (ch is '\\' or '{' or '}') rtf.Append('\\').Append(ch);
                else if (ch > 127) rtf.Append($"\\u{(short)ch}?");
                else rtf.Append(ch);
            }
            rtf.Append(@"\par ");
        }
        rtf.Append('}');
        box.Select(box.TextLength, 0);
        box.SelectedRtf = rtf.ToString();
        if (follow) ScrollToEnd();
        else box.Select(start, length);
    }

    bool AtEnd()
    {
        if (!box.IsHandleCreated || box.TextLength == 0) return true;
        var last = box.GetPositionFromCharIndex(box.TextLength - 1);
        return last.Y < box.ClientSize.Height + box.Font.Height * 2;
    }

    void ScrollToEnd()
    {
        box.Select(box.TextLength, 0);
        box.ScrollToCaret();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Activity.Line -= OnLine;
        fade.Dispose();
        base.OnFormClosed(e);
    }
}

/// <summary>The top of the activity window: the session timer, ticking over like the overlay's, and the finds count.</summary>
sealed class LogHeader : Control
{
    const double RollS = 0.26;

    readonly Func<TrackerStatus> status;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    readonly Font titleFont, subFont, timerFont, labelFont, countFont;
    readonly Spring pop = new(1, 5, 0.35, 0.002);
    readonly StringFormat format;
    TrackerStatus st = TrackerStatus.Starting;
    string time = "", prevTime = "";
    double rollAt = -10, foundAt = -10, last;
    int found = -1, dir = 1;
    string? session;

    public LogHeader(Func<TrackerStatus> status)
    {
        this.status = status;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        float s = DeviceDpi / 96f;
        titleFont = Theme.Font("Segoe UI Semibold", 17 * s, "Segoe UI");
        subFont = Theme.Font("Segoe UI", 12.5f * s, "Segoe UI");
        timerFont = Theme.Font("Bahnschrift SemiBold", 34 * s, "Segoe UI Semibold");
        labelFont = Theme.Font("Bahnschrift SemiBold", 10.5f * s, "Segoe UI Semibold");
        countFont = Theme.Font("Bahnschrift SemiBold", 22 * s, "Segoe UI Semibold");
        format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.LineAlignment = StringAlignment.Center;
        format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces;
        timer.Tick += (_, _) => Step();
        last = Clock.Mono;
        Step();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        timer.Enabled = Visible;
    }

    void Step()
    {
        double now = Clock.Mono, dt = Math.Min(0.1, now - last);
        last = now;
        st = status();
        if (st.SessionId == session && st.Found > found && found >= 0)
        {
            foundAt = now;
            pop.Kick(1.45);
        }
        (session, found) = (st.SessionId, st.Found);
        string t = st.SessionId == null ? "--:--:--" : Fmt.Duration(st.Timer(now));
        if (t != time)
        {
            dir = string.CompareOrdinal(t, time) >= 0 ? 1 : -1;
            prevTime = time.Length == t.Length ? time : t;
            time = t;
            rollAt = now;
        }
        bool moving = pop.Step(dt) | now - rollAt < RollS | now - foundAt < 1.2;
        Invalidate();
        double next = st.SessionId == null ? 0.25 : Math.Floor(st.Timer(now)) + 1 - st.Timer(now);
        timer.Interval = moving ? 15 : Math.Clamp((int)Math.Ceiling(next * 1000) + 1, 10, 250);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float s = DeviceDpi / 96f, w = Width, h = Height, cy = h / 2f;
        double now = Clock.Mono, flash = 1 - Ease.Since(now, foundAt, 1.2);
        flash *= flash;

        using (var bg = new LinearGradientBrush(new RectangleF(0, -1, w, h + 2), Theme.PanelTop, Theme.Panel, 90f))
            g.FillRectangle(bg, 0, 0, w, h);
        if (flash > 0.01)
            using (var glow = new LinearGradientBrush(new RectangleF(w * 0.4f, 0, w * 0.6f + 2, h), Color.FromArgb(0, Theme.Gold), Ease.Fade(Theme.Gold, 0.16 * flash), 0f))
                g.FillRectangle(glow, w * 0.4f, 0, w * 0.6f, h);
        using (var line = new LinearGradientBrush(new PointF(-1, 0), new PointF(w + 2, 0), Ease.Fade(Theme.Gold, 0.85), Ease.Fade(Theme.Gold, 0.05)))
            g.FillRectangle(line, 0, h - 2 * s, w, 2 * s);

        // logo: the icon's two sparkles, which twinkle on a find
        double tw = Ease.Since(now, foundAt, 0.8);
        float lx = 22 * s;
        using (var gold = new LinearGradientBrush(new RectangleF(lx, cy - 24 * s, 48 * s, 48 * s), Theme.GoldLight, Theme.GoldDeep, 90f))
        {
            Theme.Sparkle(g, gold, lx + 16 * s, cy + 4 * s, 17 * s * (float)(1 + 0.25 * Ease.Bump(tw)), (float)(90 * Ease.OutCubic(tw)));
            Theme.Sparkle(g, gold, lx + 34 * s, cy - 13 * s, 7 * s * (float)(1 + 0.6 * Ease.Bump(tw)), (float)(-90 * Ease.OutCubic(tw)));
        }
        float tx = lx + 56 * s;
        using (var b = new SolidBrush(Theme.Text)) g.DrawString("Fort Item Tracker", titleFont, b, tx, cy - 12 * s, format);
        using (var b = new SolidBrush(Theme.Dim)) g.DrawString(Subtitle(), subFont, b, tx, cy + 13 * s, format);

        // right: SESSION over the timer, and the finds count
        float right = w - 22 * s, timerW = TextW(time, timerFont);
        float x = right - timerW;
        using (var b = new SolidBrush(Theme.Dim)) g.DrawString("SESSION", labelFont, b, x, cy - 21 * s, format);
        DrawTime(g, x, cy + 7 * s, 40 * s, now);

        string count = Math.Max(0, found).ToString();
        float cw = TextW(count, countFont), lw = TextW("FOUND", labelFont), colW = Math.Max(cw, lw);
        float cx = x - 30 * s - colW;
        using (var b = new SolidBrush(Theme.Dim)) g.DrawString("FOUND", labelFont, b, cx + (colW - lw) / 2, cy - 21 * s, format);
        var saved = g.Save();
        g.TranslateTransform(cx + colW / 2, cy + 7 * s);
        g.ScaleTransform((float)pop.Value, (float)pop.Value);
        using (var b = new SolidBrush(Ease.Mix(Theme.Gold, Color.White, flash))) g.DrawString(count, countFont, b, -cw / 2, 0, format);
        g.Restore(saved);
        using (var pen = new Pen(Theme.Line, Math.Max(1, s)))
            g.DrawLine(pen, x - 15 * s, cy - 22 * s, x - 15 * s, cy + 22 * s);
    }

    string Subtitle()
    {
        var parts = st.Text.Split(" | ");
        if (st.SessionId == null) return st.Text;
        var rest = parts.Skip(2).Select(p => p.StartsWith("last: ") ? "Last find: " + p[6..] : p).ToList();
        return $"Session {st.SessionId}  ·  " + (rest.Count > 0 ? string.Join("  ·  ", rest) : "nothing found yet");
    }

    float TextW(string text, Font f) => text.Length == 0 ? 0 : CreateGraphicsCached().MeasureString(text, f, PointF.Empty, format).Width;

    Graphics? measure;
    Bitmap? probe;
    Graphics CreateGraphicsCached()
    {
        if (measure != null) return measure;
        probe = new Bitmap(1, 1);
        measure = Graphics.FromImage(probe);
        measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        return measure;
    }

    void DrawTime(Graphics g, float x, float cy, float lineH, double now)
    {
        double p = Ease.OutCubic(Ease.Since(now, rollAt, RollS));
        bool rolling = p < 1 && prevTime.Length == time.Length && prevTime != time;
        float travel = lineH * 0.6f * dir;
        var saved = g.Save();
        g.SetClip(new RectangleF(x - 4, cy - lineH / 2, TextW(time, timerFont) + 8, lineH), CombineMode.Intersect);
        using var b = new SolidBrush(Theme.Text);
        for (int i = 0; i < time.Length; i++)
        {
            char c = time[i];
            var color = c is ':' or '-' ? Ease.Fade(Theme.Dim, 0.8) : Theme.Text;
            if (rolling && prevTime[i] != c)
            {
                b.Color = Ease.Fade(color, 1 - p);
                g.DrawString(prevTime[i].ToString(), timerFont, b, x, cy - travel * (float)p, format);
                b.Color = Ease.Fade(color, p);
                g.DrawString(c.ToString(), timerFont, b, x, cy + travel * (float)(1 - p), format);
            }
            else
            {
                b.Color = color;
                g.DrawString(c.ToString(), timerFont, b, x, cy, format);
            }
            x += TextW(c.ToString(), timerFont);
        }
        g.Restore(saved);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            titleFont.Dispose(); subFont.Dispose(); timerFont.Dispose(); labelFont.Dispose(); countFont.Dispose();
            format.Dispose();
            measure?.Dispose();
            probe?.Dispose();
        }
        base.Dispose(disposing);
    }
}
