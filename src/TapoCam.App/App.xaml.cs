using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
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
    private UpdateService? _updates;
    private Task? _stopping;
    private CancellationTokenSource? _updateCancellation;
    private bool _canClose;
    private bool _updateInProgress;
    private (Guid DirectoryId, int HelperProcessId)? _updateCleanup;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("pt-BR");
        _updateCleanup = ParseUpdateCleanup(e.Args);
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
            _updates = new UpdateService();
            _tray = new TrayMenu(_model, window, ShowWindow, ExitAsync, CheckForUpdatesAsync);
            _instance.Listen(() => Dispatcher.BeginInvoke(ShowWindow));
            SystemEvents.PowerModeChanged += PowerModeChanged;
            _model.Start();
            if (_updateCleanup is { } cleanup)
                _ = CleanupUpdateDirectoryAsync(cleanup.DirectoryId, cleanup.HelperProcessId);

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

    private async Task CheckForUpdatesAsync()
    {
        if (_updateInProgress || _model is null || _updates is null)
            return;

        _updateInProgress = true;
        using var cancellation = new CancellationTokenSource();
        _updateCancellation = cancellation;
        try
        {
            if (!WindowsServices.IsInstallerManaged)
            {
                MessageBox.Show("A atualização automática está disponível somente para instalações feitas pelo instalador. Substitua manualmente os arquivos da versão portátil.",
                    "Atualizações do TapoCam", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var currentVersion = CurrentVersion();
            var update = await _updates.CheckForUpdateAsync(currentVersion, cancellation.Token);
            if (update is null)
            {
                MessageBox.Show($"O TapoCam já está atualizado (versão {currentVersion}).",
                    "Atualizações do TapoCam", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                $"A versão {update.Version} está disponível (versão atual: {currentVersion}).\n\n" +
                "Deseja baixar e instalar agora? O TapoCam será fechado e reaberto após a atualização.",
                "Atualização do TapoCam", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;

            if (HasPendingOrActiveRecording())
            {
                MessageBox.Show("Encerre as gravações ativas e tente novamente.",
                    "Atualização do TapoCam", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var updaterPath = Path.Combine(AppContext.BaseDirectory, "TapoCam.Updater.exe");
            if (!File.Exists(updaterPath))
                throw new FileNotFoundException("O componente de atualização não foi encontrado.", updaterPath);

            WindowsServices.Notify("TapoCam", $"Baixando a versão {update.Version}…");
            var installerPath = await _updates.DownloadAndVerifyAsync(update, cancellation.Token);
            if (HasPendingOrActiveRecording())
            {
                DeleteUpdateDirectory(installerPath);
                MessageBox.Show("Uma gravação foi iniciada durante o download. Nenhuma atualização foi instalada; encerre as gravações e tente novamente.",
                    "Atualização do TapoCam", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Process? updater = null;
            try
            {
                var updateDirectory = Path.GetDirectoryName(installerPath)!;
                var helperPath = Path.Combine(updateDirectory, "TapoCam.Updater.exe");
                File.Copy(updaterPath, helperPath);
                var startInfo = new ProcessStartInfo(helperPath) { UseShellExecute = true };
                startInfo.ArgumentList.Add(installerPath);
                startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TapoCam.App.exe"));
                updater = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Não foi possível iniciar o componente de atualização.");
                await StopAsync();
            }
            catch
            {
                if (updater is not null)
                {
                    try
                    {
                        if (!updater.HasExited)
                            updater.Kill(entireProcessTree: true);
                        updater.WaitForExit(5_000);
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                    updater.Dispose();
                }
                DeleteUpdateDirectory(installerPath);
                throw;
            }
            updater?.Dispose();

            _canClose = true;
            Shutdown();
        }
        catch (HttpRequestException ex)
        {
            MessageBox.Show($"Não foi possível consultar os Releases do GitHub: {CredentialMasker.Mask(ex.Message)}",
                "Atualização do TapoCam", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (OperationCanceledException)
        {
            if (cancellation.IsCancellationRequested)
                return;
            MessageBox.Show("A verificação ou o download da atualização excedeu o tempo limite.",
                "Atualização do TapoCam", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (InvalidDataException ex)
        {
            MessageBox.Show($"Os dados ou o instalador do Release são inválidos: {CredentialMasker.Mask(ex.Message)}",
                "Atualização do TapoCam", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (JsonException ex)
        {
            MessageBox.Show($"A resposta de atualização do GitHub é inválida: {CredentialMasker.Mask(ex.Message)}",
                "Atualização do TapoCam", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show($"Não foi possível preparar a atualização: {CredentialMasker.Mask(ex.Message)}",
                "Atualização do TapoCam", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _updateInProgress = false;
            _updateCancellation = null;
        }
    }

    private bool HasPendingOrActiveRecording() =>
        _model?.Settings.Cameras.Any(camera =>
        {
            var state = _model.GetCameraState(camera.Id);
            return state.IsRecording || state.HasRecordingRequest;
        }) == true;

    private static Version CurrentVersion()
    {
        var version = typeof(App).Assembly.GetName().Version
            ?? throw new InvalidOperationException("A versão atual do TapoCam não está disponível.");
        if (version.Build < 0)
            throw new InvalidOperationException("A versão atual do TapoCam não é válida.");
        return new Version(version.Major, version.Minor, version.Build);
    }

    private static void DeleteUpdateDirectory(string installerPath)
    {
        try { Directory.Delete(Path.GetDirectoryName(installerPath)!, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static (Guid DirectoryId, int HelperProcessId)? ParseUpdateCleanup(string[] args)
    {
        var directoryIndex = Array.IndexOf(args, "--cleanup-update");
        var processIndex = Array.IndexOf(args, "--update-helper-pid");
        if (directoryIndex < 0 && processIndex < 0)
            return null;
        if (directoryIndex < 0 || processIndex < 0 ||
            directoryIndex + 1 >= args.Length || processIndex + 1 >= args.Length ||
            !Guid.TryParseExact(args[directoryIndex + 1], "N", out var directoryId) ||
            !int.TryParse(args[processIndex + 1], out var processId) ||
            processId <= 0)
            return null;
        return (directoryId, processId);
    }

    private static async Task CleanupUpdateDirectoryAsync(Guid directoryId, int helperProcessId)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TapoCam", "Updates", directoryId.ToString("N"));
        var helperPath = Path.Combine(directory, "TapoCam.Updater.exe");

        try
        {
            using var helper = Process.GetProcessById(helperProcessId);
            if (!string.Equals(helper.MainModule?.FileName, helperPath, StringComparison.OrdinalIgnoreCase))
                return;
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (ArgumentException)
        {
        }
        catch (TimeoutException)
        {
            return;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task ExitAsync()
    {
        try
        {
            _updateCancellation?.Cancel();
            while (_updateInProgress)
                await Task.Delay(25);
            await StopAsync();
        }
        finally
        {
            _canClose = true;
            Shutdown();
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _updateCancellation?.Cancel();
        var stopping = StopAsync();
        if (!stopping.IsCompleted || _updateInProgress)
        {
            var frame = new DispatcherFrame();
            var deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            deadline.Tick += (_, _) => frame.Continue = false;
            var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            poll.Tick += (_, _) =>
            {
                if (stopping.IsCompleted && !_updateInProgress)
                    frame.Continue = false;
            };
            deadline.Start();
            poll.Start();
            Dispatcher.PushFrame(frame);
            deadline.Stop();
            poll.Stop();
        }
        _canClose = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        WindowsServices.SetRecordingPowerState(false);
        _tray?.Dispose();
        _updates?.Dispose();
        _processes?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
