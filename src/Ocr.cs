using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace FortItemTracker;

sealed record OcrLine(string Text, double X0, double Y0, double X1, double Y1)
{
    public double Cx => (X0 + X1) / 2;
}

/// <summary>The OCR built into Windows (Windows.Media.Ocr).</summary>
sealed class WindowsOcr
{
    readonly OcrEngine engine;

    public WindowsOcr(string language)
    {
        var lang = new Language(language);
        if (!OcrEngine.IsLanguageSupported(lang))
            throw new InvalidOperationException(
                $"Windows OCR has no \"{language}\" language pack. In an admin PowerShell run:\n" +
                "  Add-WindowsCapability -Online -Name \"Language.OCR~~~en-US~0.0.1.0\"");
        engine = OcrEngine.TryCreateFromLanguage(lang)
                 ?? throw new InvalidOperationException($"Windows OCR couldn't start for \"{language}\".");
    }

    public List<OcrLine> Read(Image32 img)
    {
        var px = (byte[])img.Px.Clone();
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;  // captured alpha can be anything
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(px.AsBuffer(), BitmapPixelFormat.Bgra8, img.Width, img.Height,
            BitmapAlphaMode.Premultiplied);
        var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var w in line.Words)
            {
                var b = w.BoundingRect;
                x0 = Math.Min(x0, b.X); y0 = Math.Min(y0, b.Y);
                x1 = Math.Max(x1, b.X + b.Width); y1 = Math.Max(y1, b.Y + b.Height);
            }
            lines.Add(new OcrLine(line.Text, x0, y0, x1, y1));
        }
        return lines;
    }
}

/// <summary>The rapidfuzz scores the Python version used: ratio and partial_ratio, 0-100.</summary>
static class Fuzz
{
    public static string Letters(string text)
    {
        var chars = text.ToUpperInvariant().Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9').ToArray();
        return new string(chars);
    }

    static int Lcs(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    public static double Ratio(string a, string b)
    {
        int n = a.Length + b.Length;
        return n == 0 ? 100 : 200.0 * Lcs(a, b) / n;
    }

    /// <summary>Best ratio of the shorter string against any same-length window of the longer one.</summary>
    public static double PartialRatio(string a, string b)
    {
        if (a.Length > b.Length) (a, b) = (b, a);
        if (a.Length == 0) return b.Length == 0 ? 100 : 0;
        double best = 0;
        for (int i = -(a.Length - 1); i < b.Length && best < 100; i++)
        {
            int s = Math.Max(0, i), e = Math.Min(b.Length, i + a.Length);
            best = Math.Max(best, Ratio(a, b[s..e]));
        }
        return best;
    }
}
