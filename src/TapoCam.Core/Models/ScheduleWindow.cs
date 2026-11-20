namespace TapoCam.Core.Models;

public sealed record ScheduleWindow(
    ScheduleRule Rule,
    Guid CameraId,
    DateTimeOffset Start,
    DateTimeOffset End)
{
    public string Key => $"{Rule.Id:D}-{CameraId:D}-{Start.ToUnixTimeSeconds()}";
}
