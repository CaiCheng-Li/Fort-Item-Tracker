using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

namespace FortItemTracker;

/// <summary>The mouse as the overlay sees it: screen position, whether a cursor is showing, and the left button.</summary>
readonly record struct MouseInfo(bool Visible, int X, int Y, bool Down);

/// <summary>
/// Always-on-top overlay over the game: session timer, time since the last find, and recent finds.
/// A layered window shown with SW_SHOWNOACTIVATE, so it never takes focus from the game. Clicks go
/// through it to the game, except on the pin that appears while the mouse is over it. Unpinned, the
/// panel can be dragged anywhere over the game; where it ends up is saved to config.toml.
/// </summary>
sealed class Overlay : NativeWindow, IDisposable
{
    const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    const int GWL_EXSTYLE = -20, SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    const uint SWP_KEEP = 0x0001 | 0x0002 | 0x0010;  // NOSIZE NOMOVE NOACTIVATE
    const int WM_SETCURSOR = 0x20, WM_MOUSEACTIVATE = 0x21, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_CAPTURECHANGED = 0x215;
    const double RollS = 0.26;     // a digit rolling over
    const double FindS = 1.2;      // the glow after a find
    const double AutoPinS = 1.5;   // the game hid the cursor this long: pin the panel where it is
    const string SessionLabel = "SESSION", PausedLabel = "PAUSED", SinceLabel = "SINCE LAST ITEM", Hint = "DRAG TO MOVE · PIN TO LOCK";

    sealed class Row(string name)
    {
        public readonly string Name = name;
        public string Count = "", Text = "";
        public Bitmap? Icon;
        public int Index;
        public bool Leaving;
        public double EnterAt, FlashAt = -10;
        public readonly Spring Y = new(0, 4.2, 0.78, 0.05);
        public readonly Spring Alpha = new(0, 4.5, 1, 0.004);
        public readonly Spring Slide = new(0, 4, 0.72, 0.05);
        public readonly Spring Pop = new(1, 5.5, 0.32, 0.002);
    }

    /// <summary>A line of digits where each changed digit rolls over like an odometer.</summary>
    sealed class Roll
    {
        public string Text = "", Prev = "";
        public double At = -10;
        public int Dir = 1;

        public bool Set(string text, double now, bool animate)
        {
            if (text == Text) return false;
            Dir = string.CompareOrdinal(text, Text) >= 0 ? 1 : -1;
            Prev = animate && Text.Length == text.Length ? Text : text;
            Text = text;
            At = now;
            return true;
        }

        public bool Active(double now) => Prev != Text && now - At < RollS;
    }

    readonly Config cfg;
    readonly Func<OverlayState> source;
    readonly double scale, opacity;
    readonly int maxItems;
    readonly bool showNames;
    readonly Font timerFont, sinceFont, labelFont, countFont, rowFont;
    readonly StringFormat format;
    readonly System.Windows.Forms.Timer timer;
    readonly Dictionary<string, Bitmap?> iconCache = [];
    readonly Dictionary<(Font, char), float> advances = [];
    readonly Bitmap probe = new(1, 1);
    readonly Graphics measure;
    readonly GraphicsPath pinHead = Theme.PinHead();
    readonly SolidBrush brush = new(Color.White);
    readonly int margin, padX, padTop, padBottom, labelH, timerH, sinceH, gapH, rowH, iconBox, footerH, radius, edgeGap, snapPx;

    // what is shown
    OverlayState state = OverlayState.Hidden;
    OverlayState? lastVisible;
    RECT area;
    readonly List<Row> rows = [];
    readonly Roll mainRoll = new(), sinceRoll = new();

    // motion
    readonly Spring appear = new(0, 2.6, 0.9, 0.003);
    readonly Spring panelW = new(0, 3.6, 0.82, 0.05), panelH = new(0, 3.6, 0.82, 0.05);
    readonly Spring sinceVis = new(0, 3.4, 1, 0.003), rowsVis = new(0, 3.4, 1, 0.003), countCol = new(0, 4, 1, 0.05);
    readonly Spring hover = new(0, 4.5, 1, 0.004), pinHover = new(0, 6, 0.75, 0.004), pinPress = new(0, 9, 0.6, 0.004);
    readonly Spring resetHover = new(0, 6, 0.75, 0.004), resetPress = new(0, 9, 0.6, 0.004);
    readonly Spring pinTilt = new(0, 3.2, 0.38, 0.003);  // wobbles as it flips
    readonly Spring edit = new(0, 3.2, 1, 0.003);        // unpinned: glow, hint
    readonly Spring lift = new(0, 4.5, 0.65, 0.003);     // picked up while dragging
    readonly Spring posX = new(0, 8, 0.82, 0.05), posY = new(0, 8, 0.82, 0.05);
    readonly Spring[] springs;
    double lastNow, nextRaise, findAt = -10, editAt, resetAt = -10;
    int seed;
    float rowsTop;

    // position and the mouse
    bool anchorRight, anchorBottom;
    int offX, offY;
    bool pinned = true, dragging, settling, pinPressed, resetPressed, interactive, wantInteractive;
    double grabX, grabY;
    double? hiddenSince;
    MouseInfo pointer;

    // the window bitmap
    bool shown, busy, rolling, layerDirty = true;
    int frameW, frameH;
    IntPtr memDc, dib, oldBitmap;
    Bitmap? canvas, layer;

    public Overlay(Config cfg, Func<OverlayState> source)
    {
        this.cfg = cfg;
        this.source = source;
        scale = Math.Max(0.5, cfg.OverlayScale);
        opacity = Math.Clamp(cfg.OverlayOpacity, 0.2, 1.0);
        maxItems = Math.Max(0, cfg.OverlayItems);
        showNames = cfg.OverlayShowNames;
        anchorRight = cfg.OverlayAnchor.EndsWith("right");
        anchorBottom = cfg.OverlayAnchor.StartsWith("bottom");
        (offX, offY) = (cfg.OverlayX, cfg.OverlayY);
        timerFont = Theme.Font("Bahnschrift SemiBold", P(32), "Segoe UI Semibold");
        sinceFont = Theme.Font("Bahnschrift SemiBold", P(16), "Segoe UI Semibold");
        labelFont = Theme.Font("Bahnschrift SemiBold", P(11), "Segoe UI Semibold");
        countFont = Theme.Font("Bahnschrift SemiBold", P(18), "Segoe UI Semibold");
        rowFont = Theme.Font("Bahnschrift", P(15), "Segoe UI");
        format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.LineAlignment = StringAlignment.Center;
        format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces;
        measure = Graphics.FromImage(probe);
        measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        (margin, padX, padTop, padBottom) = (Px(30), Px(14), Px(9), Px(11));
        (labelH, timerH, sinceH, gapH, rowH, iconBox, footerH) = (Px(16), Px(38), Px(22), Px(12), Px(34), Px(30), Px(22));
        (radius, edgeGap, snapPx) = (Px(12), Px(16), Px(12));
        springs = [appear, panelW, panelH, sinceVis, rowsVis, countCol, hover, pinHover, pinPress, resetHover, resetPress, pinTilt, edit, lift, posX, posY];
        CreateHandle(new CreateParams
        {
            Caption = "Fort Item Tracker overlay",
            Style = unchecked((int)0x80000000),  // WS_POPUP
            ExStyle = WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE,
            Width = 1,
            Height = 1,
        });
        lastNow = Clock.Mono;
        timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    /// <summary>Pinned: clicks go through to the game. Unpinned: the panel can be dragged.</summary>
    public bool Pinned
    {
        get => pinned;
        set => SetPinned(value);
    }

    int Px(double v) => Math.Max(1, (int)Math.Round(v * scale));
    float P(double v) => (float)(v * scale);

    // ---- frame loop -----------------------------------------------------------------------------

    void Tick()
    {
        try
        {
            double now = Clock.Mono;
            bool draw = Advance(now, source(), ReadPointer());
            bool visible = appear.Target > 0 || appear.Value > 0.004;
            if (visible && draw) Present(now);
            if (visible != shown)
            {
                Native.ShowWindow(Handle, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
                shown = visible;
            }
            SetInteractive(shown && wantInteractive);
            if (shown && now >= nextRaise)  // stay above the game without taking focus
            {
                Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, SWP_KEEP);
                nextRaise = now + 2;
            }
            timer.Interval = NextInterval(now);
        }
        catch (Exception e)  // never take the tracker down with the overlay
        {
            timer.Stop();
            SetInteractive(false);  // and never leave it catching clicks meant for the game
            Native.ShowWindow(Handle, SW_HIDE);
            Activity.Write($"overlay stopped: {e.Message}");
        }
    }

    /// <summary>Soon, rather than at the next idle tick.</summary>
    void Kick() => timer.Interval = 10;

    int NextInterval(double now)
    {
        if (busy) return 15;
        if (!shown) return 250;
        if (rolling) return 30;  // the once-a-second digit roll: smooth enough at half the cost
        var (x, y) = PanelPos();
        float reach = P(80);
        bool near = pointer.Visible && pointer.X > x - reach && pointer.X < x + panelW.Value + reach
                    && pointer.Y > y - reach && pointer.Y < y + panelH.Value + reach;
        int idle = near ? 15 : pointer.Visible ? 60 : 250;
        double t = lastVisible?.Timer(now) ?? 0;
        int nextSecond = (int)Math.Ceiling((Math.Floor(t) + 1 - t) * 1000) + 1;  // roll the digits on time
        return Math.Clamp(Math.Min(idle, nextSecond), 10, 250);
    }

    static MouseInfo ReadPointer()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        bool visible = Native.GetCursorInfo(ref ci) && (ci.flags & 1) != 0 && ci.hCursor != IntPtr.Zero;  // a game hides it
        int button = Native.GetSystemMetrics(23 /* SM_SWAPBUTTON */) != 0 ? 0x02 : 0x01;
        bool down = (Native.GetAsyncKeyState(button) & 0x8000) != 0;
        return new MouseInfo(visible, ci.ptScreenPos.X, ci.ptScreenPos.Y, down);
    }

    /// <summary>Moves everything on to <paramref name="now"/>. True when the picture changed.</summary>
    bool Advance(double now, OverlayState st, MouseInfo p)
    {
        double dt = Math.Clamp(now - lastNow, 0, 0.1);
        lastNow = now;
        bool changed = !ReferenceEquals(st, state);
        if (changed) Apply(st, now);
        appear.Target = st.Visible ? 1 : 0;
        Track(p, now);

        bool moving = false;
        if (settling) (posX.Target, posY.Target) = Anchored();
        foreach (var s in springs) moving |= s.Step(dt);
        if (settling && posX.Settled && posY.Settled) settling = false;
        foreach (var row in rows)
        {
            bool entered = now >= row.EnterAt;
            row.Alpha.Target = !row.Leaving && entered ? 1 : 0;
            row.Slide.Target = row.Leaving ? -P(12) : entered ? 0 : row.Slide.Value;
            moving |= row.Y.Step(dt) | row.Alpha.Step(dt) | row.Slide.Step(dt) | row.Pop.Step(dt) | now < row.EnterAt;
            moving |= now - row.FlashAt < FindS;
        }
        rows.RemoveAll(r => r.Leaving && r.Alpha.Value < 0.01);

        bool timers = false;
        if (lastVisible is OverlayState v)  // keeps counting while it fades out
        {
            double t = v.Timer(now);
            int length = mainRoll.Text.Length;
            timers |= mainRoll.Set(Fmt.Duration(t), now, shown);
            if (v.LastItemT is double last) timers |= sinceRoll.Set(Fmt.Duration(t - last), now, shown);
            if (mainRoll.Text.Length != length) Measure();
        }
        rolling = mainRoll.Active(now) || sinceRoll.Active(now);
        bool effects = now - findAt < FindS || !pinned;
        busy = moving || effects;
        if (moving || effects || changed) layerDirty = true;
        return layerDirty || timers || rolling;
    }

    void Apply(OverlayState st, double now)
    {
        state = st;
        if (!st.Visible) return;  // fade out as it was
        area = st.Area;
        var prev = lastVisible;
        lastVisible = st;
        bool appearing = appear.Value < 0.05;
        bool sameSession = prev != null && prev.TimerAt == st.TimerAt && prev.MonoRef == st.MonoRef;
        bool found = sameSession && !appearing && st.LastItemT != null && st.LastItemT != prev!.LastItemT;
        if (found)
        {
            findAt = now;
            seed = Environment.TickCount;
        }

        var target = st.Rows.Take(maxItems).ToList();
        var live = new HashSet<Row>();
        for (int i = 0; i < target.Count; i++)
        {
            var r = target[i];
            var row = rows.Find(x => !x.Leaving && x.Name == r.Name);
            if (row == null)
            {
                row = new Row(r.Name);
                row.Y.Snap(i * rowH);
                row.Slide.Snap(-P(18));
                row.EnterAt = appearing ? now + 0.15 + 0.07 * i : found ? now + 0.1 : now;  // give the others time to make room
                rows.Add(row);
            }
            if (found && i == 0)
            {
                row.FlashAt = now;
                row.Pop.Kick(1.55);
            }
            row.Count = $"×{r.Count}";
            row.Icon = Icon(r.Icon);
            row.Text = row.Icon != null && !showNames ? r.Time : $"{r.Time}   {r.Name}";
            row.Index = i;
            row.Y.Target = i * rowH;
            live.Add(row);
        }
        foreach (var row in rows)
            if (!live.Contains(row)) row.Leaving = true;

        sinceVis.Target = st.LastItemT != null ? 1 : 0;
        rowsVis.Target = target.Count > 0 ? 1 : 0;
        Measure();
        if (appearing)
        {
            Spring[] layout = [panelW, panelH, sinceVis, rowsVis, countCol];
            foreach (var s in layout) s.Snap(s.Target);
        }
    }

    // ---- layout ----------------------------------------------------------------------------------

    float GlyphWidth(char c, Font f)
    {
        if (!advances.TryGetValue((f, c), out float a))
            advances[(f, c)] = a = measure.MeasureString(c.ToString(), f, PointF.Empty, format).Width;
        return a;
    }

    float Tracking => P(1.1);
    float SpacedWidth(string s, Font f) => s.Sum(c => GlyphWidth(c, f) + Tracking) - Tracking;
    float CharsWidth(string s, Font f) => s.Sum(c => GlyphWidth(c, f));
    float TextWidth(string s, Font f) => measure.MeasureString(s, f, PointF.Empty, format).Width;

    /// <summary>The size the panel is heading for.</summary>
    void Measure()
    {
        if (lastVisible is not OverlayState st) return;
        var live = rows.Where(r => !r.Leaving).ToList();
        string time = Fmt.Duration(st.Timer(Clock.Mono));
        countCol.Target = live.Count == 0 ? countCol.Target : Math.Ceiling(live.Max(r => TextWidth(r.Count, countFont)));
        double w = P(14) + SpacedWidth(SessionLabel, labelFont) + P(30);  // sparkle, label, pin
        w = Math.Max(w, CharsWidth(time, timerFont));
        if (st.LastItemT != null) w = Math.Max(w, SpacedWidth(SinceLabel, labelFont) + P(8) + CharsWidth(time, sinceFont));
        foreach (var r in live) w = Math.Max(w, iconBox + P(8) + countCol.Target + P(10) + TextWidth(r.Text, rowFont));
        if (!pinned) w = Math.Max(w, Math.Max(SpacedWidth(Hint, labelFont), P(14) + SpacedWidth(SessionLabel, labelFont) + P(56)));
        panelW.Target = Math.Ceiling(w) + 2 * padX;
        panelH.Target = padTop + labelH + timerH + (st.LastItemT != null ? sinceH : 0)
            + (live.Count > 0 ? gapH + live.Count * rowH : 0) + (pinned ? 0 : footerH) + padBottom;
    }

    /// <summary>The panel's top-left on screen.</summary>
    (double X, double Y) PanelPos() => dragging || settling ? (posX.Value, posY.Value) : Anchored();

    (double X, double Y) Anchored()
    {
        double w = panelW.Value, h = panelH.Value;
        double x = anchorRight ? area.Right - offX - w : area.Left + offX;
        double y = anchorBottom ? area.Bottom - offY - h : area.Top + offY;
        return Inside(x, y, w, h);
    }

    /// <summary>Kept on the game window.</summary>
    (double X, double Y) Inside(double x, double y, double w, double h) =>
        (Math.Max(area.Left, Math.Min(x, area.Right - w)), Math.Max(area.Top, Math.Min(y, area.Bottom - h)));

    PointF PinCenter(float w) => new(w - padX - P(5), padTop + labelH / 2f);

    /// <summary>The reset-position button, left of the pin; only there while unpinned.</summary>
    PointF ResetCenter(float w) => new(PinCenter(w).X - P(26), padTop + labelH / 2f);

    bool OverPin(int x, int y) => Over(PinCenter((float)panelW.Value), x, y);

    bool OverReset(int x, int y) => !pinned && Over(ResetCenter((float)panelW.Value), x, y);

    bool Over(PointF c, int x, int y)
    {
        var (px, py) = PanelPos();
        double dx = x - (px + c.X), dy = y - (py + c.Y);
        return dx * dx + dy * dy <= P(12) * P(12);
    }

    // ---- the mouse ---------------------------------------------------------------------------------

    void Track(MouseInfo p, double now)
    {
        pointer = p;
        bool live = state.Visible && appear.Target > 0;
        var (x, y) = PanelPos();
        bool overPanel = live && p.Visible && p.X >= x && p.X < x + panelW.Value && p.Y >= y && p.Y < y + panelH.Value;
        bool overPin = overPanel && OverPin(p.X, p.Y);
        hover.Target = overPanel || dragging ? 1 : 0;
        pinHover.Target = overPin || pinPressed ? 1 : 0;
        resetHover.Target = (overPanel && OverReset(p.X, p.Y)) || resetPressed ? 1 : 0;
        if (dragging)
        {
            if (p.Down) DragTo(p.X, p.Y);
            else EndDrag();  // the button came up somewhere we didn't hear about
        }
        if (!pinned && !dragging && live)  // back in the game: pin it where it is
        {
            if (p.Visible) hiddenSince = null;
            else if (now - (hiddenSince ??= now) >= AutoPinS) SetPinned(true);
        }
        wantInteractive = live && (dragging || pinPressed || overPin || (!pinned && overPanel));
    }

    /// <summary>Takes clicks only where they're for the overlay; everywhere else they reach the game.</summary>
    void SetInteractive(bool on)
    {
        if (on == interactive) return;
        interactive = on;
        long ex = Native.GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
        ex = on ? ex & ~WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT;
        Native.SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(ex));
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                m.Result = 3;  // MA_NOACTIVATE
                return;
            case WM_SETCURSOR:
            {
                var p = ReadPointer();
                Cursor.Current = OverPin(p.X, p.Y) || OverReset(p.X, p.Y) ? Cursors.Hand : pinned ? Cursors.Default : Cursors.SizeAll;
                m.Result = 1;
                return;
            }
            case WM_LBUTTONDOWN:
            {
                var p = ReadPointer();
                if (OverPin(p.X, p.Y))
                {
                    pinPressed = true;
                    pinPress.Target = 1;
                    Native.SetCapture(Handle);
                }
                else if (OverReset(p.X, p.Y))
                {
                    resetPressed = true;
                    resetPress.Target = 1;
                    Native.SetCapture(Handle);
                }
                else if (!pinned) BeginDrag(p.X, p.Y);
                Kick();
                return;
            }
            case WM_LBUTTONUP:
            {
                var p = ReadPointer();
                if (pinPressed)
                {
                    pinPressed = false;
                    pinPress.Target = 0;
                    Native.ReleaseCapture();
                    if (OverPin(p.X, p.Y)) SetPinned(!pinned);
                }
                else if (resetPressed)
                {
                    resetPressed = false;
                    resetPress.Target = 0;
                    Native.ReleaseCapture();
                    if (OverReset(p.X, p.Y)) ResetPosition();
                }
                else EndDrag();
                Kick();
                return;
            }
            case WM_CAPTURECHANGED when m.LParam != Handle:
                EndDrag();
                pinPressed = resetPressed = false;
                pinPress.Target = resetPress.Target = 0;
                break;
        }
        base.WndProc(ref m);
    }

    void SetPinned(bool value)
    {
        if (value == pinned) return;
        EndDrag();
        pinned = value;
        pinTilt.Target = value ? 0 : 1;
        edit.Target = value ? 0 : 1;
        hiddenSince = null;
        editAt = Clock.Mono;
        Measure();
        Activity.Write(value
            ? $"Overlay pinned {offX}, {offY} px from the {Corner().Replace('-', ' ')} corner of the game"
            : "Overlay unpinned: drag it where you want it, then click the pin");
        Kick();
    }

    void BeginDrag(int x, int y)
    {
        var (px, py) = PanelPos();
        posX.Snap(px);
        posY.Snap(py);
        (grabX, grabY) = (x - px, y - py);
        dragging = true;
        settling = false;
        lift.Target = 1;
        Native.SetCapture(Handle);
    }

    void DragTo(int x, int y)
    {
        double w = panelW.Target, h = panelH.Target;
        var (tx, ty) = Inside(x - grabX, y - grabY, w, h);
        posX.Target = Snap(tx, area.Left + edgeGap, area.Right - edgeGap - w);  // the usual gap from the edges pulls it in
        posY.Target = Snap(ty, area.Top + edgeGap, area.Bottom - edgeGap - h);
    }

    double Snap(double v, double a, double b) => Math.Abs(v - a) < snapPx ? a : Math.Abs(v - b) < snapPx ? b : v;

    /// <summary>Drops the panel and measures its spot from the nearest corner, so it grows away from that corner.</summary>
    void EndDrag()
    {
        if (!dragging) return;
        dragging = false;
        lift.Target = 0;
        Native.ReleaseCapture();
        double w = panelW.Target, h = panelH.Target, x = posX.Target, y = posY.Target;
        anchorRight = x + w / 2 > (area.Left + area.Right) / 2.0;
        anchorBottom = y + h / 2 > (area.Top + area.Bottom) / 2.0;
        offX = (int)Math.Round(anchorRight ? area.Right - (x + w) : x - area.Left);
        offY = (int)Math.Round(anchorBottom ? area.Bottom - (y + h) : y - area.Top);
        settling = true;
        if ((cfg.OverlayAnchor, cfg.OverlayX, cfg.OverlayY) == (Corner(), offX, offY)) return;  // a click, not a move
        SavePosition(cfg, Corner(), offX, offY);
    }

    /// <summary>Glides the panel back to the default spot and saves it.</summary>
    public void ResetPosition()
    {
        EndDrag();
        var (px, py) = PanelPos();
        posX.Snap(px);
        posY.Snap(py);
        (anchorRight, anchorBottom, offX, offY) = (false, false, DefaultOffset, DefaultOffset);
        settling = true;
        resetAt = Clock.Mono;
        SavePosition(cfg, DefaultAnchor, offX, offY);
        Activity.Write($"Overlay moved back to {DefaultOffset}, {DefaultOffset} px from the top left corner of the game");
        Kick();
    }

    public const string DefaultAnchor = "top-left";
    public const int DefaultOffset = 16;

    public static void SavePosition(Config cfg, string anchor, int x, int y)
    {
        (cfg.OverlayAnchor, cfg.OverlayX, cfg.OverlayY) = (anchor, x, y);
        try
        {
            Toml.SetValue(cfg.Path, "overlay", "anchor", $"\"{anchor}\"");
            Toml.SetValue(cfg.Path, "overlay", "x", x.ToString(CultureInfo.InvariantCulture));
            Toml.SetValue(cfg.Path, "overlay", "y", y.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Activity.Write($"Couldn't save the overlay position to config.toml: {e.Message}");
        }
    }

    string Corner() => (anchorBottom ? "bottom" : "top") + (anchorRight ? "-right" : "-left");

    // ---- drawing ---------------------------------------------------------------------------------

    Bitmap? Icon(string? path)
    {
        if (path == null) return null;
        if (iconCache.TryGetValue(path, out var cached)) return cached;
        Bitmap? icon = null;
        var img = ImageIO.Load(path);
        if (img != null && img.Width > 0 && img.Height > 0)
        {
            using var full = ImageIO.ToBitmap(img, PixelFormat.Format32bppPArgb);
            int box = iconBox - Px(4);
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

    void Present(double now)
    {
        int w = (int)Math.Ceiling(Math.Max(panelW.Value, panelW.Target) * 1.05) + 2 * margin;
        int h = (int)Math.Ceiling(Math.Max(panelH.Value, panelH.Target) * 1.05) + 2 * margin;
        w = (w + 15) & ~15;
        h = (h + 15) & ~15;
        if (w != frameW || h != frameH) Allocate(w, h);

        // Everything but the ticking digits is kept in `layer`, so a plain second only redraws those.
        if (layerDirty)
        {
            using var g = Graphics.FromImage(layer!);
            Setup(g);
            g.Clear(Color.Transparent);
            DrawPanel(g, now);
            layerDirty = false;
        }
        using (var g = Graphics.FromImage(canvas!))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(layer!, 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;
            Setup(g);
            DrawTimers(g, now);
        }

        var (px, py) = PanelPos();
        int wx = (int)Math.Round(px) - margin, wy = (int)Math.Round(py) - margin;
        // Clipped to the game, so the shadow doesn't spill onto the next screen.
        int x0 = Math.Max(wx, area.Left), y0 = Math.Max(wy, area.Top);
        int x1 = Math.Min(wx + w, area.Right), y1 = Math.Min(wy + h, area.Bottom);
        if (x1 <= x0 || y1 <= y0) return;
        var dst = new POINT(x0, y0);
        var size = new SIZE(x1 - x0, y1 - y0);
        var src = new POINT(x0 - wx, y0 - wy);
        var blend = new BLENDFUNCTION { SourceConstantAlpha = (byte)Math.Round(255 * Ease.Clamp01(appear.Value)), AlphaFormat = 1 };
        Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, memDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
    }

    void Allocate(int w, int h)
    {
        Free();
        memDc = Native.CreateCompatibleDC(IntPtr.Zero);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        dib = Native.CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) throw new InvalidOperationException("can't create the overlay bitmap");
        oldBitmap = Native.SelectObject(memDc, dib);
        canvas = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);  // premultiplied BGRA, as UpdateLayeredWindow wants
        layer = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        (frameW, frameH) = (w, h);
        layerDirty = true;
    }

    void Free()
    {
        canvas?.Dispose();
        layer?.Dispose();
        (canvas, layer) = (null, null);
        if (memDc != IntPtr.Zero)
        {
            Native.SelectObject(memDc, oldBitmap);
            Native.DeleteObject(dib);
            Native.DeleteDC(memDc);
            memDc = IntPtr.Zero;
        }
        (frameW, frameH) = (0, 0);
    }

    static void Setup(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
    }

    /// <summary>Moves the origin to the panel's top-left, with the appear and pick-up effects.</summary>
    GraphicsState BeginPanel(Graphics g, float w, float h)
    {
        var saved = g.Save();
        double a = Ease.Clamp01(appear.Value), l = lift.Value;
        float s = (float)(1 + 0.035 * l - 0.04 * (1 - a));
        float dy = (float)(-P(10) * (1 - a) - P(2) * l);
        g.TranslateTransform(margin + w / 2, margin + h / 2 + dy);
        g.ScaleTransform(s, s);
        g.TranslateTransform(-w / 2, -h / 2);
        return saved;
    }

    void DrawPanel(Graphics g, double now)
    {
        float w = (float)panelW.Value, h = (float)panelH.Value;
        var saved = BeginPanel(g, w, h);
        double e = Ease.Clamp01(edit.Value), pulse = 0.5 + 0.5 * Math.Sin((now - editAt) * Math.PI * 1.6);

        double l = lift.Value;  // shadow, deeper while picked up; a gold glow while unpinned
        Glow(g, w, h, P(12 + 10 * l), P(4 + 6 * l), Color.FromArgb((int)Math.Clamp(115 + 60 * l, 0, 255), 0, 0, 0));
        if (e > 0.01) Glow(g, w, h, P(9 + 5 * pulse), 0, Ease.Fade(Theme.Gold, e * (0.28 + 0.22 * pulse)));

        using var shape = Theme.RoundedRect(new RectangleF(0, 0, w, h), radius);
        using (var bg = new LinearGradientBrush(new RectangleF(0, -1, w, h + 2), Ease.Fade(Theme.PanelTop, opacity), Ease.Fade(Theme.Ink, opacity), 90f))
            g.FillPath(bg, shape);
        g.SetClip(shape);
        using (var top = new LinearGradientBrush(new PointF(-1, 0), new PointF(w * 0.8f + 2, 0),Ease.Fade(Theme.Gold, 0.9), Ease.Fade(Theme.Gold, 0)))
            g.FillRectangle(top, 0, 0, w * 0.8f, P(2));

        // header: sparkle (twinkles on a find) and label
        float cy = padTop + labelH / 2f;
        double tw = Ease.Since(now, findAt, 0.75);
        brush.Color = Theme.Gold;
        Theme.Sparkle(g, brush, padX + P(5), cy, P(5.5) * (float)(1 + 0.5 * Ease.Bump(tw)), (float)(90 * Ease.OutCubic(tw)));
        DrawSpaced(g, lastVisible?.Paused == true ? PausedLabel : SessionLabel, labelFont, Theme.Dim, padX + P(14), cy);

        float y = padTop + labelH + timerH;
        double sv = Ease.Clamp01(sinceVis.Value);
        if (sv > 0.01) DrawSpaced(g, SinceLabel, labelFont, Ease.Fade(Theme.Dim, sv), padX, y + sinceH / 2f);
        y += (float)(sinceH * sv);

        double rv = Ease.Clamp01(rowsVis.Value);
        if (rv > 0.01)
        {
            float ly = y + gapH / 2f * (float)rv;
            using var line = new LinearGradientBrush(new PointF(padX - 1, 0), new PointF(w - padX + 2, 0),Ease.Fade(Theme.Gold, 0.45 * rv), Ease.Fade(Theme.Gold, 0));
            g.FillRectangle(line, padX, ly, (w - 2 * padX) * (float)rv, Math.Max(1, P(1)));
        }
        y += (float)(gapH * rv);
        rowsTop = y;
        Row? newest = null;
        foreach (var row in rows)
        {
            if (row.Index == 0 && !row.Leaving) newest = row;
            else DrawRow(g, row, w, now);
        }
        if (newest != null) DrawRow(g, newest, w, now);  // on top while rows trade places

        if (e > 0.01) DrawSpaced(g, Hint, labelFont, Ease.Fade(Theme.GoldLight, 0.85 * e), padX, h - padBottom - footerH / 2f + P(2));
        DrawShimmer(g, w, h, now);
        g.ResetClip();

        double hv = Ease.Clamp01(hover.Value);
        var border = Ease.Mix(Color.FromArgb(34, 255, 255, 255), Ease.Fade(Theme.Gold, 0.7), Math.Max(0.35 * hv, e * (0.55 + 0.45 * pulse)));
        using (var pen = new Pen(border, Math.Max(1, P(1))))
        using (var outline = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, w - 1, h - 1), radius - 0.5f))
            g.DrawPath(pen, outline);
        DrawPin(g, w);
        DrawReset(g, w, now);
        DrawSparkles(g, now, newest);
        g.Restore(saved);
    }

    /// <summary>A soft blurred rounded rectangle around the panel: its shadow, or its glow.</summary>
    void Glow(Graphics g, float w, float h, float blur, float dy, Color color)
    {
        var r = new RectangleF(-blur, -blur + dy, w + 2 * blur, h + 2 * blur);
        using var path = Theme.RoundedRect(r, radius + blur);
        using var b = new PathGradientBrush(path)
        {
            CenterColor = color,
            SurroundColors = [Color.FromArgb(0, color)],
            FocusScales = new PointF(Math.Max(0, 1 - 2 * blur / r.Width), Math.Max(0, 1 - 2 * blur / r.Height)),
            Blend = new Blend { Factors = [0, 0.12f, 0.4f, 0.78f, 1], Positions = [0, 0.25f, 0.5f, 0.75f, 1] },
        };
        g.FillPath(b, path);
    }

    void DrawRow(Graphics g, Row row, float w, double now)
    {
        double a = Ease.Clamp01(row.Alpha.Value);
        if (a < 0.01) return;
        float x = padX + (float)row.Slide.Value, y = rowsTop + (float)row.Y.Value;
        double flash = 1 - Ease.Since(now, row.FlashAt, FindS);
        flash *= flash;
        if (flash > 0.01)
        {
            var band = new RectangleF(P(5), y + P(1), w - P(10), rowH - P(2));
            using var path = Theme.RoundedRect(band, P(8));
            using var fill = new LinearGradientBrush(RectangleF.Inflate(band, 2, 0), Ease.Fade(Theme.Gold, 0.34 * flash * a), Ease.Fade(Theme.Gold, 0), 0f);
            g.FillPath(fill, path);
        }

        var slot = new RectangleF(x, y + (rowH - iconBox) / 2f, iconBox, iconBox);
        using (var path = Theme.RoundedRect(slot, P(7)))
        {
            brush.Color = Color.FromArgb((int)(20 * a), 255, 255, 255);
            g.FillPath(brush, path);
            var rim = row.Index == 0 && !row.Leaving ? Ease.Fade(Theme.Gold, (0.55 + 0.45 * flash) * a) : Color.FromArgb((int)(28 * a), 255, 255, 255);
            using var pen = new Pen(rim, Math.Max(1, P(1.2)));
            g.DrawPath(pen, path);
        }
        float pop = (float)row.Pop.Value;
        if (row.Icon is Bitmap icon)
        {
            float s = 1 + (pop - 1) * 0.6f, iw = icon.Width * s, ih = icon.Height * s;
            float ix = slot.X + (iconBox - iw) / 2, iy = slot.Y + (iconBox - ih) / 2;
            if (a >= 0.995 && s == 1) g.DrawImage(icon, (int)Math.Round(ix), (int)Math.Round(iy), icon.Width, icon.Height);
            else
            {
                using var attrs = new ImageAttributes();
                attrs.SetColorMatrix(new ColorMatrix { Matrix33 = (float)a });
                g.DrawImage(icon, [new PointF(ix, iy), new PointF(ix + iw, iy), new PointF(ix, iy + ih)],
                    new RectangleF(0, 0, icon.Width, icon.Height), GraphicsUnit.Pixel, attrs);
            }
        }

        float cx = x + iconBox + P(8), cy = y + rowH / 2f;
        var saved = g.Save();
        g.TranslateTransform(cx, cy);
        g.ScaleTransform(pop, pop);  // the count pops when it goes up
        brush.Color = Ease.Fade(Ease.Mix(Theme.Text, Theme.GoldLight, flash), a);
        g.DrawString(row.Count, countFont, brush, 0, 0, format);
        g.Restore(saved);
        brush.Color = Ease.Fade(Theme.Dim, a);
        g.DrawString(row.Text, rowFont, brush, cx + (float)countCol.Value + P(10), cy, format);
    }

    /// <summary>A band of light that sweeps across the panel after a find.</summary>
    void DrawShimmer(Graphics g, float w, float h, double now)
    {
        double p = Ease.Since(now, findAt, 0.95);
        if (p >= 1) return;
        var saved = g.Save();
        g.TranslateTransform(w / 2, h / 2);
        g.RotateTransform(18);
        float span = w + h, bw = Math.Max(P(50), w * 0.4f);
        float cx = (float)Ease.Lerp(-span / 2 - bw, span / 2 + bw, Ease.InOutCubic(p));
        var band = new RectangleF(cx - bw / 2, -span, bw, 2 * span);
        var tint = Color.FromArgb(255, 246, 214);
        using var b = new LinearGradientBrush(new RectangleF(band.X - 1, band.Y, band.Width + 2, band.Height), Color.Transparent, Color.Transparent, 0f)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = [Color.FromArgb(0, tint), Ease.Fade(tint, 0.24 * (1 - p * 0.5)), Color.FromArgb(0, tint)],
                Positions = [0, 0.5f, 1],
            },
        };
        g.FillRectangle(b, band);
        g.Restore(saved);
    }

    void DrawPin(Graphics g, float w)
    {
        double vis = Math.Max(Ease.Clamp01(hover.Value), Ease.Clamp01(edit.Value));
        if (vis < 0.01) return;
        var c = PinCenter(w);
        double ph = Ease.Clamp01(pinHover.Value), press = 1 - 0.14 * pinPress.Value, tilt = pinTilt.Value;
        float r = P(11) * (float)((0.7 + 0.3 * ph) * press);
        brush.Color = Ease.Fade(Theme.Gold, (0.08 + 0.24 * ph) * vis);
        g.FillEllipse(brush, c.X - r, c.Y - r, 2 * r, 2 * r);

        var saved = g.Save();
        float u = P(15) / 16f * (float)((0.75 + 0.25 * vis) * press);
        g.TranslateTransform(c.X, c.Y - P(1.5) * (float)Ease.Clamp01(tilt));  // lifts as it tilts
        g.RotateTransform((float)(40 * tilt));
        g.ScaleTransform(u, u);
        g.TranslateTransform(-8, -8.6f);
        var color = Ease.Fade(Ease.Mix(Theme.Gold, Theme.Text, Ease.Clamp01(tilt)), vis);
        brush.Color = color;
        g.FillPath(brush, pinHead);
        using (var needle = new Pen(color, 1.5f) { EndCap = LineCap.Triangle })
            g.DrawLine(needle, 8, 9.2f, 8, 15f);
        g.Restore(saved);
    }

    /// <summary>A circular arrow; spins once when clicked.</summary>
    void DrawReset(Graphics g, float w, double now)
    {
        double vis = Ease.Clamp01(edit.Value);
        if (vis < 0.01) return;
        var c = ResetCenter(w);
        double rh = Ease.Clamp01(resetHover.Value), press = 1 - 0.14 * resetPress.Value;
        float r = P(11) * (float)((0.7 + 0.3 * rh) * press);
        brush.Color = Ease.Fade(Theme.Gold, (0.08 + 0.24 * rh) * vis);
        g.FillEllipse(brush, c.X - r, c.Y - r, 2 * r, 2 * r);

        var saved = g.Save();
        float u = P(15) / 16f * (float)((0.75 + 0.25 * vis) * press);
        g.TranslateTransform(c.X, c.Y);
        g.RotateTransform((float)(360 * Ease.OutCubic(Ease.Since(now, resetAt, 0.7))));
        g.ScaleTransform(u, u);
        var color = Ease.Fade(Ease.Mix(Theme.Gold, Theme.Text, rh), vis);
        using (var arc = new Pen(color, 1.6f) { StartCap = LineCap.Round })
            g.DrawArc(arc, -5.5f, -5.5f, 11, 11, -60, 290);
        brush.Color = color;  // arrowhead where the arc ends, pointing on round
        double a = (-60 + 290) * Math.PI / 180;
        float tx = (float)(5.5 * Math.Cos(a)), ty = (float)(5.5 * Math.Sin(a));
        float fx = (float)-Math.Sin(a), fy = (float)Math.Cos(a);  // clockwise tangent
        float nx = (float)Math.Cos(a), ny = (float)Math.Sin(a);   // outward
        g.FillPolygon(brush, [
            new PointF(tx + 3.2f * fx, ty + 3.2f * fy),
            new PointF(tx + 2.6f * nx, ty + 2.6f * ny),
            new PointF(tx - 2.6f * nx, ty - 2.6f * ny),
        ]);
        g.Restore(saved);
    }

    /// <summary>Little stars that burst out of the newest item's icon.</summary>
    void DrawSparkles(Graphics g, double now, Row? row)
    {
        double p = Ease.Since(now, findAt, 0.9);
        if (p >= 1 || row == null) return;
        float cx = padX + (float)row.Slide.Value + iconBox / 2f, cy = rowsTop + (float)row.Y.Value + rowH / 2f;
        var rnd = new Random(seed);  // the same burst every frame
        for (int i = 0; i < 8; i++)
        {
            double angle = (i + rnd.NextDouble() * 0.7) / 8 * Math.PI * 2, delay = rnd.NextDouble() * 0.2;
            double size = 3 + rnd.NextDouble() * 3.5, reach = 20 + rnd.NextDouble() * 14;
            double t = Ease.Clamp01((p - delay) / (1 - delay));
            float dist = P(10 + reach * Ease.OutCubic(t)), r = P(size) * (float)Ease.Bump(Math.Min(1, t * 1.2));
            brush.Color = Ease.Fade(i % 3 == 0 ? Color.White : i % 3 == 1 ? Theme.GoldLight : Theme.Gold, 1 - t * t);
            Theme.Sparkle(g, brush, cx + (float)(Math.Cos(angle) * dist), cy + (float)(Math.Sin(angle) * dist), r, (float)(t * 140));
        }
    }

    void DrawTimers(Graphics g, double now)
    {
        float w = (float)panelW.Value, h = (float)panelH.Value;
        var saved = BeginPanel(g, w, h);
        using (var shape = Theme.RoundedRect(new RectangleF(0, 0, w, h), radius)) g.SetClip(shape);
        float y = padTop + labelH;
        DrawRoll(g, mainRoll, timerFont, padX - P(1), y, timerH, Theme.Text, Ease.Fade(Theme.Dim, 0.75), 1, now);
        y += timerH;
        double sv = Ease.Clamp01(sinceVis.Value);
        if (sv > 0.01 && sinceRoll.Text.Length > 0)
        {
            double flash = 1 - Ease.Since(now, findAt, FindS);
            var color = Ease.Mix(Theme.Gold, Color.White, flash * flash);
            float x = padX + SpacedWidth(SinceLabel, labelFont) + P(8);
            DrawRoll(g, sinceRoll, sinceFont, x, y, sinceH, color, Ease.Fade(color, 0.6), sv, now);
        }
        g.Restore(saved);
    }

    void DrawRoll(Graphics g, Roll roll, Font font, float x, float top, float lineH, Color digit, Color colon, double alpha, double now)
    {
        double p = roll.Active(now) ? Ease.OutCubic((now - roll.At) / RollS) : 1;
        bool rolling = p < 1 && roll.Prev.Length == roll.Text.Length;
        float cy = top + lineH / 2f, travel = lineH * 0.6f * roll.Dir;
        var saved = g.Save();
        if (rolling) g.SetClip(new RectangleF(x - P(2), top, CharsWidth(roll.Text, font) + P(4), lineH), CombineMode.Intersect);
        for (int i = 0; i < roll.Text.Length; i++)
        {
            char c = roll.Text[i];
            var color = c == ':' ? colon : digit;
            if (rolling && roll.Prev[i] != c)  // the old digit leaves, the new one arrives
            {
                brush.Color = Ease.Fade(color, alpha * (1 - p));
                g.DrawString(roll.Prev[i].ToString(), font, brush, x, cy - travel * (float)p, format);
                brush.Color = Ease.Fade(color, alpha * p);
                g.DrawString(c.ToString(), font, brush, x, cy + travel * (float)(1 - p), format);
            }
            else
            {
                brush.Color = Ease.Fade(color, alpha);
                g.DrawString(c.ToString(), font, brush, x, cy, format);
            }
            x += GlyphWidth(c, font);
        }
        g.Restore(saved);
    }

    void DrawSpaced(Graphics g, string text, Font font, Color color, float x, float cy)
    {
        brush.Color = color;
        foreach (char c in text)
        {
            if (c != ' ') g.DrawString(c.ToString(), font, brush, x, cy, format);
            x += GlyphWidth(c, font) + Tracking;
        }
    }

    public void Dispose()
    {
        timer.Dispose();
        if (dragging || pinPressed) Native.ReleaseCapture();
        DestroyHandle();
        Free();
        foreach (var icon in iconCache.Values) icon?.Dispose();
        timerFont.Dispose(); sinceFont.Dispose(); labelFont.Dispose(); countFont.Dispose(); rowFont.Dispose();
        format.Dispose();
        measure.Dispose();
        probe.Dispose();
        pinHead.Dispose();
        brush.Dispose();
    }
}
