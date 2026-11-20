using TapoCam.Core.Models;

namespace TapoCam.Core;

public static class ScheduleCalculator
{
    public static IReadOnlyDictionary<Guid, ScheduleWindow> CurrentWindows(
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        IEnumerable<ScheduleRule> rules,
        IEnumerable<Guid> eligibleCameraIds)
    {
        var eligible = eligibleCameraIds.ToHashSet();
        var windows = new Dictionary<Guid, ScheduleWindow>();
        var localDate = TimeZoneInfo.ConvertTime(now, timeZone).Date;

        foreach (var rule in rules.Where(rule => rule.IsActive))
        {
            foreach (var dayOffset in new[] { -1, 0 })
            {
                var date = localDate.AddDays(dayOffset);
                if (!rule.Weekdays.Contains(WeekdayNumber(date.DayOfWeek)) ||
                    !TryCreateWindow(rule, date, timeZone, out var start, out var end) ||
                    now < start || now >= end)
                    continue;

                foreach (var cameraId in rule.CameraIds.Where(eligible.Contains))
                {
                    if (!windows.TryGetValue(cameraId, out var existing) || end > existing.End)
                        windows[cameraId] = new ScheduleWindow(rule, cameraId, start, end);
                }
            }
        }

        return windows;
    }

    public static ScheduleWindow? NextScheduled(
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        IEnumerable<ScheduleRule> rules,
        IEnumerable<Guid> eligibleCameraIds)
    {
        var eligible = eligibleCameraIds.ToHashSet();
        if (eligible.Count == 0)
            return null;

        var localDate = TimeZoneInfo.ConvertTime(now, timeZone).Date;
        var limit = now.AddDays(8);
        ScheduleWindow? earliest = null;

        foreach (var rule in rules.Where(rule => rule.IsActive))
        {
            var cameraId = rule.CameraIds
                .Where(eligible.Contains)
                .OrderBy(id => id.ToString("D"), StringComparer.Ordinal)
                .Select(id => (Guid?)id)
                .FirstOrDefault();
            if (cameraId is null)
                continue;

            for (var dayOffset = 0; dayOffset <= 8; dayOffset++)
            {
                var date = localDate.AddDays(dayOffset);
                if (!rule.Weekdays.Contains(WeekdayNumber(date.DayOfWeek)) ||
                    !TryCreateWindow(rule, date, timeZone, out var start, out var end) ||
                    start <= now || start > limit)
                    continue;

                if (earliest is null || start < earliest.Start)
                    earliest = new ScheduleWindow(rule, cameraId.Value, start, end);
            }
        }

        return earliest;
    }

    public static int WeekdayNumber(DayOfWeek day) =>
        day == DayOfWeek.Sunday ? 1 : (int)day + 1;

    private static bool TryCreateWindow(
        ScheduleRule rule,
        DateTime date,
        TimeZoneInfo timeZone,
        out DateTimeOffset start,
        out DateTimeOffset end)
    {
        start = default;
        end = default;
        if (rule.Hour is < 0 or > 23 || rule.Minute is < 0 or > 59)
            return false;

        var localStart = DateTime.SpecifyKind(date.AddHours(rule.Hour).AddMinutes(rule.Minute), DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(localStart))
            return false;

        var offset = timeZone.IsAmbiguousTime(localStart)
            ? timeZone.GetAmbiguousTimeOffsets(localStart).Max()
            : timeZone.GetUtcOffset(localStart);
        start = new DateTimeOffset(localStart, offset);
        end = start.AddMinutes(rule.EffectiveDurationMinutes);
        return true;
    }
}
