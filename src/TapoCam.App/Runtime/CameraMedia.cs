using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using TapoCam.Core;
using TapoCam.Core.Models;

namespace TapoCam.App.Runtime;

internal sealed class CameraMedia(
    string? ffmpeg, HttpClient http, ICameraHealthProbe probe, Func<ProcessStartInfo, Process> startProcess)
{
    public async Task<string?> SnapshotErrorAsync(Camera camera, string path, CancellationToken cancellationToken)
    {
        var stream = camera.StreamName(StreamQuality.Hd);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.GetAsync($"{RelayController.ApiAddress}/api/frame.jpeg?src={stream}",
                HttpCompletionOption.ResponseContentRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
                if (bytes is [0xff, 0xd8, ..])
                {
                    await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                    return null;
                }
            }
        }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }

        if (ffmpeg is null || !File.Exists(ffmpeg))
            return "ffmpeg não encontrado. Instale com: winget install Gyan.FFmpeg.Essentials";
        var (code, output) = await ProcessSupport.RunShortAsync(startProcess, ffmpeg,
            FfmpegCommandBuilder.Snapshot(RelayController.LocalRtsp(stream), path),
            TimeSpan.FromSeconds(15), cancellationToken);
        if (code == 0 && File.Exists(path))
        {
            using var image = File.OpenRead(path);
            if (image.ReadByte() == 0xff && image.ReadByte() == 0xd8)
                return null;
        }
        return LastLine(output);
    }

    public async Task<string> TestConnectionAsync(Camera camera, string secret, CancellationToken cancellationToken)
    {
        if (!camera.IsConfigured(secret) || camera.RtspEndpoint(secret) is not { } endpoint)
            return camera.UseCustomRtspUrl ? Camera.InvalidRtspUrlMessage : "Preencha o IP, o usuário e a senha.";
        var result = await probe.ProbeAsync(endpoint.Host, endpoint.Port, cancellationToken);
        if (!result.Reachable)
            return result.Message ?? "Sem acesso à câmera.";
        if (ffmpeg is null || !File.Exists(ffmpeg))
            return "A câmera respondeu, mas o vídeo falhou: ffmpeg não encontrado. Instale com: winget install Gyan.FFmpeg.Essentials";
        try
        {
            var (code, output) = await ProcessSupport.RunShortAsync(startProcess, ffmpeg,
                FfmpegCommandBuilder.ConnectionTest(camera.RtspUrl(StreamQuality.Sd, secret)),
                TimeSpan.FromSeconds(15), cancellationToken);
            if (code == 0)
                return "Conexão OK: a câmera enviou vídeo.";
            if (output.Contains("401", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
                return camera.UseCustomRtspUrl
                    ? "Usuário ou senha incorretos. Confira as credenciais na URL RTSP."
                    : "Usuário ou senha incorretos. Use a Conta da Câmera criada no app Tapo.";
            return $"A câmera respondeu, mas o vídeo falhou: {LastLine(output)}";
        }
        catch (Exception ex) when (CameraSession.IsFileError(ex) ||
                                   ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"A câmera respondeu, mas o vídeo falhou: {CredentialMasker.Mask(ex.Message)}";
        }
    }

    private static string LastLine(string output) =>
        LogParser.LastLine(output) is { Length: > 0 } line ? line : "Nenhum vídeo foi recebido.";
}
