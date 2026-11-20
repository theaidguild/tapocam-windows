using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TapoCam.App.Player;

internal sealed class LivePlayer : Grid, IDisposable
{
    private static Task<CoreWebView2Environment>? environment;
    private static readonly string RuntimeFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TapoCam");
    private const string DiagnosticsScript = """
        (() => {
            const video = document.querySelector('video');
            if (!video) return null;
            const frames = video.getVideoPlaybackQuality?.();
            return {
                currentTime: video.currentTime, playbackRate: video.playbackRate,
                paused: video.paused, readyState: video.readyState, muted: video.muted,
                totalFrames: frames?.totalVideoFrames, droppedFrames: frames?.droppedVideoFrames,
                buffered: Array.from({length: video.buffered.length}, (_, i) =>
                    [video.buffered.start(i), video.buffered.end(i)]),
                width: video.videoWidth
            };
        })()
        """;

    private readonly WebView2CompositionControl view = new() { DefaultBackgroundColor = System.Drawing.Color.Black };
    private readonly TextBlock message = new()
    {
        Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly DispatcherTimer retry = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer debug = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly bool grid;
    private string mode;
    private Uri source;
    private bool disposed;
    private bool initialized;
    private bool debugBusy;

    public LivePlayer(string url, string mode, bool grid)
    {
        this.grid = grid;
        this.mode = mode;
        source = new Uri(url);
        Background = Brushes.Black;
        Children.Add(view);
        Children.Add(message);
        Loaded += Initialize;
        Unloaded += (_, _) => Dispose();
        retry.Tick += (_, _) =>
        {
            retry.Stop();
            if (!disposed)
                view.CoreWebView2?.Navigate(source.AbsoluteUri);
        };
        debug.Tick += WriteDiagnostics;
    }

    public bool Debug
    {
        set
        {
            if (value && !disposed)
                debug.Start();
            else
                debug.Stop();
        }
    }

    public void SetSource(string url, string playerMode)
    {
        var uri = new Uri(url);
        if (uri == source)
            return;
        mode = playerMode;
        source = uri;
        retry.Stop();
        if (initialized && !disposed)
            Navigate();
    }

    private void Navigate()
    {
        var core = view.CoreWebView2;
        core.IsMuted = grid || mode == "mse";
        core.Navigate(source.AbsoluteUri);
    }

    private static Task<CoreWebView2Environment> GetEnvironment()
    {
        if (environment is null || environment.IsFaulted || environment.IsCanceled)
            environment = CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(RuntimeFolder, "WebView2"),
                options: new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
        return environment;
    }

    private async void Initialize(object sender, RoutedEventArgs args)
    {
        Loaded -= Initialize;
        try
        {
            var shared = await GetEnvironment();
            if (disposed)
                return;
            await view.EnsureCoreWebView2Async(shared);
            if (disposed)
                return;
            var core = view.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.NavigationStarting += GuardNavigation;
            core.FrameNavigationStarting += GuardNavigation;
            core.NewWindowRequested += (_, eventArgs) => eventArgs.Handled = true;
            core.DownloadStarting += (_, eventArgs) => eventArgs.Cancel = true;
            core.PermissionRequested += (_, eventArgs) => eventArgs.State = CoreWebView2PermissionState.Deny;
            core.NavigationCompleted += NavigationCompleted;
            var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Player", "Assets", "grid-preview.js"));
            if (disposed)
                return;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                $"(() => {{ const tapoGrid = {(grid ? "true" : "false")};\n{script}\n}})();");
            if (disposed)
                return;
            initialized = true;
            Navigate();
        }
        catch (Exception)
        {
            if (!disposed)
                message.Text = "Não foi possível iniciar o player. Verifique a instalação do Microsoft Edge WebView2 Runtime.";
        }
    }

    private void GuardNavigation(object? sender, CoreWebView2NavigationStartingEventArgs args) =>
        args.Cancel = !Uri.TryCreate(args.Uri, UriKind.Absolute, out var navigation) || navigation != source;

    private void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (disposed)
            return;
        if (args.IsSuccess)
        {
            message.Text = "";
            retry.Stop();
        }
        else if (args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
        {
            message.Text = "Reconectando o vídeo…";
            retry.Start();
        }
    }

    private async void WriteDiagnostics(object? sender, EventArgs args)
    {
        if (!initialized || disposed || debugBusy)
            return;
        debugBusy = true;
        try
        {
            var selectedMode = mode;
            var json = await view.CoreWebView2.ExecuteScriptAsync(DiagnosticsScript);
            if (disposed || !debug.IsEnabled || json == "null")
                return;
            using var diagnostics = JsonDocument.Parse(json);
            var line = JsonSerializer.Serialize(new { mode = selectedMode, video = diagnostics.RootElement });
            File.AppendAllText(Path.Combine(RuntimeFolder, "player-debug.log"),
                $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()} {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or JsonException)
        {
        }
        finally
        {
            debugBusy = false;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        retry.Stop();
        debug.Stop();
        try
        {
            if (view.CoreWebView2 is { } core)
            {
                core.NavigationStarting -= GuardNavigation;
                core.FrameNavigationStarting -= GuardNavigation;
                core.NavigationCompleted -= NavigationCompleted;
                core.Stop();
                core.Navigate("about:blank");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
        finally
        {
            view.Dispose();
        }
    }
}
