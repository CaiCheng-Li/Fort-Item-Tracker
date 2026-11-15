using System.Text.RegularExpressions;

namespace FortItemTracker;

/// <summary>Finding the "YOU FOUND &lt;item&gt;" banner and turning the frames it is on screen for into one event.</summary>
sealed class Gate
{
    public const int Height = 120;  // the pre-check runs on the search area scaled to this height

    // Kept between calls: the pre-check runs several times a second on same-sized images.
    (int SrcW, int SrcH, int W, int H) cachedFor;
    (int Start, float[] W)[] wx = [], wy = [];
    float[] tmp = [];
    Image32? small;

    /// <summary>
    /// Share of bright gold-yellow pixels (the banner frame and item name). Banners score 0.02+,
    /// scenery around 0.0005, so OCR only runs on frames that probably contain one. Hue is 0-180.
    /// </summary>
    public double Score(Image32 roi, int hueLo, int hueHi)
    {
        if (roi.Width == 0 || roi.Height == 0) return 0;
        double scale = Math.Min(1.0, Height / (double)roi.Height);
        if (scale < 1)
        {
            int w = Math.Max(1, (int)Math.Round(roi.Width * scale)), h = Math.Max(1, (int)Math.Round(roi.Height * scale));
            if (cachedFor != (roi.Width, roi.Height, w, h))
            {
                cachedFor = (roi.Width, roi.Height, w, h);
                (wx, wy) = (Image32.Weights(roi.Width, w), Image32.Weights(roi.Height, h));
                tmp = new float[roi.Height * w * 4];
                small = new Image32(w, h);
            }
            roi.ResizeInto(small!, wx, wy, tmp);
        }
        return GoldShare(scale < 1 ? small! : roi, hueLo, hueHi);
    }

    static double GoldShare(Image32 small, int hueLo, int hueHi)
    {
        int hits = 0;
        var p = small.Px;
        for (int i = 0; i < p.Length; i += 4)
        {
            Hsv.From(p[i], p[i + 1], p[i + 2], out int h, out int s, out int v);
            if (v >= 200 && s >= 100 && h >= hueLo && h <= hueHi) hits++;
        }
        return hits / (double)(small.Width * small.Height);
    }
}

/// <summary>GoldCard: the card box came from the gold card itself, not from the text height.</summary>
sealed record BannerRead(string? Name, IconMatch? Icon, Image32? Card, bool GoldCard = false);

sealed class BannerReader(WindowsOcr ocr, string anchor, IconLibrary? icons)
{
    readonly string anchor = Fuzz.Letters(anchor);

    double AnchorScore(string text)
    {
        string key = Fuzz.Letters(text);
        if (key.Length < 4) return 0;
        return key.Length <= anchor.Length + 2 ? Fuzz.Ratio(anchor, key) : Fuzz.PartialRatio(anchor, key);
    }

    // Windows OCR can miss the banner text at one size and read it at another (and can't read text much
    // under 12 px), so after a miss the next frame is read at the next size; the size that last found a
    // banner is kept. Sizes other than 100% are only used when OCR gets 240-1000 px of height.
    public static readonly double[] OcrScales = [1.0, 0.8, 0.6, 1.5, 2.0];
    const int MinOcrHeight = 240, MaxOcrHeight = 1000;
    int scale;

    public double OcrScale => OcrScales[scale];

    public void ResetScale() => scale = 0;

    /// <summary>Null when no banner is visible. <paramref name="lines"/> is everything OCR read, in roi pixels.</summary>
    public BannerRead? Read(Image32 roi, out List<OcrLine> lines)
    {
        double f = OcrScales[scale];
        lines = f == 1 ? ocr.Read(roi)
            : ocr.Read(roi.Resize((int)Math.Round(roi.Width * f), (int)Math.Round(roi.Height * f)))
                .Select(ln => new OcrLine(ln.Text, ln.X0 / f, ln.Y0 / f, ln.X1 / f, ln.Y1 / f)).ToList();
        OcrLine? found = null;
        double bestScore = 75 - 1e-9;
        foreach (var ln in lines)
        {
            double s = AnchorScore(ln.Text);
            if (s > bestScore) (found, bestScore) = (ln, s);
        }
        if (found == null)
        {
            do scale = (scale + 1) % OcrScales.Length;
            while (scale > 0 && roi.Height * OcrScales[scale] is < MinOcrHeight or > MaxOcrHeight);
            return null;
        }
        var a = found;
        double h = a.Y1 - a.Y0, w = a.X1 - a.X0;
        var below = lines
            .Where(ln => !ReferenceEquals(ln, a)
                         && a.Y1 - 0.3 * h <= ln.Y0 && ln.Y0 <= a.Y1 + 2.5 * h
                         && Math.Abs(ln.Cx - a.Cx) <= 0.6 * Math.Max(w, ln.X1 - ln.X0))
            .OrderBy(ln => ln.Y0);
        string name = string.Join(" ", below.Select(ln => ln.Text)).Trim();
        var card = IconLibrary.CardCrop(roi, a, out bool gold);
        var icon = icons?.Identify(card);
        return new BannerRead(Fuzz.Letters(name).Length >= 2 ? name : null, icon, card, gold);
    }
}

/// <summary>Snaps OCR text to a known item name from items.txt when it is close enough.</summary>
sealed class Namer(string itemsFile, int snapScore)
{
    List<string> known = [];

    public void Reload()
    {
        known = File.Exists(itemsFile)
            ? File.ReadAllLines(itemsFile).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList()
            : [];
    }

    /// <summary>(display name, match score); the score is null when the name isn't in items.txt.</summary>
    public (string Name, int? Score) Resolve(string raw)
    {
        if (known.Count > 0)
        {
            string q = Fuzz.Letters(raw);
            string? hit = null;
            double best = -1;
            foreach (var k in known)
            {
                double s = Fuzz.Ratio(q, Fuzz.Letters(k));
                if (s > best) (hit, best) = (k, s);
            }
            if (hit != null && best >= snapScore) return (hit, (int)best);
        }
        return (CleanName(raw), null);
    }

    public static string CleanName(string raw)
    {
        string text = Regex.Replace(Regex.Replace(raw, @"[^A-Za-z0-9 '&.:-]", " "), @"\s+", " ").Trim();
        return string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }
}

sealed record LootEvent(
    double WallTime,      // when the banner first appeared
    double MonoTime,      // Clock.Mono at the same moment
    string Item,          // the reward: from the icon when it is recognised, else from the text
    string OcrText,
    int? Score,           // how well the text matched items.txt; null if it isn't listed
    int Reads,
    string BannerText = "",
    double? IconScore = null,
    string Learned = "",     // path of a newly saved reference icon
    IconMatch? Closest = null,  // best icon guess for a shared banner text (Sprite Gizmo) it wasn't sure of
    Image32? Card = null)    // the item card, saved to logs\cards
{
    public string Describe()
    {
        var notes = new List<string>();
        if (BannerText.Length > 0 && BannerText != Item) notes.Add($"banner: {BannerText}");
        if (IconScore is double s) notes.Add($"icon {s:0.00}");
        else if (Closest != null) notes.Add(Closest.Name.Length > 0 ? $"icon unsure, closest: {Closest.Name} {Closest.Raw:0.00}" : "icon unsure");
        else if (Score == null && Reads > 0) notes.Add("not in items.txt");
        string text = notes.Count > 0 ? $"{Item} ({string.Join(", ", notes)})" : Item;
        if (Learned.Length > 0) text += $"  [new icon saved: {Path.GetFileName(Learned)}]";
        return text;
    }
}

/// <summary>
/// Collapses the frames a banner stays on screen for into one LootEvent. A banner counts once, from
/// when it is first read until it leaves the screen; back-to-back banners (even of the same item)
/// disappear between each other, and that gap starts a new event.
/// </summary>
sealed class BannerTracker(Namer namer, double gapS, IconLibrary? icons, bool learn, IEnumerable<string> generic)
{
    sealed class Active(double wall, double mono)
    {
        public readonly double Wall = wall, Mono = mono;
        public double LastSeen = mono;
        public readonly List<string> Reads = [];
        public readonly List<IconMatch> Icons = [];
        public Image32? Card;          // the best-matching card, preferring frames where the text was read too
        public (bool Named, double Score) CardKey = (false, double.MinValue);

        public string? IconConsensus()
        {
            var counts = new List<(string Name, int N)>();
            foreach (var m in Icons.Where(m => m.Confident))
            {
                int i = counts.FindIndex(c => c.Name == m.Name);
                if (i < 0) counts.Add((m.Name, 1));
                else counts[i] = (m.Name, counts[i].N + 1);
            }
            return counts.Count == 0 ? null : counts.OrderByDescending(c => c.N).First().Name;
        }
    }

    readonly bool learn = learn && icons != null;
    readonly string[] generic = generic.Select(Fuzz.Letters).ToArray();
    Active? active;

    public bool IsGeneric(string name) => generic.Any(g => Fuzz.Ratio(Fuzz.Letters(name), g) >= 80);

    void Add(double wall, double mono, BannerRead read)
    {
        var a = active ??= new Active(wall, mono);
        a.LastSeen = mono;
        if (read.Name != null) a.Reads.Add(read.Name);
        if (read.Icon != null) a.Icons.Add(read.Icon);
        var key = (read.Name != null, read.Icon?.Score ?? 0);
        if (read.Card != null && key.CompareTo(a.CardKey) > 0) (a.Card, a.CardKey) = (read.Card, key);
    }

    /// <summary>A banner is on screen and hasn't been logged yet.</summary>
    public bool Busy => active != null;

    /// <summary>
    /// <paramref name="visible"/>: false when the pre-check sees no banner at all (it has left the screen),
    /// true when it passes but OCR may have missed the text, null when no frame was checked.
    /// </summary>
    public List<LootEvent> Update(double wall, double mono, BannerRead? read, bool? visible)
    {
        if (read != null)
        {
            Add(wall, mono, read);
            return [];
        }
        if (active == null) return [];
        // Gone: the banner's gold left the screen. The timeout covers something else gold
        // (an open chest) keeping the pre-check passing after the banner itself has gone.
        if (visible == false || mono - active.LastSeen > gapS) return [Finish()];
        return [];
    }

    /// <summary>No new frame came in, so the banner on screen hasn't changed: keep it from timing out.</summary>
    public void Touch(double mono)
    {
        if (active != null) active.LastSeen = mono;
    }

    public List<LootEvent> Flush() => active != null ? [Finish()] : [];

    LootEvent Finish()
    {
        var a = active!;
        active = null;
        string? iconItem = a.IconConsensus();
        double? iconScore = iconItem != null ? Math.Round(a.Icons.Where(m => m.Name == iconItem).Max(m => m.Raw), 2) : null;
        if (a.Reads.Count == 0)
            return new LootEvent(a.Wall, a.Mono, iconItem ?? "(unreadable)", "", null, 0, "", iconScore, Card: a.Card);

        var resolved = a.Reads.Select(r => (Res: namer.Resolve(r), Raw: r)).ToList();
        var names = resolved.Select(x => x.Res.Name).Distinct().ToList();  // first-seen order
        string top = names[0];
        (int, int) Key(string n) => (resolved.Count(x => x.Res.Name == n), resolved.Where(x => x.Res.Name == n).Max(x => x.Res.Score ?? 0));
        foreach (var n in names.Skip(1))
            if (Key(n).CompareTo(Key(top)) > 0) top = n;
        var first = resolved.First(x => x.Res.Name == top);

        string item = iconItem ?? top, learned = "";
        IconMatch? closest = null;
        if (iconItem == null && IsGeneric(top))
        {
            // Shared text, unknown icon: most likely a known item that looks different on this screen, so
            // it isn't learned (a learned guess would take over the other items with that text).
            closest = a.Icons.MaxBy(m => m.Score) ?? new IconMatch("", 0, 0);
        }
        else if (iconItem == null && learn && a.Card is { Width: > 0, Height: > 0 } && a.Icons.Count >= 2)
        {
            // No frame matched a reference image: save the icon so the next one is named by it.
            learned = icons!.Learn(item, a.Card);
        }
        return new LootEvent(a.Wall, a.Mono, item, first.Raw, first.Res.Score, a.Reads.Count, top, iconScore, learned, closest, a.Card);
    }
}
