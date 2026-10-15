namespace DispCtrl.Linux.Gui.Services.Tray;

public enum TrayToggle { None, Check, Radio }

/// <summary>One entry of the tray icon's menu, as the desktop's tray draws
/// it: text, a separator, a tick or a dot, or a submenu.</summary>
public sealed class TrayMenuItem
{
    public TrayMenuItem(string label, Action? click = null)
    {
        Label = label;
        Click = click;
    }

    public static TrayMenuItem Separator() => new("") { IsSeparator = true };

    public string Label { get; }
    public Action? Click { get; }
    public bool IsSeparator { get; private init; }
    public bool Enabled { get; init; } = true;
    public TrayToggle Toggle { get; init; }
    public bool Checked { get; init; }
    public List<TrayMenuItem> Children { get; } = [];

    /// <summary>Its number in the menu as sent over D-Bus; 0 is the root.</summary>
    internal int Id { get; set; }

    public TrayMenuItem Add(TrayMenuItem child)
    {
        Children.Add(child);
        return this;
    }
}
