using TapoCam.Core.Models;

namespace TapoCam.Core;

public static class PlayerUrlBuilder
{
    public static string Grid(Camera camera) => Focused(camera, StreamQuality.Sd, includeAudio: false);

    public static string Focused(Camera camera, StreamQuality quality, bool includeAudio) =>
        $"http://127.0.0.1:11984/stream.html?src={camera.StreamName(quality)}&mode=mse&media={Uri.EscapeDataString(includeAudio ? "video,audio" : "video")}";

    public static string NormalizeMode(string? mode) =>
        string.Equals(mode, "mse", StringComparison.OrdinalIgnoreCase) ? "mse" : "hls";
}
