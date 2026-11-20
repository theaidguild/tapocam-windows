using System.IO;
using TapoCam.App.Runtime;
using TapoCam.Core;
using TapoCam.Core.Models;

namespace TapoCam.App.Tests;

public sealed class AppModelTests
{
    [Fact]
    public async Task DisconnectAllSuppressesCurrentWindowEvenAfterAnIndividualDisconnect()
    {
        var (model, camera, clock) = CreateModel();
        try
        {
            await model.TickAsync();
            Assert.Equal(camera.Id, model.NextScheduled?.CameraId);
            Assert.False(model.AnyConnectionWanted);

            clock.Now = clock.Now.AddMinutes(2);
            await model.TickAsync();
            Assert.True(model.GetCameraState(camera.Id).HasRecordingRequest);
            Assert.Equal(RelayState.Failed, model.RelayState);

            await model.DisconnectCameraAsync(camera.Id);
            await model.DisconnectAllAsync();
            await model.TickAsync();
            Assert.False(model.AnyConnectionWanted);
            Assert.False(model.GetCameraState(camera.Id).HasRecordingRequest);

            await model.ToggleRecordingAsync(camera.Id);
            Assert.True(model.GetCameraState(camera.Id).WantsConnection);
            Assert.True(model.GetCameraState(camera.Id).HasRecordingRequest);
        }
        finally { await model.ShutdownAsync(); }

        Assert.False(model.AnyConnectionWanted);
        Assert.False(model.GetCameraState(camera.Id).HasRecordingRequest);
        Assert.Equal(RelayState.Stopped, model.RelayState);
    }

    [Fact]
    public async Task ResumeDefersRelayAttemptForFiveSeconds()
    {
        var (model, camera, clock) = CreateModel();
        try
        {
            model.ConnectCamera(camera.Id);
            model.Resume();
            clock.Now = clock.Now.AddSeconds(4);
            await model.TickAsync();
            Assert.Equal(RelayState.Stopped, model.RelayState);
            Assert.True(model.GetCameraState(camera.Id).WantsConnection);

            clock.Now = clock.Now.AddSeconds(1);
            await model.TickAsync();
            Assert.Equal(RelayState.Failed, model.RelayState);
            Assert.Equal("go2rtc não encontrado dentro do app.", model.RelayError);
        }
        finally { await model.ShutdownAsync(); }
    }

    private static (AppModel Model, Camera Camera, TestClock Clock) CreateModel()
    {
        var camera = new Camera { Name = "Sala", Ip = "192.0.2.1", User = "viewer" };
        var clock = new TestClock { Now = new DateTimeOffset(2026, 11, 20, 7, 59, 0, TimeSpan.Zero) };
        var settings = new AppSettings
        {
            AutoConnect = false,
            Cameras = [camera],
            ScheduleRules = [new ScheduleRule { Hour = 8, Minute = 0, CameraIds = [camera.Id] }]
        };
        var runtime = Path.Combine(AppContext.BaseDirectory, $"model-test-{Guid.NewGuid():N}");
        var model = new AppModel(new SettingsStore(settings), new Secrets(), runtime, null, null,
            _ => throw new InvalidOperationException("No process should launch in these tests."),
            (_, _) => { }, clock: clock, timeZone: TimeZoneInfo.Utc);
        return (model, camera, clock);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SettingsStore(AppSettings settings) : ISettingsStore
    {
        public AppSettings Load() => settings;
        public void Save(AppSettings value) { }
    }

    private sealed class Secrets : ISecretStore
    {
        public string Get(Guid id) => "example";
        public void Set(Guid id, string password) { }
        public void Delete(Guid id) { }
    }
}
