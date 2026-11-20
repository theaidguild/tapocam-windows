using System.Text.Json.Serialization;

namespace TapoCam.Core.Models;

public sealed class ScheduleRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    private HashSet<int> _weekdays = [2, 3, 4, 5, 6];
    private HashSet<Guid> _cameraIds = [];

    public HashSet<int> Weekdays
    {
        get => _weekdays;
        set => _weekdays = value ?? [2, 3, 4, 5, 6];
    }
    public int Hour { get; set; } = 8;
    public int Minute { get; set; }
    public int DurationMinutes { get; set; } = 60;
    public string Quality { get; set; } = "hd";
    public bool Audio { get; set; } = true;
    public HashSet<Guid> CameraIds
    {
        get => _cameraIds;
        set => _cameraIds = value ?? [];
    }

    [JsonIgnore]
    public bool IsActive =>
        Enabled && Weekdays.Count > 0 && CameraIds.Count > 0;

    [JsonIgnore]
    public int EffectiveDurationMinutes => Math.Clamp(DurationMinutes, 1, 1440);
    [JsonIgnore]
    public StreamQuality EffectiveQuality => StreamQualityExtensions.Parse(Quality);
}
