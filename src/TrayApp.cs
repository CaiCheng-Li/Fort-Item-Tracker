using System.Diagnostics;
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
    readonly System.Windows.Forms.Timer tick;
    readonly ToolStripMenuItem header, saveItem, resetItem, endItem, resumeMenu, overlayItem, startupItem, testItem;
    readonly ToolStripMenuItem[] recent = new ToolStripMenuItem[5];
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
        header = new ToolStripMenuItem("Fort Item Tracker") { Enabled = false };
        header.Font = new Font(header.Font, FontStyle.Bold);
        menu.Items.Add(header);
        for (int i = 0; i < recent.Length; i++)
        {
            recent[i] = new ToolStripMenuItem { Enabled = false, Visible = false };
            menu.Items.Add(recent[i]);
        }
        menu.Items.Add(new ToolStripSeparator());
        saveItem = Item("Save session now", tracker.RequestSave);
        resetItem = Item("Reset timer and counts", tracker.RequestReset);
        endItem = Item("End session", tracker.RequestEnd);
        resumeMenu = new ToolStripMenuItem("Resume a saved session");
        resumeMenu.DropDownItems.Add(new ToolStripMenuItem("...") { Enabled = false });
        resumeMenu.DropDownOpening += (_, _) => FillResumeMenu();
        menu.Items.AddRange([saveItem, resetItem, endItem, resumeMenu, new ToolStripSeparator()]);
        overlayItem = Item("Show overlay", ToggleOverlay);
        startupItem = Item("Start with Windows", ToggleStartup);
        menu.Items.AddRange([overlayItem, startupItem, new ToolStripSeparator()]);
        testItem = Item("Test screenshots...", TestScreenshots);
        menu.Items.AddRange([
            Item("Activity log", ShowLog),
            testItem,
            Item("Open logs folder", () => OpenFolder(tracker.LogFolder)),
            Item("Open icons folder", () => OpenFolder(Config.Resolve(root, cfg.IconFolder))),
            Item("Edit settings (config.toml)", () => Notepad(cfg.Path)),
            Item("Edit item names (items.txt)", () => Notepad(Path.Combine(root, "items.txt"))),
            new ToolStripSeparator(),
            Item("Restart (applies settings)", Restart),
            Item("Exit", Exit),
        ]);
        menu.Opening += (_, _) => RefreshMenu();

        tray = new NotifyIcon { Icon = trayIcon, Text = "Fort Item Tracker", ContextMenuStrip = menu, Visible = true };
        tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowLog(); };

        hotkeys = new Hotkeys();
        hotkeys.Pressed += name => { if (name == "reset") tracker.RequestReset(); };
        if (hotkeys.Register("reset", cfg.HotkeyReset) is string error) Activity.Write($"Hotkey not available: {error}");
        else if (cfg.HotkeyReset.Trim().Length > 0)
        {
            resetItem.ShortcutKeyDisplayString = cfg.HotkeyReset.ToUpperInvariant();
            Activity.Write($"Reset hotkey (works in game): {cfg.HotkeyReset.ToUpperInvariant()}");
        }

        overlayItem.Checked = cfg.OverlayEnabled || opts.Overlay;
        SetOverlay(overlayItem.Checked);
        startupItem.Checked = StartupEnabled();

        tick = new System.Windows.Forms.Timer { Interval = 1000 };
        tick.Tick += (_, _) => RefreshTooltip();
        tick.Start();
        RefreshTooltip();

        SystemEvents.SessionEnded += OnWindowsSessionEnded;
        if (!opts.Startup)
            tray.ShowBalloonTip(4000, "Fort Item Tracker", "Running in the tray. A session starts when Fortnite launches.", ToolTipIcon.None);
    }

    static ToolStripMenuItem Item(string text, Action action) => new(text, null, (_, _) => action());

    // ---- status ---------------------------------------------------------------------------------

    void RefreshTooltip()
    {
        string text = "Fort Item Tracker\n" + tracker.Status.Text.Replace(" | ", "\n");
        tray.Text = text.Length <= 127 ? text : text[..124] + "...";
    }

    void RefreshMenu()
    {
        var st = tracker.Status;
        header.Text = string.Join("  ·  ", st.Text.Split(" | ").Where(p => !p.StartsWith("last: ")));
        for (int i = 0; i < recent.Length; i++)
        {
            recent[i].Visible = i < st.Recent.Length;
            recent[i].Text = i < st.Recent.Length ? "    " + st.Recent[i] : "";
        }
        bool running = st.SessionId != null;
        saveItem.Enabled = running;
        endItem.Enabled = running;
        resetItem.Text = running ? "Reset timer and counts" : "Start a new session";
        startupItem.Checked = StartupEnabled();
    }

    void FillResumeMenu()
    {
        resumeMenu.DropDownItems.Clear();
        string? current = tracker.Status.SessionId;
        var saved = tracker.Log.SavedSessions().Take(15).ToList();
        if (saved.Count == 0)
            resumeMenu.DropDownItems.Add(new ToolStripMenuItem("No saved sessions yet") { Enabled = false });
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

    // ---- actions --------------------------------------------------------------------------------

    void SetOverlay(bool on)
    {
        if (on && overlay == null)
        {
            overlay = new Overlay(cfg, () => tracker.Overlay);
            Activity.Write("Overlay: on (top-left of the game window)");
        }
        else if (!on && overlay != null)
        {
            overlay.Dispose();
            overlay = null;
            Activity.Write("Overlay: off");
        }
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
        if (logWindow == null || logWindow.IsDisposed) logWindow = new LogWindow(windowIcon);
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
        tray.Visible = false;
        overlay?.Dispose();
        hotkeys.Dispose();
        logWindow?.Close();
        tracker.Dispose();  // ends and saves the session
        tray.Dispose();
        ExitThread();
    }
}
