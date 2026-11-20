using System.Globalization;

namespace TapoCam.Core;

public static class TimeFormatting
{
    public static string Duration(int minutes)
    {
        minutes = Math.Max(0, minutes);
        var hours = minutes / 60;
        var remainder = minutes % 60;
        return hours == 0 ? Invariant($"{remainder} min") :
            remainder == 0 ? Invariant($"{hours} h") : Invariant($"{hours} h {remainder} min");
    }

    public static string Elapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        var hours = (long)elapsed.TotalHours;
        return hours == 0 ? Invariant($"{elapsed.Minutes:00}:{elapsed.Seconds:00}") :
            Invariant($"{hours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}");
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
