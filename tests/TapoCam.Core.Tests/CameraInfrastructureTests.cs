using TapoCam.Core.Models;

namespace TapoCam.Core.Tests;

public sealed class CameraInfrastructureTests
{
    [Theory]
    [InlineData("  rtsp://viewer:p%40ss@camera.local:8554/cam/realmonitor?channel=1&subtype=0  ", "camera.local", 8554)]
    [InlineData("rtsp://camera.local/live", "camera.local", 554)]
    [InlineData("rtsp://[2001:db8::1]/live", "2001:db8::1", 554)]
    public void DirectRtspUsesTheOriginalUrlAndItsEndpoint(string url, string host, int port)
    {
        var camera = new Camera { UseCustomRtspUrl = true, Ip = "old-host", Port = 9999 };

        Assert.True(camera.IsActive(url));
        Assert.Equal((host, port), camera.RtspEndpoint(url));
        Assert.Equal(url.Trim(), camera.RtspUrl(StreamQuality.Hd, url));
        Assert.Equal(url.Trim(), camera.RtspUrl(StreamQuality.Sd, url));
    }

    [Theory]
    [InlineData("")]
    [InlineData("camera.local/live")]
    [InlineData("http://camera.local/live")]
    [InlineData("rtsp:///live")]
    [InlineData("rtsp://camera.local:0/live")]
    [InlineData("rtsp://camera.local:65536/live")]
    [InlineData("rtsp://camera.local/live#option")]
    [InlineData("rtsp://camera.local/li\nve")]
    public void InvalidDirectRtspDoesNotFallBackToTapo(string url)
    {
        var camera = new Camera { UseCustomRtspUrl = true, Ip = "old-host", User = "viewer" };

        Assert.False(camera.IsConfigured(url));
        Assert.Null(camera.RtspEndpoint(url));
        Assert.Contains("streams: {}", Go2RtcConfigBuilder.Build([(camera, url)], null, audioMode: false));
    }

    [Fact]
    public void DirectRtspSharesOneRelayStreamAndSavesOnlyTheModeInSettings()
    {
        var camera = new Camera { UseCustomRtspUrl = true };
        const string url = "rtsp://viewer:example@camera.local:554/cam/realmonitor?channel=1&subtype=0";
        var stream = camera.StreamName(StreamQuality.Hd);
        var config = Go2RtcConfigBuilder.Build([(camera, url)], null, audioMode: false);

        Assert.Equal(stream, camera.StreamName(StreamQuality.Sd));
        Assert.Equal($"  {stream}: '{url}'", Assert.Single(config.Split('\n')
            .Select(line => line.TrimEnd()), line => line.StartsWith("  cam_", StringComparison.Ordinal)));
        Assert.Contains($"?src={stream}&", PlayerUrlBuilder.Grid(camera));

        var settings = new AppSettings { Cameras = [camera] };
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("rtsp://", json);
        Assert.True(Assert.Single(System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!.Cameras).UseCustomRtspUrl);
    }

    [Fact]
    public void DirectRtspAudioConfigPreservesUrlAndUsesOneStream()
    {
        var camera = new Camera { UseCustomRtspUrl = true };
        const string url = "rtsp://viewer:example@camera.local/live?name=cam'1&channel=1";
        var stream = camera.StreamName(StreamQuality.Hd);

        var config = Go2RtcConfigBuilder.Build([(camera, url)], "ffmpeg", audioMode: true);

        Assert.Single(config.Split('\n'), line => line.StartsWith("  cam_", StringComparison.Ordinal));
        Assert.Contains("    - 'rtsp://viewer:example@camera.local/live?name=cam''1&channel=1'", config);
        Assert.Contains($"    - 'ffmpeg:{stream}#audio=aac'", config);
    }

    [Theory]
    [InlineData("  ", "user", "password", false)]
    [InlineData("192.0.2.10", " \t", "password", false)]
    [InlineData("192.0.2.10", "user", null, false)]
    [InlineData("192.0.2.10", "user", " \t", false)]
    [InlineData("192.0.2.10", "user", "  password \t", true)]
    public void ConfigurationRequiresThreeNonBlankFields(string host, string user, string? password, bool configured)
    {
        var camera = new Camera { Ip = host, User = user, Enabled = false };
        Assert.Equal(configured, camera.IsConfigured(password));
        Assert.False(camera.IsActive(password));
    }

    [Fact]
    public void PlayerUsesMutedMseInGridAndConfiguredQualityAndAudioInFocus()
    {
        var camera = new Camera { Id = Guid.Parse("12345678-90ab-cdef-1234-567890abcdef") };
        var settings = new AppSettings { ViewQuality = "hd", PlayerMode = "mse" };
        const string prefix = "http://127.0.0.1:11984/stream.html?src=cam_1234567890abcdef1234567890abcdef";

        Assert.Equal(prefix + "_sd&mode=mse&media=video", PlayerUrlBuilder.Grid(camera));
        Assert.Equal(prefix + "_hd&mode=mse&media=video%2Caudio",
            PlayerUrlBuilder.Focused(camera, settings.EffectiveViewQuality, includeAudio: true));
        Assert.Equal(prefix + "_sd&mode=mse&media=video",
            PlayerUrlBuilder.Focused(camera, StreamQuality.Sd, includeAudio: false));
    }

    [Fact]
    public void LogParserMatchesRecentWholeHostsAcrossMidnightAndMasksCredentials()
    {
        var now = new DateTimeOffset(2025, 1, 1, 0, 0, 3, TimeSpan.FromHours(-3));
        var camera = new Camera { Ip = "192.0.2.10", User = "viewer" };
        var source = camera.RtspUrl(StreamQuality.Hd, "example");
        var log = $"""
            23:59:47 WRN error="stale matching 192.0.2.10"
            23:59:48 WRN error="recent {source}"
            00:00:02 WRN error="wrong host 192.0.2.100"
            00:00:03 WRN error="wrong address 1.192.0.2.10"
            00:00:04 WRN error="future 192.0.2.10"
            """;

        Assert.Equal("recent rtsp://***@192.0.2.10:554/stream1", LogParser.RecentError(log, "192.0.2.10", now));
        Assert.Null(LogParser.RecentError(log, "192.0.2.10", now.AddMinutes(1)));
        Assert.Null(LogParser.RecentError(log, "192.0.2.1", now));
        Assert.Equal("failed rtsp://***@192.0.2.10:554/stream1",
            LogParser.LastLine($"opening ...\r\nfailed {source}\r\n  "));
    }

    [Theory]
    [InlineData(false, false, false, RelayState.Failed, false, "Desativada", CameraStatusColor.DimGray)]
    [InlineData(true, false, false, RelayState.Failed, false, "Configuração incompleta", CameraStatusColor.DimGray)]
    [InlineData(true, true, false, RelayState.Failed, false, "Desconectada", CameraStatusColor.Gray)]
    [InlineData(true, true, true, RelayState.Failed, true, "Falha, tentando de novo…", CameraStatusColor.Red)]
    [InlineData(true, true, true, RelayState.Starting, false, "Conectando…", CameraStatusColor.Orange)]
    [InlineData(true, true, true, RelayState.Running, false, "Sem resposta", CameraStatusColor.Red)]
    [InlineData(true, true, true, RelayState.Running, true, "Instável", CameraStatusColor.Orange)]
    [InlineData(true, true, true, RelayState.Running, null, "Verificando…", CameraStatusColor.Orange)]
    public void StatusUsesPriorityOrder(bool enabled, bool configured, bool wantsConnection, RelayState relayState,
        bool? reachable, string text, CameraStatusColor color)
    {
        Assert.Equal(new CameraStatus(text, color),
            CameraStatus.Resolve(enabled, configured, wantsConnection, relayState, reachable, "warning"));
        Assert.Equal(new CameraStatus("Online", CameraStatusColor.Green),
            CameraStatus.Resolve(true, true, true, RelayState.Running, true, null));
    }

    [Fact]
    public async Task HealthChecksThrottleAndReportOnlyTransitionsAfterFirstResult()
    {
        var clock = new TestClock();
        var probe = new StubProbe();
        var monitor = new CameraHealthMonitor(probe, clock);

        Assert.False((await monitor.CheckAsync("camera.local", 554))!.Changed);
        Assert.True(monitor.Reachable);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Null(await monitor.CheckAsync("camera.local", 554));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await monitor.CheckAsync("camera.local", 554))!.Changed);
        Assert.False(monitor.Reachable);
        monitor.Reset();
        Assert.Null(monitor.Reachable);
        Assert.False((await monitor.CheckAsync("camera.local", 554))!.Changed);
        Assert.Equal(3, probe.Calls);
    }

    private sealed class TestClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }

    private sealed class StubProbe : ICameraHealthProbe
    {
        public int Calls { get; private set; }

        public Task<CameraProbeResult> ProbeAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            Assert.Equal("camera.local", host);
            Assert.Equal(554, port);
            return Task.FromResult(new CameraProbeResult(++Calls != 2));
        }
    }
}
