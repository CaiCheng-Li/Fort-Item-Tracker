namespace FortItemTracker;

/// <summary>
/// Finds the gold item card above the "YOU FOUND" line from its own pixels, so the card box fits any
/// resolution, HUD scale or text size. The text only says where to look: the card is the gold area
/// right above it, around its centre.
/// </summary>
static class CardFinder
{
    // The crop is a little wider than the gold: the light frame around the card and items that stick out of it.
    const double CropPerGold = 1.07;
    const double Trim = 0.4;  // rows and columns with less gold than this share of the fullest one are edges or glow

    readonly record struct Box(int X0, int Y0, int X1, int Y1)
    {
        public int W => X1 - X0;
        public int H => Y1 - Y0;
        public int Size => Math.Max(W, H);

        /// <summary>
        /// <paramref name="p"/> lines up with this box: beside it, overlapping it vertically by half its own
        /// height, or above or below it, overlapping it horizontally by half its own width, within <paramref name="reach"/>.
        /// </summary>
        public bool LinesUp(Box p, double reach)
        {
            int overlapY = Math.Min(Y1, p.Y1) - Math.Max(Y0, p.Y0), overlapX = Math.Min(X1, p.X1) - Math.Max(X0, p.X0);
            return (overlapY >= 0.5 * p.H && -overlapX <= reach) || (overlapX >= 0.5 * p.W && -overlapY <= reach);
        }

        public Box Union(Box o) => new(Math.Min(X0, o.X0), Math.Min(Y0, o.Y0), Math.Max(X1, o.X1), Math.Max(Y1, o.Y1));
    }

    /// <summary>The square to crop around the card (centre and side, in roi pixels), or null if no gold card is there.</summary>
    public static (double Cx, double Cy, double Side)? Find(Image32 roi, OcrLine anchor)
    {
        // Everything above the text, as far either side of its centre as the text is from the top.
        int h = Math.Min(roi.Height, (int)anchor.Y0);
        int x0 = Math.Max(0, (int)(anchor.Cx - h)), w = Math.Min(roi.Width, (int)(anchor.Cx + h)) - x0;
        if (h < 16 || w < 16) return null;

        var gold = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0, i = (y * roi.Width + x0) * 4; x < w; x++, i += 4)
            {
                Hsv.From(roi.Px[i], roi.Px[i + 1], roi.Px[i + 2], out int hue, out int s, out int v);
                gold[y * w + x] = hue >= 15 && hue <= 35 && s > 100 && v > 150;  // as CardGold in Icons.cs
            }
        var (labels, parts) = Label(gold, w, h);

        // Seed: the biggest piece over the text's centre that ends just above it. The item drawn on the
        // card can cut its gold into pieces, so pieces that line up with it join it (the lightning
        // beside the text doesn't: it is below the card).
        double cx = anchor.Cx - x0;
        int seed = -1;
        for (int p = 0; p < parts.Count; p++)
        {
            var (area, b) = parts[p];
            if (area < 64 || Math.Max(0, Math.Max(b.X0 - cx, cx - b.X1)) > 0.5 * b.W || h - b.Y1 > 0.5 * b.H) continue;
            if (seed < 0 || area > parts[seed].Area) seed = p;
        }
        if (seed < 0) return null;
        var member = new bool[parts.Count];
        member[seed] = true;
        var box = parts[seed].Box;
        int total = parts[seed].Area;
        for (bool grew = true; grew;)
        {
            grew = false;
            for (int p = 0; p < parts.Count; p++)
            {
                if (member[p] || parts[p].Area < 0.05 * total || !box.LinesUp(parts[p].Box, 0.5 * box.Size)) continue;
                member[p] = grew = true;
                box = box.Union(parts[p].Box);
                total += parts[p].Area;
            }
        }

        // Columns first, then rows within those columns, so glow and the lightning beside the text drop out.
        var cols = new int[w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (labels[y * w + x] is int l and >= 0 && member[l]) cols[x]++;
        var (c0, c1) = Span(cols, Trim);
        var rows = new int[h];
        for (int y = 0; y < h; y++)
            for (int x = c0; x < c1; x++)
                if (labels[y * w + x] is int l and >= 0 && member[l]) rows[y]++;
        var (r0, r1) = Span(rows, Trim);

        int side = Math.Max(c1 - c0, r1 - r0);
        double th = anchor.Y1 - anchor.Y0;
        // A card is roughly square and somewhere between a little and a lot bigger than the text.
        if (side < 16 || r1 - r0 < 0.5 * (c1 - c0) || c1 - c0 < 0.5 * (r1 - r0) || side < 1.5 * th || side > 30 * th)
            return null;
        return (x0 + (c0 + c1) / 2.0, (r0 + r1) / 2.0, side * CropPerGold);
    }

    /// <summary>First and last index (end exclusive) holding at least <paramref name="share"/> of the maximum.</summary>
    static (int Start, int End) Span(int[] counts, double share)
    {
        double min = Math.Max(1, counts.Max() * share);
        int a = Array.FindIndex(counts, c => c >= min), b = Array.FindLastIndex(counts, c => c >= min);
        return a < 0 ? (0, 0) : (a, b + 1);
    }

    /// <summary>8-connected pieces of the mask: each pixel's piece index (-1 outside), and each piece's area and box.</summary>
    static (int[] Labels, List<(int Area, Box Box)> Parts) Label(bool[] mask, int w, int h)
    {
        var labels = new int[w * h];
        var parent = new List<int> { 0 };  // union-find over provisional labels; 0 = none
        int Root(int a)
        {
            while (parent[a] != a) a = parent[a] = parent[parent[a]];
            return a;
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!mask[i]) continue;
                int l = 0;
                // Neighbours already visited: left, and the three above.
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + (k == 0 ? -1 : k - 2), ny = k == 0 ? y : y - 1;
                    if (nx < 0 || nx >= w || ny < 0) continue;
                    int n = labels[ny * w + nx];
                    if (n == 0) continue;
                    if (l == 0) l = n;
                    else
                    {
                        int a = Root(l), b = Root(n);
                        if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
                    }
                }
                if (l == 0)
                {
                    l = parent.Count;
                    parent.Add(l);
                }
                labels[i] = l;
            }

        var index = new Dictionary<int, int>();
        var parts = new List<(int Area, Box Box)>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (labels[i] == 0) { labels[i] = -1; continue; }
                int r = Root(labels[i]);
                if (!index.TryGetValue(r, out int p))
                {
                    index[r] = p = parts.Count;
                    parts.Add((0, new Box(x, y, x + 1, y + 1)));
                }
                var (area, b) = parts[p];
                parts[p] = (area + 1, new Box(Math.Min(b.X0, x), Math.Min(b.Y0, y), Math.Max(b.X1, x + 1), Math.Max(b.Y1, y + 1)));
                labels[i] = p;
            }
        return (labels, parts);
    }
}
