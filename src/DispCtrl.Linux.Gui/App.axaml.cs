using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DispCtrl.Linux.Gui.Services;
using DispCtrl.Linux.Gui.Views;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Gui;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private TrayHost? _tray;
    private MainWindow? _window;
    private QuickPanel? _panel;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // GNOME's theme and accent where GNOME's settings tool is there to ask;
        // elsewhere Avalonia follows the desktop portal's light or dark itself.
        bool gsettings = Linux.Hardware.Shell.TryWhich("gsettings");
        foreach (var theme in Styles.OfType<FluentAvalonia.Styling.FluentAvaloniaTheme>())
        {
            theme.PreferSystemTheme = gsettings;
            theme.PreferUserAccentColor = gsettings;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            // Explicit shutdown: with the tray icon the process outlives its
            // window; without it, closing the window ends it (OnWindowClosed).
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            if (SettingsStore.Load().Tray.Enabled) StartTray();
            else if (Program.Request == Instance.Tray) { desktop.Shutdown(); return; }

            if (Program.Request == Instance.Window) OpenWindow();
            else if (Program.Request == Instance.Panel) OpenPanel();
            if (_tray is not null) _ = Desk.Current.RescanAsync();
            Instance.Listen(request => Dispatcher.UIThread.Post(() => Handle(request)));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Handle(string request)
    {
        if (request == Instance.Panel) OpenPanel();
        else if (request == Instance.Window) OpenWindow();
        else if (_tray is null && SettingsStore.Load().Tray.Enabled) StartTray();
    }

    /// <summary>Shows the tray icon now, when it was switched on in Settings.</summary>
    public void EnsureTray()
    {
        if (_tray is null) StartTray();
    }

    private void StartTray()
    {
        try { _tray = new TrayHost(OpenWindow, OpenPage, OpenPanel, Quit); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No session bus or no tray host: the window still works without it.
            System.Diagnostics.Trace.WriteLine($"tray: {ex.Message}");
            _tray = null;
        }
    }

    public void OpenWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow();
            _window.Closed += (_, _) =>
            {
                _window = null;
                if (_tray is null) _desktop?.Shutdown();
            };
            if (_desktop is not null) _desktop.MainWindow = _window;
            _window.Show();
        }
        if (_window.WindowState == Avalonia.Controls.WindowState.Minimized) _window.WindowState = Avalonia.Controls.WindowState.Normal;
        _window.Activate();
    }

    private void OpenPage(string tag)
    {
        OpenWindow();
        _window?.Show(tag);
    }

    public void OpenPanel()
    {
        if (_panel is not null) { _panel.Close(); return; }
        _panel = new QuickPanel(OpenWindow);
        _panel.Closed += (_, _) =>
        {
            _panel = null;
            if (_tray is null && _window is null) _desktop?.Shutdown();
        };
        _panel.ShowNearPointer();
    }

    private void Quit()
    {
        _tray?.Dispose();
        _tray = null;
        if (_window is null && _panel is null) _desktop?.Shutdown();
    }
}
