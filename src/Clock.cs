using System.Diagnostics;
using System.Globalization;

namespace FortItemTracker;

static class Clock
{
    static readonly long Origin = Stopwatch.GetTimestamp();

    /// <summary>Seconds on a monotonic clock (only differences mean anything).</summary>
    public static double Mono => (Stopwatch.GetTimestamp() - Origin) / (double)Stopwatch.Frequency;

    /// <summary>Unix time in seconds, as Python's time.time() wrote it.</summary>
    public static double Wall => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
}

static class Fmt
{
    static DateTime Local(double wall) => DateTime.UnixEpoch.AddSeconds(wall).ToLocalTime();

    public static string Duration(double seconds)
    {
        long s = (long)Math.Max(0, seconds);
        return $"{s / 3600:00}:{s % 3600 / 60:00}:{s % 60:00}";
    }

    /// <summary>12-hour clock time, e.g. 8:16:01 PM.</summary>
    public static string Time(double wall) => Local(wall).ToString("h:mm:ss tt", CultureInfo.InvariantCulture);

    /// <summary>Date and 12-hour time, e.g. 2026-11-13 8:16:01 PM (Excel reads it as a date-time).</summary>
    public static string ClockTime(double wall) => Local(wall).ToString("yyyy-MM-dd ", CultureInfo.InvariantCulture) + Time(wall);

    public static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);
}
