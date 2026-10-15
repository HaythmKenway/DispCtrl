using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using DispCtrl.Linux.Gui.Services;
using DispCtrl.Linux.Settings;
using FluentAvalonia.UI.Controls;
using static DispCtrl.Linux.Gui.Views.CardBuilder;

namespace DispCtrl.Linux.Gui.Views.Pages;

/// <summary>Hotkeys, features, triggers and app rules: the Windows app's
/// Hotkeys page and its tabs, on one page.</summary>
public sealed class AutomationPage : UserControl
{
    private readonly StackPanel _hotkeys = List(), _features = List(), _triggers = List(), _rules = List();
    private readonly TextBlock _hotkeyNote = Caption("");
    private readonly TextBlock _status = new() { Classes = { "status" }, IsVisible = false, Margin = new Thickness(2, 8, 0, 0) };

    public AutomationPage()
    {
        var (_, body) = Page(this, "Automation",
            "Shortcuts that work with every window closed, named steps, and running them when something happens. The engine does all of it.");
        body.Children.Add(_status);

        body.Children.Add(Section("Hotkeys"));
        body.Children.Add(_hotkeyNote);
        body.Children.Add(_hotkeys);
        var keys = new TextBox { PlaceholderText = "Ctrl+Alt+M", Width = 150, Margin = new Thickness(0, 0, 8, 8) };
        var command = new TextBox { PlaceholderText = "preset apply Movie", Width = 240, Margin = new Thickness(0, 0, 8, 8) };
        AutomationProperties.SetName(keys, "New hotkey's keys");
        AutomationProperties.SetName(command, "New hotkey's command");
        var add = Button("Add", async () => await Run(["hotkeys", "add", keys.Text ?? "", .. Words(command.Text)], () => { keys.Text = command.Text = ""; }));
        body.Children.Add(Card(FASymbol.Add, "Add a hotkey", "Modifiers and a key, and any dispctrl-linux command: nightlight toggle, brightness up, preset apply Night, rgb colour warm, focus toggle…",
            null, out _, new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Children = { keys, command, add } }));
        body.Children.Add(Button("Put the default hotkeys back", async () => await Run(["hotkeys", "reset"]), new Thickness(0, 8, 0, 0)));

        body.Children.Add(Section("Features"));
        body.Children.Add(Caption("Steps run in order: a dispctrl-linux command, wait 500 (milliseconds), or run a program."));
        body.Children.Add(_features);
        var fname = new TextBox { PlaceholderText = "Name", Width = 150, Margin = new Thickness(0, 0, 8, 8) };
        var steps = new TextBox { PlaceholderText = "nightlight 80; dim 0.6 --all; rgb colour warm", Width = 320, Margin = new Thickness(0, 0, 8, 8) };
        AutomationProperties.SetName(fname, "New feature's name");
        AutomationProperties.SetName(steps, "New feature's steps");
        body.Children.Add(Card(FASymbol.Add, "Add a feature", "Steps separated by semicolons.", null, out _,
            new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Children = { fname, steps,
                Button("Add", async () => await Run(["features", "add", fname.Text ?? "", steps.Text ?? ""], () => { fname.Text = steps.Text = ""; })) } }));

        body.Children.Add(Section("Triggers"));
        body.Children.Add(Caption("When something happens, run a feature."));
        body.Children.Add(_triggers);
        var ev = new ComboBox { ItemsSource = Trigger.Events, SelectedIndex = 0, Width = 190, Margin = new Thickness(0, 0, 8, 8) };
        var match = new TextBox { PlaceholderText = "Match (app, display, 22:30)", Width = 200, Margin = new Thickness(0, 0, 8, 8) };
        var minutes = new NumericUpDown { Minimum = 1, Maximum = 1440, Value = 10, Width = 120, Margin = new Thickness(0, 0, 8, 8), FormatString = "0" };
        _featurePick = new ComboBox { Width = 160, Margin = new Thickness(0, 0, 8, 8), PlaceholderText = "Feature" };
        AutomationProperties.SetName(ev, "Event");
        AutomationProperties.SetName(match, "Match");
        AutomationProperties.SetName(minutes, "Idle minutes");
        AutomationProperties.SetName(_featurePick, "Feature to run");
        body.Children.Add(Card(FASymbol.Add, "Add a trigger", "Idle uses the minutes; the others use the match, when given.", null, out _,
            new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Children = { ev, match, minutes, _featurePick,
                Button("Add", async () =>
                {
                    var args = new List<string> { "triggers", "add", ev.SelectedItem as string ?? "", _featurePick.SelectedItem as string ?? "" };
                    if (!string.IsNullOrWhiteSpace(match.Text)) { args.Add("--match"); args.Add(match.Text.Trim()); }
                    args.Add("--minutes"); args.Add(((int)(minutes.Value ?? 10)).ToString(CultureInfo.InvariantCulture));
                    await Run([.. args], () => match.Text = "");
                }) } }));

        body.Children.Add(Section("App rules"));
        body.Children.Add(Caption("While an app is in front, a preset applies; when it leaves, the desk goes back, stays, or takes another preset."));
        body.Children.Add(_rules);
        var app = new TextBox { PlaceholderText = "App (vlc, steam, firefox)", Width = 190, Margin = new Thickness(0, 0, 8, 8) };
        _presetPick = new ComboBox { Width = 160, Margin = new Thickness(0, 0, 8, 8), PlaceholderText = "Preset" };
        var then = new ComboBox { ItemsSource = new[] { "previous", "keep" }, SelectedIndex = 0, Width = 130, Margin = new Thickness(0, 0, 8, 8) };
        AutomationProperties.SetName(app, "App");
        AutomationProperties.SetName(_presetPick, "Preset while in front");
        AutomationProperties.SetName(then, "Afterwards");
        body.Children.Add(Card(FASymbol.Add, "Add an app rule", "The app's window class, as dispctrl-linux windows lists it.", null, out _,
            new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Children = { app, _presetPick, then,
                Button("Add", async () => await Run(["preset", "rule", "add", app.Text?.Trim() ?? "", _presetPick.SelectedItem as string ?? "",
                    "--then", then.SelectedItem as string ?? "previous"], () => app.Text = "")) } }));

        AttachedToVisualTree += (_, _) => _ = LoadAsync();
    }

    private readonly ComboBox _featurePick, _presetPick;

    private async Task LoadAsync()
    {
        var s = await Task.Run(SettingsStore.Load);
        var engine = await CommandSender.RunAsync(["hotkeys"]);
        var note = engine.Stdout.Split("\n\n", 2) is [_, var tail] ? tail.Trim() : "";
        _hotkeyNote.Text = note.Length > 0 ? note : "On GNOME they are listed under Settings, Keyboard, Custom Shortcuts too.";

        _hotkeys.Children.Clear();
        foreach (var h in s.Hotkeys ?? [])
        {
            var keys = h.Keys;
            var on = Switch(h.Enabled);
            on.IsCheckedChanged += async (_, _) => await Run(["hotkeys", on.IsChecked == true ? "on" : "off", keys]);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { on, Button("Remove", async () => await Run(["hotkeys", "remove", keys])) } };
            _hotkeys.Children.Add(Card(FASymbol.Keyboard, h.Keys, h.Command, row, out _));
        }

        _features.Children.Clear();
        if (s.Features.Count == 0) _features.Children.Add(Caption("None yet."));
        foreach (var f in s.Features)
        {
            var name = f.Name;
            TextBlock status = null!;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
            {
                Button("Run", async () => Show(status, await DeskActions.RunAsync("features", "run", name))),
                Button("Remove", async () => await Run(["features", "remove", name])),
            } };
            _features.Children.Add(Card(FASymbol.Play, f.Name, string.Join("; ", f.Steps), row, out status));
        }

        _triggers.Children.Clear();
        if (s.Triggers.Count == 0) _triggers.Children.Add(Caption("None yet."));
        for (int i = 0; i < s.Triggers.Count; i++)
        {
            var t = s.Triggers[i];
            string n = (i + 1).ToString(CultureInfo.InvariantCulture);
            string when = t.Event == "idle" ? $"Idle for {t.Minutes} min" : string.IsNullOrEmpty(t.Match) ? Pretty(t.Event) : $"{Pretty(t.Event)}: {t.Match}";
            var on = Switch(t.Enabled);
            on.IsCheckedChanged += async (_, _) => await Run(["triggers", on.IsChecked == true ? "on" : "off", n]);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { on, Button("Remove", async () => await Run(["triggers", "remove", n])) } };
            _triggers.Children.Add(Card(FASymbol.AlertOn, when, $"Runs {t.Feature}", row, out _));
        }
        _featurePick.ItemsSource = s.Features.Select(f => f.Name).ToList();

        _rules.Children.Clear();
        if (s.AppRules.Count == 0) _rules.Children.Add(Caption("None yet."));
        for (int i = 0; i < s.AppRules.Count; i++)
        {
            var r = s.AppRules[i];
            string n = (i + 1).ToString(CultureInfo.InvariantCulture);
            _rules.Children.Add(Card(FASymbol.AllApps, r.App, $"{r.Preset} after {r.DelaySeconds} s in front; then {r.Then}",
                Button("Remove", async () => await Run(["preset", "rule", "remove", n])), out _));
        }
        _presetPick.ItemsSource = s.Presets.Select(p => p.Name).ToList();
    }

    private static string Pretty(string ev) => char.ToUpperInvariant(ev[0]) + ev[1..].Replace('-', ' ');

    private static string[] Words(string? text) => Automation.FeatureRunner.Split(text ?? "").ToArray();

    private async Task Run(string[] args, Action? done = null)
    {
        var error = await DeskActions.RunAsync(args);
        Show(_status, error);
        if (error is null) done?.Invoke();
        await LoadAsync();
    }

    private static Button Button(string text, Func<Task> click, Thickness? margin = null)
    {
        var b = new Button { Content = text, Margin = margin ?? default, VerticalAlignment = VerticalAlignment.Top };
        b.Click += async (_, _) => await click();
        return b;
    }
}
