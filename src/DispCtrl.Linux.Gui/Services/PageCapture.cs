using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace DispCtrl.Linux.Gui.Services;

/// <summary>Draws each page of the window to a PNG file, for the store listing
/// and the documentation, from the control tree rather than from the screen.</summary>
internal static class PageCapture
{
    private static readonly string[] Pages = ["displays", "brightness", "care", "lighting", "presets", "automation", "settings"];

    private static Task Save(Avalonia.Controls.Window window, string path) => Dispatcher.UIThread.InvokeAsync(() =>
    {
        var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        using var file = File.Create(path);
#pragma warning disable CS0618
        bitmap.Save(file);
#pragma warning restore CS0618
    }, DispatcherPriority.Render).GetTask();

    public static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var window = new MainWindow { Width = 1180, Height = 780 };
            desktop.MainWindow = window;
            window.Show();
            await Task.Delay(1500);
            // The engine is not this build's: the banners would be in every picture.
            window.EngineBanner.IsVisible = false;
            window.InstallBanner.IsVisible = false;
            foreach (var page in Pages)
            {
                window.Show(page);
                // Pages read the hardware off the UI thread and fill in when it answers.
                await Task.Delay(3500);
                window.EngineBanner.IsVisible = false;
                window.InstallBanner.IsVisible = false;
                await Save(window, Path.Combine(folder, page + ".png"));
            }
            var panel = new Views.QuickPanel();
            panel.Show();
            await Task.Delay(3500);
            await Save(panel, Path.Combine(folder, "quick-panel.png"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"capture: {ex}");
        }
        finally { Dispatcher.UIThread.Post(() => desktop.Shutdown()); }
    }
}
