namespace FortItemTracker;

/// <summary>
/// A damped spring that moves <see cref="Value"/> toward <see cref="Target"/>. Low damping overshoots
/// and settles; 1 glides in without overshoot. Frame-rate independent.
/// </summary>
sealed class Spring(double value, double hz, double damping, double epsilon = 0.002)
{
    public double Value = value, Velocity, Target = value;

    public bool Settled => Math.Abs(Value - Target) < epsilon && Math.Abs(Velocity) < epsilon * 20;

    /// <summary>Advances by <paramref name="dt"/> seconds; false once it rests on the target.</summary>
    public bool Step(double dt)
    {
        if (Settled)
        {
            Value = Target;
            Velocity = 0;
            return false;
        }
        double w = 2 * Math.PI * hz;
        const double h = 1 / 240.0;  // small fixed steps keep stiff springs stable
        for (double left = Math.Min(dt, 0.1); left > 1e-9; left -= h)
        {
            double s = Math.Min(h, left);
            Velocity += (-w * w * (Value - Target) - 2 * damping * w * Velocity) * s;
            Value += Velocity * s;
        }
        return true;
    }

    public void Snap(double v)
    {
        Value = Target = v;
        Velocity = 0;
    }

    /// <summary>Jumps to <paramref name="from"/> and springs back to the current target.</summary>
    public void Kick(double from)
    {
        Value = from;
        Velocity = 0;
    }
}

static class Ease
{
    public static double Clamp01(double t) => t < 0 ? 0 : t > 1 ? 1 : t;
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    public static double OutCubic(double t)
    {
        t = 1 - Clamp01(t);
        return 1 - t * t * t;
    }

    public static double InOutCubic(double t)
    {
        t = Clamp01(t);
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    /// <summary>0 → 1 → 0 over t in 0..1, a soft bump.</summary>
    public static double Bump(double t) => t <= 0 || t >= 1 ? 0 : Math.Sin(Math.PI * t);

    /// <summary>Progress of an effect that started at <paramref name="start"/> and lasts <paramref name="length"/> s.</summary>
    public static double Since(double now, double start, double length) => Clamp01((now - start) / length);

    public static Color Mix(Color a, Color b, double t)
    {
        t = Clamp01(t);
        return Color.FromArgb(
            (int)Math.Round(Lerp(a.A, b.A, t)), (int)Math.Round(Lerp(a.R, b.R, t)),
            (int)Math.Round(Lerp(a.G, b.G, t)), (int)Math.Round(Lerp(a.B, b.B, t)));
    }

    /// <summary>The colour with its alpha multiplied by <paramref name="a"/>.</summary>
    public static Color Fade(Color c, double a) => Color.FromArgb((int)Math.Round(c.A * Clamp01(a)), c.R, c.G, c.B);
}
