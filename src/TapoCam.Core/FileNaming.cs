using System.Globalization;
using System.Text;

namespace TapoCam.Core;

public enum ManagedFileType
{
    Video,
    Photo
}

public sealed record ParsedManagedFile(ManagedFileType Type, string? Slug, DateTime Timestamp);

public static class FileNaming
{
    public static string Slug(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(normalized.Length);
        var pendingSeparator = false;

        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;

            if (character is >= 'A' and <= 'Z')
            {
                if (pendingSeparator && result.Length > 0)
                    result.Append('-');
                result.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
            }
            else if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingSeparator && result.Length > 0)
                    result.Append('-');
                result.Append(character);
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return result.Length == 0 ? "camera" : result.ToString();
    }

    public static string VideoName(string slug, DateTime localTime) =>
        $"tapo_{Slug(slug)}_{localTime.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.mp4";

    public static string PhotoName(string slug, DateTime localTime) =>
        $"snap_{Slug(slug)}_{localTime.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.jpg";

    public static ParsedManagedFile? Parse(string fileName)
    {
        var type = fileName.StartsWith("tapo_", StringComparison.OrdinalIgnoreCase) &&
                   fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            ? ManagedFileType.Video
            : fileName.StartsWith("snap_", StringComparison.OrdinalIgnoreCase) &&
              fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                ? ManagedFileType.Photo
                : (ManagedFileType?)null;

        if (type is null)
            return null;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var parts = stem.Split('_');
        if (parts.Length is not (3 or 4))
            return null;

        var dateText = parts[^2] + "_" + parts[^1];
        if (!DateTime.TryParseExact(
                dateText,
                "yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timestamp))
            return null;

        var prefix = type == ManagedFileType.Video ? "tapo" : "snap";
        if (!string.Equals(parts[0], prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        return new ParsedManagedFile(type.Value, parts.Length == 4 ? parts[1] : null, timestamp);
    }
}
