using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DispCtrl.Linux.Gui.Services;

namespace DispCtrl.Linux.Gui;

class Program
{
    /// <summary>What this start asked for: the window, the quick panel
    /// (--quick-panel) or only the tray icon (--tray, the sign-in start).</summary>
    public static string Request { get; private set; } = Instance.Window;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        Request = args.Contains("--tray") ? Instance.Tray : args.Contains("--quick-panel") ? Instance.Panel : Instance.Window;
        // A process already running (the tray's, from sign-in) does it instead.
        if (Instance.TrySend(Request)) return 0;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // The window's X class names its desktop entry, dispctrl-linux.desktop.
            // Avalonia's default is the program's name, dispctrl-linux-gui, which
            // matched no entry: GNOME's dock and KDE's task manager showed the
            // generic icon for the window instead of DispCtrl's.
            .With(new X11PlatformOptions { WmClass = "dispctrl-linux" })
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            // Inter, which ships inside the app, as the default too: Avalonia
            // asks fontconfig for one and aborted at start where it named none
            // (a minimal Arch or openSUSE with no font package yet).
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
            .LogToTrace();
}
