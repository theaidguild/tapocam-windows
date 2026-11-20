using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TapoCam.App.Runtime;
using TapoCam.App.Services;
using TapoCam.App.Views;
using TapoCam.Core;
using TapoCam.Core.Models;

namespace TapoCam.App;

public partial class MainWindow : Window
{
    private readonly AppModel model;
    private readonly Dictionary<Guid, CameraTile> tiles = [];
    private readonly Dictionary<Guid, (TextBlock Dot, TextBlock Recording, Button Button)> sidebarRows = [];
    private Button? allCameras;
    private WrapPanel? grid;
    private SettingsWindow? settings;
    private RecordingsWindow? recordings;
    private string? layoutKey;
    private string? sidebarKey;
    private string? controlsKey;
    private string? statusKey;
    private string? bannerKey;
    private bool refreshing;

    public MainWindow(AppModel model)
    {
        this.model = model;
        InitializeComponent();
        AddCameraButton.Click += async (_, _) => await AddCameraAsync();
        AddUtility("▣", "Abrir a pasta de gravações", () => Ui.Run(this, () =>
        {
            Directory.CreateDirectory(model.Settings.RecordDir);
            WindowsServices.Open(model.Settings.RecordDir);
        }));
        AddUtility("▶", "Gravações e fotos", OpenRecordings);
        AddUtility("▦", "Agenda de gravações", () => ShowSettings(2));
        AddUtility("⚙", "Ajustes (Ctrl+,)", OpenSettings);
        PreviewKeyDown += Shortcuts;
        VideoHost.SizeChanged += (_, _) => ResizeTiles();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                Refresh();
            else
                ClearVideo();
        };
        Loaded += (_, _) => Refresh();
        Closed += (_, _) =>
        {
            model.Changed -= ModelChanged;
            ClearVideo();
        };
        model.Changed += ModelChanged;
    }

    public void FocusCamera(Guid id)
    {
        SelectCamera(id);
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        if (!IsVisible)
            Show();
        Activate();
    }

    public void OpenSettings() => ShowSettings();

    public void OpenRecordings()
    {
        if (recordings is null)
        {
            recordings = new RecordingsWindow(model) { Icon = Icon };
            if (IsVisible)
                recordings.Owner = this;
            recordings.Closed += (_, _) => recordings = null;
            recordings.Show();
        }
        recordings.Activate();
    }

    private void ShowSettings(int? tab = null, Guid? camera = null)
    {
        if (settings is null)
        {
            settings = new SettingsWindow(model) { Icon = Icon };
            if (IsVisible)
                settings.Owner = this;
            settings.Closed += (_, _) => settings = null;
            settings.Show();
        }
        if (tab is not null)
            settings.Navigate(tab.Value, camera);
        settings.Activate();
    }

    private async Task AddCameraAsync()
    {
        if (settings is not null)
        {
            await settings.AddCameraAsync();
            settings.Activate();
            return;
        }
        var camera = new Camera { Name = Camera.NewName(model.Settings.Cameras.Select(c => c.DisplayName)) };
        model.Settings.Cameras.Add(camera);
        await Ui.Run(this, model.SaveSettingsAsync);
        ShowSettings(0, camera.Id);
    }

    private void AddUtility(string glyph, string title, Action action)
    {
        var button = Ui.Button(glyph, action, title);
        AutomationProperties.SetName(button, title);
        UtilityBar.Children.Add(button);
    }

    private void Shortcuts(object sender, KeyEventArgs args)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;
        if (args.Key == Key.OemComma)
        {
            args.Handled = true;
            OpenSettings();
        }
        else if (args.Key == Key.R && model.SelectedCameraId is { } id && !args.IsRepeat)
        {
            args.Handled = true;
            _ = Ui.Run(this, () => model.ToggleRecordingAsync(id));
        }
    }

    private void ModelChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.CheckAccess()) Refresh();
        else Dispatcher.InvokeAsync(Refresh);
    }

    private void SelectCamera(Guid? id)
    {
        model.SelectedCameraId = id;
        Refresh();
    }

    private void Refresh()
    {
        if (!IsVisible || refreshing)
            return;
        refreshing = true;
        try
        {
            var cameras = model.Settings.Cameras;
            var focused = cameras.FirstOrDefault(c => c.Id == model.SelectedCameraId);
            var state = cameras.ToDictionary(c => c.Id, c => model.GetCameraState(c.Id));
            UpdateSidebar(cameras, state, focused);
            UpdateVideo(cameras, state, focused);
            UpdateControls(focused, focused is null ? null : state[focused.Id]);
            UpdateStatus(focused is null ? model.GlobalStatus : state[focused.Id].Status, state.Values.Count(s => s.IsRecording));
            UpdateBanners(cameras, state);
        }
        finally { refreshing = false; }
    }

    private void UpdateSidebar(IReadOnlyList<Camera> cameras, Dictionary<Guid, CameraState> states, Camera? focused)
    {
        var key = string.Join('|', cameras.Select(c => $"{c.Id:N}:{c.DisplayName}:{c.Enabled}"));
        if (key != sidebarKey)
        {
            sidebarKey = key;
            Sidebar.Children.Clear();
            sidebarRows.Clear();
            allCameras = Ui.Button("Todas as câmeras", () => SelectCamera(null));
            allCameras.HorizontalContentAlignment = HorizontalAlignment.Left;
            Sidebar.Children.Add(allCameras);
            Sidebar.Children.Add(new TextBlock { Text = "Câmeras", Margin = new Thickness(4, 12, 0, 5), FontWeight = FontWeights.SemiBold });
            foreach (var camera in cameras)
            {
                var button = Ui.Button("", () => SelectCamera(camera.Id));
                button.Opacity = camera.Enabled ? 1 : .5;
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                var dot = Ui.Dot("Gray");
                var recording = Ui.Text("");
                var row = new DockPanel();
                DockPanel.SetDock(recording, Dock.Right);
                row.Children.Add(recording);
                row.Children.Add(dot);
                row.Children.Add(new TextBlock { Text = camera.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis });
                button.Content = row;
                AutomationProperties.SetName(button, camera.DisplayName);
                button.ContextMenu = new ContextMenu();
                button.ContextMenuOpening += (_, _) => ConfigureMenu(button.ContextMenu, camera);
                Sidebar.Children.Add(button);
                sidebarRows.Add(camera.Id, (dot, recording, button));
            }
        }
        allCameras!.FontWeight = focused is null ? FontWeights.Bold : FontWeights.Normal;
        foreach (var camera in cameras)
        {
            var state = states[camera.Id];
            var row = sidebarRows[camera.Id];
            row.Button.FontWeight = focused?.Id == camera.Id ? FontWeights.Bold : FontWeights.Normal;
            row.Dot.Foreground = Ui.Color(state.Status.Color.ToString());
            row.Dot.ToolTip = state.Status.Text;
            row.Recording.Text = state.IsRecording ? "●" : state.HasRecordingRequest ? "○" : "";
            row.Recording.Foreground = state.IsRecording ? Brushes.Red : Brushes.DarkOrange;
            row.Recording.ToolTip = state.IsRecording ? "Gravando" : state.HasRecordingRequest ? "Aguardando para gravar" : null;
        }
    }

    private void ConfigureMenu(ContextMenu menu, Camera camera)
    {
        var state = model.GetCameraState(camera.Id);
        menu.Items.Clear();
        Add(RecordText(state), () => _ = Ui.Run(this, () => model.ToggleRecordingAsync(camera.Id)), state.IsActive);
        Add("Tirar foto", () => _ = TakePhoto(camera.Id), state.IsConnected);
        Add(ConnectionText(state), () => _ = ChangeConnection(camera.Id), state.IsActive);
        menu.Items.Add(new Separator());
        Add("Editar…", () => ShowSettings(0, camera.Id), true);

        void Add(string text, Action action, bool enabled)
        {
            var item = new MenuItem { Header = text, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    private void UpdateVideo(IReadOnlyList<Camera> cameras, Dictionary<Guid, CameraState> states, Camera? focused)
    {
        var key = focused is null
            ? "grade:" + string.Join(',', cameras.Select(c => c.Id))
            : $"{focused.Id}:{model.Settings.EffectiveViewQuality}";
        if (layoutKey != key)
        {
            ClearVideo();
            layoutKey = key;
            if (cameras.Count == 0)
            {
                var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                empty.Children.Add(new TextBlock { Text = "◉", FontSize = 42, HorizontalAlignment = HorizontalAlignment.Center });
                empty.Children.Add(new TextBlock { Text = "Nenhuma câmera", FontSize = 22, Margin = new Thickness(0, 6, 0, 12) });
                var add = Ui.Button("Adicionar câmera", () => _ = AddCameraAsync());
                add.IsDefault = true;
                empty.Children.Add(add);
                VideoHost.Children.Add(empty);
                return;
            }
            if (focused is null)
            {
                grid = new WrapPanel { Margin = new Thickness(4) };
                VideoHost.Children.Add(new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
                foreach (var camera in cameras)
                    grid.Children.Add(CreateTile(camera, false));
                ResizeTiles();
            }
            else
                VideoHost.Children.Add(CreateTile(focused, true));
        }
        foreach (var (id, tile) in tiles)
        {
            var camera = cameras.First(c => c.Id == id);
            var state = states[id];
            tile.Update(camera, state, model.Settings);
        }
    }

    private CameraTile CreateTile(Camera camera, bool focused)
    {
        var id = camera.Id;
        var tile = new CameraTile(focused, () => SelectCamera(id), () => _ = ChangeConnection(id),
            () => _ = TakePhoto(id), () => _ = Ui.Run(this, () => model.ToggleRecordingAsync(id)));
        tiles.Add(id, tile);
        return tile;
    }

    private void ResizeTiles()
    {
        if (grid is null)
            return;
        var width = Math.Min(Math.Clamp(model.Settings.GridTileWidth, 200, 800), Math.Max(1, VideoHost.ActualWidth - 34));
        foreach (var tile in tiles.Values)
        {
            tile.Width = width;
            tile.Height = width * 9 / 16;
        }
    }

    private void ClearVideo()
    {
        foreach (var tile in tiles.Values)
            tile.Stop();
        tiles.Clear();
        VideoHost.Children.Clear();
        grid = null;
        layoutKey = null;
    }

    private void UpdateControls(Camera? focused, CameraState? state)
    {
        var disconnectAll = model.AnyConnectionWanted || model.RelayState is RelayState.Starting or RelayState.Running;
        var key = focused is null ? $"all:{disconnectAll}" :
            $"{focused.Id}:{state!.WantsConnection}:{state.IsConnected}:{state.HasRecordingRequest}:{state.IsRecording}:{state.IsActive}:{model.Settings.ViewQuality}:{model.Settings.RecordAudio}";
        if (key == controlsKey)
            return;
        controlsKey = key;
        Controls.Children.Clear();
        if (focused is null)
        {
            var slider = new Slider
            {
                Minimum = 200, Maximum = 800, TickFrequency = 40, IsSnapToTickEnabled = true, Width = 125,
                Value = model.Settings.GridTileWidth, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Tamanho dos blocos de câmera", Margin = new Thickness(2, 0, 8, 0)
            };
            AutomationProperties.SetName(slider, "Tamanho dos blocos de câmera");
            slider.ValueChanged += (_, args) =>
            {
                model.Settings.GridTileWidth = (int)args.NewValue;
                ResizeTiles();
                _ = Ui.Run(this, model.SaveSettingsAsync);
            };
            Controls.Children.Add(slider);
            Controls.Children.Add(Ui.Button(disconnectAll ? "Desconectar todas" : "Conectar todas", () =>
            {
                if (disconnectAll) _ = Ui.Run(this, model.DisconnectAllAsync);
                else model.ConnectAll();
            }));
            return;
        }
        var id = focused.Id;
        var current = state!;
        foreach (var quality in new[] { StreamQuality.Hd, StreamQuality.Sd })
        {
            var option = new RadioButton
            {
                Content = quality.Label(), GroupName = "ViewQuality", IsChecked = model.Settings.EffectiveViewQuality == quality,
                Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Qualidade da visualização"
            };
            option.Checked += (_, _) =>
            {
                model.Settings.ViewQuality = quality.Suffix();
                Refresh();
                _ = Ui.Run(this, model.SaveSettingsAsync);
            };
            Controls.Children.Add(option);
        }
        var audio = Ui.Check("Áudio na gravação", model.Settings.RecordAudio, value =>
        {
            model.Settings.RecordAudio = value;
            _ = Ui.Run(this, model.SaveSettingsAsync);
        });
        audio.IsEnabled = !current.HasRecordingRequest;
        audio.Foreground = Foreground;
        Controls.Children.Add(audio);
        var photo = Ui.Button("Tirar foto", () => _ = TakePhoto(id));
        photo.IsEnabled = current.IsConnected;
        Controls.Children.Add(photo);
        var record = Ui.Button(RecordText(current), () => _ = Ui.Run(this, () => model.ToggleRecordingAsync(id)), "Ctrl+R");
        record.Background = Brushes.DarkRed;
        record.Foreground = Brushes.White;
        record.IsEnabled = current.IsActive;
        Controls.Children.Add(record);
        var connect = Ui.Button(ConnectionText(current), () => _ = ChangeConnection(id));
        connect.IsEnabled = current.IsActive;
        Controls.Children.Add(connect);
    }

    private void UpdateStatus(StatusInfo status, int count)
    {
        var key = $"{status}:{count}:{model.NextScheduleText}:{model.PendingRestart}";
        if (statusKey == key)
            return;
        statusKey = key;
        StatusBarPanel.Children.Clear();
        StatusBarPanel.Children.Add(Ui.Dot(status.Color.ToString()));
        StatusBarPanel.Children.Add(Ui.Text(status.Text));
        if (count > 0) StatusBarPanel.Children.Add(Ui.Text($"  • {count} gravando", Brushes.Tomato));
        if (model.NextScheduleText is { Length: > 0 } next) StatusBarPanel.Children.Add(Ui.Text($"  • {next}"));
        if (model.PendingRestart)
            StatusBarPanel.Children.Add(Ui.Button("Aplicar alterações", () => _ = Ui.Run(this, model.ApplyChangesAsync),
                "A configuração das câmeras mudou. Aplicar agora reinicia a conexão e interrompe as gravações (elas continuam em novos arquivos)."));
    }

    private void UpdateBanners(IReadOnlyList<Camera> cameras, Dictionary<Guid, CameraState> states)
    {
        var key = string.Join('|', cameras.Select(c => $"{c.DisplayName}:{states[c.Id].Error}:{states[c.Id].Warning}:{states[c.Id].LastRecording}")) +
                  $"|{model.Error}|{model.RelayError}|{model.LastPhoto}";
        if (key == bannerKey)
            return;
        bannerKey = key;
        Banners.Children.Clear();
        if (model.Error is { Length: > 0 } error) Add(error, Brushes.DarkRed, "Fechar", () => model.ClearErrors());
        if (model.RelayError is { Length: > 0 } relayError && relayError != model.Error)
            Add(relayError, Brushes.DarkRed, "Fechar", () => model.ClearErrors());
        foreach (var camera in cameras)
        {
            var state = states[camera.Id];
            if (state.Error is { Length: > 0 } cameraError)
                Add($"{camera.DisplayName}: {cameraError}", Brushes.DarkRed, "Fechar", () => model.ClearErrors(camera.Id));
            if (state.Warning is { Length: > 0 } warning)
                Add($"{camera.DisplayName}: {warning}", Brushes.SaddleBrown);
            if (state.LastRecording is { Length: > 0 } recording && File.Exists(recording))
                Add($"Gravação salva: {Path.GetFileName(recording)}", Brushes.DarkGreen, "Mostrar no Explorador",
                    () => Ui.Run(this, () => WindowsServices.ShowInExplorer(recording)));
        }
        if (model.LastPhoto is { Length: > 0 } photo && File.Exists(photo))
            Add($"Foto salva: {Path.GetFileName(photo)}", Brushes.DarkGreen, "Abrir", () => Ui.Run(this, () => WindowsServices.Open(photo)));

        void Add(string text, Brush color, string? label = null, Action? action = null)
        {
            var panel = new DockPanel { Background = color };
            if (action is not null && label is not null)
            {
                var button = Ui.Button(label, action);
                DockPanel.SetDock(button, Dock.Right);
                panel.Children.Add(button);
            }
            panel.Children.Add(new TextBox
            {
                Text = text, IsReadOnly = true, Background = Brushes.Transparent, Foreground = Brushes.White,
                BorderThickness = new Thickness(0), TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(8, 4, 8, 4), MaxLines = 2,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden
            });
            Banners.Children.Add(panel);
        }
    }

    internal static string RecordText(CameraState state) => state.HasRecordingRequest || state.IsRecording ? "Parar gravação" : "Gravar";
    private static string ConnectionText(CameraState state) => state.WantsConnection ? "Desconectar câmera" : "Conectar câmera";

    private Task ChangeConnection(Guid id)
    {
        if (model.GetCameraState(id).WantsConnection)
            return Ui.Run(this, () => model.DisconnectCameraAsync(id));
        model.ConnectCamera(id);
        return Task.CompletedTask;
    }

    private Task TakePhoto(Guid id) => Ui.Run(this, async () => { await model.SnapshotAsync(id); });
}
