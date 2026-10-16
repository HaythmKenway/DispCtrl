using System.Text.Json;
using System.Text.Json.Nodes;
using DispCtrl.Linux.Automation;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Commands;

public static partial class CommandRunner
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary><c>settings get|set|reset|export|validate|import</c>: the saved
    /// settings, by JSON path, as the Windows <c>settings</c> command.</summary>
    private static int SettingsCommand(string[] args, CommandContext c)
    {
        if (args.Length == 0) args = ["get"];
        var tree = JsonNode.Parse(SettingsStore.ToJson(SettingsStore.Load()))!;
        switch (args[0])
        {
            case "get":
            {
                if (!Pairs(args[1..], c, out var flags)) return AskedWrongly;
                var node = flags.TryGetValue("--path", out var path) ? Walk(tree, path, false) : tree;
                if (node is null) return Wrong(c, $"no setting at {flags["--path"]}");
                c.Out.WriteLine(node.ToJsonString(Pretty));
                return Done;
            }
            case "set":
            {
                if (!Pairs(args[1..], c, out var flags) || !flags.TryGetValue("--path", out var path) || !flags.TryGetValue("--value", out var value))
                    return Wrong(c, "settings set takes --path /unison/level --value 50 (JSON; text needs quotes)");
                JsonNode? parsed;
                try { parsed = JsonNode.Parse(value); }
                catch (JsonException) { return Wrong(c, "--value is JSON: 50, true, \"text\" or an object"); }
                return Change(tree, path, parsed, c);
            }
            case "reset":
            {
                if (!Pairs(args[1..], c, out var flags)) return AskedWrongly;
                var defaults = JsonNode.Parse(SettingsStore.ToJson(new LinuxSettings()))!;
                if (!flags.TryGetValue("--path", out var path))
                {
                    SettingsStore.Replace(new LinuxSettings());
                    c.Out.WriteLine("Every setting is back to its default.");
                    return Done;
                }
                var fresh = Walk(defaults, path, false);
                if (fresh is null) return Wrong(c, $"no setting at {path}");
                return Change(tree, path, fresh.DeepClone(), c);
            }
            case "export":
            {
                var text = tree.ToJsonString(Pretty);
                if (args is [_, "--output", var file])
                {
                    File.WriteAllText(file, text);
                    c.Out.WriteLine($"Settings written to {file}.");
                }
                else c.Out.WriteLine(text);
                return Done;
            }
            case "validate" or "import":
            {
                if (args.Length != 2) return Wrong(c, $"settings {args[0]} takes a file");
                string text;
                try { text = File.ReadAllText(args[1]); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Refuse(c, ex.Message); }
                if (!SettingsStore.TryParse(text, out var settings, out var error)) return Refuse(c, $"not settings: {error}");
                if (args[0] == "import") SettingsStore.Replace(settings);
                c.Out.WriteLine(args[0] == "import" ? "Settings replaced." : "These are valid settings.");
                return Done;
            }
            default:
                return Wrong(c, "settings takes get [--path P], set --path P --value JSON, reset [--path P], export [--output FILE], validate FILE or import FILE");
        }
    }

    /// <summary>--name value pairs; TryFlags treats --path as bare, for logs.</summary>
    private static bool Pairs(string[] args, CommandContext c, out Dictionary<string, string> flags)
    {
        flags = new(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) { Wrong(c, $"{args[i]} needs a value"); return false; }
            flags[args[i]] = args[i + 1];
        }
        return true;
    }

    private static int Change(JsonNode tree, string path, JsonNode? value, CommandContext c)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Wrong(c, "the path names a setting: /unison/level");
        if (Walk(tree, string.Join('/', parts[..^1]), true) is not JsonObject parent) return Wrong(c, $"no setting at {path}");
        parent[parts[^1]] = value;
        if (!SettingsStore.TryParse(tree.ToJsonString(), out var settings, out var error)) return Refuse(c, $"{path} would not be a valid setting: {error}");
        SettingsStore.Replace(settings);
        c.Out.WriteLine($"{path} saved.");
        return Done;
    }

    private static JsonNode? Walk(JsonNode root, string path, bool allowEmpty)
    {
        var node = root;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node switch
            {
                JsonObject o => o.FirstOrDefault(p => string.Equals(p.Key, part, StringComparison.OrdinalIgnoreCase)).Value,
                JsonArray a when int.TryParse(part, out int i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (node is null) return null;
        }
        return node ?? (allowEmpty ? root : null);
    }

    /// <summary><c>commands</c>: every command's first word.</summary>
    private static int CommandsCommand(CommandContext c)
    {
        foreach (var verb in Verbs.Append("doctor").Append("settings").Append("apply").Append("engine").Append("report").Append("logs").Append("update").Append("hotkeys").Append("triggers").Distinct().Order(StringComparer.Ordinal))
            c.Out.WriteLine(verb);
        return Done;
    }

    /// <summary><c>apply FILE [--dry-run]</c>: a file of commands, one a line
    /// (blank lines and lines starting with # are skipped), run in order.</summary>
    private static int ApplyCommand(string[] args, CommandContext c)
    {
        bool dry = args.Contains("--dry-run");
        var files = args.Where(a => a != "--dry-run").ToArray();
        if (files.Length != 1) return Wrong(c, "apply takes a file of commands, one a line, and --dry-run");
        string[] lines;
        try { lines = File.ReadAllLines(files[0]); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Refuse(c, ex.Message); }
        var steps = lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        if (steps.Count == 0) return Wrong(c, "the file holds no commands");
        var problems = FeatureRunner.Run(new Feature { Name = Path.GetFileName(files[0]), Steps = steps }, step => RunStep(step, c), dry, c.Out);
        foreach (var p in problems) c.Err.WriteLine($"dispctrl-linux: {p}");
        return problems.Count == 0 ? Done : Refused;
    }
}
