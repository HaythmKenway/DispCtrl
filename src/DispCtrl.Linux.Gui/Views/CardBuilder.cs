using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using FluentAvalonia.UI.Controls;

namespace DispCtrl.Linux.Gui.Views;

/// <summary>Builds the pages' cards from code, in the same classes the XAML
/// pages use (Styles/Cards.axaml), for pages whose rows come and go: hotkeys,
/// features, triggers, lighting devices.</summary>
public static class CardBuilder
{
    /// <summary>A page: a scrolling column under a title and subtitle, with the
    /// narrow layout below <see cref="PageWidth.Narrow"/>.</summary>
    public static (Panel Root, StackPanel Body) Page(UserControl owner, string title, string subtitle)
    {
        var body = new StackPanel { Classes = { "page" } };
        body.Children.Add(new TextBlock { Classes = { "pagetitle" }, Text = title });
        body.Children.Add(new TextBlock { Classes = { "pagesubtitle" }, Text = subtitle });
        var root = new Panel();
        root.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = body });
        owner.Content = root;
        PageWidth.Track(owner, root);
        return (root, body);
    }

    public static TextBlock Section(string text) => new() { Classes = { "section" }, Text = text };

    public static TextBlock Caption(string text) => new() { Classes = { "caption" }, Text = text, Margin = new Thickness(2, 0, 0, 8) };

    public static StackPanel List() => new() { Spacing = 4 };

    /// <summary>A card: icon, title and caption, a control beside them (under
    /// them when narrow), and a status line for refusals.</summary>
    public static Border Card(FASymbol? icon, string title, string? caption, Control? control, out TextBlock status, Control? below = null)
    {
        var grid = new Grid
        {
            Classes = { "cardgrid" },
            ColumnDefinitions = new ColumnDefinitions(icon is null ? "0,*,Auto" : "40,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };
        if (icon is null) grid.Classes.Add("bare");
        if (icon is { } symbol) grid.Children.Add(new FASymbolIcon { Classes = { "cardicon" }, Symbol = symbol });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        text.Children.Add(new TextBlock { Classes = { "title" }, Text = title });
        if (caption is not null) text.Children.Add(new TextBlock { Classes = { "caption" }, Text = caption });
        status = new TextBlock { Classes = { "status" }, IsVisible = false };
        text.Children.Add(status);
        if (below is not null) text.Children.Add(below);
        grid.Children.Add(text);
        if (control is not null)
        {
            if (control is ToggleSwitch) control.Classes.Add("toggle");
            else control.Classes.Add("control");
            if (control is ToggleSwitch or Button or ComboBox) AutomationProperties.SetName(control, title);
            grid.Children.Add(control);
        }
        return new Border { Classes = { "card" }, Child = grid };
    }

    public static ToggleSwitch Switch(bool on) => new() { IsChecked = on, OnContent = "On", OffContent = "Off" };

    /// <summary>A slider with its value beside it.</summary>
    public static Grid Slider(double min, double max, double value, Func<double, string> label, Action<double> changed, string name)
    {
        var grid = new Grid { Classes = { "slider" }, ColumnDefinitions = new ColumnDefinitions("*,56") };
        var text = new TextBlock { Text = label(value) };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center, TickFrequency = 1, IsSnapToTickEnabled = true };
        AutomationProperties.SetName(slider, name);
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty) return;
            text.Text = label(slider.Value);
            changed(slider.Value);
        };
        grid.Children.Add(slider);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    public static void Show(TextBlock status, string? error)
    {
        status.Text = error ?? "";
        status.IsVisible = !string.IsNullOrEmpty(error);
    }
}
