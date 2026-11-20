using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using TapoCam.Core;
using TapoCam.Core.Models;

namespace TapoCam.App.Runtime;

public sealed record StatusInfo(string Text, string Color);

public sealed record CameraState(StatusInfo Status, bool WantsConnection, bool IsConnected,
    bool IsRecording, bool HasRecordingRequest, string Elapsed, string? Error,
    string? Warning, string? LastRecording, bool IsActive);

// All model state and UI notifications are owned by the app dispatcher.
public sealed class AppModel
{
    private readonly ISettingsStore _settingsStore;
    private readonly ISecretStore _secrets;
    private readonly string? _ffmpeg;
    private readonly Action<string, string> _notify;
    private readonly Action<bool> _preventSleep;
    private readonly Func<ProcessStartInfo, Process> _startProcess;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _timeZone;
    private readonly ICameraHealthProbe _probe;
    private readonly RelayController _relay;
    private readonly CameraMedia _media;
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        MaxResponseContentBufferSize = 32 * 1024 * 1024
    };
    private readonly CameraConnections _connections = new();
    private readonly RetryBackoff _relayBackoff = new();
    private readonly Dictionary<Guid, CameraSession> _sessions = [];
    private readonly Dictionary<Guid, string> _passwords = [];
    private readonly HashSet<string> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset _relayRetryAt;
    private DateTimeOffset _resumeUntil;
    private DateTimeOffset? _lastRetention;
    private DateTimeOffset? _lastLogRead;
    private Guid? _selectedCameraId;
    private bool _relayBusy;
    private bool _sleepBlocked;
    private bool _started;
    private bool _shuttingDown;
    private Task? _shutdown;

    public AppModel(ISettingsStore settingsStore, ISecretStore secrets, string runtimeDirectory,
        string? go2rtcPath, string? ffmpegPath, Func<ProcessStartInfo, Process> startProcess,
        Action<string, string> notify, Action<bool>? preventSleep = null,
        TimeProvider? clock = null, TimeZoneInfo? timeZone = null, ICameraHealthProbe? probe = null)
    {
        _settingsStore = settingsStore;
        _secrets = secrets;
        _ffmpeg = ffmpegPath;
        _startProcess = startProcess;
        _notify = notify;
        _preventSleep = preventSleep ?? (_ => { });
        _clock = clock ?? TimeProvider.System;
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        _probe = probe ?? new TcpCameraHealthProbe();
        _relay = new RelayController(runtimeDirectory, go2rtcPath, _http, startProcess);
        _media = new CameraMedia(ffmpegPath, _http, _probe, startProcess);
        try { Settings = _settingsStore.Load(); }
        catch (Exception ex) when (IsSettingsError(ex))
        {
            Settings = new AppSettings();
            Error = $"Não foi possível carregar os ajustes: {CredentialMasker.Mask(ex.Message)}";
        }
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public event EventHandler? Changed;
    public AppSettings Settings { get; }
    public string? Error { get; private set; }
    public string? RelayError => _relay.Error;
    public string? LastPhoto { get; private set; }
    public RelayState RelayState => _relay.State;
    public bool PendingRestart { get; private set; }
    public ScheduleWindow? NextScheduled { get; private set; }
    public bool AnyConnectionWanted => Settings.Cameras.Any(IsRequested);
    public IReadOnlyList<string> ActiveRecordingPaths => _sessions.Values
        .Select(session => session.ActivePath).OfType<string>().ToArray();

    public Guid? SelectedCameraId
    {
        get => _selectedCameraId;
        set
        {
            if (_selectedCameraId == value)
                return;
            _selectedCameraId = value;
            Publish();
        }
    }

    public StatusInfo GlobalStatus => RelayState switch
    {
        RelayState.Running => new("Conectado", "Green"),
        RelayState.Starting => new("Conectando…", "Orange"),
        RelayState.Failed when AnyConnectionWanted => new("Falha, tentando de novo…", "Red"),
        RelayState.Failed => new("Falha", "Red"),
        _ when AnyConnectionWanted => new("Aguardando…", "Orange"),
        _ => new("Desconectado", "Gray")
    };

    public string TrayStatus => _sessions.Values.Count(session => session.IsRecording) switch
    {
        0 => GlobalStatus.Text,
        1 => "Gravando 1 câmera",
        var count => $"Gravando {count} câmeras"
    };

    public string? NextScheduleText
    {
        get
        {
            if (NextScheduled is not { } window || FindCamera(window.CameraId) is not { } camera)
                return null;
            var extra = Settings.Cameras.Count(c => c.Id != camera.Id && window.Rule.CameraIds.Contains(c.Id)
                && _connections.AllowsSchedule(c.Id) && IsActive(c));
            var start = TimeZoneInfo.ConvertTime(window.Start, _timeZone);
            var date = start.ToString("ddd HH:mm", CultureInfo.GetCultureInfo("pt-BR"));
            return $"Próxima gravação: {date} ({camera.DisplayName}{(extra > 0 ? $" +{extra}" : "")})";
        }
    }

    public CameraState GetCameraState(Guid id)
    {
        var camera = FindCamera(id);
        _sessions.TryGetValue(id, out var session);
        var configured = camera?.IsConfigured(GetPassword(id)) == true;
        var active = camera is { Enabled: true } && configured;
        var wanted = active && _connections.WantsConnection(id);
        var connected = wanted && RelayState == RelayState.Running && !_relayBusy;
        var status = CameraStatus.Resolve(camera?.Enabled == true, configured, wanted,
            connected ? RelayState.Running : _relayBusy ? RelayState.Starting : RelayState,
            session?.Health.Reachable, session?.Warning);
        return new(new(status.Text, status.Color.ToString()), wanted, connected,
            session?.IsRecording == true, session?.Request is not null,
            TimeFormatting.Elapsed(session?.Elapsed(Now) ?? TimeSpan.Zero),
            session?.Error, session?.ProbeWarning ?? session?.Warning, session?.LastRecording, active);
    }

    public void Start()
    {
        if (_started || _shuttingDown)
            return;
        _started = true;
        _timer.Start();
        if (Settings.AutoConnect && Settings.Cameras.Any(IsActive))
            ConnectAll();
        else
            Kick();
    }

    public void ResumeFromSleep() => Resume();

    public void Resume()
    {
        if (_shuttingDown)
            return;
        _resumeUntil = Now.AddSeconds(5);
        foreach (var session in _sessions.Values)
        {
            session.DelayUntil(_resumeUntil);
            session.ResetHealth();
        }
    }

    public void ConnectAll()
    {
        if (_shuttingDown)
            return;
        var cameras = Settings.Cameras.Where(IsActive).ToArray();
        if (cameras.Length == 0)
        {
            Error = "Adicione uma câmera com IP, usuário e senha em Ajustes.";
            Publish();
            return;
        }
        _connections.SetAll(true);
        ResetConnectErrors();
        foreach (var camera in cameras)
            PrepareConnection(camera);
        Kick();
    }

    public void ConnectCamera(Guid id)
    {
        if (_shuttingDown || FindCamera(id) is not { } camera)
            return;
        if (!IsActive(camera))
        {
            ShowIncompleteCamera();
            return;
        }
        _connections.Set(true, id);
        ResetConnectErrors();
        PrepareConnection(camera);
        Kick();
    }

    public Task DisconnectCameraAsync(Guid id) => RunSerialAsync(async () =>
    {
        if (FindCamera(id) is not { } camera)
            return;
        _connections.Set(false, id);
        var session = Session(id);
        session.Request = null;
        session.ResetHealth();
        Publish();
        await session.StopAsync(camera, Now);
        if (!AnyConnectionWanted && !IsRecording)
            await StopRelayAsync();
    });

    public Task DisconnectAllAsync() => RunSerialAsync(DisconnectAllCoreAsync);

    public Task ToggleRecordingAsync(Guid id) => RunSerialAsync(async () =>
    {
        if (FindCamera(id) is not { } camera)
            return;
        var session = Session(id);
        if (session.Request is not null || session.IsRecording)
        {
            session.SuppressedWindowKey = CurrentWindows(Now).GetValueOrDefault(id)?.Key;
            session.Request = null;
            Publish();
            await session.StopAsync(camera, Now);
        }
        else
        {
            if (!IsActive(camera))
            {
                ShowIncompleteCamera();
                return;
            }
            if (!AnyConnectionWanted)
                ResetConnectErrors();
            _connections.Set(true, id);
            session.Error = null;
            session.ResetRetry();
            session.Request = RecordingRequest.Manual(Settings);
        }
    });

    public Task ApplyChangesAsync() => RunSerialAsync(async () =>
    {
        if (!AnyConnectionWanted || RelayState != RelayState.Running)
            return;
        PendingRestart = false;
        _relayBusy = true;
        Publish();
        try
        {
            await StopAllRecordersAsync();
            ResetConnectErrors();
            await StopRelayAsync();
            if (AnyConnectionWanted && !_shuttingDown && Now >= _resumeUntil)
                await StartRelayAsync(BuildConfig());
        }
        finally { _relayBusy = false; }
    });

    public async Task<string?> SnapshotAsync(Guid id)
    {
        if (_shuttingDown || !GetCameraState(id).IsConnected || FindCamera(id) is not { } camera)
            return null;
        var session = Session(id);
        string? path = null;
        string? partial = null;
        try
        {
            var folder = Settings.RecordDir;
            Directory.CreateDirectory(folder);
            path = CameraSession.NextFile(folder, LocalNow.DateTime, at => FileNaming.PhotoName(camera.Slug, at),
                _snapshots.Contains);
            _snapshots.Add(path);
            partial = Path.Combine(folder, $".snap-{Guid.NewGuid():N}.partial.jpg");
            var error = await _media.SnapshotErrorAsync(camera, partial, _lifetime.Token);
            if (error is not null)
            {
                session.Error = $"Não foi possível salvar a foto: {error}";
                return null;
            }
            File.Move(partial, path);
            LastPhoto = path;
            return path;
        }
        catch (OperationCanceledException) when (_shuttingDown) { return null; }
        catch (Exception ex) when (CameraSession.IsFileError(ex) ||
                                   ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            session.Error = $"Não foi possível salvar a foto: {CredentialMasker.Mask(ex.Message)}";
            return null;
        }
        finally
        {
            if (path is not null)
                _snapshots.Remove(path);
            if (partial is not null)
                TryDelete(partial);
            Publish();
        }
    }

    public async Task<string> TestConnectionAsync(Camera camera, string password)
    {
        if (_shuttingDown)
            return "Teste cancelado.";
        try { return await _media.TestConnectionAsync(camera, password, _lifetime.Token); }
        catch (OperationCanceledException) when (_shuttingDown) { return "Teste cancelado."; }
    }

    public Task RemoveCameraAsync(Guid id) => RunSerialAsync(async () =>
    {
        if (FindCamera(id) is not { } camera)
            return;
        _connections.Remove(id);
        Settings.Cameras.Remove(camera);
        foreach (var rule in Settings.ScheduleRules)
            rule.CameraIds.Remove(id);
        if (_selectedCameraId == id)
            _selectedCameraId = null;
        if (Settings.SettingsCamera == id)
            Settings.SettingsCamera = null;
        _passwords.Remove(id);
        try { _secrets.Delete(id); }
        catch (Exception ex) when (IsSettingsError(ex))
        {
            Error = "Não foi possível apagar a senha da câmera.";
        }
        if (_sessions.TryGetValue(id, out var session))
        {
            session.Request = null;
            session.ResetHealth();
            await session.StopAsync(camera, Now);
            _sessions.Remove(id);
        }
        SaveSettings();
    });

    public string GetPassword(Guid id)
    {
        if (_passwords.TryGetValue(id, out var password))
            return password;
        try { password = _secrets.Get(id) ?? ""; }
        catch (Exception ex) when (IsSettingsError(ex)) { password = ""; }
        _passwords[id] = password;
        return password;
    }

    public void SetPassword(Guid id, string password)
    {
        if (password == GetPassword(id))
            return;
        try
        {
            if (password.Length == 0)
                _secrets.Delete(id);
            else
                _secrets.Set(id, password);
            _passwords[id] = password;
        }
        catch (Exception ex) when (IsSettingsError(ex))
        {
            Error = "Não foi possível salvar a senha. Tente de novo.";
            Notify("Senha não salva", Error);
        }
        Publish();
    }

    public Task SaveSettingsAsync()
    {
        SaveSettings();
        Kick();
        return Task.CompletedTask;
    }

    public void ClearErrors(Guid? cameraId = null)
    {
        if (cameraId is { } id)
        {
            if (_sessions.TryGetValue(id, out var session))
                session.Error = null;
        }
        else
        {
            Error = null;
            _relay.ClearError();
            foreach (var session in _sessions.Values)
                session.Error = null;
        }
        Publish();
    }

    public Task ShutdownAsync()
    {
        if (_shutdown is not null)
            return _shutdown;
        _shuttingDown = true;
        _timer.Stop();
        _lifetime.Cancel();
        _shutdown = ShutdownCoreAsync();
        return _shutdown;
    }

    private async Task ShutdownCoreAsync()
    {
        await _serial.WaitAsync();
        try { await DisconnectAllCoreAsync(); }
        finally
        {
            SetSleepBlocked(false);
            _http.Dispose();
            _serial.Release();
            Publish();
        }
    }

    public async Task TickAsync()
    {
        if (_shuttingDown || !_serial.Wait(0))
            return;
        try
        {
            var now = Now;
            await RunRetentionAsync(now);
            if (_shuttingDown)
                return;
            ReconcileSchedules(now);
            await ReconcileRelayAsync(now);
            if (_shuttingDown)
                return;
            await ReconcileSessionsAsync();
            await ReadWarningsAsync();
        }
        catch (Exception ex) when (CameraSession.IsFileError(ex) ||
                                   ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Error = $"Não foi possível atualizar as câmeras: {CredentialMasker.Mask(ex.Message)}";
        }
        finally
        {
            _serial.Release();
            Publish();
        }
    }

    private async Task RunSerialAsync(Func<Task> action)
    {
        if (_shuttingDown)
            return;
        await _serial.WaitAsync();
        try
        {
            if (!_shuttingDown)
                await action();
        }
        finally
        {
            _serial.Release();
            Kick();
        }
    }

    private void ReconcileSchedules(DateTimeOffset now)
    {
        var eligible = EligibleCameraIds();
        var windows = ScheduleCalculator.CurrentWindows(now, _timeZone, Settings.ScheduleRules, eligible);
        NextScheduled = ScheduleCalculator.NextScheduled(now, _timeZone, Settings.ScheduleRules, eligible);
        foreach (var camera in Settings.Cameras)
        {
            var session = Session(camera.Id);
            var window = windows.GetValueOrDefault(camera.Id);
            session.SetCurrentWindow(window?.Key);
            var request = session.Request;
            session.Request = RecordingRequest.ResolveScheduled(request, window, now,
                session.IsRecording, session.SuppressedWindowKey);
            if (request is null && session.Request is not null)
            {
                if (!_connections.WantsConnection(camera.Id))
                {
                    if (!AnyConnectionWanted)
                        ResetConnectErrors();
                    _connections.Set(true, camera.Id);
                }
                session.ResetRetry();
            }
        }
    }

    private async Task ReconcileRelayAsync(DateTimeOffset now)
    {
        if (_relay.HasUnexpectedExit)
        {
            await _relay.RegisterUnexpectedExitAsync();
            ResetHealth();
            if (AnyConnectionWanted)
            {
                var delay = ScheduleRelayRetry();
                Notify("TapoCam desconectou", $"O go2rtc foi encerrado. Tentando de novo em {delay} s.");
            }
        }

        if (!AnyConnectionWanted)
        {
            PendingRestart = false;
            if (!IsRecording && RelayState != RelayState.Stopped)
                await StopRelayAsync();
            return;
        }

        if (RelayState == RelayState.Running)
        {
            var config = BuildConfig();
            PendingRestart = !string.Equals(config, _relay.AppliedConfig, StringComparison.Ordinal);
            if (PendingRestart && !IsRecording && now >= _resumeUntil)
            {
                PendingRestart = false;
                await StopRelayAsync();
                if (AnyConnectionWanted && Now >= _resumeUntil)
                    await StartRelayAsync(BuildConfig());
            }
        }
        else if (now >= _relayRetryAt && now >= _resumeUntil)
        {
            await StartRelayAsync(BuildConfig());
        }
    }

    private async Task ReconcileSessionsAsync()
    {
        var now = Now;
        var local = TimeZoneInfo.ConvertTime(now, _timeZone).DateTime;
        var folder = Settings.RecordDir;
        var cameras = Settings.Cameras.ToArray();
        await Task.WhenAll(cameras.Select(async camera =>
        {
            var session = Session(camera.Id);
            var requested = IsRequested(camera);
            var running = RelayState == RelayState.Running && !_relayBusy && requested;
            if (!requested)
                session.Request = null;
            if (!running)
                session.ResetHealth();
            if (now < _resumeUntil)
                session.DelayUntil(_resumeUntil);
            if (running)
                _ = ProbeAsync(camera, session);
            await session.ReconcileAsync(camera, running, _ffmpeg, folder, now, local,
                path => _sessions.Values.Any(s => string.Equals(s.ActivePath, path, StringComparison.OrdinalIgnoreCase)));
        }));
    }

    private async Task ProbeAsync(Camera camera, CameraSession session)
    {
        var host = camera.Host;
        var port = camera.Port;
        if (session.ProbeHost != host || session.ProbePort != port)
        {
            session.ResetHealth();
            session.ProbeHost = host;
            session.ProbePort = port;
        }
        try
        {
            var update = await session.Health.CheckAsync(host, port, _lifetime.Token);
            if (update is null || _shuttingDown || !IsRequested(camera) || RelayState != RelayState.Running || _relayBusy
                || camera.Host != host || camera.Port != port)
                return;
            session.ProbeWarning = update.Result.Reachable ? null : update.Result.Message;
            if (update.Changed && Settings.NotifyCameraStatus)
            {
                if (update.Result.Reachable)
                    Notify($"{camera.DisplayName} voltou", "A câmera está acessível de novo.");
                else
                    Notify($"{camera.DisplayName} sem resposta", "Não foi possível acessar a câmera na rede.");
            }
            Publish();
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
    }

    private async Task ReadWarningsAsync()
    {
        var now = Now;
        if (RelayState != RelayState.Running || (_lastLogRead is { } previous && now - previous < TimeSpan.FromSeconds(2)))
            return;
        _lastLogRead = now;
        var log = await _relay.ReadLogTailAsync();
        var local = TimeZoneInfo.ConvertTime(now, _timeZone);
        foreach (var camera in Settings.Cameras)
            Session(camera.Id).Warning = IsRequested(camera) ? LogParser.RecentError(log, camera.Host, local) : null;
    }

    private async Task RunRetentionAsync(DateTimeOffset now)
    {
        var days = Settings.RetentionDays;
        if (days <= 0 || (_lastRetention is { } previous && now - previous < TimeSpan.FromHours(1)))
            return;
        _lastRetention = now;
        var folder = Settings.RecordDir;
        var active = ActiveRecordingPaths;
        try
        {
            await Task.Run(() =>
            {
                foreach (var path in RecordingRetention.ExpiredPaths(folder, days, now, active))
                    TryDelete(path);
            });
        }
        catch (Exception ex) when (CameraSession.IsFileError(ex)) { }
    }

    private async Task DisconnectAllCoreAsync()
    {
        _connections.SetAll(false);
        var windows = CurrentWindows(Now);
        PendingRestart = false;
        foreach (var (id, session) in _sessions)
        {
            session.Request = null;
            session.SuppressedWindowKey = windows.GetValueOrDefault(id)?.Key;
            session.ResetHealth();
        }
        foreach (var (id, window) in windows)
            Session(id).SuppressedWindowKey = window.Key;
        Publish();
        await StopAllRecordersAsync();
        if (!AnyConnectionWanted)
            await StopRelayAsync();
    }

    private Task StopAllRecordersAsync()
    {
        var now = Now;
        return Task.WhenAll(Settings.Cameras.ToArray().Select(camera => Session(camera.Id).StopAsync(camera, now)));
    }

    private async Task StartRelayAsync(string config)
    {
        if (_shuttingDown)
            return;
        _relayBusy = true;
        ResetHealth();
        try
        {
            await _relay.StartAsync(config, Publish, _lifetime.Token);
            if (RelayState == RelayState.Running)
            {
                _relayBackoff.Reset();
                _relayRetryAt = default;
            }
            else if (RelayState == RelayState.Failed)
                ScheduleRelayRetry();
        }
        finally { _relayBusy = false; }
    }

    private async Task StopRelayAsync()
    {
        ResetHealth();
        await _relay.StopAsync();
    }

    private int ScheduleRelayRetry()
    {
        var seconds = _relayBackoff.NextSeconds;
        _relayRetryAt = Now.AddSeconds(seconds);
        return seconds;
    }

    private void ResetHealth()
    {
        _lastLogRead = null;
        foreach (var session in _sessions.Values)
            session.ResetHealth();
    }

    private void PrepareConnection(Camera camera)
    {
        var session = Session(camera.Id);
        session.Error = null;
        session.ResetRetry();
        if (Settings.AutoRecord && session.Request is null)
            session.Request = RecordingRequest.Manual(Settings);
    }

    private void ResetConnectErrors()
    {
        Error = null;
        _relay.ClearError();
        _relayBackoff.Reset();
        _relayRetryAt = default;
    }

    private void ShowIncompleteCamera()
    {
        Error = "Preencha o IP, o usuário e a senha desta câmera em Ajustes.";
        Publish();
    }

    private void SaveSettings()
    {
        try { _settingsStore.Save(Settings); }
        catch (Exception ex) when (IsSettingsError(ex))
        {
            Error = $"Não foi possível salvar os ajustes: {CredentialMasker.Mask(ex.Message)}";
        }
    }

    private void SetSleepBlocked(bool blocked)
    {
        if (blocked == _sleepBlocked)
            return;
        _sleepBlocked = blocked;
        _preventSleep(blocked);
    }

    private void RecordingChanged()
    {
        SetSleepBlocked(IsRecording);
        Publish();
    }

    private void Notify(string title, string body)
    {
        try { _notify(title, body); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }
    private void Publish() => Changed?.Invoke(this, EventArgs.Empty);

    private void Kick()
    {
        Publish();
        if (_started && !_shuttingDown)
            _ = TickAsync();
    }

    private CameraSession Session(Guid id)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            session = new(new CameraHealthMonitor(_probe, _clock), Notify, RecordingChanged, _startProcess);
            _sessions.Add(id, session);
        }
        return session;
    }

    private Camera? FindCamera(Guid id) => Settings.Cameras.Find(camera => camera.Id == id);
    private bool IsActive(Camera camera) => camera.IsActive(GetPassword(camera.Id));
    private bool IsRequested(Camera camera) => _connections.WantsConnection(camera.Id) && IsActive(camera);
    private bool IsRecording => _sessions.Values.Any(session => session.IsRecording);
    private DateTimeOffset Now => _clock.GetUtcNow();
    private DateTimeOffset LocalNow => TimeZoneInfo.ConvertTime(Now, _timeZone);
    private Guid[] EligibleCameraIds() => Settings.Cameras
        .Where(c => _connections.AllowsSchedule(c.Id) && IsActive(c)).Select(c => c.Id).ToArray();
    private IReadOnlyDictionary<Guid, ScheduleWindow> CurrentWindows(DateTimeOffset now) =>
        ScheduleCalculator.CurrentWindows(now, _timeZone, Settings.ScheduleRules, EligibleCameraIds());
    private string BuildConfig() => Go2RtcConfigBuilder.Build(Settings.Cameras.Select(c => (c, GetPassword(c.Id))),
        _ffmpeg is not null && File.Exists(_ffmpeg) ? _ffmpeg : null, Settings.EffectivePlayerMode == "hls");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (CameraSession.IsFileError(ex)) { }
    }

    private static bool IsSettingsError(Exception ex) => CameraSession.IsFileError(ex)
        || ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.Text.Json.JsonException;
}
