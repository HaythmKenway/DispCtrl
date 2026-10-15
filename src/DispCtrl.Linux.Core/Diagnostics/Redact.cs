using System.Text.RegularExpressions;

namespace DispCtrl.Linux.Diagnostics;

/// <summary>Takes out what identifies the person or the hardware, for the log
/// and for every problem report.</summary>
public static partial class Redact
{
    [GeneratedRegex(@"(?<=\|)[^|\s]{4,}")]
    private static partial Regex Serials();

    [GeneratedRegex(@"(?i)\b(serial(?: number)?|s/n)\b(\s*[:=]\s*)\S+")]
    private static partial Regex LabelledSerials();

    /// <summary>The home folder and account name, and serial numbers (the
    /// <c>model|serial</c> form and <c>serial: X</c>).</summary>
    public static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 1) text = text.Replace(home, "~", StringComparison.Ordinal);
        var user = Environment.UserName;
        if (user.Length > 2) text = Regex.Replace(text, $@"\b{Regex.Escape(user)}\b", "<user>");
        text = Serials().Replace(text, "<serial>");
        text = LabelledSerials().Replace(text, "$1$2<serial>");
        return text;
    }
}
