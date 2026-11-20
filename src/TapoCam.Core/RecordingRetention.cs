namespace TapoCam.Core;

public static class RecordingRetention
{
    public static IReadOnlyList<string> ExpiredPaths(
        string directory, int retentionDays, DateTimeOffset now, IEnumerable<string> activePaths)
    {
        if (retentionDays <= 0 || !Directory.Exists(directory))
            return [];

        var cutoff = now.UtcDateTime.AddDays(-retentionDays);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var active = activePaths.Select(Path.GetFullPath).ToHashSet(comparer);
        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };
        return new DirectoryInfo(Path.GetFullPath(directory))
            .EnumerateFiles("*", options)
            .Where(file => file.Name.StartsWith("tapo_", StringComparison.OrdinalIgnoreCase) &&
                file.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) &&
                !active.Contains(file.FullName) && file.Exists && file.LastWriteTimeUtc < cutoff)
            .Select(file => file.FullName)
            .ToArray();
    }
}
