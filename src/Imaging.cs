using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FortItemTracker;

/// <summary>A BGRA image, 4 bytes per pixel, rows packed with no padding.</summary>
sealed class Image32
{
    public readonly int Width, Height;
    public readonly byte[] Px;

    public Image32(int width, int height) : this(width, height, new byte[Math.Max(0, width) * Math.Max(0, height) * 4]) { }

    public Image32(int width, int height, byte[] px)
    {
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        Px = px;
    }

    /// <summary>Rows y0..y1 and columns x0..x1 (exclusive), clamped to the image like a numpy slice.</summary>
    public Image32 Crop(int x0, int y0, int x1, int y1)
    {
        x0 = Math.Clamp(x0, 0, Width); x1 = Math.Clamp(x1, x0, Width);
        y0 = Math.Clamp(y0, 0, Height); y1 = Math.Clamp(y1, y0, Height);
        var o = new Image32(x1 - x0, y1 - y0);
        for (int y = 0; y < o.Height; y++)
            System.Buffer.BlockCopy(Px, ((y0 + y) * Width + x0) * 4, o.Px, y * o.Width * 4, o.Width * 4);
        return o;
    }

    /// <summary>Area averaging when shrinking (like OpenCV's INTER_AREA), bilinear when growing.</summary>
    public Image32 Resize(int width, int height)
    {
        if (width == Width && height == Height) return this;
        var o = new Image32(width, height);
        ResizeInto(o, Weights(Width, width), Weights(Height, height), new float[Height * width * 4]);
        return o;
    }

    /// <summary>The resize itself, into <paramref name="o"/>, with weights and a scratch buffer the caller can keep.</summary>
    public void ResizeInto(Image32 o, (int Start, float[] W)[] wx, (int Start, float[] W)[] wy, float[] tmp)
    {
        int width = o.Width, height = o.Height;
        for (int y = 0; y < Height; y++)
        {
            int src = y * Width * 4, dst = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                var (start, ws) = wx[x];
                float b = 0, g = 0, r = 0, a = 0;
                for (int k = 0; k < ws.Length; k++)
                {
                    int p = src + (start + k) * 4;
                    float w = ws[k];
                    b += Px[p] * w; g += Px[p + 1] * w; r += Px[p + 2] * w; a += Px[p + 3] * w;
                }
                int d = dst + x * 4;
                tmp[d] = b; tmp[d + 1] = g; tmp[d + 2] = r; tmp[d + 3] = a;
            }
        }
        int row = width * 4;
        for (int y = 0; y < height; y++)
        {
            var (start, ws) = wy[y];
            for (int i = 0; i < row; i++)
            {
                float v = 0;
                for (int k = 0; k < ws.Length; k++) v += tmp[(start + k) * row + i] * ws[k];
                o.Px[y * row + i] = (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
            }
        }
    }

    public static (int Start, float[] W)[] Weights(int src, int dst)
    {
        var res = new (int, float[])[dst];
        double scale = (double)src / dst;
        for (int d = 0; d < dst; d++)
        {
            if (scale >= 1)
            {
                double a = d * scale, b = Math.Min(src, (d + 1) * scale);
                int s0 = (int)Math.Floor(a), s1 = Math.Min(src, (int)Math.Ceiling(b));
                var ws = new float[s1 - s0];
                for (int s = s0; s < s1; s++) ws[s - s0] = (float)((Math.Min(b, s + 1) - Math.Max(a, s)) / scale);
                res[d] = (s0, ws);
            }
            else
            {
                double x = (d + 0.5) * scale - 0.5;
                int s0 = (int)Math.Floor(x);
                float f = (float)(x - s0);
                if (s0 < 0) { s0 = 0; f = 0; }
                if (s0 >= src - 1) { s0 = src - 1; f = 0; }
                res[d] = f == 0 ? (s0, [1f]) : (s0, [1 - f, f]);
            }
        }
        return res;
    }

    /// <summary>True when (nearly) every pixel is black: what a window capture gives when it doesn't work.</summary>
    public bool IsBlack()
    {
        for (int i = 0; i < Px.Length; i += 4 * 7)
            if (Px[i] > 16 || Px[i + 1] > 16 || Px[i + 2] > 16) return false;
        return true;
    }

    public void MakeOpaque()
    {
        for (int i = 3; i < Px.Length; i += 4) Px[i] = 255;
    }
}

/// <summary>OpenCV-compatible 8-bit HSV: H 0-180, S and V 0-255.</summary>
static class Hsv
{
    public static void From(int b, int g, int r, out int h, out int s, out int v)
    {
        v = Math.Max(r, Math.Max(g, b));
        int diff = v - Math.Min(r, Math.Min(g, b));
        s = v == 0 ? 0 : (diff * 255 + v / 2) / v;
        if (diff == 0) { h = 0; return; }
        int units = v == r ? g - b : v == g ? b - r + 2 * diff : r - g + 4 * diff;
        h = (int)Math.Floor(units * 30.0 / diff + 0.5);
        if (h < 0) h += 180;
    }
}

static class ImageIO
{
    /// <summary>Decodes PNG, JPEG or WebP with the Windows codecs, as premultiplied BGRA. Null if it can't.</summary>
    public static Image32? Load(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            // Runs on the thread pool so it's safe to call from the UI thread too.
            return Task.Run(async () =>
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                return new Image32((int)decoder.PixelWidth, (int)decoder.PixelHeight, data.DetachPixelData());
            }).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void SavePng(Image32 img, string path)
    {
        using var bmp = ToBitmap(img, PixelFormat.Format32bppArgb);
        bmp.Save(path, ImageFormat.Png);
    }

    public static Bitmap ToBitmap(Image32 img, PixelFormat format)
    {
        var bmp = new Bitmap(Math.Max(1, img.Width), Math.Max(1, img.Height), format);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, format);
        try
        {
            for (int y = 0; y < img.Height; y++)
                Marshal.Copy(img.Px, y * img.Width * 4, data.Scan0 + y * data.Stride, img.Width * 4);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }
}
