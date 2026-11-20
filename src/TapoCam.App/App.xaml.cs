using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using TapoCam.App.Runtime;
using TapoCam.App.Services;
using TapoCam.Core;

namespace TapoCam.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private ChildProcesses? _processes;
    private AppModel? _model;
    private TrayMenu? _tray;
    private Task? _stopping;
    private bool _canClose;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("pt-BR");
        try
        {
            _instance = new SingleInstance();
            if (!_instance.IsFirst)
            {
                _instance.ActivateExisting();
                Shutdown();
                return;
            }

            var runtime = WindowsServices.CreateRuntimeDirectory();
            _processes = new ChildProcesses();
            _model = new AppModel(
                new JsonSettingsStore(WindowsServices.SettingsPath), new CredentialStore(),
                runtime, WindowsServices.FindTool("go2rtc.exe"), WindowsServices.FindTool("ffmpeg.exe"),
                _processes.Start, WindowsServices.Notify, WindowsServices.SetRecordingPowerState);
            var window = new MainWindow(_model)
            {
                Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/Camera.ico"))
            };
            MainWindow = window;
            window.Closing += (_, args) =>
            {
                if (!_canClose)
                {
                    args.Cancel = true;
                    window.Hide();
                }
            };
            window.StateChanged += (_, _) =>
            {
                if (window.WindowState == WindowState.Minimized)
                    window.Hide();
            };
            _tray = new TrayMenu(_model, window, ShowWindow, ExitAsync);
            _instance.Listen(() => Dispatcher.BeginInvoke(ShowWindow));
            SystemEvents.PowerModeChanged += PowerModeChanged;
            _model.Start();

            if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
                ShowWindow();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não foi possível iniciar o TapoCam: {CredentialMasker.Mask(ex.Message)}",
                "TapoCam", MessageBoxButton.OK, MessageBoxImage.Error);
            _canClose = true;
            Shutdown(1);
        }
    }

    private void ShowWindow()
    {
        if (_stopping is not null || MainWindow is not { } window)
            return;
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    }

    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            _ = Dispatcher.BeginInvoke(() => _model?.Resume());
    }

    private Task StopAsync() => _stopping ??= _model?.ShutdownAsync() ?? Task.CompletedTask;

    private async Task ExitAsync()
    {
        try { await StopAsync(); }
        finally
        {
            _canClose = true;
            Shutdown();
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        var stopping = StopAsync();
        if (!stopping.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            deadline.Tick += (_, _) => frame.Continue = false;
            _ = stopping.ContinueWith(_ => Dispatcher.BeginInvoke(() => frame.Continue = false),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            deadline.Start();
            Dispatcher.PushFrame(frame);
            deadline.Stop();
        }
        _canClose = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        WindowsServices.SetRecordingPowerState(false);
        _tray?.Dispose();
        _processes?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
