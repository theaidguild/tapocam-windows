using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TapoCam.App.Services;

internal sealed class SingleInstance : IDisposable
{
    private readonly string _name = $"TapoCam.{WindowsServices.CurrentUser()}.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();
    private Task? _server;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, _name, out var first);
        if (!first)
        {
            try { first = _mutex.WaitOne(0); }
            catch (AbandonedMutexException) { first = true; }
        }
        IsFirst = first;
    }

    public bool IsFirst { get; }

    public void Listen(Action activate) => _server = ListenAsync(activate, _stop.Token);

    public void ActivateExisting()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2500);
            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out var processId))
                _ = AllowSetForegroundWindow(processId);
            client.WriteByte(1);
        }
        catch (TimeoutException) { }
        catch (IOException) { }
    }

    private async Task ListenAsync(Action activate, CancellationToken token)
    {
        using var pipe = new NamedPipeServerStream(
            _name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var message = new byte[1];
        try
        {
            while (!token.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                try
                {
                    if (await pipe.ReadAsync(message, token).ConfigureAwait(false) == 1 && message[0] == 1)
                        activate();
                }
                catch (IOException) { }
                finally { pipe.Disconnect(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _server?.GetAwaiter().GetResult(); }
        finally
        {
            _stop.Dispose();
            if (IsFirst)
                _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
