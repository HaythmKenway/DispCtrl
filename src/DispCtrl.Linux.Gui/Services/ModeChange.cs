using Avalonia.Threading;
using DispCtrl.Linux.Arrangement;
using DispCtrl.Linux.Hardware;
using FluentAvalonia.UI.Controls;

namespace DispCtrl.Linux.Gui.Services;

/// <summary>Applies a change to the desk's geometry (a mode, a turn, an
/// arrangement, Extend) and offers to keep it, putting everything back if
/// nobody answers: a layout the monitor cannot show leaves no picture to find
/// the button on.</summary>
/// <remarks>Every read and write of X happens off the UI thread; only the
/// dialog and its countdown run on it.</remarks>
public static class ModeChange
{
    /// <summary>How long a change lasts unless kept: Windows' own fifteen
    /// seconds, long enough to find the button on a picture that came back.</summary>
    public const int KeepSeconds = 15;

    private static bool _busy;

    /// <summary>True while a change is applied or being confirmed, so a second
    /// one is not started on top of it.</summary>
    public static bool Busy => _busy;

    /// <summary>Runs <paramref name="args"/>, asks to keep it, and returns what
    /// to say: null when it was applied and kept, the refusal when it was
    /// refused, or how it was put back.</summary>
    public static async Task<(bool Kept, string? Message)> RunAsync(string[] args)
    {
        if (_busy) return (false, "Another change is waiting for an answer.");
        _busy = true;
        try
        {
            var before = await Task.Run(XRandR.Query);
            var reply = await CommandSender.RunAsync(args);
            if (reply.ExitCode != 0) return (false, CommandSender.Reason(reply));
            if (await KeepAsync()) return (true, null);

            var undo = await Task.Run(() =>
            {
                var plan = DisplayPlans.Changes(DisplayPlans.Restore(before), XRandR.Query());
                string? primary = before.FirstOrDefault(o => o.Primary)?.Name;
                return plan.Count == 0 || XRandR.Configure(plan, primary, out var error) ? null : error;
            });
            return (false, undo is null ? "Put back as it was." : $"Could not put it back: {undo}");
        }
        finally { _busy = false; }
    }

    private static async Task<bool> KeepAsync()
    {
        int left = KeepSeconds;
        var dialog = new FAContentDialog
        {
            Title = "Keep these display settings?",
            Content = $"Going back in {left} seconds.",
            PrimaryButtonText = "Keep changes",
            CloseButtonText = "Go back",
            DefaultButton = FAContentDialogButton.Close,
        };
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) =>
        {
            left--;
            dialog.Content = $"Going back in {left} second{(left == 1 ? "" : "s")}.";
            if (left <= 0) dialog.Hide(FAContentDialogResult.None);
        });
        timer.Start();
        try { return await dialog.ShowAsync() == FAContentDialogResult.Primary; }
        finally { timer.Stop(); }
    }
}
