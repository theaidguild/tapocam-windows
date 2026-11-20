namespace TapoCam.Core.Models;

public sealed class ScheduleRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public HashSet<int> Weekdays { get; set; } = [2, 3, 4, 5, 6];
    public int Hour { get; set; } = 8;
    public int Minute { get; set; }
    public int DurationMinutes { get; set; } = 60;
    public string Quality { get; set; } = "hd";
    public bool Audio { get; set; } = true;
    public HashSet<Guid> CameraIds { get; set; } = [];

    public bool IsActive =>
        Enabled && Weekdays.Count > 0 && CameraIds.Count > 0;

    public int EffectiveDurationMinutes => Math.Clamp(DurationMinutes, 1, 1440);
    public StreamQuality EffectiveQuality => StreamQualityExtensions.Parse(Quality);
}
