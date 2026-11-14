using System.Collections.Concurrent;
using Windows.Graphics.Capture;

namespace FortItemTracker;

sealed record OverlayRow(string Name, string Time, string? Icon, int Count);

/// <summary>What the overlay draws. Replaced whole, so the UI thread always sees a consistent state.</summary>
sealed record OverlayState(bool Visible, int X, int Y, double TimerAt, double MonoRef, double? LastItemT, OverlayRow[] Rows)
{
    public static readonly OverlayState Hidden = new(false, 0, 0, 0, 0, null, []);

    public double Timer(double now) => TimerAt + now - MonoRef;

    public bool Equals(OverlayState? o) =>
        o is not null && Visible == o.Visible && X == o.X && Y == o.Y && TimerAt == o.TimerAt && MonoRef == o.MonoRef
        && LastItemT == o.LastItemT && Rows.SequenceEqual(o.Rows);

    public override int GetHashCode() => HashCode.Combine(Visible, X, Y, TimerAt, MonoRef, LastItemT, Rows.Length);
}

sealed record TrackerStatus(string Text, string? SessionId, bool HoldNew, string[] Recent)
{
    public static readonly TrackerStatus Starting = new("Starting...", null, false, []);
}

/// <summary>
/// The tracking loop on its own thread: watches for Fortnite, captures the banner area, reads banners and
/// keeps the session. The tray talks to it through Request* methods, which run on this thread.
/// </summary>
sealed class Tracker : IDisposable
{
    const double AutosaveS = 15;
    const double ProcessCheckS = 2;   // while the game runs (checking a handle, nearly free)
    const double WaitingCheckS = 5;   // while waiting for it (walks the process list)
    const double WindowCaptureGraceS = 15;  // window capture that shows nothing for this long falls back to the screen

    readonly Config cfg;
    readonly string root;
    readonly Options opts;
    readonly ConcurrentQueue<Action> commands = new();
    readonly AutoResetEvent wake = new(false);
    readonly ManualResetEventSlim ready = new(false);
    Thread? thread;
    Exception? initError;
    volatile bool stopping;

    // Owned by the tracker thread.
    LootLog log = null!;
    WindowsOcr ocr = null!;
    IconLibrary icons = null!;
    BannerReader reader = null!;
    Namer namer = null!;
    BannerTracker banners = null!;
    GameWatcher watcher = null!;
    readonly Gate gate = new();
    Session? session;
    IntPtr hwnd;
    bool gameRunning;
    double? missingSince;
    bool holdNew;            // after "End session": don't start a new one until the game restarts
    string? resume;          // session id to resume when the next session starts
    double nextAutosave;
    WgcCapture? capture;
    IntPtr windowCaptureFailed;  // window that window capture didn't work for (auto mode then captures its monitor)
    double? blankSince;
    double? lastReadAt;      // when the last captured frame had a banner read on it
    int captureFailures;
    string lastError = "";
    (IntPtr Handle, string Device, RECT Rect)? display;  // --always
    string? debugDir;
    int debugSaved;
    double lastDebug;

    public volatile TrackerStatus Status = TrackerStatus.Starting;
    public volatile OverlayState Overlay = OverlayState.Hidden;

    public string LogFolder { get; }
    public LootLog Log => log;

    public Tracker(Config cfg, string root, Options opts)
    {
        this.cfg = cfg;
        this.root = root;
        this.opts = opts;
        LogFolder = Config.Resolve(root, cfg.Folder);
    }

    /// <summary>Starts the thread; throws what went wrong if it can't start (no OCR language, etc.).</summary>
    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "tracker" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        ready.Wait();
        if (initError != null) throw initError;
    }

    public void Dispose()
    {
        stopping = true;
        wake.Set();
        thread?.Join(TimeSpan.FromSeconds(10));
    }

    void Post(Action action)
    {
        commands.Enqueue(action);
        wake.Set();
    }

    public void RequestSave() => Post(() => SaveSession(announce: true));
    public void RequestReset() => Post(Reset);
    public void RequestEnd() => Post(() =>
    {
        if (session == null) return;
        EndSession();
        holdNew = true;
    });
    public void RequestResume(string id) => Post(() => ResumeNow(id));

    // ---- setup ----------------------------------------------------------------------------

    void Init()
    {
        Native.RoInitialize(1);  // multithreaded apartment
        log = new LootLog(LogFolder);
        ocr = new WindowsOcr(cfg.OcrLanguage);
        icons = MakeIcons(cfg, root, Activity.Write);
        reader = new BannerReader(ocr, cfg.Anchor, icons);
        namer = new Namer(Path.Combine(root, "items.txt"), cfg.SnapScore);
        namer.Reload();
        banners = new BannerTracker(namer, cfg.BannerGapS, icons, cfg.LearnIcons, cfg.GenericNames);
        watcher = new GameWatcher(cfg.ProcessNames);
        if (!GraphicsCaptureSession.IsSupported())
            throw new InvalidOperationException("Windows Graphics Capture isn't available on this PC (it needs Windows 10 version 2004 or later).");
        if (opts.Always) display = PickDisplay(opts.Display);
        if (opts.Debug)
        {
            debugDir = Path.Combine(LogFolder, "debug");
            Directory.CreateDirectory(debugDir);
        }
        if (opts.Resume != null) resume = FindResumable(opts.Resume);  // fail now, not when Fortnite starts
    }

    public static IconLibrary MakeIcons(Config cfg, string root, Action<string> print)
    {
        var lib = new IconLibrary(Config.Resolve(root, cfg.IconFolder));
        if (lib.Count == 0) print($"icons: no reference images in {lib.Folder}; rewards will be named from the banner text");
        return lib;
    }

    static (IntPtr, string, RECT) PickDisplay(int number)
    {
        var monitors = Win.Monitors();
        if (number > 0)
        {
            foreach (var m in monitors)
                if (m.Device.ToUpperInvariant().EndsWith($"DISPLAY{number}")) return (m.Handle, m.Device, m.Rect);
            throw new InvalidOperationException($"No display {number}. Found: {string.Join(", ", monitors.Select(m => m.Device))}");
        }
        var p = monitors.First(m => m.Primary);
        return (p.Handle, p.Device, p.Rect);
    }

    string FindResumable(string which)
    {
        var saved = log.SavedSessions();
        if (which != "latest") saved = saved.Where(s => s.Id == which).ToList();
        if (saved.Count == 0) throw new InvalidOperationException($"No saved session '{which}'. Saved sessions are in {log.SavedDir}.");
        return saved[0].Id;
    }

    // ---- main loop --------------------------------------------------------------------------

    void Run()
    {
        try
        {
            Init();
        }
        catch (Exception e)
        {
            initError = e;
            ready.Set();
            return;
        }
        ready.Set();
        try
        {
            Loop();
        }
        catch (Exception e)
        {
            Activity.Write($"Tracking stopped by an error: {e}");
            Status = new TrackerStatus("Stopped by an error (see the activity log)", null, false, []);
        }
        finally
        {
            try { EndSession(); } catch (Exception e) { Activity.Write($"Couldn't save the session: {e.Message}"); }
            DropCapture();
            watcher?.Dispose();
        }
    }

    void Loop()
    {
        double interval = 1.0 / Math.Max(0.5, cfg.PollHz);
        string where = opts.Always ? $"display {display!.Value.Device}" : "the Fortnite window";
        string how = opts.Always || cfg.Source == "monitor" ? "screen capture" : cfg.Source == "window" ? "window capture" : "window capture, screen as fallback";
        Activity.Write($"Capture: Windows Graphics Capture ({how}), {cfg.PollHz:g} checks/s on {where}");
        Activity.Write($"Logging to {LogFolder}");
        if (resume != null) Activity.Write($"Will resume session {resume} when tracking starts.");
        double nextProcessCheck = 0, nextStatus = 0;
        while (!stopping)
        {
            double mono = Clock.Mono;
            while (commands.TryDequeue(out var cmd)) cmd();
            if (session != null && mono >= nextAutosave) SaveSession();
            if (mono >= nextProcessCheck)
            {
                CheckProcess(mono);
                nextProcessCheck = mono + (gameRunning || session != null ? ProcessCheckS : WaitingCheckS);
            }
            string state = "";
            if (session != null)
            {
                BannerRead? read = null;
                bool? visible = null;  // unknown when nothing new was captured
                string? paused = CaptureRoi(out var small);
                if (paused != null)
                {
                    state = paused;
                    lastReadAt = null;
                }
                else if (small != null)
                {
                    double score = gate.Score(small, cfg.GateHue[0], cfg.GateHue[1]);
                    visible = score >= cfg.GateThreshold;
                    if (visible == true && ReadFullRoi() is Image32 roi)  // full size only when it may be a banner
                    {
                        read = reader.Read(roi, out var lines);
                        if (debugDir != null) SaveDebug(roi, score, lines);
                    }
                    lastReadAt = read != null ? mono : null;
                }
                else if (lastReadAt is double at && mono - at < 10)
                {
                    // Capture only delivers frames when the window changes: the banner is still there.
                    banners.Touch(mono);
                }
                foreach (var ev in banners.Update(Clock.Wall, mono, read, visible)) LogEvent(ev);
            }
            if (mono >= nextStatus)
            {
                nextStatus = mono + 1;
                PublishStatus(state);
            }
            double wait = session != null && state.Length == 0 ? interval : 0.5;
            double left = wait - (Clock.Mono - mono);
            if (left > 0) wake.WaitOne(TimeSpan.FromSeconds(left));
        }
    }

    // ---- capture ------------------------------------------------------------------------------

    static RECT RoiRect(RECT area, double[] roi) => new(
        area.Left + (int)(area.Width * roi[0]), area.Top + (int)(area.Height * roi[1]),
        area.Left + (int)(area.Width * roi[2]), area.Top + (int)(area.Height * roi[3]));

    /// <summary>
    /// Captures the banner search area. Returns why capture is paused, or null with <paramref name="small"/>
    /// set to the area shrunk for the pre-check (null when no new frame came in).
    /// </summary>
    string? CaptureRoi(out Image32? small)
    {
        small = null;
        RECT region = default;
        bool window = false;
        if (opts.Always)
        {
            var (hmon, _, mr) = display!.Value;
            region = RoiRect(new RECT(0, 0, mr.Width, mr.Height), cfg.Roi);
            if (!EnsureCapture(hmon, false)) return "capture unavailable";
        }
        else
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return Paused("no Fortnite window");
            if (Native.IsIconic(hwnd)) return Paused("paused: Fortnite minimized");
            if (cfg.OnlyWhenFocused && Native.GetForegroundWindow() != hwnd) return Paused("paused: Fortnite not focused");
            if (Win.ClientRect(hwnd) is not RECT client) return Paused("no Fortnite window");
            var r = RoiRect(client, cfg.Roi);
            window = cfg.Source == "window" || (cfg.Source == "auto" && windowCaptureFailed != hwnd);
            if (window)
            {
                var frame = Win.FrameBounds(hwnd) ?? client;
                region = r.Offset(-frame.Left, -frame.Top);
                if (!EnsureCapture(hwnd, true))
                {
                    if (cfg.Source == "window") return "window capture unavailable";
                    window = false;  // EnsureCapture switched auto mode to the monitor
                }
            }
            if (!window)
            {
                var (hmon, _, mr) = Win.MonitorForWindow(hwnd);
                r = r.Intersect(mr);
                region = r.Offset(-mr.Left, -mr.Top);
                if (!EnsureCapture(hmon, false)) return "capture unavailable";
            }
            if (region.Width < 50 || region.Height < 50) return Paused("paused: Fortnite window off-screen");
        }

        try
        {
            small = capture!.CaptureRegion(region) ? capture.ReadShrunk() : null;
            captureFailures = 0;
        }
        catch (Exception e)
        {
            CaptureFailed(e, window);
            return null;
        }

        if (window && cfg.Source == "auto")
        {
            bool blank = small?.IsBlack() ?? Clock.Mono - capture!.LastFrameAt > 5;
            if (small != null && !blank) blankSince = null;
            else if (blank && (blankSince ??= Clock.Mono) is double since && Clock.Mono - since > WindowCaptureGraceS)
                FallBack("the game window shows up black or not at all");
        }
        return null;
    }

    /// <summary>The search area of the frame CaptureRoi just took, at full size.</summary>
    Image32? ReadFullRoi()
    {
        try
        {
            return capture?.ReadFull();
        }
        catch (Exception e)
        {
            CaptureFailed(e, capture?.IsWindow ?? false);
            return null;
        }
    }

    void CaptureFailed(Exception e, bool window)
    {
        captureFailures++;
        DropCapture();
        if (window && cfg.Source == "auto" && captureFailures >= 3)
            FallBack($"it keeps failing: {e.Message}");
        else if (e.Message != lastError)
            Activity.Write($"capture error: {e.Message}");
        lastError = e.Message;
    }

    string Paused(string why)
    {
        blankSince = null;
        return why;
    }

    void FallBack(string why)
    {
        windowCaptureFailed = hwnd;
        blankSince = null;
        DropCapture();
        Activity.Write($"Window capture isn't working ({why}). Capturing the screen where the game is instead, " +
                       "so banners are missed while another window covers the game.");
    }

    bool EnsureCapture(IntPtr key, bool window)
    {
        if (capture != null && (capture.Key != key || capture.IsWindow != window || capture.Closed)) DropCapture();
        if (capture != null) return true;
        try
        {
            var interval = TimeSpan.FromSeconds(0.5 / Math.Max(0.5, cfg.PollHz));
            capture = window ? WgcCapture.ForWindow(key, interval, Gate.Height) : WgcCapture.ForMonitor(key, interval, Gate.Height);
            blankSince = null;
            return true;
        }
        catch (Exception e)
        {
            if (window && cfg.Source == "auto")
                FallBack($"can't start it: {e.Message}");
            else if (e.Message != lastError)
                Activity.Write($"capture error: {e.Message}");
            lastError = e.Message;
            return false;
        }
    }

    void DropCapture()
    {
        capture?.Dispose();
        capture = null;
        lastReadAt = null;
    }

    void SaveDebug(Image32 roi, double score, List<OcrLine> lines)
    {
        double now = Clock.Mono;
        if (debugSaved >= 1000 || now - lastDebug < 0.5) return;
        lastDebug = now;
        debugSaved++;
        string stamp = DateTime.Now.ToString("HHmmss_fff");
        var copy = new Image32(roi.Width, roi.Height, (byte[])roi.Px.Clone());
        copy.MakeOpaque();
        ImageIO.SavePng(copy, Path.Combine(debugDir!, $"{stamp}_{score:0.0000}.png"));
        File.AppendAllText(Path.Combine(debugDir!, "ocr.txt"),
            $"{stamp} gate={score:0.0000} lines=[{string.Join(", ", lines.Select(l => $"'{l.Text}'"))}]\n");
    }

    // ---- sessions -------------------------------------------------------------------------------

    void CheckProcess(double mono)
    {
        if (opts.Always)
        {
            gameRunning = true;
            if (session == null && !holdNew) StartSession();
            PushOverlay();
            return;
        }
        var pids = watcher.Poll();
        gameRunning = pids.Count > 0;
        if (gameRunning)
        {
            missingSince = null;
            var found = Win.FindMainWindow(pids);
            if (found != hwnd)
            {
                hwnd = found;
                DropCapture();
            }
            if (session == null && !holdNew) StartSession();
        }
        else if (session == null)
        {
            holdNew = false;  // game closed: the next launch starts a new session
            DropCapture();
        }
        else
        {
            missingSince ??= mono;
            if (mono - missingSince >= cfg.EndGraceS) EndSession();
        }
        PushOverlay();  // follows the game window if it moves, hides while minimised
    }

    void StartSession()
    {
        namer.Reload();
        icons.Reload();
        holdNew = false;
        if (resume != null)
        {
            session = log.ResumeSession(resume);
            resume = null;  // later sessions in this run are new ones
            Activity.Write($"Session {session.Id} resumed at {Fmt.Duration(session.Prior)} with {session.Items.Count} item(s)");
        }
        else
        {
            session = log.StartSession();
            Activity.Write($"Session {session.Id} started {Fmt.ClockTime(Clock.Wall)}");
        }
        nextAutosave = Clock.Mono + AutosaveS;
        PushOverlay();
        PublishStatus("");
    }

    void SaveSession(bool announce = false)
    {
        if (session == null)
        {
            if (announce) Activity.Write("No session running, nothing to save.");
            return;
        }
        log.Save(session);
        nextAutosave = Clock.Mono + AutosaveS;
        if (announce)
            Activity.Write($"Session {session.Id} saved at {Fmt.Duration(session.Elapsed())}. Continue it later from Resume a session in the tray menu.");
    }

    void EndSession()
    {
        if (session == null) return;
        foreach (var ev in banners.Flush()) LogEvent(ev);
        string summary = log.EndSession(session);
        var s = session;
        Activity.Write($"Session {s.Id} ended at {Fmt.Duration(s.Elapsed())}: {s.Items.Count} item(s). {summary}");
        session = null;
        hwnd = IntPtr.Zero;
        PushOverlay();
        PublishStatus("");
    }

    /// <summary>Timer and counts back to zero: the current session is saved and ended, a new one starts.</summary>
    void Reset()
    {
        if (session != null) Activity.Write("Reset: saving the current session and starting a new one.");
        EndSession();
        StartSession();
    }

    void ResumeNow(string id)
    {
        if (session?.Id == id) return;
        resume = id;
        if (session != null || gameRunning)
        {
            EndSession();
            StartSession();
        }
        else
        {
            holdNew = false;
            Activity.Write($"Session {id} will be resumed when Fortnite starts.");
            PublishStatus("");
        }
    }

    void LogEvent(LootEvent ev)
    {
        log.Add(session!, ev);
        string clock = Fmt.Time(ev.WallTime).PadLeft(11);
        Activity.Write($"  {clock}  +{Fmt.Duration(session!.At(ev.MonoTime))}  {ev.Describe()}");
        PushOverlay();
        PublishStatus("");
    }

    // ---- status and overlay -------------------------------------------------------------------

    void PublishStatus(string state)
    {
        string text;
        if (session == null && holdNew) text = "Session ended. A new one starts with the next game (or Reset)";
        else if (session == null && resume != null) text = $"Waiting for Fortnite (will resume {resume})";
        else if (session == null) text = "Waiting for Fortnite to start...";
        else
        {
            var s = session;
            text = $"Session {Fmt.Duration(s.Elapsed())} | {s.Items.Count} found";
            if (s.Items.Count > 0)
            {
                var last = s.Items[^1];
                text += $" | last: {last.Item} ({Fmt.Duration(s.Elapsed() - last.T)} ago)";
            }
            if (state.Length > 0) text += $" | {state}";
        }
        var recent = session?.Items.TakeLast(5).Reverse().Select(i => $"{TimeOf(i.FoundAt)}  {i.Item}").ToArray() ?? [];
        Status = new TrackerStatus(text, session?.Id, holdNew, recent);
    }

    static string TimeOf(string foundAt)
    {
        int sp = foundAt.IndexOf(' ');
        return sp >= 0 ? foundAt[(sp + 1)..] : foundAt;
    }

    /// <summary>(name, count, last found_at) per item, the most recently found item first.</summary>
    static List<(string Name, int Count, string FoundAt)> Tally(List<FoundItem> items, int limit)
    {
        var order = new List<string>();
        var tally = new Dictionary<string, (int Count, string FoundAt)>();
        foreach (var i in items)
        {
            tally[i.Item] = (tally.GetValueOrDefault(i.Item).Count + 1, i.FoundAt);
            order.Remove(i.Item);
            order.Add(i.Item);
        }
        order.Reverse();
        return order.Take(Math.Max(0, limit)).Select(n => (n, tally[n].Count, tally[n].FoundAt)).ToList();
    }

    void PushOverlay()
    {
        RECT? area = opts.Always ? display!.Value.Rect
            : hwnd != IntPtr.Zero && Native.IsWindow(hwnd) && !Native.IsIconic(hwnd) ? Win.ClientRect(hwnd)
            : null;
        if (session is not Session s || area is not RECT a)
        {
            Overlay = OverlayState.Hidden;
            return;
        }
        var rows = Tally(s.Items, cfg.OverlayItems)
            .Select(t => new OverlayRow(t.Name, TimeOf(t.FoundAt), icons.PathFor(t.Name), t.Count)).ToArray();
        var next = new OverlayState(true, a.Left + cfg.OverlayX, a.Top + cfg.OverlayY, s.Prior, s.MonoStart,
            s.Items.Count > 0 ? s.Items[^1].T : null, rows);
        if (!next.Equals(Overlay)) Overlay = next;
    }
}
