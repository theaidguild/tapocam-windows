using TapoCam.App.Services;

namespace TapoCam.App.Tests;

public sealed class TrayStatusTests
{
    [Theory]
    [InlineData(0, "Conectado")]
    [InlineData(1, "Gravando 1 câmera")]
    [InlineData(3, "Gravando 3 câmeras")]
    public void RecordingStatusOverridesConnectionStatus(int recordings, string expected) =>
        Assert.Equal(expected, TrayMenu.StatusLine(recordings, "Conectado"));
}
