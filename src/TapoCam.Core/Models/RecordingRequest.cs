namespace TapoCam.Core.Models;

public sealed record RecordingRequest(
    StreamQuality Quality,
    bool Audio,
    string? ScheduledKey = null,
    DateTimeOffset? ScheduledEnd = null)
{
    public bool IsScheduled => ScheduledKey is not null;
}
