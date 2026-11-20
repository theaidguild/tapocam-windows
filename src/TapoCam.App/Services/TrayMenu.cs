using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using TapoCam.App.Runtime;
using TapoCam.Core;

namespace TapoCam.App.Services;

internal sealed class TrayMenu : IDisposable
{
    private readonly TaskbarIcon _tray;
    private readonly AppModel _model;
    private readonly MainWindow _window;
    private readonly Action _show;
    private readonly Func<Task> _exit;
    private readonly BitmapImage _camera = LoadIcon("Camera");
    private readonly BitmapImage _recording = LoadIcon("Recording");

    public TrayMenu(AppModel model, MainWindow window, Action show, Func<Task> exit)
    {
        _model = model;
        _window = window;
        _show = show;
        _exit = exit;
        _tray = new TaskbarIcon
        {
            IconSource = _camera,
            ContextMenu = new ContextMenu(),
            DoubleClickCommand = new RelayCommand(show)
        };
        _tray.PreviewTrayContextMenuOpen += (_, _) => BuildMenu();
        _model.Changed += Update;
        Update(null, EventArgs.Empty);
        _tray.ForceCreate(false);
    }

    private int RecordingCount => _model.Settings.Cameras.Count(c => _model.GetCameraState(c.Id).IsRecording);

    internal static string StatusLine(int count, string globalStatus) => count switch
    {
        0 => globalStatus,
        1 => "Gravando 1 câmera",
        _ => $"Gravando {count} câmeras"
    };

    private void Update(object? sender, EventArgs args)
    {
        var count = RecordingCount;
        _tray.IconSource = count > 0 ? _recording : _camera;
        _tray.ToolTipText = StatusLine(count, _model.GlobalStatus.Text);
    }

    private void BuildMenu()
    {
        var items = _tray.ContextMenu.Items;
        items.Clear();
        items.Add(Item(StatusLine(RecordingCount, _model.GlobalStatus.Text)));
        if (!string.IsNullOrEmpty(_model.NextScheduleText))
            items.Add(Item(_model.NextScheduleText));
        items.Add(new Separator());

        foreach (var camera in _model.Settings.Cameras)
        {
            var id = camera.Id;
            var state = _model.GetCameraState(id);
            var group = new MenuItem
            {
                Header = state.IsRecording ? $"● {camera.DisplayName} — {state.Elapsed}" : camera.DisplayName
            };
            group.Items.Add(Item(state.Status.Text));
            group.Items.Add(state.WantsConnection
                ? Item("Desconectar câmera", () => _model.DisconnectCameraAsync(id), state.IsActive)
                : Item("Conectar câmera", () => { _model.ConnectCamera(id); return Task.CompletedTask; }, state.IsActive));
            group.Items.Add(Item(state.IsRecording || state.HasRecordingRequest ? "Parar gravação" : "Gravar agora",
                () => _model.ToggleRecordingAsync(id), state.IsActive));
            group.Items.Add(Item("Tirar foto", async () => { await _model.SnapshotAsync(id); }, state.IsConnected));
            group.Items.Add(Item("Mostrar", () => ShowThen(() => _window.FocusCamera(id))));
            items.Add(group);
        }

        items.Add(new Separator());
        items.Add(Item("Abrir janela", () => ShowThen()));
        items.Add(Item("Gravações…", () => ShowThen(_window.OpenRecordings)));
        items.Add(Item("Ajustes…", () => ShowThen(_window.OpenSettings)));
        items.Add(_model.AnyConnectionWanted
            ? Item("Desconectar todas", _model.DisconnectAllAsync)
            : Item("Conectar todas", () => { _model.ConnectAll(); return Task.CompletedTask; }));
        items.Add(new Separator());
        items.Add(Item("Sair do TapoCam", _exit));
    }

    private Task ShowThen(Action? action = null)
    {
        _show();
        action?.Invoke();
        return Task.CompletedTask;
    }

    private static MenuItem Item(string title, Func<Task>? action = null, bool enabled = true)
    {
        var item = new MenuItem { Header = title, IsEnabled = action is not null && enabled };
        if (action is not null)
            item.Click += async (_, _) =>
            {
                try { await action(); }
                catch (Exception ex) { MessageBox.Show(CredentialMasker.Mask(ex.Message), "TapoCam"); }
            };
        return item;
    }

    private static BitmapImage LoadIcon(string name) =>
        new(new Uri($"pack://application:,,,/Assets/{name}.ico"));

    public void Dispose()
    {
        _model.Changed -= Update;
        _tray.Dispose();
    }
}
