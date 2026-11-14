using System.Diagnostics;

namespace FortItemTracker;

/// <summary>Runs detection on screenshots (each whole image counts as the game window).</summary>
static class TestMode
{
    public static Image32 CropRoi(Image32 img, double[] roi) => img.Crop(
        (int)(img.Width * roi[0]), (int)(img.Height * roi[1]), (int)(img.Width * roi[2]), (int)(img.Height * roi[3]));

    public static int Run(Config cfg, string root, IReadOnlyList<string> paths, bool verbose, Action<string> print)
    {
        var ocr = new WindowsOcr(cfg.OcrLanguage);
        var icons = Tracker.MakeIcons(cfg, root, print);
        var reader = new BannerReader(ocr, cfg.Anchor, icons);
        var namer = new Namer(Path.Combine(root, "items.txt"), cfg.SnapScore);
        var gate = new Gate();
        namer.Reload();
        foreach (var path in paths)
        {
            string name = Path.GetFileName(path);
            var img = ImageIO.Load(path);
            if (img == null)
            {
                print($"{name}: can't read image");
                continue;
            }
            var roi = CropRoi(img, cfg.Roi);
            var sw = Stopwatch.StartNew();
            double score = gate.Score(roi, cfg.GateHue[0], cfg.GateHue[1]);
            double t1 = sw.Elapsed.TotalMilliseconds;
            var read = reader.Read(roi, out var lines);
            double t2 = sw.Elapsed.TotalMilliseconds;
            string verdict = score >= cfg.GateThreshold ? "pass" : "below threshold";
            print($"{name}: pre-check {score:0.0000} ({verdict}, {t1:0.00} ms), read {t2 - t1:0.0} ms");
            if (read == null)
            {
                print("   no banner");
            }
            else
            {
                if (read.Icon is { Confident: true } m)
                    print($"   FOUND: {m.Name}  (icon {m.Score:0.00}, next best {m.RunnerUp:0.00})");
                else if (read.Icon is IconMatch u)
                    print(icons.Count == 0 ? "   icon not recognised (no reference images)"
                        : $"   icon not recognised (best {u.Name} {u.Score:0.00}, next {u.RunnerUp:0.00})");
                if (read.Name == null)
                {
                    print("   banner text unreadable");
                }
                else
                {
                    var (item, match) = namer.Resolve(read.Name);
                    string known = match != null ? $"matches items.txt ({match})" : "not in items.txt";
                    print($"   banner text: {item}  [OCR '{read.Name}', {known}]");
                }
            }
            if (verbose)
                foreach (var ln in lines)
                    print($"      ocr line: '{ln.Text}' at ({ln.X0:0},{ln.Y0:0})-({ln.X1:0},{ln.Y1:0})");
        }
        return 0;
    }
}
