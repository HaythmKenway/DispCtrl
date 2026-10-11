using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DispCtrl.Linux.Gui.ViewModels;

namespace DispCtrl.Linux.Gui.Services;

/// <summary>Shows each display's number on the display itself, as Identify
/// does on Windows: a small undecorated window in its bottom-left corner.</summary>
public static class DisplayIdentifier
{
    private static readonly List<Window> Open = [];
    private static int _shown;

    public static void Show(IEnumerable<OutputViewModel> outputs, TimeSpan duration)
    {
        CloseAll();
        foreach (var view in outputs)
        {
            var o = view.Output;
            var window = new Window
            {
                WindowDecorations = WindowDecorations.None,
                Topmost = true,
                ShowActivated = false,
                ShowInTaskbar = false,
                CanResize = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Background = (IBrush?)Application.Current?.FindResource("AccentFillColorDefaultBrush") ?? Brushes.SteelBlue,
                Position = new PixelPoint(o.PosX + 48, o.PosY + o.HeightPx - 240),
                Content = new StackPanel
                {
                    Margin = new Thickness(32, 12, 32, 16),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = view.Number.ToString(CultureInfo.InvariantCulture),
                            FontSize = 96,
                            FontWeight = FontWeight.SemiBold,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Foreground = (IBrush?)Application.Current?.FindResource("TextOnAccentFillColorPrimaryBrush") ?? Brushes.White,
                        },
                        new TextBlock
                        {
                            Text = view.Label,
                            FontSize = 14,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Foreground = (IBrush?)Application.Current?.FindResource("TextOnAccentFillColorPrimaryBrush") ?? Brushes.White,
                        },
                    },
                },
            };
            // Placed once its real size is known: the corner, 48 px in, on
            // whatever scale this display is drawn at.
            window.Opened += (_, _) =>
            {
                var size = PixelSize.FromSize(window.ClientSize, window.DesktopScaling);
                window.Position = new PixelPoint(o.PosX + 48, o.PosY + o.HeightPx - size.Height - 48);
            };
            window.Show();
            Open.Add(window);
        }
        // Pressed again while showing: the newer set gets its full time.
        int shown = ++_shown;
        DispatcherTimer.RunOnce(() => { if (shown == _shown) CloseAll(); }, duration);
    }

    private static void CloseAll()
    {
        foreach (var window in Open) window.Close();
        Open.Clear();
    }
}
