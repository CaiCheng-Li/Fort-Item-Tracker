using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FortItemTracker;

sealed class FoundItem
{
    [JsonPropertyName("item")] public string Item { get; set; } = "";
    [JsonPropertyName("found_at")] public string FoundAt { get; set; } = "";
    [JsonPropertyName("t")] public double T { get; set; }  // master timer when it was found
}

/// <summary>logs/sessions/&lt;id&gt;.json, the same format the Python version wrote.</summary>
sealed class SavedSession
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("started")] public double Started { get; set; }
    [JsonPropertyName("elapsed")] public double Elapsed { get; set; }
    [JsonPropertyName("ended")] public bool Ended { get; set; }
    [JsonPropertyName("saved_at")] public double SavedAt { get; set; }
    [JsonPropertyName("runs")] public List<double[]> Runs { get; set; } = [];
    [JsonPropertyName("items")] public List<FoundItem> Items { get; set; } = [];
}

sealed class Session(string id, double wallStart, double monoStart, double prior = 0)
{
    public readonly string Id = id;
    public readonly double WallStart = wallStart;  // when the session was first started
    public readonly double MonoStart = monoStart;  // Clock.Mono when this run of it started
    public readonly double Prior = prior;          // master-timer seconds from earlier runs (a resumed session)
    public List<FoundItem> Items = [];
    public List<double[]> Runs = [];               // [start, end] wall times of each run

    /// <summary>Master timer value at a Clock.Mono moment of this run.</summary>
    public double At(double mono) => Prior + mono - MonoStart;

    public double Elapsed() => At(Clock.Mono);

    public string Summary() => string.Join("; ", Items
        .GroupBy(i => i.Item)
        .Select(g => (Name: g.Key, N: g.Count()))
        .OrderByDescending(x => x.N)
        .Select(x => $"{x.Name} x{x.N}"));
}

/// <summary>Saved sessions plus loot.csv and sessions.csv.</summary>
sealed class LootLog
{
    static readonly string[] LootHeader =
        ["session_id", "found_at", "session_time", "item", "banner_text", "ocr_text", "match_score", "icon_score", "reads"];
    static readonly string[] SessionHeader = ["session_id", "start", "end", "duration", "items_found", "summary"];
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly Encoding Utf8 = new UTF8Encoding(false);

    public readonly string Folder, LootCsv, SessionsCsv, SavedDir;

    public LootLog(string folder)
    {
        Folder = folder;
        LootCsv = Path.Combine(folder, "loot.csv");
        SessionsCsv = Path.Combine(folder, "sessions.csv");
        SavedDir = Path.Combine(folder, "sessions");
        Directory.CreateDirectory(SavedDir);
    }

    // ---- CSV ------------------------------------------------------------------------------

    static void KeepIfOldFormat(string path, string[] header)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return;
        string first;
        using (var reader = new StreamReader(path, Encoding.UTF8))
            first = reader.ReadLine() ?? "";
        var old = Csv.Parse(first).FirstOrDefault() ?? [];
        if (!old.SequenceEqual(header))  // columns changed: keep the old file aside
        {
            string name = $"{Path.GetFileNameWithoutExtension(path)}_old_{(long)Clock.Wall}{Path.GetExtension(path)}";
            File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, name));
        }
    }

    static void Append(string path, string[] header, string[] row)
    {
        KeepIfOldFormat(path, header);
        bool isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
        var text = new StringBuilder();
        if (isNew) text.Append('﻿').Append(Csv.Line(header));  // BOM so Excel reads it as UTF-8
        text.Append(Csv.Line(row));
        File.AppendAllText(path, text.ToString(), Utf8);
    }

    /// <summary>One row per session in sessions.csv, replaced when a resumed session ends again.</summary>
    void UpsertSessionRow(Session s)
    {
        KeepIfOldFormat(SessionsCsv, SessionHeader);
        var rows = File.Exists(SessionsCsv)
            ? Csv.Parse(File.ReadAllText(SessionsCsv, Encoding.UTF8).TrimStart('﻿')).Skip(1).Where(r => r.Length > 0 && r[0] != s.Id).ToList()
            : [];
        double end = s.Runs.Count > 0 ? s.Runs[^1][1] : Clock.Wall;
        rows.Add([
            s.Id, Fmt.ClockTime(s.WallStart), Fmt.ClockTime(end), Fmt.Duration(s.Elapsed()), s.Items.Count.ToString(), s.Summary(),
        ]);
        var text = new StringBuilder("﻿").Append(Csv.Line(SessionHeader));
        foreach (var r in rows) text.Append(Csv.Line(r));
        AtomicWrite(SessionsCsv, text.ToString());
    }

    static void AtomicWrite(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, Utf8);
        File.Move(tmp, path, overwrite: true);  // a crash never leaves a half-written file
    }

    // ---- saved session state ----------------------------------------------------------------

    string StatePath(string id) => Path.Combine(SavedDir, id + ".json");

    public void Save(Session s, bool ended = false)
    {
        double now = Clock.Wall;
        if (s.Runs.Count > 0) s.Runs[^1][1] = now;
        var state = new SavedSession
        {
            Id = s.Id, Started = s.WallStart, Elapsed = Math.Round(s.Elapsed(), 1), Ended = ended, SavedAt = now,
            Runs = s.Runs, Items = s.Items,
        };
        AtomicWrite(StatePath(s.Id), JsonSerializer.Serialize(state, Json));
    }

    /// <summary>Saved sessions, most recently saved first.</summary>
    public List<SavedSession> SavedSessions()
    {
        var list = new List<SavedSession>();
        foreach (var path in Directory.GetFiles(SavedDir, "*.json"))
        {
            try
            {
                var s = JsonSerializer.Deserialize<SavedSession>(File.ReadAllText(path));
                if (s != null && s.Id.Length > 0) list.Add(s);
            }
            catch (Exception e) when (e is IOException or JsonException) { }
        }
        return list.OrderByDescending(s => s.SavedAt).ToList();
    }

    // ---- session lifecycle ------------------------------------------------------------------

    public Session StartSession()
    {
        double wall = Clock.Wall;
        string baseId = DateTime.Now.ToString("yyyyMMdd-HHmmss"), id = baseId;
        for (int n = 2; File.Exists(StatePath(id)); n++) id = $"{baseId}-{n}";  // two sessions in the same second
        var s = new Session(id, wall, Clock.Mono);
        s.Runs.Add([wall, wall]);
        Save(s);
        return s;
    }

    /// <summary>Continues a saved session: its master timer carries on from where it stopped.</summary>
    public Session ResumeSession(string id)
    {
        var st = SavedSessions().FirstOrDefault(s => s.Id == id) ?? throw new KeyNotFoundException($"no saved session {id}");
        double wall = Clock.Wall;
        var s = new Session(st.Id, st.Started, Clock.Mono, st.Elapsed) { Items = st.Items, Runs = st.Runs };
        s.Runs.Add([wall, wall]);
        Save(s);
        return s;
    }

    public void Add(Session s, LootEvent ev)
    {
        double t = s.At(ev.MonoTime);
        s.Items.Add(new FoundItem { Item = ev.Item, FoundAt = Fmt.ClockTime(ev.WallTime), T = Math.Round(t, 1) });
        Append(LootCsv, LootHeader, [
            s.Id, Fmt.ClockTime(ev.WallTime), Fmt.Duration(t), ev.Item, ev.BannerText, ev.OcrText,
            ev.Score?.ToString() ?? "", ev.IconScore is double d ? Fmt.Num(d) : "", ev.Reads.ToString(),
        ]);
        Save(s);
    }

    public string EndSession(Session s)
    {
        Save(s, ended: true);
        UpsertSessionRow(s);
        return s.Summary();
    }
}

static class Csv
{
    public static string Line(IEnumerable<string> fields) => string.Join(",", fields.Select(Escape)) + "\r\n";

    static string Escape(string f) =>
        f.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + f.Replace("\"", "\"\"") + "\"" : f;

    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; any = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); any = true; break;
                case '\r': break;
                case '\n':
                    if (any || field.Length > 0) { row.Add(field.ToString()); rows.Add([.. row]); }
                    row.Clear(); field.Clear(); any = false;
                    break;
                default: field.Append(c); any = true; break;
            }
        }
        if (any || field.Length > 0) { row.Add(field.ToString()); rows.Add([.. row]); }
        return rows;
    }
}
