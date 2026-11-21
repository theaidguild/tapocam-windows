using System.Diagnostics;
using System.Net.Http;
using System.Text;
using TapoCam.App.Runtime;
using TapoCam.Core;
using TapoCam.Core.Models;

namespace TapoCam.App.Tests;

public sealed class CameraMediaTests
{
    [Fact]
    public async Task ConnectionTestReportsEachStepUpToTheFailure()
    {
        using var http = new HttpClient();
        var media = new CameraMedia(null, http, new ReachableProbe(),
            _ => throw new InvalidOperationException("No process should launch in this test."));
        var camera = new Camera { Ip = "192.0.2.1", Port = 554, User = "viewer" };

        var report = await media.TestConnectionAsync(camera, "secret", CancellationToken.None);

        Assert.False(report.Success);
        Assert.Collection(report.Steps,
            step => Assert.True(step.Ok),
            step => { Assert.True(step.Ok); Assert.Contains("192.0.2.1:554", step.Detail); },
            step => { Assert.False(step.Ok); Assert.Contains("ffmpeg não encontrado", step.Detail); });
        Assert.DoesNotContain("secret", report.Details());
    }

    [Fact]
    public async Task InvalidUrlProducesValidationDiagnosticWithoutProbing()
    {
        using var http = new HttpClient();
        var media = new CameraMedia(null, http, new ReachableProbe(),
            _ => throw new InvalidOperationException("No process should launch in this test."));

        var report = await media.TestConnectionAsync(new Camera { UseCustomRtspUrl = true },
            "not a URL", CancellationToken.None);

        Assert.False(report.Success);
        Assert.Equal(Camera.InvalidRtspUrlMessage, report.Summary);
        var step = Assert.Single(report.Steps);
        Assert.Equal("Validação", step.Name);
        Assert.False(step.Ok);
    }

    [Fact]
    public async Task DiagnosticCaptureRedactsUrlBeforeTruncatingLongOutput()
    {
        var url = $"rtsp://viewer:secret@camera/{new string('a', 5000)}?token=hidden";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"Input from '{url}'{Environment.NewLine}"));
        using var reader = new StreamReader(stream);
        var tail = new OutputTail();

        await ProcessSupport.CaptureAsync(reader, tail, hideRtspUrls: true);

        var output = tail.ToString();
        Assert.Contains("[URL RTSP ocultada]", output);
        Assert.DoesNotContain("secret", output);
        Assert.DoesNotContain("token=hidden", output);
    }

    [Fact]
    public async Task FailedVideoTestRedactsUrlInBothSummaryAndDetails()
    {
        using var http = new HttpClient();
        var executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var media = new CameraMedia(executable, http, new ReachableProbe(), _ =>
        {
            var info = ProcessSupport.Hidden(executable, []);
            info.Arguments = "/c echo rtsp://viewer:secret@camera/stream?token=hidden & exit /b 1";
            info.RedirectStandardOutput = true;
            return Process.Start(info)!;
        });
        var camera = new Camera { UseCustomRtspUrl = true };

        var report = await media.TestConnectionAsync(camera,
            "rtsp://viewer:secret@camera/stream?token=hidden", CancellationToken.None);

        Assert.False(report.Success);
        Assert.Collection(report.Steps,
            step => Assert.True(step.Ok),
            step => Assert.True(step.Ok),
            step => Assert.False(step.Ok));
        Assert.Contains("[URL RTSP ocultada]", report.Summary);
        Assert.DoesNotContain("secret", report.Summary);
        Assert.DoesNotContain("token=hidden", report.Details());
    }

    private sealed class ReachableProbe : ICameraHealthProbe
    {
        public Task<CameraProbeResult> ProbeAsync(string host, int port, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CameraProbeResult(true));
    }
}
