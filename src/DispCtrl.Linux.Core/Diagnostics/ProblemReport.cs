using System.Text;

namespace DispCtrl.Linux.Diagnostics;

/// <summary>What <c>report</c> hands to a browser or a mail program, built so
/// it always fits and always reads as redacted.</summary>
/// <remarks>
/// GitHub refuses an issue address much over 8 KB, and a mailto link is
/// unreliable past about 2 KB, and neither can carry an attachment. So the
/// link holds the diagnostics and as much of the log's end as fits, and says
/// when it did not hold it all; the whole redacted log goes to a file the
/// person attaches by hand.
/// </remarks>
public static class ProblemReport
{
    public const string Email = "hello@akileswar.com";
    public const string Repository = "https://github.com/HaythmKenway/DispCtrl-linux";

    public const int IssueLimit = 7500, MailLimit = 2000, LogLines = 40;

    private const string AttachNote = "The whole redacted log is longer than a link can carry; attach the file saved by `dispctrl-linux report --save`.";

    public static string IssueUrl(string what, string report, IReadOnlyList<string> log, out bool complete)
    {
        what = Cap(what, 1500);
        var title = Uri.EscapeDataString(what.Length > 0 ? Cap(what.Split('\n')[0], 100) : "Problem report");
        string Build(string diagnostics, int logLines)
        {
            var body = new StringBuilder();
            body.Append("**What happened**\n\n").Append(what.Length > 0 ? what : "(describe it here)")
                .Append("\n\n**Diagnostics** (from dispctrl-linux report)\n\n```\n").Append(diagnostics).Append("\n```");
            if (logLines > 0)
                body.Append("\n\n**Log** (the last ").Append(logLines).Append(" lines, redacted)\n\n```\n")
                    .Append(string.Join('\n', log.Skip(log.Count - logLines))).Append("\n```");
            if (logLines < log.Count) body.Append("\n\n").Append(AttachNote);
            return $"{Repository}/issues/new?title={title}&body={Uri.EscapeDataString(body.ToString())}";
        }
        return Fit(Build, report, log.Count, IssueLimit, out complete);
    }

    public static string MailUrl(string what, string report, IReadOnlyList<string> log, string bundlePath, out bool complete)
    {
        what = Cap(what, 300);
        var subject = Uri.EscapeDataString("DispCtrl for Linux: " + (what.Length > 0 ? Cap(what.Split('\n')[0], 80) : "problem report"));
        string Build(string diagnostics, int logLines)
        {
            var body = new StringBuilder();
            body.Append(what.Length > 0 ? what : "(describe what happened here)")
                .Append("\n\nPlease attach the full redacted log, saved at:\n").Append(Redact.Scrub(bundlePath))
                .Append("\n\n").Append(diagnostics);
            if (logLines > 0)
                body.Append("\n\nLog (last ").Append(logLines).Append(" lines):\n").Append(string.Join('\n', log.Skip(log.Count - logLines)));
            return $"mailto:{Email}?subject={subject}&body={Uri.EscapeDataString(body.ToString())}";
        }
        return Fit(Build, report, log.Count, MailLimit, out complete);
    }

    /// <summary>The longest link under <paramref name="limit"/>: all the
    /// diagnostics and the newest log lines that fit; failing that, fewer
    /// diagnostics. Trimming the text, never the link, keeps every escape whole.</summary>
    private static string Fit(Func<string, int, string> build, string report, int logCount, int limit, out bool complete)
    {
        var whole = build(report, logCount);
        complete = whole.Length <= limit;
        if (complete) return whole;

        if (build(report, 0).Length <= limit)
        {
            int lo = 0, hi = logCount;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (build(report, mid).Length <= limit) lo = mid; else hi = mid - 1;
            }
            return build(report, lo);
        }

        int low = 0, high = report.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (build(report[..mid], 0).Length <= limit) low = mid; else high = mid - 1;
        }
        return build(report[..low], 0);
    }

    /// <summary>The whole redacted report and log, as one text file.</summary>
    public static string Bundle(string what, string report)
    {
        var text = new StringBuilder();
        text.AppendLine("DispCtrl for Linux problem report. Serial numbers, your account name and your home folder are taken out;");
        text.AppendLine("read it before you send it, and edit anything else you would rather not share.");
        text.AppendLine();
        if (what.Length > 0) text.AppendLine($"What happened: {what}").AppendLine();
        text.AppendLine(report).AppendLine();
        text.AppendLine("---- log ----");
        text.AppendLine(AppLog.ReadAll());
        return Redact.Scrub(text.ToString());
    }

    public static string DefaultBundlePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        $"dispctrl-linux-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

    /// <summary>Writes the bundle, readable by its owner only, and returns where.</summary>
    public static string SaveBundle(string? file, string what, string report)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.GetFullPath(string.IsNullOrWhiteSpace(file) ? DefaultBundlePath() : file.Replace("~/", home + "/", StringComparison.Ordinal));
        if (Directory.Exists(path)) path = Path.Combine(path, Path.GetFileName(DefaultBundlePath()));
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        stream.Write(Encoding.UTF8.GetBytes(Bundle(what, report)));
        return path;
    }

    private static string Cap(string text, int length) => text.Length <= length ? text : text[..length];
}
