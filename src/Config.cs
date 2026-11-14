namespace FortItemTracker;

/// <summary>Settings from config.toml. Anything missing keeps the default below.</summary>
sealed class Config
{
    // [capture]
    public double PollHz = 4;
    public string Source = "auto";  // "auto", "window" or "monitor"
    public bool OnlyWhenFocused;
    public double[] Roi = [0.25, 0.08, 0.75, 0.50];

    // [detect]
    public double GateThreshold = 0.005;
    public int[] GateHue = [22, 34];
    public string OcrLanguage = "en";
    public string Anchor = "YOU FOUND";
    public double BannerGapS = 1.0;
    public int SnapScore = 80;

    // [icons]
    public string IconFolder = "assets";
    public bool LearnIcons = true;
    public string[] GenericNames = ["Sprite Gizmo"];

    // [session]
    public string[] ProcessNames = ["FortniteClient-Win64-Shipping.exe"];
    public double EndGraceS = 10;

    // [output]
    public string Folder = "logs";

    // [hotkey]
    public string HotkeyReset = "ctrl+shift+r";

    // [overlay]
    public bool OverlayEnabled = true;
    public string OverlayAnchor = "top-left";  // the game window corner x and y are measured from
    public int OverlayX = 16, OverlayY = 16;
    public double OverlayScale = 1.0;
    public double OverlayOpacity = 0.85;
    public int OverlayItems = 5;
    public bool OverlayShowNames;

    public string Path = "";
    public readonly List<string> Warnings = [];

    public static Config Load(string path)
    {
        var cfg = new Config { Path = path };
        if (!File.Exists(path)) return cfg;
        Dictionary<string, Dictionary<string, object>> data;
        try
        {
            data = Toml.Parse(File.ReadAllText(path));
        }
        catch (FormatException e)
        {
            throw new FormatException($"{System.IO.Path.GetFileName(path)} {e.Message}");
        }
        foreach (var (section, values) in data)
            foreach (var (key, value) in values)
            {
                try
                {
                    if (!cfg.Set(section, key, value))
                        cfg.Warnings.Add($"config: ignoring unknown setting '{key}' in [{section}]");
                }
                catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
                {
                    cfg.Warnings.Add($"config: '{key}' in [{section}] has the wrong type, using the default");
                }
            }
        cfg.Source = cfg.Source.ToLowerInvariant();
        if (cfg.Source is not ("auto" or "window" or "monitor"))
        {
            cfg.Warnings.Add($"config: source = \"{cfg.Source}\" isn't auto, window or monitor; using auto");
            cfg.Source = "auto";
        }
        cfg.OverlayAnchor = cfg.OverlayAnchor.ToLowerInvariant();
        if (cfg.OverlayAnchor is not ("top-left" or "top-right" or "bottom-left" or "bottom-right"))
        {
            cfg.Warnings.Add($"config: anchor = \"{cfg.OverlayAnchor}\" isn't top-left, top-right, bottom-left or bottom-right; using top-left");
            cfg.OverlayAnchor = "top-left";
        }
        if (cfg.Roi.Length != 4) { cfg.Warnings.Add("config: roi needs 4 numbers; using the default"); cfg.Roi = [0.25, 0.08, 0.75, 0.50]; }
        if (cfg.GateHue.Length != 2) { cfg.Warnings.Add("config: gate_hue needs 2 numbers; using the default"); cfg.GateHue = [22, 34]; }
        return cfg;
    }

    bool Set(string section, string key, object v)
    {
        switch (section, key)
        {
            case ("capture", "poll_hz"): PollHz = Num(v); break;
            case ("capture", "source"): Source = Str(v); break;
            case ("capture", "backend"):
                Warnings.Add("config: 'backend' is gone (capture is now Windows Graphics Capture); see 'source' in [capture]");
                break;
            case ("capture", "only_when_focused"): OnlyWhenFocused = (bool)v; break;
            case ("capture", "roi"): Roi = List(v).Select(Num).ToArray(); break;
            case ("detect", "gate_threshold"): GateThreshold = Num(v); break;
            case ("detect", "gate_hue"): GateHue = List(v).Select(x => (int)Num(x)).ToArray(); break;
            case ("detect", "ocr_language"): OcrLanguage = Str(v); break;
            case ("detect", "anchor"): Anchor = Str(v); break;
            case ("detect", "banner_gap_s"): BannerGapS = Num(v); break;
            case ("detect", "snap_score"): SnapScore = (int)Num(v); break;
            case ("icons", "icon_folder"): IconFolder = Str(v); break;
            case ("icons", "learn_icons"): LearnIcons = (bool)v; break;
            case ("icons", "generic_names"): GenericNames = List(v).Select(Str).ToArray(); break;
            case ("session", "process_names"): ProcessNames = List(v).Select(Str).ToArray(); break;
            case ("session", "end_grace_s"): EndGraceS = Num(v); break;
            case ("output", "folder"): Folder = Str(v); break;
            case ("hotkey", "reset"): HotkeyReset = Str(v); break;
            case ("overlay", "enabled"): OverlayEnabled = (bool)v; break;
            case ("overlay", "anchor"): OverlayAnchor = Str(v); break;
            case ("overlay", "x"): OverlayX = (int)Num(v); break;
            case ("overlay", "y"): OverlayY = (int)Num(v); break;
            case ("overlay", "scale"): OverlayScale = Num(v); break;
            case ("overlay", "opacity"): OverlayOpacity = Num(v); break;
            case ("overlay", "items"): OverlayItems = (int)Num(v); break;
            case ("overlay", "show_names"): OverlayShowNames = (bool)v; break;
            default: return false;
        }
        return true;
    }

    static double Num(object v) => v switch
    {
        long l => l,
        double d => d,
        _ => throw new InvalidCastException(),
    };

    static string Str(object v) => v as string ?? throw new InvalidCastException();

    static List<object> List(object v) => v as List<object> ?? throw new InvalidCastException();

    /// <summary>A path from the config, relative to the app folder unless it is absolute.</summary>
    public static string Resolve(string root, string path) => System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
}
