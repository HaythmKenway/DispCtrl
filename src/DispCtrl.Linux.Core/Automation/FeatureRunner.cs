using System.Diagnostics;
using System.Globalization;
using DispCtrl.Linux.Commands;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Automation;

/// <summary>Runs a feature's steps in order: a dispctrl-linux command, <c>wait
/// MS</c>, or <c>run PROGRAM ARGS</c>. A step that fails is reported and the
/// rest still run, as on Windows.</summary>
public static class FeatureRunner
{
    /// <summary>Why a step could never run; null when it can.</summary>
    public static string? Problem(string step)
    {
        var words = Split(step);
        if (words.Count == 0) return "an empty step";
        if (words[0] == "wait")
            return words.Count == 2 && int.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms) && ms is >= 0 and <= 600_000
                ? null : "wait takes milliseconds, up to ten minutes";
        if (words[0] == "run") return words.Count >= 2 ? null : "run takes a program";
        if (words[0] == "features") return "a feature cannot run features";
        return CommandRunner.HotkeyCommandProblem(step);
    }

    /// <param name="command">Runs a dispctrl-linux command and returns the reason
    /// it was refused, or null.</param>
    public static List<string> Run(Feature feature, Func<string[], string?> command, bool dryRun = false, TextWriter? log = null)
    {
        var problems = new List<string>();
        foreach (var step in feature.Steps)
        {
            var words = Split(step);
            if (Problem(step) is { } why) { problems.Add($"{step}: {why}"); continue; }
            log?.WriteLine($"{(dryRun ? "would run" : "run")}: {step}");
            if (dryRun) continue;
            switch (words[0])
            {
                case "wait":
                    Thread.Sleep(int.Parse(words[1], CultureInfo.InvariantCulture));
                    break;
                case "run":
                    try
                    {
                        var info = new ProcessStartInfo(words[1]) { UseShellExecute = false };
                        foreach (var a in words.Skip(2)) info.ArgumentList.Add(a);
                        Process.Start(info)?.Dispose();
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        problems.Add($"{step}: {ex.Message}");
                    }
                    break;
                default:
                    if (command([.. words]) is { } error) problems.Add($"{step}: {error}");
                    break;
            }
        }
        return problems;
    }

    /// <summary>Words, with double quotes holding spaces: <c>preset apply "Late night"</c>.</summary>
    internal static List<string> Split(string text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (char ch in text.Trim())
        {
            if (ch == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (any) { words.Add(current.ToString()); current.Clear(); any = false; }
                continue;
            }
            current.Append(ch);
            any = true;
        }
        if (any) words.Add(current.ToString());
        return words;
    }
}
