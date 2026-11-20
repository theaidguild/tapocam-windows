using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TapoCam.App.Runtime;
using TapoCam.App.Services;
using TapoCam.Core;

namespace TapoCam.App.Views;

internal sealed class RecordingsWindow : Window
{
    private readonly AppModel model;
    private readonly ComboBox cameras = new() { MinWidth = 140, Margin = new Thickness(2), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ListBox fileList = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly DatePicker date = new() { Margin = new Thickness(2), SelectedDate = DateTime.Today, IsEnabled = false };
    private readonly TextBlock empty = Ui.Text("Nenhum arquivo", Brushes.DimGray);
    private readonly Grid preview = new() { Background = Brushes.Black };
    private readonly TextBlock filename = Ui.Text("");
    private readonly TextBlock metadata = Ui.Text("");
    private readonly TextBlock duration = Ui.Text("");
    private readonly WrapPanel actions = new();
    private readonly Button recycle;
    private readonly Button copy;
    private readonly StackPanel transport = new() { Orientation = Orientation.Horizontal };
    private readonly Slider position = new() { IsEnabled = false, MinWidth = 140, Margin = new Thickness(5, 0, 0, 0) };
    private readonly DispatcherTimer progress = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly Button play;
    private readonly Button pause;
    private MediaElement? media;
    private BitmapImage? picture;
    private string? loadedFolder;
    private Dictionary<string, string> cameraNames = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<RecordingFile> files = [];
    private HashSet<string> active = new(StringComparer.OrdinalIgnoreCase);
    private ManagedFileType? type;
    private bool filtersUpdating;
    private bool positioning;
    private bool closed;
    private int refreshVersion;
    private RecordingFile? selectedFile;

    public RecordingsWindow(AppModel model)
    {
        this.model = model;
        Title = "Gravações";
        Width = 900;
        Height = 560;
        MinWidth = 760;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage("pt-BR");

        var root = new DockPanel { Margin = new Thickness(10) };
        Content = root;
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        AutomationProperties.SetName(cameras, "Filtrar por câmera");
        cameras.DisplayMemberPath = nameof(CameraFilter.Name);
        toolbar.Children.Add(cameras);
        cameras.SelectionChanged += (_, _) => ApplyFilters();
        foreach (var (label, kind) in new (string, ManagedFileType?)[] { ("Tudo", null), ("Vídeos", ManagedFileType.Video), ("Fotos", ManagedFileType.Photo) })
        {
            var filter = new RadioButton
            {
                Content = label, GroupName = "TipoArquivo", IsChecked = kind is null,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 3, 0)
            };
            filter.Checked += (_, _) => { type = kind; ApplyFilters(); };
            toolbar.Children.Add(filter);
        }
        var enableDate = Ui.Check("Data", false, value => { date.IsEnabled = value; ApplyFilters(); });
        enableDate.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(enableDate);
        date.SelectedDateChanged += (_, _) => ApplyFilters();
        AutomationProperties.SetName(date, "Data da gravação");
        toolbar.Children.Add(date);
        toolbar.Children.Add(Ui.Button("Atualizar", () => _ = ReloadAsync()));
        toolbar.Children.Add(Ui.Button("Abrir a pasta no Explorador", () => Ui.Run(this, () =>
        {
            Directory.CreateDirectory(model.Settings.RecordDir);
            WindowsServices.Open(model.Settings.RecordDir);
        })));

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290), MinWidth = 210 });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 250 });
        root.Children.Add(columns);
        var listPanel = new Grid();
        listPanel.Children.Add(fileList);
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.VerticalAlignment = VerticalAlignment.Center;
        empty.IsHitTestVisible = false;
        listPanel.Children.Add(empty);
        columns.Children.Add(listPanel);
        fileList.ItemTemplate = BuildFileTemplate();
        fileList.GroupStyle.Add(new GroupStyle { HeaderTemplate = BuildGroupTemplate() });
        VirtualizingPanel.SetIsVirtualizingWhenGrouping(fileList, true);
        VirtualizingPanel.SetVirtualizationMode(fileList, VirtualizationMode.Recycling);
        fileList.SelectionChanged += (_, _) => ShowSelection(fileList.SelectedItem as RecordingFile);
        columns.Children.Add(new GridSplitter
        {
            Width = 5, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
        });
        Grid.SetColumn(columns.Children[1], 1);
        var detail = new DockPanel();
        Grid.SetColumn(detail, 2);
        columns.Children.Add(detail);
        var footer = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        detail.Children.Add(footer);
        detail.Children.Add(preview);

        play = Ui.Button("Reproduzir", () => media?.Play());
        pause = Ui.Button("Pausar", () => media?.Pause());
        transport.Children.Add(play);
        transport.Children.Add(pause);
        transport.Children.Add(position);
        AutomationProperties.SetName(position, "Posição do vídeo");
        footer.Children.Add(transport);
        footer.Children.Add(filename);
        filename.FontWeight = FontWeights.SemiBold;
        footer.Children.Add(metadata);
        footer.Children.Add(duration);
        footer.Children.Add(actions);
        copy = Ui.Button("Copiar", CopyPhoto);
        actions.Children.Add(copy);
        actions.Children.Add(Ui.Button("Abrir", () => WithSelection(WindowsServices.Open)));
        actions.Children.Add(Ui.Button("Mostrar no Explorador", () => WithSelection(WindowsServices.ShowInExplorer)));
        recycle = Ui.Button("Mover para a Lixeira", Recycle);
        ToolTipService.SetShowOnDisabled(recycle, true);
        actions.Children.Add(recycle);

        position.ValueChanged += (_, _) =>
        {
            if (!positioning && media is { } video && video.NaturalDuration.HasTimeSpan)
                video.Position = TimeSpan.FromSeconds(position.Value);
        };
        progress.Tick += (_, _) =>
        {
            if (media is not { } video || !video.NaturalDuration.HasTimeSpan || position.IsMouseCaptureWithin)
                return;
            positioning = true;
            position.Value = video.Position.TotalSeconds;
            positioning = false;
        };
        ShowSelection(null);
        model.Changed += ModelChanged;
        Loaded += (_, _) => _ = ReloadAsync();
        Closed += (_, _) =>
        {
            closed = true;
            model.Changed -= ModelChanged;
            ReleasePreview();
        };
    }

    private static DataTemplate BuildFileTemplate()
    {
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(FrameworkElement.MarginProperty, new Thickness(3));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(RecordingFile.Title)));
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        row.AppendChild(title);
        var details = new FrameworkElementFactory(typeof(TextBlock));
        details.SetBinding(TextBlock.TextProperty, new Binding(nameof(RecordingFile.Details)));
        details.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(RecordingFile.DetailColor)));
        row.AppendChild(details);
        return new DataTemplate { VisualTree = row };
    }

    private static DataTemplate BuildGroupTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(TextBlock.MarginProperty, new Thickness(4, 8, 4, 3));
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        return new DataTemplate { VisualTree = text };
    }

    private void ModelChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => ModelChanged(sender, args));
            return;
        }
        if (closed)
            return;
        var names = GetCameraNames();
        if (!string.Equals(loadedFolder, model.Settings.RecordDir, StringComparison.OrdinalIgnoreCase) ||
            !active.SetEquals(model.ActiveRecordingPaths) ||
            active.Any(path => !files.Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase)) && File.Exists(path)) ||
            names.Count != cameraNames.Count || names.Any(entry => !cameraNames.TryGetValue(entry.Key, out var value) || value != entry.Value))
            _ = ReloadAsync();
    }

    private Dictionary<string, string> GetCameraNames() => model.Settings.Cameras
        .GroupBy(camera => camera.Slug, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.OrdinalIgnoreCase);

    private async Task ReloadAsync()
    {
        var revision = ++refreshVersion;
        var directory = loadedFolder = model.Settings.RecordDir;
        var currentPaths = active = model.ActiveRecordingPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = cameraNames = GetCameraNames();
        try
        {
            var list = await Task.Run(() => RecordingFile.Read(directory, currentPaths, names));
            if (closed || revision != refreshVersion)
                return;
            files = list;
            var previous = cameras.SelectedItem as CameraFilter;
            var options = files.Where(file => file.Info.Slug is not null).Select(file => file.Info.Slug!).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(slug => new CameraFilter(names.GetValueOrDefault(slug, slug), slug))
                .OrderBy(item => item.Name, StringComparer.Create(Ui.Culture, true)).ToList();
            options.Insert(0, CameraFilter.All);
            if (files.Any(file => file.Info.Slug is null))
                options.Add(new("Sem câmera", null));
            filtersUpdating = true;
            cameras.ItemsSource = options;
            cameras.SelectedItem = options.FirstOrDefault(item => item.AllCameras == previous?.AllCameras && item.Slug == previous?.Slug) ?? CameraFilter.All;
            filtersUpdating = false;
            ApplyFilters();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (closed || revision != refreshVersion)
                return;
            files = [];
            ApplyFilters();
            Ui.Failure(this, ex);
        }
    }

    private void ApplyFilters()
    {
        if (filtersUpdating)
            return;
        var filter = cameras.SelectedItem as CameraFilter;
        var day = date.IsEnabled ? date.SelectedDate?.Date : null;
        var filtered = files.Where(file =>
            (filter is null || filter.AllCameras || string.Equals(filter.Slug, file.Info.Slug, StringComparison.OrdinalIgnoreCase)) &&
            (type is null || file.Info.Type == type) &&
            (day is null || file.Info.Timestamp.Date == day)).ToList();
        var selected = selectedFile?.Path;
        var view = new ListCollectionView(filtered);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RecordingFile.Day)));
        filtersUpdating = true;
        fileList.ItemsSource = view;
        fileList.SelectedItem = filtered.FirstOrDefault(file => string.Equals(file.Path, selected, StringComparison.OrdinalIgnoreCase));
        filtersUpdating = false;
        ShowSelection(fileList.SelectedItem as RecordingFile);
        empty.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowSelection(RecordingFile? file)
    {
        if (filtersUpdating)
            return;
        var reuse = file is not null && selectedFile is not null &&
            string.Equals(file.Path, selectedFile.Path, StringComparison.OrdinalIgnoreCase) && file.IsRecording == selectedFile.IsRecording;
        selectedFile = file;
        actions.Visibility = file is null ? Visibility.Collapsed : Visibility.Visible;
        transport.Visibility = file?.Info.Type == ManagedFileType.Video ? Visibility.Visible : Visibility.Collapsed;
        if (file is null)
        {
            ReleasePreview();
            SetPreviewMessage("Selecione uma gravação ou foto.");
            filename.Text = metadata.Text = duration.Text = "";
            return;
        }
        filename.Text = Path.GetFileName(file.Path);
        metadata.Text = file.Info.Timestamp.ToString("f", Ui.Culture);
        copy.Visibility = file.Info.Type == ManagedFileType.Photo ? Visibility.Visible : Visibility.Collapsed;
        recycle.IsEnabled = !file.IsRecording;
        recycle.ToolTip = file.IsRecording ? "Este arquivo ainda está sendo gravado" : null;
        if (reuse)
            return;
        ReleasePreview();
        duration.Text = "";
        if (file.Info.Type == ManagedFileType.Photo)
        {
            try
            {
                using var stream = File.OpenRead(file.Path);
                picture = new BitmapImage();
                picture.BeginInit();
                picture.CacheOption = BitmapCacheOption.OnLoad;
                picture.StreamSource = stream;
                picture.EndInit();
                picture.Freeze();
                preview.Children.Add(new Image { Source = picture, Stretch = Stretch.Uniform });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FormatException)
            {
                SetPreviewMessage("Não foi possível exibir a foto.");
            }
            return;
        }
        var element = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close,
            Stretch = Stretch.Uniform, ScrubbingEnabled = true, Source = new Uri(file.Path)
        };
        media = element;
        play.IsEnabled = pause.IsEnabled = true;
        element.MediaOpened += (_, _) =>
        {
            if (media == element && element.NaturalDuration.HasTimeSpan)
            {
                duration.Text = $"Duração: {TimeFormatting.Elapsed(element.NaturalDuration.TimeSpan)}";
                position.Maximum = element.NaturalDuration.TimeSpan.TotalSeconds;
                position.IsEnabled = true;
            }
        };
        element.MediaFailed += (_, _) =>
        {
            if (media != element)
                return;
            ReleasePreview();
            SetPreviewMessage("Não foi possível reproduzir o vídeo. Tente abrir no aplicativo padrão.");
        };
        preview.Children.Add(element);
        element.Play();
        progress.Start();
    }

    private void SetPreviewMessage(string text)
    {
        preview.Children.Clear();
        var placeholder = Ui.Text(text, Brushes.White);
        placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        placeholder.TextAlignment = TextAlignment.Center;
        placeholder.Margin = new Thickness(12);
        preview.Children.Add(placeholder);
    }

    private void ReleasePreview()
    {
        progress.Stop();
        var previous = media;
        media = null;
        previous?.Close();
        picture = null;
        play.IsEnabled = pause.IsEnabled = false;
        position.IsEnabled = false;
        position.Value = 0;
        preview.Children.Clear();
    }

    private void WithSelection(Action<string> action)
    {
        if (selectedFile is { } file)
            Ui.Run(this, () => action(file.Path));
    }

    private void CopyPhoto()
    {
        if (picture is null)
            return;
        try { Clipboard.SetImage(picture); }
        catch (Exception)
        {
            MessageBox.Show(this, "Não foi possível copiar a foto.", Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Recycle()
    {
        if (selectedFile is not { } file || model.ActiveRecordingPaths.Contains(file.Path, StringComparer.OrdinalIgnoreCase))
            return;
        ReleasePreview();
        selectedFile = null;
        Ui.Run(this, () => WindowsServices.Recycle(file.Path));
        _ = ReloadAsync();
    }

    private sealed record CameraFilter(string Name, string? Slug, bool AllCameras = false)
    {
        public static CameraFilter All { get; } = new("Todas as câmeras", null, true);
    }

    private sealed record RecordingFile(string Path, ParsedManagedFile Info, string Camera, long Bytes, bool IsRecording)
    {
        public string Day => Info.Timestamp.ToString("D", Ui.Culture);

        public string Title => $"{(Info.Type == ManagedFileType.Video ? "▶" : "▧")} {Camera}";
        public string Details => $"{Info.Timestamp:HH:mm}   {(IsRecording ? "● Gravando" : FormatBytes(Bytes))}";
        public Brush DetailColor => IsRecording ? Brushes.DarkRed : Brushes.DimGray;

        private static string FormatBytes(long bytes) => bytes switch
        {
            >= 1L << 30 => $"{(bytes / (double)(1L << 30)).ToString("0.#", Ui.Culture)} GB",
            >= 1L << 20 => $"{(bytes / (double)(1L << 20)).ToString("0.#", Ui.Culture)} MB",
            >= 1L << 10 => $"{(bytes / (double)(1L << 10)).ToString("0.#", Ui.Culture)} KB",
            _ => $"{bytes} B"
        };

        public static List<RecordingFile> Read(string directory, HashSet<string> activePaths, Dictionary<string, string> names)
        {
            if (!Directory.Exists(directory))
                return [];
            var result = new List<RecordingFile>();
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
            {
                if (FileNaming.Parse(file.Name) is not { } info)
                    continue;
                try
                {
                    var name = info.Slug is { } slug ? names.GetValueOrDefault(slug, slug) : "Sem câmera";
                    result.Add(new(file.FullName, info, name, file.Length, activePaths.Contains(file.FullName)));
                }
                catch (FileNotFoundException) { }
            }
            return result.OrderByDescending(file => file.Info.Timestamp).ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
