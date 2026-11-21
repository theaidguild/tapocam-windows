using System.Text;
using TapoCam.Core.Models;

namespace TapoCam.Core;

public static class Go2RtcConfigBuilder
{
    public static string Build(
        IEnumerable<(Camera Camera, string Secret)> cameras,
        string? ffmpegPath,
        bool audioMode)
    {
        ArgumentNullException.ThrowIfNull(cameras);
        var configured = cameras
            .Where(item => item.Camera.IsActive(item.Secret))
            .OrderBy(item => item.Camera.Id)
            .ToArray();

        var yaml = new StringBuilder()
            .AppendLine("api:")
            .AppendLine("  listen: \"127.0.0.1:11984\"")
            .AppendLine("rtsp:")
            .AppendLine("  listen: \"127.0.0.1:18554\"")
            .AppendLine("webrtc:")
            .AppendLine("  listen: \"127.0.0.1:18555\"")
            .AppendLine("log:")
            .AppendLine("  level: warn");

        if (!string.IsNullOrWhiteSpace(ffmpegPath))
        {
            yaml.AppendLine("ffmpeg:")
                .Append("  bin: ").AppendLine(Quote(ffmpegPath));
        }

        if (configured.Length == 0)
        {
            yaml.AppendLine("streams: {}");
            return yaml.ToString();
        }

        yaml.AppendLine("streams:");
        foreach (var (camera, secret) in configured)
        {
            foreach (var quality in new[] { StreamQuality.Hd, StreamQuality.Sd })
            {
                if (camera.UseCustomRtspUrl && quality == StreamQuality.Sd)
                    continue;
                var name = camera.StreamName(quality);
                var url = camera.RtspUrl(quality, secret);
                if (audioMode && !string.IsNullOrWhiteSpace(ffmpegPath))
                {
                    yaml.Append("  ").Append(name).AppendLine(":")
                        .Append("    - ").AppendLine(Quote(url))
                        .Append("    - ").Append(Quote($"ffmpeg:{name}#audio=aac")).AppendLine();
                }
                else
                {
                    yaml.Append("  ").Append(name).Append(": ").AppendLine(Quote(url));
                }
            }
        }

        return yaml.ToString();
    }

    private static string Quote(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
