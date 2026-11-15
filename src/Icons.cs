using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace FortItemTracker;

/// <summary>Score and RunnerUp are ranking scores: a learned icon's template score minus LearnedPenalty.</summary>
sealed record IconMatch(string Name, double Score, double RunnerUp, bool Learned = false)
{
    public const double Accept = 0.6;   // template score needed to trust a match
    public const double Margin = 0.08;  // ...and its lead over the next-best item
    // A learned icon is cut from the player's own screen, so it also fits other items' cards there: the
    // same item scores 0.97+, a different gizmo up to 0.68. It needs 0.85 to count like a shipped 0.6.
    public const double LearnedPenalty = 0.25;

    public bool Confident => Score >= Accept && Score - RunnerUp >= Margin;

    /// <summary>The template score itself (0-1), for logs.</summary>
    public double Raw => Learned ? Score + LearnedPenalty : Score;
}

/// <summary>
/// Names the reward from the icon on the banner card by multi-scale template matching. Reference
/// images come from the icon folder (named after the item) and icon_folder/learned, where icons of
/// rewards that had no image are saved.
/// </summary>
sealed class IconLibrary
{
    public const int Card = 80;  // the banner card is matched at this size (px)
    static readonly string[] ImageTypes = [".png", ".webp", ".jpg", ".jpeg"];
    static readonly double[] Scales = Enumerable.Range(0, 10).Select(i => i == 9 ? 0.95 : i * ((0.95 - 0.5) / 9) + 0.5).ToArray();
    static readonly float[] DefaultGold = [40, 190, 245];  // BGR, when the card colour can't be measured

    sealed record Template(int W, int H, float[] Premul, float[] Alpha);  // Premul: B,G,R interleaved, 0-255

    public readonly string Folder, LearnedDir;
    readonly List<(string Name, List<(Template[] Set, bool Learned)> Sets)> refs = [];
    readonly Dictionary<string, string> paths = [];

    /// <summary>Image files in the icon folders that Windows couldn't decode (e.g. .webp without its codec).</summary>
    public readonly List<string> Unreadable = [];

    public IconLibrary(string folder)
    {
        Folder = folder;
        LearnedDir = Path.Combine(folder, "learned");
        Reload();
    }

    public int Count => refs.Count;

    /// <summary>Items with a learned icon (some may also have a shipped one).</summary>
    public int LearnedCount => refs.Count(r => r.Sets.Any(s => s.Learned));

    public void Reload()
    {
        refs.Clear();
        paths.Clear();
        Unreadable.Clear();
        if (Directory.Exists(Folder))
            foreach (var path in Directory.GetFiles(Folder).Order(StringComparer.OrdinalIgnoreCase))
                Load(path, NameFromFile(path), false);
        if (Directory.Exists(LearnedDir))
            foreach (var path in Directory.GetFiles(LearnedDir).Order(StringComparer.OrdinalIgnoreCase))
                Load(path, Path.GetFileNameWithoutExtension(path), true);
    }

    /// <summary>Lucky_Locator_-_Item_-_Fortnite.webp → Lucky Locator; keeps capitals like A$AP.</summary>
    public static string NameFromFile(string path)
    {
        var words = Regex.Replace(Path.GetFileNameWithoutExtension(path), @"[_\-]+", " ")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.ToLowerInvariant() is not ("item" or "fortnite"))
            .Select(w => w.Any(char.IsLetter) && !w.Any(char.IsUpper) ? char.ToUpperInvariant(w[0]) + w[1..] : w);
        return string.Join(" ", words);
    }

    public static string SafeFilename(string name)
    {
        string s = Regex.Replace(name, @"[\\/:*?""<>|]+", " ").Trim();
        return s.Length > 0 ? s : "unnamed";
    }

    /// <summary>
    /// The item card above the "YOU FOUND" line: the gold card itself when it can be found
    /// (<paramref name="found"/>), else a box sized from the text height.
    /// </summary>
    public static Image32 CardCrop(Image32 roi, OcrLine anchor, out bool found)
    {
        int R(double v) => Math.Max(0, (int)Math.Round(v));
        var card = CardFinder.Find(roi, anchor);
        found = card != null;
        if (card is { } c)
            return roi.Crop(R(c.Cx - c.Side / 2), R(c.Cy - c.Side / 2), R(c.Cx + c.Side / 2), R(c.Cy + c.Side / 2));
        double h = anchor.Y1 - anchor.Y0;
        return roi.Crop(R(anchor.Cx - 3 * h), R(anchor.Y0 - 6.5 * h), R(anchor.Cx + 3 * h), R(anchor.Y0 - 0.5 * h));
    }

    /// <summary>The middle of a banner card, opaque: what a learned icon is made of.</summary>
    public static Image32 IconCrop(Image32 card)
    {
        int h = card.Height, w = card.Width;
        var inner = card.Crop((int)(w * 0.15), (int)(h * 0.15), (int)(w * 0.85), (int)(h * 0.85));
        inner.MakeOpaque();
        return inner;
    }

    void Load(string path, string name, bool learned)
    {
        if (!ImageTypes.Contains(Path.GetExtension(path).ToLowerInvariant())) return;
        var img = ImageIO.Load(path);
        if (img == null || img.Width == 0 || img.Height == 0)
        {
            Unreadable.Add(Path.GetFileName(path));
            return;
        }
        var set = new Template[Scales.Length];
        for (int i = 0; i < Scales.Length; i++)
        {
            int side = Math.Max(8, (int)(Card * Scales[i]));
            double f = side / (double)Math.Max(img.Width, img.Height);
            var t = img.Resize(Math.Max(4, (int)(img.Width * f)), Math.Max(4, (int)(img.Height * f)));
            int n = t.Width * t.Height;
            var premul = new float[n * 3];
            var alpha = new float[n];
            for (int p = 0; p < n; p++)
            {
                premul[p * 3] = t.Px[p * 4];
                premul[p * 3 + 1] = t.Px[p * 4 + 1];
                premul[p * 3 + 2] = t.Px[p * 4 + 2];
                alpha[p] = t.Px[p * 4 + 3] / 255f;
            }
            set[i] = new Template(t.Width, t.Height, premul, alpha);
        }
        int idx = refs.FindIndex(r => r.Name == name);
        if (idx < 0) refs.Add((name, [(set, learned)]));
        else refs[idx].Sets.Add((set, learned));
        paths.TryAdd(name, path);
    }

    /// <summary>The image file for an item, if it has one (the overlay shows it).</summary>
    public string? PathFor(string name) => paths.GetValueOrDefault(name);

    public IconMatch? Identify(Image32 cardImg)
    {
        if (cardImg.Width < 16 || cardImg.Height < 16) return null;
        if (refs.Count == 0) return new IconMatch("", 0, 0);  // nothing to compare with; the icon can still be learned
        var card = cardImg.Resize(Card, Card);
        var gold = CardGold(card);
        var pixels = new float[Card * Card * 3];
        for (int p = 0; p < Card * Card; p++)
        {
            pixels[p * 3] = card.Px[p * 4];
            pixels[p * 3 + 1] = card.Px[p * 4 + 1];
            pixels[p * 3 + 2] = card.Px[p * 4 + 2];
        }
        var sums = new Integrals(pixels);
        var scores = new List<(string Name, double Score, bool Learned)>();
        foreach (var (name, sets) in refs)
        {
            (double Score, bool Learned) best = (-1, false);
            foreach (var (set, learned) in sets)
                foreach (var t in set)
                {
                    double s = Match(pixels, sums, t, gold) - (learned ? IconMatch.LearnedPenalty : 0);
                    if (s > best.Score) best = (s, learned);
                }
            scores.Add((name, best.Score, best.Learned));
        }
        var ranked = scores.OrderByDescending(s => s.Score).ToList();  // stable: ties keep load order
        return new IconMatch(ranked[0].Name, ranked[0].Score, ranked.Count > 1 ? ranked[1].Score : 0, ranked[0].Learned);
    }

    static float[] CardGold(Image32 card)
    {
        var chans = new[] { new List<byte>(), new List<byte>(), new List<byte>() };
        for (int i = 0; i < card.Px.Length; i += 4)
        {
            Hsv.From(card.Px[i], card.Px[i + 1], card.Px[i + 2], out int h, out int s, out int v);
            if (h >= 15 && h <= 35 && s > 100 && v > 150)
                for (int c = 0; c < 3; c++) chans[c].Add(card.Px[i + c]);
        }
        if (chans[0].Count <= 20) return DefaultGold;
        return chans.Select(Median).ToArray();
    }

    static float Median(List<byte> values)
    {
        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2f;
    }

    /// <summary>Summed-area tables of the card, for the window means and variances in Match.</summary>
    sealed class Integrals
    {
        const int S = Card + 1;
        readonly double[] sum0 = new double[S * S], sum1 = new double[S * S], sum2 = new double[S * S], sq = new double[S * S];

        public Integrals(float[] px)
        {
            for (int y = 0; y < Card; y++)
                for (int x = 0; x < Card; x++)
                {
                    int p = (y * Card + x) * 3, i = (y + 1) * S + x + 1;
                    double b = px[p], g = px[p + 1], r = px[p + 2];
                    sum0[i] = b + sum0[i - 1] + sum0[i - S] - sum0[i - S - 1];
                    sum1[i] = g + sum1[i - 1] + sum1[i - S] - sum1[i - S - 1];
                    sum2[i] = r + sum2[i - 1] + sum2[i - S] - sum2[i - S - 1];
                    sq[i] = b * b + g * g + r * r + sq[i - 1] + sq[i - S] - sq[i - S - 1];
                }
        }

        static double Box(double[] a, int x, int y, int w, int h) =>
            a[(y + h) * S + x + w] - a[y * S + x + w] - a[(y + h) * S + x] + a[y * S + x];

        /// <summary>Sum of squared deviations from the per-channel mean in a window.</summary>
        public double Variance(int x, int y, int w, int h)
        {
            double n = w * h, b = Box(sum0, x, y, w, h), g = Box(sum1, x, y, w, h), r = Box(sum2, x, y, w, h);
            return Box(sq, x, y, w, h) - (b * b + g * g + r * r) / n;
        }
    }

    /// <summary>Best normalised correlation coefficient (OpenCV TM_CCOEFF_NORMED) of the template laid on gold.</summary>
    static double Match(float[] card, Integrals sums, Template t, float[] gold)
    {
        int tw = t.W, th = t.H, n = tw * th;
        if (tw > Card || th > Card) return -1;
        var tz = new float[n * 3];
        Span<double> mean = stackalloc double[3];
        for (int i = 0; i < n; i++)
        {
            float a = 1 - t.Alpha[i];
            for (int c = 0; c < 3; c++)
            {
                float v = MathF.Floor(Math.Clamp(t.Premul[i * 3 + c] + gold[c] * a, 0, 255));
                tz[i * 3 + c] = v;
                mean[c] += v;
            }
        }
        for (int c = 0; c < 3; c++) mean[c] /= n;
        double tss = 0;
        for (int i = 0; i < n * 3; i++)
        {
            tz[i] -= (float)mean[i % 3];
            tss += tz[i] * tz[i];
        }
        if (tss < 1e-6) return 0;

        double best = -1;
        int row = tw * 3;
        for (int y = 0; y + th <= Card; y++)
            for (int x = 0; x + tw <= Card; x++)
            {
                double num = 0;
                for (int r = 0; r < th; r++)
                    num += Dot(tz.AsSpan(r * row, row), card.AsSpan(((y + r) * Card + x) * 3, row));
                double denom = Math.Sqrt(Math.Max(0, sums.Variance(x, y, tw, th)) * tss);
                double score = Math.Abs(num) < denom ? num / denom
                    : Math.Abs(num) < denom * 1.125 ? Math.Sign(num)
                    : 0;
                if (score > best) best = score;
            }
        return best;
    }

    static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int vn = Vector<float>.Count, i = 0;
        var acc = Vector<float>.Zero;
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        for (int k = 0; k < va.Length; k++) acc += va[k] * vb[k];
        i = va.Length * vn;
        float sum = Vector.Dot(acc, Vector<float>.One);
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    /// <summary>Saves the centre of a banner card as the reference icon for <paramref name="name"/>.</summary>
    public string Learn(string name, Image32 card)
    {
        Directory.CreateDirectory(LearnedDir);
        string path = Path.Combine(LearnedDir, SafeFilename(name) + ".png");
        ImageIO.SavePng(IconCrop(card), path);
        Reload();
        return path;
    }
}
