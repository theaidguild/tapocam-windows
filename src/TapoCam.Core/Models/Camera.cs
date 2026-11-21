using System.Text.Json.Serialization;

namespace TapoCam.Core.Models;

public sealed class Camera
{
    [JsonRequired]
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; } = 554;
    public string User { get; set; } = "";
    public bool UseCustomRtspUrl { get; set; }
    public int Fps { get; set; } = 25;
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string Host => Ip?.Trim() ?? "";
    [JsonIgnore]
    public string Username => User?.Trim() ?? "";
    [JsonIgnore]
    public int EffectiveFps => Math.Max(1, Fps);
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Câmera" : Name.Trim();
    [JsonIgnore]
    public string Slug => FileNaming.Slug(DisplayName);

    public const string InvalidRtspUrlMessage = "Informe uma URL RTSP válida, começando com rtsp://.";

    public bool IsConfigured(string? secret) => UseCustomRtspUrl
        ? ParseRtspUrl(secret) is not null
        : !string.IsNullOrWhiteSpace(Host) &&
          !string.IsNullOrWhiteSpace(Username) &&
          !string.IsNullOrWhiteSpace(secret);

    public bool IsActive(string? secret) => Enabled && IsConfigured(secret);

    public (string Host, int Port)? RtspEndpoint(string? secret)
    {
        if (!UseCustomRtspUrl)
            return (Host, Port);
        return ParseRtspUrl(secret) is { } uri
            ? (uri.IdnHost, uri.Port == -1 ? 554 : uri.Port)
            : null;
    }

    public string StreamName(StreamQuality quality) =>
        $"cam_{Id:N}_{(UseCustomRtspUrl ? StreamQuality.Hd : quality).Suffix()}".ToLowerInvariant();

    public string RtspUrl(StreamQuality quality, string secret)
    {
        if (UseCustomRtspUrl)
            return secret.Trim();
        var user = UriCredentialEncoder.Encode(Username);
        var pass = UriCredentialEncoder.Encode(secret);
        return $"rtsp://{user}:{pass}@{Host}:{Port}/{quality.StreamPath()}";
    }

    private static Uri? ParseRtspUrl(string? value)
    {
        var url = value?.Trim();
        return url is not null && url.StartsWith("rtsp://", StringComparison.Ordinal) &&
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsWellFormedOriginalString() &&
            uri.Host.Length > 0 && uri.Port != 0 && uri.Fragment.Length == 0
            ? uri : null;
    }

    public static string NewName(IEnumerable<string> existingNames)
    {
        var names = existingNames.ToList();
        var usedNames = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var number = names.Count + 1; ; number++)
        {
            var candidate = $"Câmera {number}";
            if (!usedNames.Contains(candidate))
                return candidate;
        }
    }
}
