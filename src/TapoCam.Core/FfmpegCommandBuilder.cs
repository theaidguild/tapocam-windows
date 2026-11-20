namespace TapoCam.Core;

public static class FfmpegCommandBuilder
{
    public static IReadOnlyList<string> Recording(
        string input,
        string output,
        int fps,
        bool audio)
    {
        var frame = 90_000 / Math.Max(1, fps);
        var half = frame / 2;
        var args = new List<string>
        {
            "-loglevel", "warning",
            "-nostats",
            "-rtsp_transport", "tcp",
            "-fflags", "+genpts",
            "-i", input,
            "-map", "0:v",
            "-c:v", "copy",
            "-bsf:v", $"setts=ts='if(eq(N,0),TS,if(lt(TS,PREV_OUTPTS+{half}),PREV_OUTPTS+{frame},TS))'"
        };

        if (audio)
        {
            args.AddRange(["-map", "0:a:0?", "-af", "asetpts=N/SR/TB+STARTPTS",
                "-c:a", "aac", "-ar", "16000", "-ac", "1", "-b:a", "48k"]);
        }
        else
        {
            args.AddRange(["-an"]);
        }

        args.AddRange(["-movflags", "+frag_keyframe+empty_moov+default_base_moof",
            "-f", "mp4", output]);
        return args;
    }

    public static IReadOnlyList<string> Snapshot(string localRtspUrl, string output) =>
    [
        "-nostdin",
        "-loglevel", "error",
        "-rtsp_transport", "tcp",
        "-i", localRtspUrl,
        "-frames:v", "1",
        "-q:v", "2",
        "-y", output
    ];

    public static IReadOnlyList<string> ConnectionTest(string cameraSdRtspUrl) =>
    [
        "-nostdin",
        "-loglevel", "error",
        "-rtsp_transport", "tcp",
        "-timeout", "8000000",
        "-i", cameraSdRtspUrl,
        "-frames:v", "1",
        "-f", "null",
        "-"
    ];

    public static int FrameDurationTicks(int fps) =>
        90_000 / Math.Max(1, fps);
}
