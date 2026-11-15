using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Win32;

namespace FortItemTracker;

static class AppIcon
{
    public static Icon Load(Size? size = null)
    {
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("FortItemTracker.app.ico");
        if (s == null) return (Icon)SystemIcons.Application.Clone();
        return size is Size sz ? new Icon(s, sz) : new Icon(s);
    }
}

/// <summary>The tray icon and its menu. All tracking happens on the Tracker thread.</summary>
sealed class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "Fort Item Tracker";

    readonly Config cfg;
    readonly string root;
    readonly Options opts;
    readonly Tracker tracker;
    readonly NotifyIcon tray;
    readonly Icon trayIcon, windowIcon;
    readonly Hotkeys hotkeys;
    readonly System.Windows.Forms.Timer tick, pulse;
    readonly MenuStyle style = new();
    readonly ToolStripMenuItem header, saveItem, pauseItem, resetItem, endItem, resumeMenu, overlayItem, pinItem, resetPosItem, startupItem, testItem, reportItem;
    readonly ToolStripMenuItem[] recent = new ToolStripMenuItem[5];
    readonly Dictionary<string, Bitmap?> menuIcons = [];
    readonly List<IntPtr> pulseHandles = [];
    Icon[]? pulseFrames;
    int pulseFrame = -1, lastFound;
    string? lastSession;
    Overlay? overlay;
    LogWindow? logWindow;
    bool exiting;

    public TrayApp(Config cfg, string root, Options opts, Tracker tracker)
    {
        this.cfg = cfg;
        this.root = root;
        this.opts = opts;
        this.tracker = tracker;
        trayIcon = AppIcon.Load(SystemInformation.SmallIconSize);
        windowIcon = AppIcon.Load();

        var menu = new ContextMenuStrip();
        header = new ToolStripMenuItem("Fort Item Tracker") { Enabled = false, Tag = MenuStyle.Header, Image = SparkleImage() };
        header.Font = new Font(header.Font, FontStyle.Bold);
        menu.Items.Add(header);
        for (int i = 0; i < recent.Length; i++)
        {
            recent[i] = new ToolStripMenuItem { Enabled = false, Visible = false, Tag = MenuStyle.Info };
            menu.Items.Add(recent[i]);
        }
        menu.Items.Add(new ToolStripSeparator());
        saveItem = Item("Save session now", tracker.RequestSave);
        pauseItem = Item("Pause session", tracker.RequestTogglePause);
        resetItem = Item("Reset timer and counts", tracker.RequestReset);
        endItem = Item("End session", tracker.RequestEnd);
        resumeMenu = new ToolStripMenuItem("Resume a saved session");
        resumeMenu.DropDownItems.Add(new ToolStripMenuItem("...") { Enabled = false });
        resumeMenu.DropDownOpening += (_, _) => FillResumeMenu();
        menu.Items.AddRange([saveItem, pauseItem, resetItem, endItem, resumeMenu, new ToolStripSeparator()]);
        overlayItem = Item("Show overlay", ToggleOverlay);
        pinItem = Item("Pin overlay in place", () => { if (overlay != null) overlay.Pinned = !overlay.Pinned; });
        startupItem = Item("Start with Windows", ToggleStartup);
        resetPosItem = Item("Reset overlay position", ResetOverlayPosition);
        menu.Items.AddRange([overlayItem, pinItem, resetPosItem, startupItem, new ToolStripSeparator()]);
        testItem = Item("Test screenshots...", TestScreenshots);
        reportItem = Item("Save a problem report", SaveReport);
        menu.Items.AddRange([
            Item("Activity log", ShowLog),
            testItem,
            reportItem,
            Item("Open logs folder", () => OpenFolder(tracker.LogFolder)),
            Item("Open icons folder", () => OpenFolder(Config.Resolve(root, cfg.IconFolder))),
            Item("Edit settings (config.toml)", () => Notepad(cfg.Path)),
            Item("Edit item names (items.txt)", () => Notepad(Path.Combine(root, "items.txt"))),
            new ToolStripSeparator(),
            Item("Restart (applies settings)", Restart),
            Item("Exit", Exit),
        ]);
        float dpi = menu.DeviceDpi / 96f;
        menu.ImageScalingSize = new Size((int)(20 * dpi), (int)(20 * dpi));
        menu.Opening += (_, _) => RefreshMenu();
        style.Attach(menu);
        style.Attach(resumeMenu.DropDown);

        tray = new NotifyIcon { Icon = trayIcon, Text = "Fort Item Tracker", ContextMenuStrip = menu, Visible = true };
        tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowLog(); };

        hotkeys = new Hotkeys();
        hotkeys.Pressed += name =>
        {
            if (name == "reset") tracker.RequestReset();
            else if (name == "pause") tracker.RequestTogglePause();
        };
        AddHotkey("reset", "Reset", cfg.HotkeyReset, resetItem);
        AddHotkey("pause", "Pause/resume", cfg.HotkeyPause, pauseItem);

        overlayItem.Checked = cfg.OverlayEnabled || opts.Overlay;
        SetOverlay(overlayItem.Checked);
        startupItem.Checked = StartupEnabled();

        pulse = new System.Windows.Forms.Timer { Interval = 55 };
        pulse.Tick += (_, _) => PulseTick();
        tick = new System.Windows.Forms.Timer { Interval = 1000 };
        tick.Tick += (_, _) => RefreshTooltip();
        tick.Start();
        RefreshTooltip();

        SystemEvents.SessionEnded += OnWindowsSessionEnded;
        if (!opts.Startup)
            tray.ShowBalloonTip(4000, "Fort Item Tracker", "Running in the tray. A session starts when Fortnite launches.", ToolTipIcon.None);
    }

    static ToolStripMenuItem Item(string text, Action action) => new(text, null, (_, _) => action());

    void AddHotkey(string name, string label, string keys, ToolStripMenuItem item)
    {
        if (hotkeys.Register(name, keys) is string error) Activity.Write($"Hotkey not available: {error}");
        else if (keys.Trim().Length > 0)
        {
            item.ShortcutKeyDisplayString = keys.ToUpperInvariant();
            Activity.Write($"{label} hotkey (works in game): {keys.ToUpperInvariant()}");
        }
    }

    // ---- status ---------------------------------------------------------------------------------

    void RefreshTooltip()
    {
        var st = tracker.Status;
        string text = "Fort Item Tracker\n" + st.Text.Replace(" | ", "\n");
        tray.Text = text.Length <= 127 ? text : text[..124] + "...";
        if (st.SessionId != null && st.SessionId == lastSession && st.Found > lastFound) Celebrate();
        (lastSession, lastFound) = (st.SessionId, st.Found);
    }

    void RefreshMenu()
    {
        var st = tracker.Status;
        header.Text = string.Join("  ·  ", st.Text.Split(" | ").Where(p => !p.StartsWith("last: ")));
        for (int i = 0; i < recent.Length; i++)
        {
            bool on = i < st.Recent.Length;
            recent[i].Visible = on;
            recent[i].Text = on ? $"{st.Recent[i].Time}   {st.Recent[i].Item}" : "";
            recent[i].Image = on ? MenuIcon(st.Recent[i].Icon) : null;
        }
        bool running = st.SessionId != null;
        saveItem.Enabled = running;
        pauseItem.Enabled = running;
        pauseItem.Text = st.Paused ? "Resume paused session" : "Pause session";
        endItem.Enabled = running;
        resetItem.Text = running ? "Reset timer and counts" : "Start a new session";
        pinItem.Enabled = overlay != null;
        pinItem.Checked = overlay?.Pinned ?? true;
        startupItem.Checked = StartupEnabled();
    }

    Bitmap? MenuIcon(string? path)
    {
        if (path == null) return null;
        if (menuIcons.TryGetValue(path, out var cached)) return cached;
        Bitmap? bmp = null;
        if (ImageIO.Load(path) is Image32 img && img.Width > 0 && img.Height > 0)
        {
            using var full = ImageIO.ToBitmap(img, PixelFormat.Format32bppPArgb);
            const int box = 48;  // the menu scales it down smoothly
            double f = Math.Min(1.0, box / (double)Math.Max(img.Width, img.Height));
            int w = Math.Max(1, (int)(img.Width * f)), h = Math.Max(1, (int)(img.Height * f));
            bmp = new Bitmap(box, box, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(full, new Rectangle((box - w) / 2, (box - h) / 2, w, h));
        }
        menuIcons[path] = bmp;
        return bmp;
    }

    /// <summary>The app icon's two gold sparkles, for the status line.</summary>
    static Bitmap SparkleImage()
    {
        var bmp = new Bitmap(48, 48, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var gold = new LinearGradientBrush(new Rectangle(0, 0, 48, 48), Theme.GoldLight, Theme.GoldDeep, 90f);
        Theme.Sparkle(g, gold, 19, 28, 18);
        Theme.Sparkle(g, gold, 38, 10, 8);
        return bmp;
    }

    void FillResumeMenu()
    {
        resumeMenu.DropDownItems.Clear();
        string? current = tracker.Status.SessionId;
        var saved = tracker.Log.SavedSessions().Take(15).ToList();
        if (saved.Count == 0)
            resumeMenu.DropDownItems.Add(new ToolStripMenuItem("No saved sessions yet") { Enabled = false, Tag = MenuStyle.Info });
        foreach (var s in saved)
        {
            string state = s.Id == current ? "   (running now)" : s.Ended ? "" : "   (not ended cleanly)";
            var item = new ToolStripMenuItem($"{s.Id}    {Fmt.Duration(s.Elapsed)}    {s.Items.Count} item(s){state}")
            {
                Checked = s.Id == current,
                Enabled = s.Id != current,
            };
            string id = s.Id;
            item.Click += (_, _) => tracker.RequestResume(id);
            resumeMenu.DropDownItems.Add(item);
        }
    }

    // ---- the tray icon sparkles when something is found ---------------------------------------

    void Celebrate()
    {
        try
        {
            pulseFrames ??= MakePulse();
        }
        catch (Exception e)  // a decoration: never worth an error
        {
            Activity.Write($"tray animation unavailable: {e.Message}");
            pulseFrames = [];
        }
        pulseFrame = 0;
        pulse.Start();
    }

    void PulseTick()
    {
        if (pulseFrames == null || pulseFrame < 0 || pulseFrame >= pulseFrames.Length)
        {
            tray.Icon = trayIcon;
            pulse.Stop();
            pulseFrame = -1;
            return;
        }
        tray.Icon = pulseFrames[pulseFrame++];
    }

    Icon[] MakePulse()
    {
        using var icon = trayIcon.ToBitmap();
        int w = icon.Width, h = icon.Height;
        var frames = new Icon[18];
        for (int i = 0; i < frames.Length; i++)
        {
            double t = (i + 1) / (double)(frames.Length + 1);
            float glow = (float)Ease.Bump(t);
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var attrs = new ImageAttributes();  // the gold flares, the dark background stays dark
                attrs.SetColorMatrix(new ColorMatrix([
                    [1 + 0.6f * glow, 0, 0, 0, 0], [0, 1 + 0.55f * glow, 0, 0, 0], [0, 0, 1 + 0.25f * glow, 0, 0],
                    [0, 0, 0, 1, 0], [0, 0, 0, 0, 1],
                ]));
                g.DrawImage(icon, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel, attrs);
                using var star = new SolidBrush(Color.FromArgb((int)(255 * glow), 255, 252, 230));
                Theme.Sparkle(g, star, w * 0.76f, h * 0.24f, w * 0.34f * glow, (float)(t * 180));
            }
            IntPtr handle = bmp.GetHicon();
            pulseHandles.Add(handle);
            frames[i] = Icon.FromHandle(handle);
        }
        return frames;
    }

    // ---- actions --------------------------------------------------------------------------------

    void SetOverlay(bool on)
    {
        if (on && overlay == null)
        {
            overlay = new Overlay(cfg, () => tracker.Overlay);
            Activity.Write("Overlay: on. To move it, hover it and click its pin, then drag it.");
        }
        else if (!on && overlay != null)
        {
            overlay.Dispose();
            overlay = null;
            Activity.Write("Overlay: off");
        }
    }

    void ResetOverlayPosition()
    {
        if (overlay != null)
        {
            overlay.ResetPosition();  // writes the log line too
            return;
        }
        Overlay.SavePosition(cfg, Overlay.DefaultAnchor, Overlay.DefaultOffset, Overlay.DefaultOffset);
        Activity.Write($"Overlay moved back to {Overlay.DefaultOffset}, {Overlay.DefaultOffset} px from the top left corner of the game");
    }

    void ToggleOverlay()
    {
        overlayItem.Checked = !overlayItem.Checked;
        SetOverlay(overlayItem.Checked);
        try
        {
            Toml.SetValue(cfg.Path, "overlay", "enabled", overlayItem.Checked ? "true" : "false");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Activity.Write($"Couldn't save the overlay setting to config.toml: {e.Message}");
        }
    }

    static string StartupCommand => $"\"{Environment.ProcessPath}\" --startup";

    static bool StartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(RunName) as string, StartupCommand, StringComparison.OrdinalIgnoreCase);
    }

    void ToggleStartup()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (StartupEnabled()) key.DeleteValue(RunName, false);
            else key.SetValue(RunName, StartupCommand);
        }
        startupItem.Checked = StartupEnabled();
    }

    void ShowLog()
    {
        if (logWindow == null || logWindow.IsDisposed) logWindow = new LogWindow(windowIcon, () => tracker.Status);
        logWindow.Show();
        if (logWindow.WindowState == FormWindowState.Minimized) logWindow.WindowState = FormWindowState.Normal;
        logWindow.Activate();
    }

    async void TestScreenshots()
    {
        string samples = Path.Combine(root, "samples");
        using var dialog = new OpenFileDialog
        {
            Title = "Pick screenshots of the YOU FOUND banner",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All files|*.*",
            Multiselect = true,
            InitialDirectory = Directory.Exists(samples) ? samples : root,
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        ShowLog();
        var files = dialog.FileNames;
        testItem.Enabled = false;
        try
        {
            Activity.Write($"Testing {files.Length} screenshot(s)...");
            await Task.Run(() => TestMode.Run(cfg, root, files, verbose: true, Activity.Write));
        }
        catch (Exception e)
        {
            Activity.Write($"Test failed: {e.Message}");
        }
        finally
        {
            testItem.Enabled = true;
        }
    }

    async void SaveReport()
    {
        reportItem.Enabled = false;
        try
        {
            string status = tracker.Status.Text;
            string zip = await Task.Run(() => ProblemReport.Save(cfg, root, tracker.LogFolder, status));
            Activity.Write($"Problem report saved: {zip}. Send that file to whoever is helping you. It holds your logs, " +
                           "settings, learned icons and item card images, and no screenshots of the game.");
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Activity.Write($"Couldn't save the problem report: {e.Message}");
            ShowLog();
        }
        finally
        {
            reportItem.Enabled = true;
        }
    }

    static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    static void Notepad(string path)
    {
        if (!File.Exists(path)) File.WriteAllText(path, "");
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    void Restart()
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { Arguments = opts.RestartArgs(), UseShellExecute = false });
        Exit();
    }

    void OnWindowsSessionEnded(object? sender, SessionEndedEventArgs e) => tracker.Dispose();  // saves the session

    void Exit()
    {
        if (exiting) return;
        exiting = true;
        SystemEvents.SessionEnded -= OnWindowsSessionEnded;
        tick.Stop();
        pulse.Stop();
        tray.Visible = false;
        overlay?.Dispose();
        hotkeys.Dispose();
        logWindow?.Close();
        tracker.Dispose();  // ends and saves the session
        tray.Dispose();
        style.Dispose();
        foreach (var handle in pulseHandles) Native.DestroyIcon(handle);
        ExitThread();
    }
}
