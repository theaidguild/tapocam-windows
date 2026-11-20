using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TapoCam.App.Player;
using TapoCam.App.Runtime;
using TapoCam.Core;
using TapoCam.Core.Models;

namespace TapoCam.App.Views;

internal sealed class CameraTile : Border
{
    private readonly bool focused;
    private readonly Grid video = new();
    private readonly TextBlock placeholder;
    private readonly ProgressBar spinner;
    private readonly Button connect;
    private readonly StackPanel badges;
    private readonly StackPanel actions;
    private readonly TextBlock dot = Ui.Dot("Gray");
    private readonly TextBlock title = Ui.Text("");
    private readonly Border recordingBadge;
    private readonly TextBlock recording = Ui.Text("", Brushes.White);
    private readonly Button photo;
    private readonly Button record;
    private LivePlayer? player;

    public CameraTile(bool focused, Action focus, Action connectAction, Action photoAction, Action recordAction)
    {
        this.focused = focused;
        Margin = new Thickness(focused ? 14 : 6);
        BorderThickness = new Thickness(1);
        BorderBrush = Ui.Color("#344354");
        CornerRadius = new CornerRadius(10);
        Background = Brushes.Black;
        ForegroundForText();

        var layers = new Grid();
        Child = layers;
        layers.Children.Add(video);
        SizeChanged += (_, _) => layers.Clip = new RectangleGeometry(
            new Rect(0, 0, layers.ActualWidth, layers.ActualHeight), 9, 9);

        placeholder = Ui.Text("", Brushes.White);
        placeholder.TextAlignment = TextAlignment.Center;
        spinner = new ProgressBar { Width = 90, Height = 4, Margin = new Thickness(0, 0, 0, 8) };
        connect = Ui.Button("Conectar câmera", connectAction);
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(spinner);
        center.Children.Add(placeholder);
        center.Children.Add(connect);
        layers.Children.Add(center);
        if (!focused)
        {
            var click = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand };
            click.MouseLeftButtonUp += (_, _) => focus();
            layers.Children.Add(click);
        }

        badges = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        };
        var namePanel = new StackPanel { Orientation = Orientation.Horizontal };
        namePanel.Children.Add(dot);
        namePanel.Children.Add(title);
        badges.Children.Add(Pill(namePanel, Ui.Color("#E61A2430")));
        recordingBadge = Pill(recording, Brushes.DarkRed);
        badges.Children.Add(recordingBadge);
        layers.Children.Add(badges);

        photo = Ui.Button("Tirar foto", photoAction);
        record = Ui.Button("Gravar", recordAction);
        record.Background = Brushes.DarkRed;
        record.Foreground = Brushes.White;
        actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom
        };
        actions.Children.Add(photo);
        actions.Children.Add(record);
        layers.Children.Add(actions);
        MouseEnter += (_, _) => UpdateHover();
        MouseLeave += (_, _) => UpdateHover();
        UpdateHover();
    }

    private void ForegroundForText()
    {
        title.Foreground = Brushes.White;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.MaxWidth = 160;
    }

    private static Border Pill(UIElement content, Brush background) => new()
    {
        Child = content, CornerRadius = new CornerRadius(10), Background = background,
        Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0, 0, 4, 0)
    };

    private void UpdateHover()
    {
        badges.Visibility = Visibility.Visible;
        actions.Visibility = !focused && IsMouseOver ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Update(Camera camera, CameraState state, AppSettings settings)
    {
        var active = state.IsActive;
        title.Text = camera.DisplayName;
        dot.Foreground = Ui.StatusColor(state.Status.Color);
        recording.Text = $"● REC {state.Elapsed}";
        recordingBadge.Visibility = state.IsRecording ? Visibility.Visible : Visibility.Collapsed;
        photo.IsEnabled = state.IsConnected;
        record.IsEnabled = active;
        record.Content = MainWindow.RecordText(state);
        var playing = active && state.IsConnected;
        var waiting = active && state.WantsConnection && state.Status.Text == "Conectando…";
        spinner.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        spinner.IsIndeterminate = waiting;
        connect.Visibility = focused && active && !state.WantsConnection ? Visibility.Visible : Visibility.Collapsed;
        placeholder.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        placeholder.Text = !camera.Enabled ? "Câmera desativada" :
            !active ? "Configure a câmera em Ajustes" : state.Status.Text;
        if (!playing)
        {
            Stop();
            return;
        }
        var audioEnabled = focused && settings.EffectivePlayerMode == "hls";
        var url = focused
            ? PlayerUrlBuilder.Focused(camera, settings.EffectiveViewQuality, audioEnabled)
            : PlayerUrlBuilder.Grid(camera);
        if (player is null)
        {
            player = new LivePlayer(url, audioEnabled, !focused);
            video.Children.Add(player);
            if (!focused)
                player.IsHitTestVisible = false;
        }
        else
            player.SetSource(url, audioEnabled);
        player.Debug = settings.DebugPlayer;
    }

    public void Stop()
    {
        if (player is null)
            return;
        player.Dispose();
        video.Children.Clear();
        player = null;
    }
}
