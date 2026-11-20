using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TapoCam.Core;

namespace TapoCam.App.Views;

internal static class Ui
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("pt-BR");

    public static Button Button(string text, Action action, string? tooltip = null)
    {
        var button = new Button { Content = text, Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(2), ToolTip = tooltip };
        button.Click += (_, _) => action();
        return button;
    }

    public static TextBlock Text(string text, Brush? foreground = null)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        if (foreground is not null)
            block.Foreground = foreground;
        return block;
    }

    public static CheckBox Check(string text, bool initial, Action<bool> change)
    {
        var box = new CheckBox { Content = text, IsChecked = initial, Margin = new Thickness(2, 5, 2, 5) };
        box.Checked += (_, _) => change(true);
        box.Unchecked += (_, _) => change(false);
        return box;
    }

    public static Brush Color(string color) => (Brush?)new BrushConverter().ConvertFromInvariantString(color) ?? Brushes.Gray;

    public static TextBlock Dot(string color) => new()
    {
        Text = "●", Foreground = Color(color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0)
    };

    public static void Failure(Window owner, Exception error) =>
        MessageBox.Show(owner, $"Não foi possível concluir: {CredentialMasker.Mask(error.Message)}",
            owner.Title, MessageBoxButton.OK, MessageBoxImage.Error);

    public static async Task Run(Window owner, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Failure(owner, ex); }
    }

    public static void Run(Window owner, Action action)
    {
        try { action(); }
        catch (Exception ex) { Failure(owner, ex); }
    }
}
