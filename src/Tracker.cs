using System.Collections.Concurrent;
using Windows.Graphics.Capture;

namespace FortItemTracker;

sealed record OverlayRow(string Name, string Time, string? Icon, int Count);

/// <summary>
/// What the overlay draws, and the game window's client area (screen pixels) it sits on. Replaced
/// whole, so the UI thread always sees a consistent state.
/// </summary>
sealed record OverlayState(bool Visible, RECT Area, double TimerAt, double MonoRef, double? LastItemT, OverlayRow[] Rows, bool Paused = false)
{
    public static readonly OverlayState Hidden = new(false, default, 0, 0, null, []);

    public double Timer(double now) => Paused ? TimerAt : TimerAt + now - MonoRef;

    public bool Equals(OverlayState? o) =>
        o is not null && Visible == o.Visible && Area.Equals(o.Area) && TimerAt == o.TimerAt && MonoRef == o.MonoRef
        && LastItemT == o.LastItemT && Paused == o.Paused && Rows.SequenceEqual(o.Rows);

    public override int GetHashCode() => HashCode.Combine(Visible, Area.Left, Area.Top, TimerAt, MonoRef, LastItemT, Rows.Length);
}

sealed record RecentFind(string Time, string Item, string? Icon);

/// <summary>
/// For the tray menu and the activity window. The session timer reads TimerAt + Clock.Mono - MonoRef, or
/// TimerAt while paused.
/// </summary>
sealed record TrackerStatus(string Text, string? SessionId, bool HoldNew, RecentFind[] Recent, int Found = 0, double TimerAt = 0,
    double MonoRef = 0, bool Paused = false)
{
    public static readonly TrackerStatus Starting = new("Starting...", null, false, []);

    public double Timer(double now) => SessionId == null ? 0 : Paused ? TimerAt : TimerAt + now - MonoRef;
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
    const double WindowCaptureGraceS = 15;  // window capture that shows nothing for this long is checked against the screen
    const double RetryWindowFirstS = 60;    // after falling back to the screen, window capture is tried again this soon,
    const double RetryWindowMaxS = 960;     // ...then twice as long after each try, up to this

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
    double retryWindowAt, retryWindowEvery = RetryWindowFirstS;
    IntPtr reportedFor;      // game window the setup line was written for
    IntPtr warnedBlank;      // game window the "window capture shows black" warning was written for (source = "window")
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
    public void RequestTogglePause() => Post(TogglePause);

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
        if (lib.Unreadable.Count > 0)
        {
            bool webp = lib.Unreadable.Any(f => f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));
            print($"Warning: Windows can't read {lib.Unreadable.Count} icon image(s) in {lib.Folder}: " +
                  string.Join(", ", lib.Unreadable) + ". Those items can't be named by their icon. " +
                  (webp ? "Save them as .png, or install \"WebP Image Extensions\" from the Microsoft Store and restart."
                        : "Save them again as .png."));
        }
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
            if (session is { Paused: false } && mono >= nextAutosave) SaveSession();
            if (mono >= nextProcessCheck)
            {
                CheckProcess(mono);
                nextProcessCheck = mono + (gameRunning || session != null ? ProcessCheckS : WaitingCheckS);
            }
            string state = "";
            if (session is { Paused: false })
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
            double wait = session is { Paused: false } && state.Length == 0 ? interval : 0.5;
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
        RECT region = default, onScreen = default;
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
            var r = onScreen = RoiRect(client, cfg.Roi);
            if (cfg.Source == "auto" && windowCaptureFailed == hwnd && Clock.Mono >= retryWindowAt && !banners.Busy) RetryWindow(r);
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

        bool black = small?.IsBlack() ?? false;
        if (small != null && !black && !opts.Always && reportedFor != hwnd) ReportSetup(window);
        if (window)
        {
            bool blank = small != null ? black : Clock.Mono - capture!.LastFrameAt > 5;
            if (small != null && !blank) blankSince = null;
            else if (blank && (blankSince ??= Clock.Mono) is double since && Clock.Mono - since > WindowCaptureGraceS)
                CheckBlankWindow(onScreen);
        }
        return null;
    }

    /// <summary>
    /// Window capture has shown black (or nothing) for a while. That is only a failure if the screen shows
    /// the game there: a black screen (loading, a fade) is just black, and window capture stays.
    /// </summary>
    void CheckBlankWindow(RECT onScreen)
    {
        blankSince = null;  // look again after another grace period
        if (Native.GetForegroundWindow() != hwnd) return;  // the screen may show another window there
        var (hmon, _, mr) = Win.MonitorForWindow(hwnd);
        var region = onScreen.Intersect(mr).Offset(-mr.Left, -mr.Top);
        if (Probe(() => WgcCapture.ForMonitor(hmon, ProbeInterval, Gate.Height), region) != true) return;
        if (cfg.Source == "auto")
        {
            FallBack("shows black while the game is on screen");
        }
        else if (warnedBlank != hwnd)
        {
            warnedBlank = hwnd;
            Activity.Write("Warning: window capture shows black while the game is on screen, so banners can't be seen. " +
                           $"Set source = \"auto\" under [capture] in config.toml.{FullscreenHint()}");
        }
    }

    /// <summary>On the screen fallback: tries window capture again, and goes back to it once it shows the game.</summary>
    void RetryWindow(RECT onScreen)
    {
        retryWindowEvery = Math.Min(retryWindowEvery * 2, RetryWindowMaxS);
        retryWindowAt = Clock.Mono + retryWindowEvery;
        if (Win.FrameBounds(hwnd) is not RECT frame) return;
        var region = onScreen.Offset(-frame.Left, -frame.Top);
        if (Probe(() => WgcCapture.ForWindow(hwnd, ProbeInterval, Gate.Height), region) != true) return;
        windowCaptureFailed = IntPtr.Zero;
        DropCapture();
        Activity.Write("Window capture works again: capturing the game window, so banners are seen even while a window covers the game.");
    }

    static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(30);

    /// <summary>
    /// One frame of <paramref name="region"/> from a short-lived capture: true if it shows something, false if
    /// it is black, null if no frame came within a second.
    /// </summary>
    static bool? Probe(Func<WgcCapture> start, RECT region)
    {
        try
        {
            using var cap = start();
            double until = Clock.Mono + 1;
            while (Clock.Mono < until)
            {
                if (cap.CaptureRegion(region)) return !cap.ReadShrunk().IsBlack();
                Thread.Sleep(30);
            }
        }
        catch (Exception) { }
        return null;
    }

    static string FullscreenHint() => Diagnose.ExclusiveFullscreen()
        ? " Fortnite looks like it's in Fullscreen mode: Windowed Fullscreen (Window Mode in its video settings) avoids this."
        : " If it keeps happening, try Windowed Fullscreen (Window Mode in Fortnite's video settings).";

    /// <summary>Once per game window: what is being watched and how, so a setup problem shows before the first find.</summary>
    void ReportSetup(bool window)
    {
        reportedFor = hwnd;
        Activity.Write($"Watching {Diagnose.Game(hwnd)}. {(window ? "Window" : "Screen")} capture is working.");
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
            FallBack($"keeps failing ({e.Message})");
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
        retryWindowAt = Clock.Mono + retryWindowEvery;
        Activity.Write($"Warning: window capture {why}, so the tracker is capturing the screen instead and only sees " +
                       $"banners while nothing covers the middle of the game. It tries the window again in {retryWindowEvery / 60:0} min." +
                       FullscreenHint());
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
                FallBack($"can't start ({e.Message})");
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
                retryWindowEvery = RetryWindowFirstS;
            }
            if (session == null && !holdNew) StartSession();
        }
        else if (session == null)
        {
            holdNew = false;  // game closed: the next launch starts a new session
            DropCapture();
        }
        else if (!session.Paused)  // a paused session waits for you, even with the game closed
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

    void TogglePause()
    {
        if (session == null)
        {
            Activity.Write("No session running, nothing to pause.");
            return;
        }
        if (session.Paused)
        {
            session.Unpause();
            missingSince = null;  // the end-of-game grace period starts again
            Activity.Write($"Session {session.Id} resumed at {Fmt.Duration(session.Elapsed())}");
        }
        else
        {
            foreach (var ev in banners.Flush()) LogEvent(ev);
            session.Pause();
            DropCapture();
            Activity.Write($"Session {session.Id} paused at {Fmt.Duration(session.Elapsed())}. Nothing is tracked until you resume it.");
        }
        log.Save(session);
        nextAutosave = Clock.Mono + AutosaveS;
        PushOverlay();
        PublishStatus("");
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
        try
        {
            log.SaveCard(session!, ev);
        }
        catch (Exception e)  // only a record for checking names later: never worth losing the find over
        {
            Activity.Write($"Couldn't save the item card image: {e.Message}");
        }
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
            text = $"Session {Fmt.Duration(s.Elapsed())}{(s.Paused ? " (paused)" : "")} | {s.Items.Count} found";
            if (s.Items.Count > 0)
            {
                var last = s.Items[^1];
                text += $" | last: {last.Item} ({Fmt.Duration(s.Elapsed() - last.T)} ago)";
            }
            if (state.Length > 0) text += $" | {state}";
        }
        var recent = session?.Items.TakeLast(5).Reverse().Select(i => new RecentFind(TimeOf(i.FoundAt), i.Item, icons.PathFor(i.Item))).ToArray() ?? [];
        double timerAt = session is { Paused: true } p ? p.Elapsed() : session?.Prior ?? 0;
        Status = new TrackerStatus(text, session?.Id, holdNew, recent, session?.Items.Count ?? 0, timerAt, session?.MonoStart ?? 0,
            session?.Paused ?? false);
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
        var next = new OverlayState(true, a, s.Paused ? s.Elapsed() : s.Prior, s.MonoStart, s.Items.Count > 0 ? s.Items[^1].T : null,
            rows, s.Paused);
        if (!next.Equals(Overlay)) Overlay = next;
    }
}
