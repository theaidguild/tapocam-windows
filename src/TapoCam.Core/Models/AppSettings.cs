using System.Text.Json.Serialization;

namespace TapoCam.Core.Models;

public sealed class AppSettings
{
    public List<Camera> Cameras { get; set; } = [];
    public List<ScheduleRule> ScheduleRules { get; set; } = [];
    public int GridTileWidth { get; set; } = 320;
    public string ViewQuality { get; set; } = "hd";
    public string PlayerMode { get; set; } = "hls";
    public string RecordQuality { get; set; } = "hd";
    public bool RecordAudio { get; set; } = true;
    public string RecordDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos", "TapoCam");
    public int RetentionDays { get; set; }
    public bool AutoConnect { get; set; } = true;
    public bool AutoRecord { get; set; }
    public bool NotifyCameraStatus { get; set; } = true;
    public Guid? SettingsCamera { get; set; }
    public int SettingsTab { get; set; }
    public bool DebugPlayer { get; set; }

    [JsonIgnore]
    public StreamQuality EffectiveViewQuality => StreamQualityExtensions.Parse(ViewQuality);
    [JsonIgnore]
    public StreamQuality EffectiveRecordQuality => StreamQualityExtensions.Parse(RecordQuality);
    [JsonIgnore]
    public string EffectivePlayerMode => PlayerUrlBuilder.NormalizeMode(PlayerMode);
}
