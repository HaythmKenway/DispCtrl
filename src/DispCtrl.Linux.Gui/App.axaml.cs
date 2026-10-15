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
        // An exception from a handler the code did not foresee (a desktop
        // service that crashed under a picker, a launcher) is logged, not
        // allowed to close the tray and the window with it.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            DispCtrl.Linux.Diagnostics.AppLog.Error("gui", "unhandled exception on the interface thread", e.Exception);
            e.Handled = true;
        };
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
            if (_tray is not null || _trayStarting) _ = Desk.Current.RescanAsync();
            // Answered only once the interface thread has run the request, so a
            // blocked one is reported by the second start instead of swallowed.
            Instance.Listen(request => Dispatcher.UIThread.InvokeAsync(() => Handle(request)).Wait(TimeSpan.FromSeconds(3)));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Handle(string request)
    {
        try { Dispatch(request); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Write($"'{request}' failed: {ex}"); }
    }

    private void Dispatch(string request)
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

    private bool _trayStarting;

    private async void StartTray()
    {
        if (_trayStarting || _tray is not null) return;
        _trayStarting = true;
        try
        {
            _tray = await TrayHost.StartAsync(OpenWindow, OpenPage, OpenPanel, Quit, Program.Request == Instance.Tray);
            // Built now, while nobody waits: the first click shows it at once.
            Dispatcher.UIThread.Post(CreatePanel, DispatcherPriority.Background);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No session bus or no tray host: the window still works without it.
            Log.Write($"tray: {ex.Message}");
            _tray = null;
            if (Program.Request == Instance.Tray && _window is null && _panel?.IsVisible != true) _desktop?.Shutdown();
        }
        finally { _trayStarting = false; }
    }

    public void OpenWindow()
    {
        Log.Write("open window");
        // A window that was built but never shown (its Show threw) must not stay
        // as _window: every later click would activate nothing, for ever.
        if (_window is not null && !_window.IsVisible && _window.WindowState != Avalonia.Controls.WindowState.Minimized)
        {
            Log.Write("the window was never shown; building it again");
            _window = null;
        }
        if (_window is null)
        {
            MainWindow window;
            try
            {
                window = new MainWindow();
                window.Closed += (_, _) =>
                {
                    if (_window == window) _window = null;
                    if (_tray is null && !_trayStarting && _panel?.IsVisible != true) _desktop?.Shutdown();
                };
                if (_desktop is not null) _desktop.MainWindow = window;
                window.Show();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Write($"the window could not be opened: {ex}");
                _window = null;
                return;
            }
            _window = window;
        }
        if (_window.WindowState == Avalonia.Controls.WindowState.Minimized) _window.WindowState = Avalonia.Controls.WindowState.Normal;
        _window.Activate();
        // Mutter often refuses plain focus to a window that is open behind
        // others or on another workspace; a moment on top brings it forward.
        _window.Topmost = true;
        _window.Topmost = false;
    }

    private void OpenPage(string tag)
    {
        OpenWindow();
        _window?.Show(tag);
    }

    /// <summary>Shows the quick panel, or hides it when it is open: one panel
    /// for the life of the process, built ahead of the first click.</summary>
    public void OpenPanel()
    {
        Log.Write("open panel");
        CreatePanel();
        // Visible but not active: it never took focus (Mutter may refuse it), so
        // it is not the one a click should close; show it properly instead.
        if (_panel!.IsVisible && _panel.IsActive) { _panel.Hide(); return; }
        // The click on the icon that hid the panel (it lost focus) arrives
        // here a moment later: it means close, not open again.
        if (DateTime.UtcNow - _panelHidden < TimeSpan.FromMilliseconds(400)) return;
        _panel.ShowNearPointer();
    }

    /// <summary>Builds the panel without showing it.</summary>
    private void CreatePanel()
    {
        if (_panel is null)
        {
            _panel = new QuickPanel(OpenWindow);
            _panel.PropertyChanged += (_, e) =>
            {
                if (e.Property != Avalonia.Visual.IsVisibleProperty || _panel.IsVisible) return;
                _panelHidden = DateTime.UtcNow;
                // Opened on its own (--quick-panel), with no tray or window to stay for.
                if (_tray is null && !_trayStarting && _window is null) _desktop?.Shutdown();
            };
        }
    }

    private DateTime _panelHidden;

    private void Quit()
    {
        _tray?.Dispose();
        _tray = null;
        if (_window is null && _panel?.IsVisible != true) _desktop?.Shutdown();
    }
}
