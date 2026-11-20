using System.Diagnostics;
using System.IO;
using TapoCam.Core;
using TapoCam.Core.Models;

namespace TapoCam.App.Runtime;

internal sealed class CameraSession(CameraHealthMonitor health, Action<string, string> notify,
    Action recordingChanged, Func<ProcessStartInfo, Process> startProcess)
{
    private readonly RetryBackoff _backoff = new();
    private Process? _process;
    private Task? _capture;
    private OutputTail? _tail;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _retryAt;

    public RecordingRequest? Request { get; set; }
    public string? SuppressedWindowKey { get; set; }
    public string? Error { get; set; }
    public string? Warning { get; set; }
    public string? ProbeWarning { get; set; }
    public string? ActivePath { get; private set; }
    public string? LastRecording { get; private set; }
    public bool IsRecording => _process is not null;
    public CameraHealthMonitor Health { get; } = health;
    public string? ProbeHost { get; set; }
    public int ProbePort { get; set; }
    public TimeSpan Elapsed(DateTimeOffset now) => IsRecording ? now - _startedAt : TimeSpan.Zero;

    public void ResetRetry()
    {
        _backoff.Reset();
        _retryAt = default;
    }

    public void DelayUntil(DateTimeOffset time)
    {
        if (time > _retryAt)
            _retryAt = time;
    }

    public void ResetHealth()
    {
        Health.Reset();
        ProbeHost = null;
        ProbeWarning = null;
        Warning = null;
    }

    public async Task ReconcileAsync(
        Camera camera, bool serverRunning, string? ffmpegPath, string folder,
        DateTimeOffset now, DateTime localNow, Func<string, bool> isReserved)
    {
        if (_process is { HasExited: true })
            await FinishAsync(camera, expected: Request is null || !serverRunning, now);
        if (!serverRunning || Request is null)
        {
            await StopAsync(camera, now);
            return;
        }
        if (IsRecording)
        {
            if (Elapsed(now) >= TimeSpan.FromSeconds(60))
                _backoff.Reset();
            return;
        }
        if (now < _retryAt)
            return;

        if (ffmpegPath is null || !File.Exists(ffmpegPath))
        {
            FailPermanently(camera, "ffmpeg não encontrado.");
            return;
        }

        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (IsFileError(ex))
        {
            FailPermanently(camera, $"Não foi possível criar a pasta de gravações: {CredentialMasker.Mask(ex.Message)}");
            return;
        }

        try
        {
            var path = NextFile(folder, localNow, at => FileNaming.VideoName(camera.Slug, at), isReserved);
            var input = RelayController.LocalRtsp(camera.StreamName(Request.Quality));
            _tail = new OutputTail();
            _process = startProcess(ProcessSupport.Hidden(ffmpegPath,
                FfmpegCommandBuilder.Recording(input, path, camera.EffectiveFps, Request.Audio)));
            _capture = ProcessSupport.CaptureAsync(_process.StandardError, _tail);
            ActivePath = path;
            _startedAt = now;
            Error = null;
            recordingChanged();
            notify($"Gravação iniciada: {camera.DisplayName}", "A gravação foi iniciada.");
        }
        catch (Exception ex) when (IsFileError(ex) || ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Retry(camera, CredentialMasker.Mask(ex.Message), now);
        }
    }

    public async Task StopAsync(Camera camera, DateTimeOffset now)
    {
        if (_process is not { } process)
            return;
        if (!process.HasExited)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                await process.StandardInput.WriteLineAsync("q".AsMemory(), timeout.Token);
                await process.StandardInput.FlushAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
            {
                ProcessSupport.Kill(process);
                using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await process.WaitForExitAsync(killed.Token); }
                catch (Exception waitError) when (waitError is OperationCanceledException or InvalidOperationException) { }
            }
        }
        await FinishAsync(camera, expected: true, now);
    }

    private async Task FinishAsync(Camera camera, bool expected, DateTimeOffset now)
    {
        if (_capture is { } capture)
            await ProcessSupport.DrainAsync(capture);
        _process?.Dispose();
        _process = null;
        _capture = null;
        var saved = false;
        if (ActivePath is { } path)
        {
            try
            {
                var file = new FileInfo(path);
                if (file.Exists && file.Length < 4096)
                    file.Delete();
                else if (file.Exists)
                {
                    LastRecording = path;
                    saved = true;
                }
            }
            catch (Exception ex) when (IsFileError(ex)) { }
        }
        ActivePath = null;
        recordingChanged();
        if (expected)
            notify($"Gravação encerrada: {camera.DisplayName}",
                saved ? "A gravação foi salva." : "Nenhum arquivo de gravação foi salvo.");
        else
        {
            if (now - _startedAt >= TimeSpan.FromSeconds(60))
                _backoff.Reset();
            Retry(camera, LogParser.LastLine(_tail?.ToString()), now);
        }
        _tail = null;
    }

    private void FailPermanently(Camera camera, string message)
    {
        Error = message;
        Request = null;
        if (_currentWindowKey is not null)
            SuppressedWindowKey = _currentWindowKey;
        notify($"Falha na gravação: {camera.DisplayName}", message);
    }

    private string? _currentWindowKey;

    public void SetCurrentWindow(string? key)
    {
        _currentWindowKey = key;
        if (key != SuppressedWindowKey)
            SuppressedWindowKey = null;
    }

    private void Retry(Camera camera, string? detail, DateTimeOffset now)
    {
        var seconds = _backoff.NextSeconds;
        DelayUntil(now.AddSeconds(seconds));
        if (string.IsNullOrWhiteSpace(detail))
            detail = "ffmpeg foi encerrado";
        Error = $"A gravação parou: {detail.TrimEnd('.')}. Tentando de novo em {seconds} s.";
        notify($"Gravação interrompida: {camera.DisplayName}", $"Tentando de novo em {seconds} s.");
    }

    internal static string NextFile(string folder, DateTime localNow, Func<DateTime, string> name, Func<string, bool> isReserved)
    {
        while (true)
        {
            var path = Path.GetFullPath(Path.Combine(folder, name(localNow)));
            if (!File.Exists(path) && !Directory.Exists(path) && !isReserved(path))
                return path;
            localNow = localNow.AddSeconds(1);
        }
    }

    internal static bool IsFileError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
