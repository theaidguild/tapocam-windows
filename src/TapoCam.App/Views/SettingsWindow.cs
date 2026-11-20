using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TapoCam.App.Runtime;
using TapoCam.App.Services;
using TapoCam.Core;
using TapoCam.Core.Models;

namespace TapoCam.App.Views;

internal sealed class SettingsWindow : Window
{
    private readonly AppModel model;
    private readonly TabControl tabs = new() { Margin = new Thickness(8) };
    private readonly ListBox cameraList = new() { Margin = new Thickness(0, 0, 8, 2), AllowDrop = true, DisplayMemberPath = "DisplayName" };
    private readonly StackPanel editor = new() { Margin = new Thickness(5, 0, 10, 4) };
    private readonly StackPanel agenda = new() { Margin = new Thickness(8) };
    private readonly Button remove;
    private TextBox name = null!, host = null!, port = null!, user = null!, fps = null!;
    private PasswordBox password = null!;
    private CheckBox enabled = null!;
    private Button save = null!, revert = null!, test = null!;
    private TextBlock result = null!;
    private ProgressBar testProgress = null!;
    private Camera? selected;
    private string originalPassword = "";
    private Point dragOrigin;
    private bool building;
    private bool testing;
    private bool closing;
    private bool closeReady;

    public SettingsWindow(AppModel model)
    {
        this.model = model;
        Title = "Ajustes";
        Width = 640;
        Height = 600;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage("pt-BR");
        Content = tabs;

        var cameraPane = new DockPanel { Margin = new Thickness(8) };
        var listPane = new DockPanel { Width = 180 };
        var listButtons = new StackPanel { Orientation = Orientation.Horizontal };
        listButtons.Children.Add(Ui.Button("+", () => _ = AddCameraAsync(), "Adicionar câmera"));
        remove = Ui.Button("−", () => _ = RemoveCameraAsync(), "Remover câmera");
        listButtons.Children.Add(remove);
        DockPanel.SetDock(listButtons, Dock.Bottom);
        listPane.Children.Add(listButtons);
        listPane.Children.Add(cameraList);
        DockPanel.SetDock(listPane, Dock.Left);
        cameraPane.Children.Add(listPane);
        cameraPane.Children.Add(Scroll(editor));
        tabs.Items.Add(new TabItem { Header = "Câmeras", Content = cameraPane });
        tabs.Items.Add(new TabItem { Header = "Geral", Content = Scroll(BuildGeneral()) });
        tabs.Items.Add(new TabItem { Header = "Agenda", Content = Scroll(agenda) });
        cameraList.SelectionChanged += CameraSelectionChanged;
        cameraList.PreviewMouseLeftButtonDown += (_, args) => dragOrigin = args.GetPosition(cameraList);
        cameraList.PreviewMouseMove += StartCameraDrag;
        cameraList.Drop += DropCamera;
        LoadCameraList(model.Settings.SettingsCamera);
        BuildAgenda();
        tabs.SelectedIndex = Math.Clamp(model.Settings.SettingsTab, 0, 2);
        tabs.SelectionChanged += (_, args) =>
        {
            if (args.Source != tabs)
                return;
            model.Settings.SettingsTab = tabs.SelectedIndex;
            if (tabs.SelectedIndex == 2)
                BuildAgenda();
            _ = Persist();
        };
        Closing += SaveOnClose;
    }

    public void Navigate(int tab, Guid? id)
    {
        tabs.SelectedIndex = tab;
        if (id is not null)
            cameraList.SelectedItem = model.Settings.Cameras.FirstOrDefault(c => c.Id == id);
    }

    public async Task AddCameraAsync()
    {
        if (!await SaveEditorAsync())
            return;
        var camera = new Camera { Name = Camera.NewName(model.Settings.Cameras.Select(c => c.DisplayName)) };
        model.Settings.Cameras.Add(camera);
        LoadCameraList(camera.Id);
        tabs.SelectedIndex = 0;
        await Persist();
    }

    private static ScrollViewer Scroll(FrameworkElement element) =>
        new() { Content = element, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    private static void Heading(Panel parent, string title) => parent.Children.Add(
        new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 9, 0, 5) });

    private static void Hint(Panel parent, string value)
    {
        var hint = Ui.Text(value, Brushes.DimGray);
        hint.Margin = new Thickness(0, 5, 0, 6);
        parent.Children.Add(hint);
    }

    private void LoadCameraList(Guid? id)
    {
        building = true;
        cameraList.ItemsSource = model.Settings.Cameras.ToArray();
        selected = model.Settings.Cameras.FirstOrDefault(c => c.Id == id) ?? model.Settings.Cameras.FirstOrDefault();
        cameraList.SelectedItem = selected;
        model.Settings.SettingsCamera = selected?.Id;
        BuildEditor();
        building = false;
    }

    private async void CameraSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (building)
            return;
        var item = cameraList.SelectedItem as Camera;
        if (!await SaveEditorAsync())
        {
            building = true;
            cameraList.SelectedItem = selected;
            building = false;
            return;
        }
        LoadCameraList(item?.Id);
        await Persist();
    }

    private void BuildEditor()
    {
        editor.Children.Clear();
        remove.IsEnabled = selected is not null;
        if (selected is not { } camera)
        {
            editor.Children.Add(Ui.Text("Nenhuma câmera"));
            return;
        }
        originalPassword = model.GetPassword(camera.Id);
        name = Field("Nome", camera.Name, "Sala");
        enabled = Ui.Check("Ativa", camera.Enabled, _ => UpdateEditor());
        editor.Children.Add(enabled);
        Heading(editor, "Conexão");
        host = Field("Endereço IP", camera.Ip, "192.168.0.100");
        port = Field("Porta RTSP", camera.Port.ToString(CultureInfo.InvariantCulture));
        user = Field("Usuário", camera.User);
        password = new PasswordBox { Password = originalPassword, Margin = new Thickness(0, 0, 0, 4) };
        AddField("Senha", password);
        password.PasswordChanged += (_, _) => UpdateEditor();
        fps = Field("Quadros por segundo", camera.Fps.ToString(CultureInfo.InvariantCulture));
        Hint(editor, "Use o usuário e a senha da Conta da Câmera, criada no app Tapo em Configurações → Avançadas.");
        var buttons = new WrapPanel();
        test = Ui.Button("Testar conexão", () => _ = TestAsync());
        revert = Ui.Button("Reverter", BuildEditor);
        save = Ui.Button("Salvar", () => _ = SaveEditorAsync());
        save.IsDefault = true;
        buttons.Children.Add(test);
        buttons.Children.Add(revert);
        buttons.Children.Add(save);
        editor.Children.Add(buttons);
        testProgress = new ProgressBar { Height = 4, Margin = new Thickness(0, 4, 0, 4), Visibility = Visibility.Collapsed };
        editor.Children.Add(testProgress);
        result = Ui.Text("");
        editor.Children.Add(result);
        Hint(editor, "Alterações nas câmeras reconectam automaticamente. Se houver gravações em andamento, a reconexão espera até você aplicar na janela principal.");
        UpdateEditor();
    }

    private TextBox Field(string caption, string initial, string? placeholder = null)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 4) };
        if (placeholder is not null)
        {
            var hint = new TextBlock { Text = placeholder, Foreground = Brushes.Gray, Margin = new Thickness(4, 1, 0, 4), IsHitTestVisible = false };
            AutomationProperties.SetHelpText(box, placeholder);
            var layer = new Grid();
            layer.Children.Add(box);
            layer.Children.Add(hint);
            AddField(caption, layer, box);
            void ShowHint() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            box.TextChanged += (_, _) => ShowHint();
            ShowHint();
        }
        else
            AddField(caption, box);
        box.TextChanged += (_, _) => UpdateEditor();
        return box;
    }

    private void AddField(string text, FrameworkElement value, FrameworkElement? target = null)
    {
        editor.Children.Add(new Label { Content = text, Target = target ?? value, Padding = new Thickness(0, 1, 0, 1) });
        editor.Children.Add(value);
        AutomationProperties.SetName(target ?? value, text);
    }

    private bool IsDirty() => selected is { } camera &&
        (camera.Name != name.Text || camera.Ip != host.Text || camera.User != user.Text ||
         camera.Port.ToString(CultureInfo.InvariantCulture) != port.Text ||
         camera.Fps.ToString(CultureInfo.InvariantCulture) != fps.Text ||
         camera.Enabled != (enabled.IsChecked == true) || originalPassword != password.Password);

    private void UpdateEditor()
    {
        var dirty = IsDirty();
        save.IsEnabled = revert.IsEnabled = dirty;
        test.IsEnabled = !testing && !string.IsNullOrWhiteSpace(host.Text) &&
            !string.IsNullOrWhiteSpace(user.Text) && password.Password.Length > 0;
    }

    private Camera? Draft()
    {
        if (selected is null)
            return null;
        if (!int.TryParse(port.Text, out var portNumber) || portNumber is < 1 or > 65535 ||
            !int.TryParse(fps.Text, out var frameRate) || frameRate < 1)
        {
            result.Foreground = Brushes.DarkRed;
            result.Text = "Informe uma porta RTSP entre 1 e 65535 e pelo menos 1 quadro por segundo.";
            return null;
        }
        return new Camera
        {
            Id = selected.Id, Name = name.Text, Ip = host.Text, Port = portNumber,
            User = user.Text, Fps = frameRate, Enabled = enabled.IsChecked == true
        };
    }

    private async Task<bool> SaveEditorAsync()
    {
        if (!IsDirty())
            return true;
        if (Draft() is not { } draft || selected is not { } camera)
            return false;
        if (originalPassword != password.Password)
        {
            var value = password.Password;
            model.SetPassword(camera.Id, value);
            if (model.GetPassword(camera.Id) != value)
            {
                result.Foreground = Brushes.DarkRed;
                result.Text = model.Error ?? "Não foi possível salvar a senha.";
                return false;
            }
            originalPassword = value;
        }
        camera.Name = draft.Name;
        camera.Ip = draft.Ip;
        camera.Port = draft.Port;
        camera.User = draft.User;
        camera.Fps = draft.Fps;
        camera.Enabled = draft.Enabled;
        building = true;
        cameraList.Items.Refresh();
        building = false;
        UpdateEditor();
        await Persist();
        return true;
    }

    private async void SaveOnClose(object? sender, CancelEventArgs args)
    {
        if (closeReady || !IsDirty())
            return;
        args.Cancel = true;
        if (closing)
            return;
        closing = true;
        var saved = await SaveEditorAsync();
        closing = false;
        if (saved)
        {
            closeReady = true;
            _ = Dispatcher.InvokeAsync(Close);
        }
    }

    private async Task TestAsync()
    {
        if (Draft() is not { } camera)
            return;
        if (MessageBox.Show(this, "O teste conecta diretamente à câmera. Não o use durante uma gravação importante.",
            "Testar conexão", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        var output = result;
        var progress = testProgress;
        testing = true;
        progress.Visibility = Visibility.Visible;
        progress.IsIndeterminate = true;
        output.Text = "";
        UpdateEditor();
        await Ui.Run(this, async () =>
        {
            var text = await model.TestConnectionAsync(camera, password.Password);
            output.Text = text;
            output.Foreground = text.StartsWith("Conexão OK:", StringComparison.Ordinal) ? Brushes.DarkGreen : Brushes.DarkRed;
        });
        testing = false;
        progress.IsIndeterminate = false;
        progress.Visibility = Visibility.Collapsed;
        UpdateEditor();
    }

    private async Task RemoveCameraAsync()
    {
        if (selected is not { } camera)
            return;
        var dialog = new Window
        {
            Title = $"Remover {camera.DisplayName}?", Owner = this, Icon = Icon, Width = 390, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(Ui.Text("A senha é apagada e a câmera sai dos horários da agenda. As gravações já feitas continuam na pasta."));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var destructive = Ui.Button("Remover", () => dialog.DialogResult = true);
        destructive.Foreground = Brushes.White;
        destructive.Background = Brushes.DarkRed;
        var cancel = Ui.Button("Cancelar", () => dialog.DialogResult = false);
        cancel.IsCancel = cancel.IsDefault = true;
        buttons.Children.Add(destructive);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        if (dialog.ShowDialog() != true)
            return;
        await Ui.Run(this, () => model.RemoveCameraAsync(camera.Id));
        LoadCameraList(null);
    }

    private void StartCameraDrag(object sender, MouseEventArgs args)
    {
        if (args.LeftButton != MouseButtonState.Pressed || cameraList.SelectedItem is not Camera camera)
            return;
        var position = args.GetPosition(cameraList);
        if (Math.Abs(position.X - dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        DragDrop.DoDragDrop(cameraList, camera, DragDropEffects.Move);
    }

    private async void DropCamera(object sender, DragEventArgs args)
    {
        if (args.Data.GetData(typeof(Camera)) is not Camera source ||
            ItemsControl.ContainerFromElement(cameraList, args.OriginalSource as DependencyObject) is not ListBoxItem { DataContext: Camera target } ||
            source == target || !await SaveEditorAsync())
            return;
        var cameras = model.Settings.Cameras;
        var destination = cameras.IndexOf(target);
        if (destination < 0 || !cameras.Remove(source))
            return;
        cameras.Insert(destination, source);
        LoadCameraList(source.Id);
        await Persist();
    }

    private Task Persist() => Ui.Run(this, model.SaveSettingsAsync);

    private StackPanel BuildGeneral()
    {
        var settings = model.Settings;
        var panel = new StackPanel { Margin = new Thickness(12, 4, 12, 9) };
        Heading(panel, "Visualização");
        Choice(panel, "Modo", ["Com áudio (atraso de alguns segundos)", "Sem áudio (atraso mínimo)"],
            settings.EffectivePlayerMode == "mse" ? 1 : 0, index => { settings.PlayerMode = index == 1 ? "mse" : "hls"; _ = Persist(); });
        Hint(panel, "Na grade as câmeras ficam sem som; a câmera em foco toca o áudio. Vale na próxima conexão.");
        Heading(panel, "Gravação");
        Choice(panel, "Qualidade", ["HD (stream1)", "SD (stream2)"], settings.EffectiveRecordQuality == StreamQuality.Sd ? 1 : 0,
            index => { settings.RecordQuality = index == 1 ? "sd" : "hd"; _ = Persist(); });
        panel.Children.Add(Ui.Check("Gravar com áudio", settings.RecordAudio, value => { settings.RecordAudio = value; _ = Persist(); }));
        panel.Children.Add(Ui.Text("Pasta"));
        var folderRow = new DockPanel();
        var path = new TextBox { Text = settings.RecordDir, IsReadOnly = true, VerticalContentAlignment = VerticalAlignment.Center };
        var browse = Ui.Button("Escolher…", () =>
        {
            var picker = new OpenFolderDialog { Title = "Escolher pasta de gravações", InitialDirectory = settings.RecordDir };
            if (picker.ShowDialog(this) != true)
                return;
            path.Text = settings.RecordDir = picker.FolderName;
            _ = Persist();
        });
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(path);
        panel.Children.Add(folderRow);
        var retention = Ui.Text(RetentionLabel(settings.RetentionDays));
        panel.Children.Add(retention);
        panel.Children.Add(Number(settings.RetentionDays, 0, 365, 1, "Dias de retenção", value =>
        {
            settings.RetentionDays = value;
            retention.Text = RetentionLabel(value);
            _ = Persist();
        }));
        Hint(panel, "Remove permanentemente só os vídeos tapo_*.mp4 da pasta de gravações (as fotos ficam).");
        Heading(panel, "Geral");
        panel.Children.Add(Ui.Check("Conectar ao abrir o app", settings.AutoConnect, value => { settings.AutoConnect = value; _ = Persist(); }));
        panel.Children.Add(Ui.Check("Gravar todas as câmeras ao conectar", settings.AutoRecord, value => { settings.AutoRecord = value; _ = Persist(); }));
        panel.Children.Add(Ui.Check("Avisar quando uma câmera cair ou voltar", settings.NotifyCameraStatus, value => { settings.NotifyCameraStatus = value; _ = Persist(); }));
        var changingStartup = false;
        var launch = false;
        string? launchReadError = null;
        try { launch = WindowsServices.LaunchAtSignIn; }
        catch (Exception ex) { launchReadError = CredentialMasker.Mask(ex.Message); }
        CheckBox? startup = null;
        startup = Ui.Check("Abrir ao iniciar sessão", launch, value =>
        {
            if (changingStartup)
                return;
            try { WindowsServices.SetLaunchAtSignIn(value); }
            catch (Exception ex)
            {
                changingStartup = true;
                startup!.IsChecked = !value;
                changingStartup = false;
                MessageBox.Show(this, $"Não foi possível alterar: {CredentialMasker.Mask(ex.Message)}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
        if (launchReadError is not null)
            startup.ToolTip = $"Não foi possível ler a preferência: {launchReadError}";
        panel.Children.Add(startup);
        return panel;
    }

    private static string RetentionLabel(int days) =>
        days <= 0 ? "Apagar gravações antigas: desativado" : $"Apagar gravações com mais de {days} dias";

    private static void Choice(Panel panel, string title, string[] options, int index, Action<int> change)
    {
        var combo = new ComboBox { ItemsSource = options, SelectedIndex = index, Margin = new Thickness(0, 2, 0, 4) };
        panel.Children.Add(new Label { Content = title, Target = combo, Padding = new Thickness(0) });
        panel.Children.Add(combo);
        combo.SelectionChanged += (_, _) => change(combo.SelectedIndex);
    }

    private static StackPanel Number(int initial, int min, int max, int step, string name, Action<int> change)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var current = Math.Clamp(initial, min, max);
        var input = new TextBox { Text = current.ToString(CultureInfo.InvariantCulture), Width = 47, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(input, name);
        void Set(int value)
        {
            var next = Math.Clamp(value, min, max);
            input.Text = next.ToString(CultureInfo.InvariantCulture);
            if (current == next)
                return;
            current = next;
            change(current);
        }
        void Commit() => Set(int.TryParse(input.Text, out var number) ? number : current);
        input.LostKeyboardFocus += (_, _) => Commit();
        input.PreviewKeyDown += (_, args) =>
        {
            switch (args.Key)
            {
                case Key.Enter: Commit(); break;
                case Key.Up: Set(current + step); break;
                case Key.Down: Set(current - step); break;
                default: return;
            }
            args.Handled = true;
        };
        panel.Children.Add(Ui.Button("−", () => Set(current - step), $"Diminuir: {name}"));
        panel.Children.Add(input);
        panel.Children.Add(Ui.Button("+", () => Set(current + step), $"Aumentar: {name}"));
        return panel;
    }

    private void BuildAgenda()
    {
        agenda.Children.Clear();
        Hint(agenda, "O TapoCam grava sozinho nestes horários enquanto estiver aberto. Se você fechar a janela, ele continua na área de notificação.");
        if (model.Settings.ScheduleRules.Count == 0)
            agenda.Children.Add(Ui.Text("Nenhum horário cadastrado."));
        foreach (var rule in model.Settings.ScheduleRules)
            agenda.Children.Add(CreateRule(rule));
        var add = Ui.Button("+ Adicionar horário", () =>
        {
            model.Settings.ScheduleRules.Add(new ScheduleRule { CameraIds = model.Settings.Cameras.Select(c => c.Id).ToHashSet() });
            BuildAgenda();
            _ = Persist();
        });
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.IsEnabled = model.Settings.Cameras.Count > 0;
        agenda.Children.Add(add);
        Hint(agenda, "O PC precisa estar ligado e fora do modo de suspensão no horário agendado.");
    }

    private Border CreateRule(ScheduleRule rule)
    {
        var card = new StackPanel();
        var border = new Border
        {
            BorderThickness = new Thickness(1), BorderBrush = Brushes.LightGray, CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(8), Child = card, Opacity = rule.Enabled ? 1 : .5
        };
        var days = new WrapPanel();
        days.Children.Add(Ui.Check("Ativo", rule.Enabled, value =>
        {
            rule.Enabled = value;
            border.Opacity = value ? 1 : .5;
            _ = Persist();
        }));
        string[] labels = ["Domingo", "Segunda-feira", "Terça-feira", "Quarta-feira", "Quinta-feira", "Sexta-feira", "Sábado"];
        for (var index = 0; index < labels.Length; index++)
        {
            var day = index + 1;
            var button = new ToggleButton { Content = labels[index][0].ToString(), ToolTip = labels[index], IsChecked = rule.Weekdays.Contains(day), Margin = new Thickness(2), Width = 25 };
            AutomationProperties.SetName(button, labels[index]);
            button.Click += (_, _) =>
            {
                if (button.IsChecked == true) rule.Weekdays.Add(day);
                else rule.Weekdays.Remove(day);
                _ = Persist();
            };
            days.Children.Add(button);
        }
        days.Children.Add(Ui.Button("×", () =>
        {
            model.Settings.ScheduleRules.Remove(rule);
            BuildAgenda();
            _ = Persist();
        }, "Remover horário"));
        card.Children.Add(days);
        var timing = new WrapPanel { Margin = new Thickness(0, 5, 0, 5) };
        timing.Children.Add(Ui.Text("Início "));
        timing.Children.Add(Number(rule.Hour, 0, 23, 1, "Hora", value => { rule.Hour = value; _ = Persist(); }));
        timing.Children.Add(Ui.Text(":"));
        timing.Children.Add(Number(rule.Minute, 0, 59, 1, "Minuto", value => { rule.Minute = value; _ = Persist(); }));
        timing.Children.Add(Ui.Text("  Duração "));
        var duration = Ui.Text($" {TimeFormatting.Duration(rule.EffectiveDurationMinutes)}");
        timing.Children.Add(Number(rule.DurationMinutes, 1, 1440, 5, "Duração em minutos", value =>
        {
            rule.DurationMinutes = value;
            duration.Text = $" {TimeFormatting.Duration(value)}";
            _ = Persist();
        }));
        timing.Children.Add(duration);
        card.Children.Add(timing);
        var options = new WrapPanel();
        var warning = Ui.Text("Escolha ao menos uma câmera.", Brushes.DarkOrange);
        var cameras = Ui.Button("", () => { });
        var menu = new ContextMenu { Placement = PlacementMode.Bottom, PlacementTarget = cameras };
        void UpdateCameras()
        {
            var chosen = model.Settings.Cameras.Where(c => rule.CameraIds.Contains(c.Id)).ToList();
            cameras.Content = (chosen.Count switch
            {
                0 => "Nenhuma câmera",
                1 => chosen[0].DisplayName,
                var count when count == model.Settings.Cameras.Count => "Todas as câmeras",
                var count => $"{count} câmeras"
            }) + " ▾";
            warning.Visibility = chosen.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var camera in model.Settings.Cameras)
        {
            var item = new MenuItem { Header = camera.DisplayName, IsCheckable = true, IsChecked = rule.CameraIds.Contains(camera.Id), StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                if (item.IsChecked) rule.CameraIds.Add(camera.Id);
                else rule.CameraIds.Remove(camera.Id);
                UpdateCameras();
                _ = Persist();
            };
            menu.Items.Add(item);
        }
        cameras.Click += (_, _) => { if (menu.HasItems) menu.IsOpen = true; };
        UpdateCameras();
        options.Children.Add(cameras);
        foreach (var quality in new[] { StreamQuality.Hd, StreamQuality.Sd })
        {
            var option = new RadioButton
            {
                Content = quality.Label(), GroupName = $"Agenda{rule.Id:N}", IsChecked = rule.EffectiveQuality == quality,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0)
            };
            option.Checked += (_, _) => { rule.Quality = quality.Suffix(); _ = Persist(); };
            options.Children.Add(option);
        }
        options.Children.Add(Ui.Check("Gravar áudio", rule.Audio, value => { rule.Audio = value; _ = Persist(); }));
        card.Children.Add(options);
        card.Children.Add(warning);
        return border;
    }
}
