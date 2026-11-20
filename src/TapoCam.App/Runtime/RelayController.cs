using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using TapoCam.App.Services;
using TapoCam.Core;

namespace TapoCam.App.Runtime;

internal sealed class RelayController(
    string directory, string? executable, HttpClient http, Func<ProcessStartInfo, Process> startProcess)
{
    public const string ApiAddress = "http://127.0.0.1:11984";
    public static string LocalRtsp(string streamName) => $"rtsp://127.0.0.1:18554/{streamName}";

    private string ConfigPath => Path.Combine(directory, "go2rtc.yaml");
    private string LogPath => Path.Combine(directory, "go2rtc.log");
    private string PidPath => Path.Combine(directory, "go2rtc.pid");
    private Process? _process;
    private Task? _capture;

    public RelayState State { get; private set; }
    public string? AppliedConfig { get; private set; }
    public string? Error { get; private set; }
    public bool HasUnexpectedExit => State == RelayState.Running && _process is { HasExited: true };

    public async Task StartAsync(string config, Action stateChanged, CancellationToken cancellationToken)
    {
        Error = null;
        if (executable is null || !File.Exists(executable))
        {
            Fail("go2rtc não encontrado dentro do app.");
            return;
        }

        State = RelayState.Starting;
        stateChanged();
        try
        {
            Directory.CreateDirectory(directory);
            await KillStaleProcessAsync(cancellationToken);
            WindowsServices.WritePrivateText(ConfigPath, config);
            WindowsServices.WritePrivateText(LogPath, "");
            var startInfo = ProcessSupport.Hidden(executable, ["-config", ConfigPath]);
            startInfo.WorkingDirectory = directory;
            startInfo.RedirectStandardOutput = true;
            _process = startProcess(startInfo);
            _process.StandardInput.Close();
            _capture = CaptureLogAsync(_process.StandardOutput, _process.StandardError);
            File.WriteAllText(PidPath, _process.Id.ToString(CultureInfo.InvariantCulture));

            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(6) && !_process.HasExited)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var remaining = TimeSpan.FromSeconds(6) - watch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    break;
                deadline.CancelAfter(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1));
                try
                {
                    using var response = await http.GetAsync($"{ApiAddress}/api",
                        HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    if (response.StatusCode == HttpStatusCode.OK && !_process.HasExited)
                    {
                        State = RelayState.Running;
                        AppliedConfig = config;
                        return;
                    }
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
            await StopAsync();
            Fail($"O go2rtc não respondeu. Veja o log: {LogPath}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await StopAsync();
            Fail($"Falha ao iniciar o go2rtc: {CredentialMasker.Mask(ex.Message)}");
        }
    }

    public async Task RegisterUnexpectedExitAsync()
    {
        await StopAsync();
        Fail($"O go2rtc foi encerrado inesperadamente. Veja o log: {LogPath}");
    }

    public async Task StopAsync()
    {
        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited)
                {
                    if (process.CloseMainWindow())
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await process.WaitForExitAsync(timeout.Token);
                    }
                    else
                    {
                        ProcessSupport.Kill(process);
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await process.WaitForExitAsync(timeout.Token);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
            {
                ProcessSupport.Kill(process);
            }
            if (_capture is { } capture)
                await ProcessSupport.DrainAsync(capture);
            process.Dispose();
        }
        _process = null;
        _capture = null;
        State = RelayState.Stopped;
        AppliedConfig = null;
        Error = null;
        TryDeletePid();
    }

    public async Task<string> ReadLogTailAsync()
    {
        try
        {
            await using var log = new FileStream(LogPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 8192, useAsync: true);
            var size = (int)Math.Min(log.Length, 8192);
            log.Seek(-size, SeekOrigin.End);
            var bytes = new byte[size];
            var length = await log.ReadAtLeastAsync(bytes, size, throwOnEndOfStream: false);
            return CredentialMasker.Mask(Encoding.UTF8.GetString(bytes, 0, length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private async Task KillStaleProcessAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(PidPath))
            return;
        try
        {
            if (int.TryParse(File.ReadAllText(PidPath), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                && pid > 0 && pid != Environment.ProcessId)
            {
                using var previous = Process.GetProcessById(pid);
                if (string.Equals(Path.GetFileName(previous.MainModule?.FileName), "go2rtc.exe",
                        StringComparison.OrdinalIgnoreCase))
                {
                    ProcessSupport.Kill(previous);
                    await Task.Delay(300, cancellationToken);
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        TryDeletePid();
    }

    private async Task CaptureLogAsync(StreamReader stdout, StreamReader stderr)
    {
        try
        {
            await using var file = new FileStream(LogPath, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            await using var writer = new StreamWriter(file) { AutoFlush = true };
            using var gate = new SemaphoreSlim(1);
            await Task.WhenAll(PumpAsync(stdout), PumpAsync(stderr)).ConfigureAwait(false);

            async Task PumpAsync(StreamReader reader)
            {
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        await writer.WriteLineAsync(CredentialMasker.Mask(line)).ConfigureAwait(false);
                    }
                    catch (IOException) { }
                    finally { gate.Release(); }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    private void Fail(string message)
    {
        Error = message;
        State = RelayState.Failed;
    }

    public void ClearError() => Error = null;

    private void TryDeletePid()
    {
        try { File.Delete(PidPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
