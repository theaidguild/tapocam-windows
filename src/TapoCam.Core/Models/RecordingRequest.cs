namespace TapoCam.Core.Models;

public sealed record RecordingRequest(
    StreamQuality Quality,
    bool Audio,
    string? ScheduledKey = null,
    DateTimeOffset? ScheduledEnd = null)
{
    public bool IsScheduled => ScheduledKey is not null;

    public static RecordingRequest Manual(AppSettings settings) =>
        new(settings.EffectiveRecordQuality, settings.RecordAudio);

    public static RecordingRequest Scheduled(ScheduleWindow window) =>
        new(window.Rule.EffectiveQuality, window.Rule.Audio, window.Key, window.End);

    public static RecordingRequest? ResolveScheduled(
        RecordingRequest? current, ScheduleWindow? window, DateTimeOffset now,
        bool isRecording, string? suppressedWindowKey)
    {
        if (current is { IsScheduled: true })
        {
            if (window is not null && window.Key != current.ScheduledKey &&
                window.End > now && window.End > current.ScheduledEnd)
                return current with { ScheduledKey = window.Key, ScheduledEnd = window.End };

            return window is null || current.ScheduledEnd is not { } end || now >= end
                ? null
                : current;
        }

        return current is null && window is not null && window.End > now &&
            !isRecording && window.Key != suppressedWindowKey ? Scheduled(window) : current;
    }
}
