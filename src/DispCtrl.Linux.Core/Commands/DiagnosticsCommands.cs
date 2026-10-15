using System.Diagnostics;
using DispCtrl.Linux.Diagnostics;
using DispCtrl.Linux.Distribution;

namespace DispCtrl.Linux.Commands;

public static partial class CommandRunner
{
    /// <summary><c>report</c>: what a problem report needs, redacted, and the
    /// ways to send it: a GitHub issue (<c>--open</c>, <c>--url</c>), an email
    /// (<c>--mail</c>, <c>--mail-url</c>) or a file to attach (<c>--save</c>).
    /// Nothing leaves by itself: the report is shown, and a browser or a mail
    /// program opens it for the person to read and send.</summary>
    private static int ReportCommand(string[] args, CommandContext c)
    {
        // --save takes a file only when one follows, so TryFlags cannot read it.
        bool save = false;
        string? saveTo = null;
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != "--save") { rest.Add(args[i]); continue; }
            save = true;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) saveTo = args[++i];
        }
        if (!TrySplit(rest.ToArray(), out var verbs, out var flags, c)) return AskedWrongly;
        if (verbs.Length > 0) return Wrong(c, "report takes --what \"what happened\", and --open, --url, --mail, --mail-url or --save [FILE]");

        var doctor = new StringWriter();
        Doctor(c with { Out = doctor });
        var text = new System.Text.StringBuilder();
        text.AppendLine($"dispctrl-linux {Version}, {(Distro.InSnap ? "snap" : "package")}, {Distro.Family}, {RuntimeInformationText()}");
        text.AppendLine($"session: {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")} / {Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")}");
        text.AppendLine();
        text.Append(doctor);
        var report = Scrub(text.ToString());
        string what = flags.GetValueOrDefault("--what") ?? "";
        var log = AppLog.Tail(ProblemReport.LogLines);
        AppLog.Info("report", $"requested ({string.Join(' ', args.Where(a => a.StartsWith("--", StringComparison.Ordinal) && a != "--what"))})");

        // Only an address for --url and --mail-url: the window opens it itself,
        // through the desktop's launcher, which a confined engine cannot reach.
        if (flags.ContainsKey("--url"))
        {
            c.Out.WriteLine(ProblemReport.IssueUrl(what, report, log, out bool whole));
            if (!whole) c.Err.WriteLine(OversizeNote);
            return Done;
        }
        if (flags.ContainsKey("--mail-url") || flags.ContainsKey("--mail"))
        {
            string bundle;
            try { bundle = ProblemReport.SaveBundle(saveTo, what, report); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { return Refuse(c, $"the full log could not be saved to attach ({ex.Message})"); }
            var mail = ProblemReport.MailUrl(what, report, log, bundle, out bool whole);
            if (flags.ContainsKey("--mail-url"))
            {
                c.Out.WriteLine(mail);
                c.Err.WriteLine($"The full redacted log is saved at {Scrub(bundle)}; attach it to the email.");
                return Done;
            }
            c.Out.WriteLine(report);
            c.Out.WriteLine($"Nothing was sent. Opening a message to {ProblemReport.Email} with the report above{(whole ? " and the log" : " (cut to fit a link)")}; read it and send it yourself.");
            c.Out.WriteLine($"Attach the full redacted log, saved at {Scrub(bundle)}.");
            if (!Launch(mail)) c.Out.WriteLine($"No mail program could be opened. Write to {ProblemReport.Email} and attach that file.");
            return Done;
        }
        if (save)
        {
            try
            {
                var path = ProblemReport.SaveBundle(saveTo, what, report);
                c.Out.WriteLine($"Saved the redacted report and log to {Scrub(path)}. Read it, then drag it into a GitHub issue (a link cannot carry an attachment) or attach it to an email to {ProblemReport.Email}.");
                return Done;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { return Refuse(c, $"the report could not be saved ({ex.Message})"); }
        }

        c.Out.WriteLine(report);
        if (log.Length > 0)
        {
            c.Out.WriteLine($"Log (last {log.Length} lines of {Scrub(AppLog.FilePath)}):");
            foreach (var line in log) c.Out.WriteLine("  " + line);
        }
        if (!flags.ContainsKey("--open"))
            return Write(c.Out, "Nothing was sent. report --open --what \"...\" starts a GitHub issue with this, --mail an email, --save [FILE] writes the whole thing for you to attach.");
        var url = ProblemReport.IssueUrl(what, report, log, out bool complete);
        if (!complete) c.Out.WriteLine(OversizeNote);
        if (!Launch(url)) c.Out.WriteLine(url);
        return Done;
    }

    private const string OversizeNote = "The log is longer than a link can carry: run report --save and drag the file into the issue.";

    /// <summary>Hands a link or folder to the desktop's launcher.</summary>
    private static bool Launch(string target)
    {
        foreach (var tool in target.StartsWith("mailto:", StringComparison.Ordinal) ? new[] { "xdg-email", "xdg-open" } : ["xdg-open"])
        {
            if (!DispCtrl.Linux.Hardware.Shell.TryWhich(tool)) continue;
            try { Process.Start(new ProcessStartInfo(tool) { UseShellExecute = false, ArgumentList = { target } })?.Dispose(); return true; }
            catch (System.ComponentModel.Win32Exception ex) { AppLog.Warn("report", $"{tool} could not start: {ex.Message}"); }
        }
        return false;
    }

    /// <summary><c>logs</c>: the log's last lines, where it is kept, or its folder opened.</summary>
    private static int LogsCommand(string[] args, CommandContext c)
    {
        if (!TrySplit(args, out var verbs, out var flags, c)) return AskedWrongly;
        if (verbs.Length > 0) return Wrong(c, "logs takes --tail N, --path or --open");
        if (flags.ContainsKey("--path")) return Write(c.Out, AppLog.FilePath);
        if (flags.ContainsKey("--open"))
        {
            var folder = AppLog.Directory;
            try { System.IO.Directory.CreateDirectory(folder); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return Launch(folder) ? Done : Refuse(c, $"no program could open {folder}");
        }
        int tail = 50;
        if (flags.TryGetValue("--tail", out var n) && (!int.TryParse(n, out tail) || tail < 1)) return Wrong(c, "--tail takes a number of lines, 1 or more");
        var lines = AppLog.Tail(tail);
        if (lines.Length == 0) return Write(c.Out, $"Nothing logged yet ({AppLog.FilePath}).");
        foreach (var line in lines) c.Out.WriteLine(line);
        return Done;
    }
}
