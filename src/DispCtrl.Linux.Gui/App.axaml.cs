using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace DispCtrl.Linux.Gui;

public partial class App : Application
{
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
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}