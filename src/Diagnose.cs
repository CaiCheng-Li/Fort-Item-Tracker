using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace FortItemTracker;

/// <summary>Facts about the game window and the displays, for the setup line and the problem report.</summary>
static class Diagnose
{
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceName info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref ColorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PathInfo  // DISPLAYCONFIG_PATH_INFO, 72 bytes
    {
        public Luid SourceAdapter;
        public uint SourceId, SourceModeIdx, SourceStatus;
        public Luid TargetAdapter;
        public uint TargetId, TargetModeIdx, OutputTechnology, Rotation, Scaling, RefreshNum, RefreshDen, ScanLineOrdering;
        public int TargetAvailable;
        public uint TargetStatus, Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    struct ModeInfo  // DISPLAYCONFIG_MODE_INFO; the union after these fields isn't read
    {
        public uint InfoType, Id;
        public Luid Adapter;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SourceName  // DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public int Type;
        public uint Size;
        public Luid Adapter;
        public uint Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ColorInfo  // DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public int Type;
        public uint Size;
        public Luid Adapter;
        public uint Id;
        public uint Value, Encoding, Bits;
    }

    /// <summary>Whether HDR is on, by display device name (\\.\DISPLAY1). Displays Windows won't say are missing.</summary>
    static Dictionary<string, bool> Hdr()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            const uint OnlyActive = 2;
            if (GetDisplayConfigBufferSizes(OnlyActive, out uint np, out uint nm) != 0) return result;
            var paths = new PathInfo[np];
            var modes = new ModeInfo[nm];
            if (QueryDisplayConfig(OnlyActive, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return result;
            for (int i = 0; i < np; i++)
            {
                var p = paths[i];
                var name = new SourceName { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = p.SourceAdapter, Id = p.SourceId, GdiName = "" };
                var color = new ColorInfo { Type = 9, Size = (uint)Marshal.SizeOf<ColorInfo>(), Adapter = p.TargetAdapter, Id = p.TargetId };
                if (DisplayConfigGetDeviceInfo(ref name) != 0 || DisplayConfigGetDeviceInfo(ref color) != 0) continue;
                // Advanced colour on without "wide colour enforced" (that alone is SDR colour management).
                result[name.GdiName] = (color.Value & 2) != 0 && (color.Value & 4) == 0;
            }
        }
        catch (Exception) { }
        return result;
    }

    static int? ScalePercent(IntPtr hmon) =>
        GetDpiForMonitor(hmon, 0 /* MDT_EFFECTIVE_DPI */, out uint dpi, out _) == 0 ? (int)Math.Round(dpi * 100 / 96.0) : null;

    /// <summary>A Direct3D app is in exclusive fullscreen (only meaningful while the game is in front).</summary>
    public static bool ExclusiveFullscreen() => SHQueryUserNotificationState(out int state) == 0 && state == 3;

    static string Short(string device) => device.StartsWith(@"\\.\") ? device[4..] : device;

    static string Display(IntPtr hmon, string device, RECT r, Dictionary<string, bool> hdr)
    {
        var parts = new List<string> { $"{r.Width}x{r.Height}" };
        if (ScalePercent(hmon) is int s) parts.Add($"{s}% scaling");
        if (hdr.TryGetValue(device, out bool on)) parts.Add(on ? "HDR on" : "HDR off");
        return $"{Short(device)} ({string.Join(", ", parts)})";
    }

    /// <summary>"Fortnite at 2560x1440, full screen, on DISPLAY1 (2560x1440, 100% scaling, HDR off)".</summary>
    public static string Game(IntPtr hwnd)
    {
        if (Win.ClientRect(hwnd) is not RECT c) return "Fortnite";
        var (hmon, device, m) = Win.MonitorForWindow(hwnd);
        bool full = c.Left == m.Left && c.Top == m.Top && c.Right == m.Right && c.Bottom == m.Bottom;
        string mode = !full ? "in a window" : Native.GetForegroundWindow() == hwnd && ExclusiveFullscreen() ? "full screen (exclusive)" : "full screen";
        return $"Fortnite at {c.Width}x{c.Height}, {mode}, on {Display(hmon, device, m, Hdr())}";
    }

    static string WindowsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string build = key?.GetValue("CurrentBuildNumber") as string ?? Environment.OSVersion.Version.Build.ToString();
            string name = int.TryParse(build, out int b) && b >= 22000 ? "Windows 11" : "Windows 10";
            return $"{name} {key?.GetValue("DisplayVersion")} (build {build}.{key?.GetValue("UBR")})";
        }
        catch (Exception)
        {
            return Environment.OSVersion.VersionString;
        }
    }

    static IEnumerable<string> GraphicsCards()
    {
        var found = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in cls?.GetSubKeyNames().Where(n => n.All(char.IsDigit)) ?? [])
            {
                using var k = cls!.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is string desc) found.Add($"{desc} (driver {k.GetValue("DriverVersion")})");
            }
        }
        catch (Exception) { }
        return found.Distinct();
    }

    /// <summary>system.txt of the problem report.</summary>
    public static string Report(Config cfg, string status)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Fort Item Tracker {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}");
        sb.AppendLine($"Saved {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"{WindowsVersion()}");
        foreach (var gpu in GraphicsCards()) sb.AppendLine($"Graphics: {gpu}");
        var hdr = Hdr();
        sb.AppendLine("Displays:");
        foreach (var m in Win.Monitors())
            sb.AppendLine($"  {Display(m.Handle, m.Device, m.Rect, hdr)} at {m.Rect.Left},{m.Rect.Top}{(m.Primary ? ", main display" : "")}");
        var pids = new HashSet<int>();
        foreach (var name in cfg.ProcessNames)
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
            {
                pids.Add(p.Id);
                p.Dispose();
            }
        var hwnd = pids.Count > 0 ? Win.FindMainWindow(pids) : IntPtr.Zero;
        sb.AppendLine(hwnd != IntPtr.Zero ? $"Game: {Game(hwnd)}" : pids.Count > 0 ? "Game: running, no window" : "Game: not running");
        sb.AppendLine($"Tracker: {status}");
        return sb.ToString();
    }
}

/// <summary>One zip with what's needed to work out a problem, for the user to send on.</summary>
static class ProblemReport
{
    public static string Save(Config cfg, string root, string logFolder, string status, string? dir = null)
    {
        dir ??= Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(dir)) dir = logFolder;
        string zip = Path.Combine(dir, $"FortItemTracker-report-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        Write(archive, "system.txt", Diagnose.Report(cfg, status));
        AddText(archive, "config.toml", cfg.Path);
        AddText(archive, "items.txt", Path.Combine(root, "items.txt"));
        foreach (var name in new[] { "tracker.log", "tracker.old.log", "loot.csv", "sessions.csv" })
            AddText(archive, $"logs/{name}", Path.Combine(logFolder, name));
        // Which reference images there are and whether Windows can read them (a .webp needs a codec).
        string iconDir = Config.Resolve(root, cfg.IconFolder);
        var icons = new StringBuilder();
        foreach (var f in Files(iconDir).Concat(Files(Path.Combine(iconDir, "learned"))))
        {
            var img = ImageIO.Load(f);
            icons.AppendLine($"{Path.GetRelativePath(iconDir, f)}  {new FileInfo(f).Length} bytes  " +
                             (img != null ? $"{img.Width}x{img.Height}" : "CAN'T READ"));
        }
        Write(archive, "icons.txt", icons.Length > 0 ? icons.ToString() : "no images in the icon folder\n");
        foreach (var f in Files(Path.Combine(iconDir, "learned")))
            archive.CreateEntryFromFile(f, $"learned/{Path.GetFileName(f)}");
        string cards = Path.Combine(logFolder, "cards");
        if (Directory.Exists(cards))
            foreach (var session in Directory.GetDirectories(cards).OrderDescending(StringComparer.Ordinal).Take(3))
                foreach (var f in Files(session))
                    archive.CreateEntryFromFile(f, $"cards/{Path.GetFileName(session)}/{Path.GetFileName(f)}");
        return zip;
    }

    static IEnumerable<string> Files(string dir) => Directory.Exists(dir) ? Directory.GetFiles(dir) : [];

    static void AddText(ZipArchive archive, string name, string path)
    {
        if (!File.Exists(path)) return;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        Write(archive, name, reader.ReadToEnd());
    }

    /// <summary>Text with the Windows profile folder (it holds the user name) written as %USERPROFILE%.</summary>
    static void Write(ZipArchive archive, string name, string text)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        using var w = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(name.EndsWith(".csv")));
        w.Write(text);
    }
}
