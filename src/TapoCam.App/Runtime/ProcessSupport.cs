using System.Diagnostics;
using System.IO;
using System.Text;
using TapoCam.Core;

namespace TapoCam.App.Runtime;

internal static class ProcessSupport
{
    public static ProcessStartInfo Hidden(string executable, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        return info;
    }

    public static async Task<(int ExitCode, string Output)> RunShortAsync(
        Func<ProcessStartInfo, Process> start, string executable, IEnumerable<string> args,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var output = new OutputTail();
        var info = Hidden(executable, args);
        info.RedirectStandardOutput = true;
        using var process = start(info);
        process.StandardInput.Close();
        var capture = Task.WhenAll(CaptureAsync(process.StandardOutput, output), CaptureAsync(process.StandardError, output));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var exitCode = -1;
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            exitCode = process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
        }
        await DrainAsync(capture).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return (exitCode, output.ToString());
    }

    public static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (AggregateException) { }
    }

    public static async Task DrainAsync(Task capture)
    {
        try { await capture.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (IOException) { }
    }

    public static async Task CaptureAsync(StreamReader reader, OutputTail tail)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                tail.Add(CredentialMasker.Mask(line));
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
}

internal sealed class OutputTail
{
    private const int Capacity = 4_000;
    private readonly StringBuilder _text = new(Capacity);

    public void Add(string line)
    {
        lock (_text)
        {
            _text.AppendLine(line);
            if (_text.Length > Capacity)
                _text.Remove(0, _text.Length - Capacity);
        }
    }

    public override string ToString()
    {
        lock (_text)
            return _text.ToString();
    }
}
