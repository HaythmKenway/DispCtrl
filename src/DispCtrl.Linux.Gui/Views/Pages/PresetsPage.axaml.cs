using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DispCtrl.Linux.Gui.Services;
using DispCtrl.Linux.Gui.ViewModels;
using DispCtrl.Linux.Hardware;
using DispCtrl.Linux.Presets;
using DispCtrl.Linux.Settings;
using FluentAvalonia.UI.Controls;

namespace DispCtrl.Linux.Gui.Views.Pages;

/// <summary>Save the desk under a name and put it back: the Windows app's
/// Presets page, less desk profiles and app rules for now.</summary>
public partial class PresetsPage : UserControl
{
    private readonly Dictionary<string, CheckBox> _parts = new(StringComparer.Ordinal);

    public PresetsPage()
    {
        InitializeComponent();
        PageWidth.Track(this, Root);
        foreach (var part in PresetParts.All)
        {
            var box = new CheckBox { Content = PresetRowViewModel.Label(part), IsChecked = true, Margin = new(0, 0, 16, 0) };
            AutomationProperties.SetName(box, $"Include {PresetRowViewModel.Label(part)}");
            _parts[part] = box;
            PartsPanel.Children.Add(box);
        }
        NameBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) OnSave(null, new RoutedEventArgs()); };
        AttachedToVisualTree += (_, _) => _ = LoadAsync();
    }

    /// <summary>Reads the presets and whether each fits the displays now.</summary>
    public async Task LoadAsync()
    {
        var (presets, outputs, names) = await Task.Run(() =>
        {
            var x = XRandR.IsAvailable;
            return (SettingsStore.Load().Presets, x ? XRandR.Query() : [], x ? XRandR.MonitorNames() : new Dictionary<string, string>());
        });
        PresetList.ItemsSource = presets.Select(p => new PresetRowViewModel(p, PresetDesk.Mismatch(p, outputs, names))).ToList();
        EmptyText.IsVisible = presets.Count == 0;
        var suggested = PresetDesk.Suggested()
            .Select(t => new SuggestedPresetViewModel(t, presets.Any(p => string.Equals(p.Name, t.Name, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        SuggestedList.ItemsSource = suggested;
        AddAllButton.IsEnabled = suggested.Any(t => t.CanAdd);
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0) { Status(SaveStatus, "Give the preset a name first."); return; }
        var skip = _parts.Where(p => p.Value.IsChecked != true).Select(p => p.Key).ToList();
        if (skip.Count == PresetParts.All.Count) { Status(SaveStatus, "Choose at least one part to keep."); return; }
        await Run(SaveButton, SaveStatus, ["preset", "save", name, "--skip", skip.Count == 0 ? "none" : string.Join(',', skip)]);
        NameBox.Text = "";
    }

    private async void OnAddSuggested(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } button) await Run(button, ListStatus, ["preset", "add", name]);
    }

    private async void OnAddAll(object? sender, RoutedEventArgs e) => await Run(AddAllButton, ListStatus, ["preset", "add", "all"]);

    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } button) return;
        await Run(button, ListStatus, ["preset", "apply", name]);
        // The layout, brightness or night light may all have moved under the other pages.
        _ = Desk.Current.RescanAsync();
    }

    private async void OnUpdate(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string name }) await Run(null, ListStatus, ["preset", "save", name]);
    }

    private async void OnDesk(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string name }) return;
        bool on = SettingsStore.Load().Presets.FirstOrDefault(p => p.Name == name)?.Desk != true;
        await Run(null, ListStatus, ["preset", "desk", name, on ? "on" : "off"]);
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string name }) return;
        var dialog = new FAContentDialog
        {
            Title = $"Delete {name}?",
            Content = "The desk is not changed; only the saved preset goes.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == FAContentDialogResult.Primary) await Run(null, ListStatus, ["preset", "delete", name]);
    }

    private async void OnRename(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string name }) return;
        var box = new TextBox { Text = name, Width = 260 };
        var dialog = new FAContentDialog
        {
            Title = $"Rename {name}",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == FAContentDialogResult.Primary && box.Text?.Trim() is { Length: > 0 } to && to != name)
            await Run(null, ListStatus, ["preset", "rename", name, to]);
    }

    private async Task Run(Button? button, TextBlock status, string[] args)
    {
        if (button is not null) button.IsEnabled = false;
        Status(status, args[1] is "save" or "apply" ? "Working… (DDC/CI monitors take a few seconds each)" : null, error: false);
        var reply = await CommandSender.RunAsync(args);
        if (button is not null) button.IsEnabled = true;
        Status(status, reply.ExitCode == 0 ? null : string.Join(" ", reply.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Replace("dispctrl-linux: ", "", StringComparison.Ordinal))));
        await LoadAsync();
    }

    private static void Status(TextBlock block, string? text, bool error = true)
    {
        block.Text = text ?? "";
        block.IsVisible = !string.IsNullOrEmpty(text);
        block.Classes.Set("status", error);
        block.Classes.Set("caption", !error);
    }
}
