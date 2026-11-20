namespace TapoCam.Core.Models;

public enum StreamQuality
{
    Hd,
    Sd
}

public static class StreamQualityExtensions
{
    public static StreamQuality Parse(string? value) =>
        string.Equals(value, "sd", StringComparison.OrdinalIgnoreCase)
            ? StreamQuality.Sd
            : StreamQuality.Hd;

    public static string Label(this StreamQuality quality) =>
        quality == StreamQuality.Sd ? "SD" : "HD";

    public static string StreamPath(this StreamQuality quality) =>
        quality == StreamQuality.Sd ? "stream2" : "stream1";

    public static string Suffix(this StreamQuality quality) =>
        quality == StreamQuality.Sd ? "sd" : "hd";
}
