using System.Text.Json;
using TapoCam.Core.Models;

namespace TapoCam.Core.Tests;

public sealed class SettingsAndRetentionTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N"));

    [Fact]
    public void FreshStoreReturnsDefaultsAndPersistsAnEmptyCameraList()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new JsonSettingsStore(path);

        var settings = store.Load();

        Assert.False(File.Exists(path));
        Assert.Empty(settings.Cameras);
        Assert.Empty(settings.ScheduleRules);
        Assert.Equal(320, settings.GridTileWidth);
        Assert.Equal("hls", settings.EffectivePlayerMode);
        Assert.Equal(StreamQuality.Hd, settings.EffectiveViewQuality);
        Assert.Equal(StreamQuality.Hd, settings.EffectiveRecordQuality);
        Assert.EndsWith(Path.Combine("Videos", "TapoCam"), settings.RecordDir);
        Assert.True(settings.RecordAudio);
        Assert.True(settings.AutoConnect);
        Assert.False(settings.AutoRecord);
        Assert.True(settings.NotifyCameraStatus);
        Assert.False(settings.DebugPlayer);
        Assert.Equal(0, settings.RetentionDays);
        Assert.Null(settings.SettingsCamera);

        store.Save(settings);
        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(0, saved.RootElement.GetProperty("cameras").GetArrayLength());
    }

    [Fact]
    public void CameraAndRulesRoundTripWithoutDerivedFieldsOrCredentials()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new JsonSettingsStore(path);
        var camera = new Camera { Name = "Entrada", Ip = "camera.local", User = "viewer", Fps = 15, Enabled = false };
        var rule = new ScheduleRule { Weekdays = [1, 7], Hour = 19, CameraIds = [camera.Id], Audio = false, Quality = "sd" };
        var settings = new AppSettings
        {
            Cameras = [camera], ScheduleRules = [rule], SettingsCamera = camera.Id,
            SettingsTab = 2, PlayerMode = "mse", ViewQuality = "sd", RetentionDays = 30
        };
        store.Save(new AppSettings());
        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(camera.Id, Assert.Single(loaded.Cameras).Id);
        Assert.Equal("Entrada", loaded.Cameras[0].Name);
        Assert.Equal("camera.local", loaded.Cameras[0].Ip);
        Assert.Equal("viewer", loaded.Cameras[0].User);
        Assert.Equal(15, loaded.Cameras[0].Fps);
        Assert.False(loaded.Cameras[0].Enabled);
        Assert.Equal(rule.Id, Assert.Single(loaded.ScheduleRules).Id);
        Assert.Equal(new[] { 1, 7 }, loaded.ScheduleRules[0].Weekdays.Order());
        Assert.Equal(camera.Id, Assert.Single(loaded.ScheduleRules[0].CameraIds));
        Assert.False(loaded.ScheduleRules[0].Audio);
        Assert.Equal(StreamQuality.Sd, loaded.ScheduleRules[0].EffectiveQuality);
        Assert.Equal(camera.Id, loaded.SettingsCamera);
        Assert.Equal(2, loaded.SettingsTab);
        Assert.Equal("mse", loaded.EffectivePlayerMode);
        Assert.Equal(StreamQuality.Sd, loaded.EffectiveViewQuality);
        Assert.Equal(30, loaded.RetentionDays);
        Assert.Equal(path, Assert.Single(Directory.GetFiles(_directory)));
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("effectiveQuality", json);
        Assert.DoesNotContain("displayName", json);
    }

    [Fact]
    public void StoreToleratesMalformedValuesAndIndividualRecords()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, """
            {
                // Newer versions may contribute unrelated keys.
                "unknown": {},
                "autoConnect": false,
                "recordAudio": null,
                "retentionDays": "unknown",
                "viewQuality": "future",
                "cameras": [null, {}, {"id":"12345678-90ab-cdef-1234-567890abcdef"}],
                "scheduleRules": [{"cameraIds":null}],
            }
            """);

        var store = new JsonSettingsStore(path);
        var loaded = store.Load();
        Assert.False(loaded.AutoConnect);
        Assert.True(loaded.RecordAudio);
        Assert.Equal(0, loaded.RetentionDays);
        Assert.Equal("hd", loaded.ViewQuality);
        Assert.Equal(554, Assert.Single(loaded.Cameras).Port);
        Assert.False(Assert.Single(loaded.ScheduleRules).IsActive);

        File.WriteAllText(path, "{");
        Assert.True(store.Load().AutoConnect);
    }

    [Fact]
    public void RetentionSelectsOnlyOldInactiveManagedVideosWithoutDeletingThem()
    {
        Directory.CreateDirectory(_directory);
        var now = new DateTimeOffset(2025, 1, 12, 12, 0, 0, TimeSpan.FromHours(-3));
        var expired = FileAt("TAPO_legacy.MP4", now.AddDays(-6));
        var active = FileAt("tapo_active.mp4", now.AddDays(-6));
        FileAt("tapo_recent.mp4", now.AddDays(-4));
        FileAt("tapo_at_cutoff.mp4", now.AddDays(-5));
        FileAt("snap_photo.jpg", now.AddDays(-6));
        FileAt("holiday.mp4", now.AddDays(-6));
        FileAt("tapo_notes.txt", now.AddDays(-6));
        Directory.CreateDirectory(Path.Combine(_directory, "child"));
        FileAt(Path.Combine("child", "tapo_sub.mp4"), now.AddDays(-6));
        if (OperatingSystem.IsWindows())
        {
            var hidden = FileAt("tapo_hidden.mp4", now.AddDays(-6));
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        }

        Assert.Empty(RecordingRetention.ExpiredPaths(_directory, 0, now, []));
        Assert.Equal(expired, Assert.Single(RecordingRetention.ExpiredPaths(_directory, 5, now, [active])));
        Assert.True(File.Exists(expired));
    }

    private string FileAt(string name, DateTimeOffset timestamp)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "");
        File.SetLastWriteTimeUtc(path, timestamp.UtcDateTime);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
