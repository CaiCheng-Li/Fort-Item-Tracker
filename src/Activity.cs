namespace FortItemTracker;

/// <summary>What the Python version printed to its console: kept in memory for the activity window and in logs\tracker.log.</summary>
static class Activity
{
    const int Keep = 2000;
    static readonly object Sync = new();
    static readonly List<string> Lines = [];
    static string? file;

    /// <summary>Raised on whichever thread wrote the line.</summary>
    public static event Action<string>? Line;

    public static void Init(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            file = Path.Combine(folder, "tracker.log");
            if (File.Exists(file) && new FileInfo(file).Length > 1_000_000)
                File.Move(file, Path.Combine(folder, "tracker.old.log"), overwrite: true);
        }
        catch (IOException)
        {
            file = null;
        }
    }

    public static void Write(string text)
    {
        var parts = text.Replace("\r\n", "\n").Split('\n');
        lock (Sync)
        {
            Lines.AddRange(parts);
            if (Lines.Count > Keep) Lines.RemoveRange(0, Lines.Count - Keep);
            if (file != null)
            {
                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                try { File.AppendAllLines(file, parts.Select(p => $"{stamp}  {p}")); }
                catch (IOException) { }
            }
        }
        foreach (var p in parts) Line?.Invoke(p);
    }

    public static string[] Snapshot()
    {
        lock (Sync) return [.. Lines];
    }
}
