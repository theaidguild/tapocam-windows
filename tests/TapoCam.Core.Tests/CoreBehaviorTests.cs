using System.Text.Json;
using TapoCam.Core;
using TapoCam.Core.Models;

namespace TapoCam.Core.Tests;

public sealed class CoreBehaviorTests
{
    [Fact]
    public void CameraBuildsSafeStreamNamesAndEncodedRtspCredentials()
    {
        var id = Guid.Parse("12345678-90ab-cdef-1234-567890abcdef");
        var camera = new Camera { Id = id, Ip = " 192.168.0.10 ", User = "user@home" };

        Assert.Equal("cam_1234567890abcdef1234567890abcdef_hd", camera.StreamName(StreamQuality.Hd));
        var url = camera.RtspUrl(StreamQuality.Sd, "p:ss/word");
        Assert.Contains("user%40home:p%3Ass%2Fword@", url);
        Assert.EndsWith("@192.168.0.10:554/stream2", url);
    }

    [Fact]
    public void CameraJsonRequiresIdAndDefaultsMissingFields()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Camera>("{}", options));

        var camera = JsonSerializer.Deserialize<Camera>(
            """{"id":"12345678-90ab-cdef-1234-567890abcdef"}""",
            options);

        Assert.NotNull(camera);
        Assert.Equal("", camera.Name);
        Assert.Equal("", camera.Ip);
        Assert.Equal(554, camera.Port);
        Assert.Equal(25, camera.Fps);
        Assert.True(camera.Enabled);
    }

    [Fact]
    public void AudioConfigOmitsFfmpegSourceWhenFfmpegIsUnavailable()
    {
        var camera = new Camera { Ip = "192.168.0.10", User = "viewer" };

        var config = Go2RtcConfigBuilder.Build([(camera, "secret")], null, audioMode: true);

        Assert.Contains("cam_", config);
        Assert.Contains("stream1", config);
        Assert.Contains("stream2", config);
        Assert.DoesNotContain("audio=aac", config);
    }

    [Fact]
    public void AudioConfigAddsTheSpecifiedFfmpegAudioSourceForEveryStream()
    {
        var camera = new Camera
        {
            Id = Guid.Parse("12345678-90ab-cdef-1234-567890abcdef"),
            Ip = "192.168.0.10",
            User = "viewer"
        };

        var config = Go2RtcConfigBuilder.Build([(camera, "secret")], @"C:\ffmpeg.exe", audioMode: true);

        Assert.Contains("bin: 'C:\\ffmpeg.exe'", config);
        Assert.Contains($"ffmpeg:{camera.StreamName(StreamQuality.Hd)}#audio=aac", config);
        Assert.Contains($"ffmpeg:{camera.StreamName(StreamQuality.Sd)}#audio=aac", config);
    }

    [Fact]
    public void EmptyRelayConfigUsesAnEmptyStreamsMap()
    {
        var config = Go2RtcConfigBuilder.Build([], null, audioMode: false);

        Assert.Contains("streams: {}", config);
    }

    [Fact]
    public void VideoOnlyConfigQuotesWindowsPathsWithoutAddingAudioSources()
    {
        var camera = new Camera { Ip = "192.0.2.10", User = "viewer" };
        var config = Go2RtcConfigBuilder.Build([(camera, "example")], @"C:\Tools\Editor's FFmpeg\ffmpeg.exe", audioMode: false);

        Assert.Contains(@"  bin: 'C:\Tools\Editor''s FFmpeg\ffmpeg.exe'", config);
        Assert.Contains($"  {camera.StreamName(StreamQuality.Hd)}: 'rtsp://", config);
        Assert.Contains($"  {camera.StreamName(StreamQuality.Sd)}: 'rtsp://", config);
        Assert.DoesNotContain("audio=aac", config);
    }

    [Fact]
    public void SlugsAndManagedFileNamesFollowTheNamingRules()
    {
        Assert.Equal("sala-de-estar", FileNaming.Slug("Sala de Estar!"));
        Assert.Equal("camera-1", FileNaming.Slug("Câmera 1"));
        Assert.Equal("camera", FileNaming.Slug("---"));

        var parsed = FileNaming.Parse("TAPO_sala_20240229_235959.MP4");
        Assert.NotNull(parsed);
        Assert.Equal(ManagedFileType.Video, parsed.Type);
        Assert.Equal("sala", parsed.Slug);
        Assert.Null(FileNaming.Parse("tapo_sala_20230229_235959.mp4"));
        Assert.Null(FileNaming.Parse("tapo_sala_extra_20240229_235959.mp4"));
    }

    [Fact]
    public void ManagedNameFormattingRoundTripsAndSupportsLegacyNames()
    {
        var timestamp = new DateTime(2024, 2, 29, 23, 59, 59);

        Assert.Equal(new ParsedManagedFile(ManagedFileType.Video, "camera-1", timestamp),
            FileNaming.Parse(FileNaming.VideoName("Câmera 1", timestamp)));
        Assert.Equal(new ParsedManagedFile(ManagedFileType.Photo, "camera-1", timestamp),
            FileNaming.Parse(FileNaming.PhotoName("Câmera 1", timestamp)));
        Assert.Equal(new ParsedManagedFile(ManagedFileType.Video, null, timestamp),
            FileNaming.Parse("tapo_20240229_235959.mp4"));
        Assert.Null(FileNaming.Parse("snap_20240229_235959.mp4"));
    }

    [Fact]
    public void BackoffCapsAndResets()
    {
        var backoff = new RetryBackoff();

        Assert.Equal(new[] { 5, 10, 20, 40, 60, 60 }, Enumerable.Range(0, 6).Select(_ => backoff.NextSeconds));
        backoff.Reset();
        Assert.Equal(5, backoff.NextSeconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordingArgumentsPreserveFragmentedMp4AndOptionalAudio(bool audio)
    {
        const string input = "rtsp://127.0.0.1:18554/cam";
        string[] expected =
        [
            "-loglevel", "warning", "-nostats", "-rtsp_transport", "tcp", "-fflags", "+genpts", "-i", input,
            "-map", "0:v", "-c:v", "copy", "-bsf:v", "setts=ts='if(eq(N,0),TS,if(lt(TS,PREV_OUTPTS+1800),PREV_OUTPTS+3600,TS))'",
            .. (audio ? new[] { "-map", "0:a:0?", "-af", "asetpts=N/SR/TB+STARTPTS", "-c:a", "aac",
                "-ar", "16000", "-ac", "1", "-b:a", "48k" } : ["-an"]),
            "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", "clip.mp4"
        ];

        Assert.Equal(expected, FfmpegCommandBuilder.Recording(input, "clip.mp4", 25, audio));
        Assert.Equal(3600, FfmpegCommandBuilder.FrameDurationTicks(25));
        Assert.Equal(90_000, FfmpegCommandBuilder.FrameDurationTicks(0));
    }

    [Fact]
    public void SnapshotAndConnectionTestCommandsCloseStdin()
    {
        Assert.Contains("-nostdin", FfmpegCommandBuilder.Snapshot("rtsp://relay/cam", "photo.jpg"));
        Assert.Contains("-timeout", FfmpegCommandBuilder.ConnectionTest("rtsp://camera/stream2"));
    }

    [Fact]
    public void ConnectionsRespectPerCameraDisconnectsUntilChanged()
    {
        var connections = new CameraConnections();
        var cameraId = Guid.NewGuid();

        connections.SetAll(true);
        connections.Set(false, cameraId);

        Assert.False(connections.WantsConnection(cameraId));
        Assert.False(connections.AllowsSchedule(cameraId));

        connections.Remove(cameraId);
        Assert.True(connections.WantsConnection(cameraId));
        Assert.True(connections.AllowsSchedule(cameraId));
    }

    [Fact]
    public void CurrentScheduleWindowPrefersTheLatestEndAndFindsOvernightWindows()
    {
        var cameraId = Guid.NewGuid();
        var now = new DateTimeOffset(2025, 1, 7, 0, 30, 0, TimeSpan.Zero);
        var overnight = new ScheduleRule
        {
            Weekdays = [2],
            Hour = 23,
            Minute = 0,
            DurationMinutes = 120,
            CameraIds = [cameraId]
        };
        var later = new ScheduleRule
        {
            Weekdays = [3],
            Hour = 0,
            Minute = 0,
            DurationMinutes = 120,
            CameraIds = [cameraId]
        };

        var windows = ScheduleCalculator.CurrentWindows(
            now, TimeZoneInfo.Utc, [overnight, later], [cameraId]);

        Assert.Equal(later.Id, windows[cameraId].Rule.Id);
        Assert.Equal(now.AddMinutes(90), windows[cameraId].End);
    }

    [Fact]
    public void CredentialMaskerMasksOnlyUrlCredentials()
    {
        var masked = CredentialMasker.Mask("Error rtsp://user@camera/path and https://example.test");

        Assert.DoesNotContain("user@", masked);
        Assert.Contains("rtsp://***@camera/path", masked);
        Assert.Contains("https://example.test", masked);
    }
}
