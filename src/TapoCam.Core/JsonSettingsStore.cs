using System.Text.Json;
using TapoCam.Core.Models;

namespace TapoCam.Core;

public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly string _path = Path.GetFullPath(path);

    public AppSettings Load()
    {
        try
        {
            using var stream = File.OpenRead(_path);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            return Decode(document.RootElement);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        // A sibling temporary file keeps the rename on the same filesystem.
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, Options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static AppSettings Decode(JsonElement root)
    {
        var settings = new AppSettings();
        if (root.ValueKind != JsonValueKind.Object)
            return settings;

        foreach (var property in root.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name.ToLowerInvariant())
            {
                case "cameras":
                    settings.Cameras = ReadList<Camera>(value).DistinctBy(camera => camera.Id).ToList();
                    break;
                case "schedulerules":
                    settings.ScheduleRules = ReadList<ScheduleRule>(value).DistinctBy(rule => rule.Id).ToList();
                    break;
                case "gridtilewidth": settings.GridTileWidth = Math.Clamp(Read(value, settings.GridTileWidth), 200, 800); break;
                case "viewquality": settings.ViewQuality = StreamQualityExtensions.Parse(Read(value, settings.ViewQuality)).Suffix(); break;
                case "playermode": settings.PlayerMode = PlayerUrlBuilder.NormalizeMode(Read(value, settings.PlayerMode)); break;
                case "recordquality": settings.RecordQuality = StreamQualityExtensions.Parse(Read(value, settings.RecordQuality)).Suffix(); break;
                case "recordaudio": settings.RecordAudio = Read(value, settings.RecordAudio); break;
                case "recorddir": settings.RecordDir = Read(value, settings.RecordDir); break;
                case "retentiondays": settings.RetentionDays = Math.Clamp(Read(value, settings.RetentionDays), 0, 365); break;
                case "autoconnect": settings.AutoConnect = Read(value, settings.AutoConnect); break;
                case "autorecord": settings.AutoRecord = Read(value, settings.AutoRecord); break;
                case "notifycamerastatus": settings.NotifyCameraStatus = Read(value, settings.NotifyCameraStatus); break;
                case "settingscamera": settings.SettingsCamera = Read(value, settings.SettingsCamera); break;
                case "settingstab": settings.SettingsTab = Math.Clamp(Read(value, settings.SettingsTab), 0, 2); break;
                case "debugplayer": settings.DebugPlayer = Read(value, settings.DebugPlayer); break;
            }
        }

        if (string.IsNullOrWhiteSpace(settings.RecordDir))
            settings.RecordDir = new AppSettings().RecordDir;
        return settings;
    }

    private static IEnumerable<T> ReadList<T>(JsonElement value) where T : class
    {
        if (value.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var entry in value.EnumerateArray())
        {
            if (Read<T?>(entry, null) is { } item)
                yield return item;
        }
    }

    private static T Read<T>(JsonElement value, T fallback)
    {
        try
        {
            return value.Deserialize<T>(Options) is { } parsed ? parsed : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}
