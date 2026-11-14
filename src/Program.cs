using System.Globalization;
using System.Reflection;

namespace FortItemTracker;

sealed class Options
{
    public const string Usage = """
        Fort Item Tracker: logs Fortnite chest rewards with timestamps per session.

          FortItemTracker.exe                        run in the tray (one session per game launch)
          FortItemTracker.exe --test IMAGE...        check detection on screenshots (wildcards work)
                              --verbose              with --test, print every OCR line
          FortItemTracker.exe --sessions             list saved sessions
          FortItemTracker.exe --resume [ID]          continue the last (or a given) session, keeping its timer
          FortItemTracker.exe --overlay              show the timer overlay for this run
          FortItemTracker.exe --always [--display N] track a whole display without waiting for Fortnite
          FortItemTracker.exe --debug                save frames that pass the pre-check to logs\debug
          FortItemTracker.exe --config FILE          use another settings file
        """;

    public readonly List<string> Test = [];
    public bool Verbose, Always, Debug, ListSessions, Overlay, Startup, Wait, Help;
    public int Display;
    public string? Resume, ConfigPath;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        string Next(ref int i, string option) =>
            i + 1 < args.Length ? args[++i] : throw new FormatException($"{option} needs a value");
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "--test":
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--")) o.Test.Add(args[++i]);
                    if (o.Test.Count == 0) throw new FormatException("--test needs at least one image");
                    break;
                case "--verbose": o.Verbose = true; break;
                case "--always": o.Always = true; break;
                case "--display":
                    o.Display = int.TryParse(Next(ref i, a), out int n) ? n : throw new FormatException("--display needs a number");
                    break;
                case "--debug": o.Debug = true; break;
                case "--resume":
                    o.Resume = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "latest";
                    break;
                case "--sessions": o.ListSessions = true; break;
                case "--overlay": o.Overlay = true; break;
                case "--config": o.ConfigPath = Next(ref i, a); break;
                case "--startup": o.Startup = true; break;
                case "--wait": o.Wait = true; break;
                case "-h" or "--help" or "/?": o.Help = true; break;
                default: throw new FormatException($"unknown option {a}");
            }
        }
        return o;
    }

    /// <summary>Arguments for a restarted copy: same setup, but no --resume (the session carries on as saved).</summary>
    public string RestartArgs()
    {
        var args = new List<string> { "--wait", "--startup" };
        if (ConfigPath != null) args.Add($"--config \"{Path.GetFullPath(ConfigPath)}\"");
        if (Always) args.Add("--always");
        if (Display > 0) args.Add($"--display {Display}");
        if (Debug) args.Add("--debug");
        if (Overlay) args.Add("--overlay");
        return string.Join(" ", args);
    }
}

static class Program
{
    const string Title = "Fort Item Tracker";

    [STAThread]
    static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Options opts;
        try
        {
            opts = Options.Parse(args);
        }
        catch (FormatException e)
        {
            AttachParentConsole();
            Console.Error.WriteLine($"{e.Message}\n\n{Options.Usage}");
            return 2;
        }
        string root = FindRoot();
        string configPath = opts.ConfigPath != null ? Path.GetFullPath(opts.ConfigPath) : Path.Combine(root, "config.toml");
        if (opts.Help || opts.Test.Count > 0 || opts.ListSessions) return RunCli(opts, root, configPath);

        ApplicationConfiguration.Initialize();
        // One tracker per settings file, so a test copy with its own config can run next to the real one.
        string configKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(configPath.ToLowerInvariant())))[..16];
        using var mutex = new Mutex(false, $"FortItemTracker.{configKey}");
        bool owned;
        try { owned = mutex.WaitOne(opts.Wait ? 15000 : 0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            MessageBox.Show("Fort Item Tracker is already running. Look for its icon in the tray, by the clock.", Title,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        try
        {
            Config cfg;
            try
            {
                cfg = Config.Load(configPath);
            }
            catch (Exception e) when (e is FormatException or IOException)
            {
                MessageBox.Show($"There's a problem in the settings file:\n\n{e.Message}", Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            var tracker = new Tracker(cfg, root, opts);
            Activity.Init(tracker.LogFolder);
            Activity.Write($"Fort Item Tracker {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} started in {root}");
            foreach (var w in cfg.Warnings) Activity.Write(w);
            try
            {
                tracker.Start();
            }
            catch (Exception e)
            {
                Activity.Write($"Can't start: {e.Message}");
                MessageBox.Show(e.Message, $"{Title} can't start", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            Application.Run(new TrayApp(cfg, root, opts, tracker));
            return 0;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// The folder with config.toml, items.txt, assets and logs: the exe's own folder, or for a build
    /// under src\bin, the project folder above it.
    /// </summary>
    static string FindRoot()
    {
        string dir = Path.GetFullPath(AppContext.BaseDirectory);
        bool Has(string d) => File.Exists(Path.Combine(d, "config.toml")) || File.Exists(Path.Combine(d, "items.txt"));
        if (Has(dir) || !dir.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return dir;
        for (var d = new DirectoryInfo(dir).Parent; d != null; d = d.Parent)
            if (Has(d.FullName)) return d.FullName;
        return dir;
    }

    static void AttachParentConsole()
    {
        if (Native.GetStdHandle(-11) == IntPtr.Zero && Native.AttachConsole(-1))
            Console.WriteLine();  // the prompt is already on screen
    }

    static int RunCli(Options opts, string root, string configPath)
    {
        AttachParentConsole();
        try
        {
            if (opts.Help)
            {
                Console.WriteLine(Options.Usage);
                return 0;
            }
            var cfg = Config.Load(configPath);
            foreach (var w in cfg.Warnings) Console.WriteLine(w);
            if (opts.ListSessions) return ListSessions(cfg, root);
            return TestMode.Run(cfg, root, ExpandWildcards(opts.Test), opts.Verbose, Console.WriteLine);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        finally
        {
            Console.Out.Flush();
        }
    }

    static List<string> ExpandWildcards(IEnumerable<string> patterns)
    {
        var paths = new List<string>();
        foreach (var p in patterns)
        {
            string file = Path.GetFileName(p);
            if (file.IndexOfAny(['*', '?']) < 0)
            {
                paths.Add(p);
                continue;
            }
            string dir = Path.GetDirectoryName(p) is { Length: > 0 } d ? d : ".";
            var found = Directory.Exists(dir) ? Directory.GetFiles(dir, file).Order(StringComparer.OrdinalIgnoreCase).ToList() : [];
            paths.AddRange(found.Count > 0 ? found : [p]);
        }
        return paths;
    }

    static int ListSessions(Config cfg, string root)
    {
        var saved = new LootLog(Config.Resolve(root, cfg.Folder)).SavedSessions();
        if (saved.Count == 0)
        {
            Console.WriteLine("No saved sessions yet.");
            return 0;
        }
        Console.WriteLine($"{"ID",-17} {"started",-22} {"last saved",-22} {"timer",8} {"items",5}  status");
        foreach (var s in saved)
            Console.WriteLine($"{s.Id,-17} {Fmt.ClockTime(s.Started),-22} {Fmt.ClockTime(s.SavedAt),-22} " +
                              $"{Fmt.Duration(s.Elapsed),8} {s.Items.Count,5}  {(s.Ended ? "ended" : "open (not ended cleanly)")}");
        Console.WriteLine("\nContinue one with: FortItemTracker.exe --resume ID   (or Resume a saved session in the tray menu)");
        return 0;
    }
}
