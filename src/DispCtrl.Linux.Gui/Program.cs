using Avalonia;
using Avalonia.Media;
using System;

namespace DispCtrl.Linux.Gui;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

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
