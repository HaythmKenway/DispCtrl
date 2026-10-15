using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DispCtrl.Linux.Engine;
using DispCtrl.Linux.Gui.Services;
using DispCtrl.Linux.Gui.Views.Pages;
using FluentAvalonia.UI.Controls;

namespace DispCtrl.Linux.Gui;

/// <summary>The navigation frame. Each page is made once, when first
/// opened, and kept: its controls hold what was last read, and making it
/// again would read the hardware again.</summary>
public partial class MainWindow : Window
{
    private readonly Dictionary<string, Control> _pages = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _engineTimer;
    private bool? _engineRunning;

    public MainWindow()
    {
        InitializeComponent();

        Nav.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItem is FANavigationViewItem { Tag: string tag }) Show(tag);
        };
        Nav.SelectedItem = Nav.MenuItems.OfType<FANavigationViewItem>().First();

        RefreshEngine();
        // Only while the window is open: the engine can start or stop under it.
        _engineTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => RefreshEngine());
        _engineTimer.Start();
        Closed += (_, _) => _engineTimer.Stop();

        _ = Desk.Current.RescanAsync();
    }

    /// <summary>Opens a page by its tag: displays, brightness, care, windows,
    /// presets or settings.</summary>
    public void Show(string tag)
    {
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "brightness" => new BrightnessPage(),
                "care" => new ScreenCarePage(),
                "presets" => new PresetsPage(),
                "lighting" => new LightingPage(),
                "automation" => new AutomationPage(),
                "windows" => new WindowsPage(),
                "settings" => new SettingsPage(),
                _ => new DisplaysPage(),
            };
            _pages[tag] = page;
            if (page is SettingsPage settings) settings.ShowEngine(_engineRunning);
        }
        PageHost.Content = page;
        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<FANavigationViewItem>()
            .FirstOrDefault(i => (string?)i.Tag == tag);
        if (item is not null && Nav.SelectedItem != item) Nav.SelectedItem = item;
    }

    private void OnEngineHelp(object? sender, RoutedEventArgs e) => Show("settings");

    private void RefreshEngine()
    {
        _ = Task.Run(() => EngineClient.IsRunning()).ContinueWith(t =>
        {
            _engineRunning = t.Result;
            EngineBanner.IsVisible = !t.Result;
            if (_pages.TryGetValue("settings", out var page) && page is SettingsPage settings) settings.ShowEngine(t.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
