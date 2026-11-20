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

    public bool IsConfigured(string? password) =>
        !string.IsNullOrWhiteSpace(Host) &&
        !string.IsNullOrWhiteSpace(Username) &&
        !string.IsNullOrWhiteSpace(password);

    public bool IsActive(string? password) => Enabled && IsConfigured(password);

    public string StreamName(StreamQuality quality) =>
        $"cam_{Id:N}_{quality.Suffix()}".ToLowerInvariant();

    public string RtspUrl(StreamQuality quality, string password)
    {
        var user = UriCredentialEncoder.Encode(Username);
        var pass = UriCredentialEncoder.Encode(password);
        return $"rtsp://{user}:{pass}@{Host}:{Port}/{quality.StreamPath()}";
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
