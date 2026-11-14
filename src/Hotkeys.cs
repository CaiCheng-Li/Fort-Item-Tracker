namespace FortItemTracker;

/// <summary>System-wide hotkeys (RegisterHotKey), so you can act without leaving the game.</summary>
sealed class Hotkeys : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_NOREPEAT = 0x4000;

    readonly Dictionary<int, string> ids = [];

    public event Action<string>? Pressed;

    public Hotkeys() => CreateHandle(new CreateParams { Parent = new IntPtr(-3) });  // message-only window

    /// <summary>"ctrl+shift+r" → (modifiers, virtual-key code). Keys: A-Z, 0-9, F1-F24.</summary>
    public static (uint Mods, uint Vk) Parse(string text)
    {
        var parts = text.Split('+').Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0).ToArray();
        if (parts.Length == 0) throw new FormatException("empty hotkey");
        uint mods = 0;
        foreach (var p in parts[..^1])
            mods |= p switch
            {
                "alt" => 1u,
                "ctrl" or "control" => 2u,
                "shift" => 4u,
                "win" => 8u,
                _ => throw new FormatException($"unknown modifier '{p}' (use ctrl, alt, shift, win)"),
            };
        string key = parts[^1];
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) return (mods, char.ToUpperInvariant(key[0]));
        if (key[0] == 'f' && int.TryParse(key[1..], out int n) && n is >= 1 and <= 24) return (mods, (uint)(0x70 + n - 1));
        throw new FormatException($"unsupported key '{key}' (use A-Z, 0-9 or F1-F24)");
    }

    /// <summary>Null when it worked, else why not.</summary>
    public string? Register(string name, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods, vk;
        try
        {
            (mods, vk) = Parse(text);
        }
        catch (FormatException e)
        {
            return $"{name} hotkey '{text}': {e.Message}";
        }
        int id = ids.Count + 1;
        if (!Native.RegisterHotKey(Handle, id, mods | MOD_NOREPEAT, vk))
            return $"{name} hotkey '{text}' is already used by another program";
        ids[id] = name;
        return null;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && ids.TryGetValue((int)m.WParam, out var name)) Pressed?.Invoke(name);
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in ids.Keys) Native.UnregisterHotKey(Handle, id);
        ids.Clear();
        DestroyHandle();
    }
}
