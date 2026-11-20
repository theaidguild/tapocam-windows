using System.Text.Json;
using TapoCam.Core.Models;

namespace TapoCam.Core.Tests;

public sealed class ScheduleResolutionTests
{
    [Fact]
    public void MissingCameraIdsDecodeToAnInactiveRule()
    {
        var rule = JsonSerializer.Deserialize<ScheduleRule>("{}");

        Assert.NotNull(rule);
        Assert.Empty(rule.CameraIds);
        Assert.False(rule.IsActive);
        Assert.Equal(new[] { 2, 3, 4, 5, 6 }, rule.Weekdays.Order());
        Assert.Equal(60, rule.DurationMinutes);
    }

    [Fact]
    public void CurrentWindowsIncludeOnlyEligibleCamerasAndCrossMidnight()
    {
        var camera = Guid.NewGuid();
        var excluded = Guid.NewGuid();
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(-3), "Test", "Test");
        var now = new DateTimeOffset(2025, 1, 7, 3, 10, 0, TimeSpan.Zero);
        var rule = new ScheduleRule { Weekdays = [2], Hour = 23, DurationMinutes = 90, CameraIds = [camera, excluded] };

        var window = Assert.Single(ScheduleCalculator.CurrentWindows(now, zone, [rule], [camera])).Value;

        Assert.Equal(camera, window.CameraId);
        Assert.Equal(new DateTimeOffset(2025, 1, 6, 23, 0, 0, zone.BaseUtcOffset), window.Start);
        Assert.Empty(ScheduleCalculator.CurrentWindows(window.End, zone, [rule], [camera]));
    }

    [Fact]
    public void NextStartIgnoresCurrentStartsAndUsesEarliestEligibleFutureOccurrence()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var missing = Guid.NewGuid();
        var now = new DateTimeOffset(2025, 1, 6, 8, 0, 0, TimeSpan.Zero);
        ScheduleRule Rule(int hour, params Guid[] cameras) =>
            new() { Hour = hour, Weekdays = [2], CameraIds = [.. cameras] };
        var expected = Rule(10, second, first);
        ScheduleRule[] rules = [Rule(8, first), Rule(9, missing), Rule(11, first), expected, Rule(9)];

        var next = ScheduleCalculator.NextScheduled(now, TimeZoneInfo.Utc, rules, [first, second]);

        Assert.NotNull(next);
        Assert.Equal(expected, next.Rule);
        Assert.Equal(first, next.CameraId);
        Assert.Equal(now.AddHours(2), ScheduleCalculator.NextStart(now, TimeZoneInfo.Utc, rules, [first, second]));
        Assert.Null(ScheduleCalculator.NextStart(now, TimeZoneInfo.Utc, rules, []));
    }

    [Fact]
    public void ScheduledRequestExtendsWithoutChangingItsMediaOrOverwritingManualRequests()
    {
        var camera = Guid.NewGuid();
        var now = new DateTimeOffset(2025, 1, 6, 8, 15, 0, TimeSpan.Zero);
        var original = new ScheduleWindow(new ScheduleRule { Quality = "sd", Audio = false }, camera, now.AddMinutes(-15), now.AddMinutes(5));
        var longer = new ScheduleWindow(new ScheduleRule(), camera, now, now.AddHours(1));
        var current = RecordingRequest.ResolveScheduled(null, original, now, false, null);

        Assert.NotNull(current);
        Assert.Equal(RecordingRequest.Scheduled(original), current);
        Assert.Null(RecordingRequest.ResolveScheduled(null, original, now, false, original.Key));
        Assert.Null(RecordingRequest.ResolveScheduled(null, original, now, true, null));
        var extended = RecordingRequest.ResolveScheduled(current, longer, now, true, null);
        Assert.Equal(new RecordingRequest(StreamQuality.Sd, false, longer.Key, longer.End), extended);
        Assert.Same(current, RecordingRequest.ResolveScheduled(current, original, now, true, null));
        Assert.Null(RecordingRequest.ResolveScheduled(extended, longer, longer.End, true, null));
        Assert.Null(RecordingRequest.ResolveScheduled(extended, null, now, true, null));

        var manual = RecordingRequest.Manual(new AppSettings { RecordQuality = "sd", RecordAudio = false });
        Assert.Equal(new RecordingRequest(StreamQuality.Sd, false), manual);
        Assert.Same(manual, RecordingRequest.ResolveScheduled(manual, longer, now, false, null));
    }

    [Theory]
    [InlineData(45, "45 min")]
    [InlineData(60, "1 h")]
    [InlineData(90, "1 h 30 min")]
    public void DurationFormattingUsesHoursAndMinutes(int minutes, string expected) =>
        Assert.Equal(expected, TimeFormatting.Duration(minutes));

    [Theory]
    [InlineData(-1, "00:00")]
    [InlineData(65, "01:05")]
    [InlineData(3665, "1:01:05")]
    [InlineData(90065, "25:01:05")]
    public void ElapsedFormattingKeepsTotalHours(int seconds, string expected) =>
        Assert.Equal(expected, TimeFormatting.Elapsed(TimeSpan.FromSeconds(seconds)));
}
