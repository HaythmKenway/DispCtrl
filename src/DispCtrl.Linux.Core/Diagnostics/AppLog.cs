using System.Collections.Concurrent;
using System.Text;

namespace DispCtrl.Linux.Diagnostics;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>The one log the engine, the window and the command line share: a
/// rotating file in the data folder, which the report commands read and the
/// journal does not keep for long.</summary>
/// <remarks>
/// Logging must never be the reason something fails or stalls. Callers only
/// queue a line (dropped when the queue is full, so a runaway loop cannot eat
/// memory); one background thread formats, redacts and appends it, and every
/// step on that thread swallows its own failure - a full disk, a read-only
/// folder or a folder removed under it lose lines and nothing else, and are
/// tried again after a pause rather than on every line. Several processes
/// append to the same file, so it is opened per batch with shared access, and
/// rotation (a rename, 1 MB by three files) tolerates another process having
/// done it first.
/// </remarks>
public static class AppLog
{
    public const string FileName = "dispctrl-linux.log";
    public const int Files = 3;
    private const int QueueLimit = 2000, MaxMessage = 16_000;

    /// <summary>Raised in tests to see rotation without writing megabytes.</summary>
    internal static long MaxBytes = 1_000_000;

    private static readonly BlockingCollection<Entry> Queue = new(QueueLimit);
    private static readonly object Start = new();
    private static Thread? _writer;
    private static int _pending;
    private static string? _failedFolder;
    private static DateTime _retryAfter;
    private static bool _hooked;

    private readonly record struct Entry(DateTimeOffset At, LogLevel Level, string Process, string Component, string Message);

    /// <summary>What lines are tagged with, set once by the program: engine, gui, or cli.</summary>
    public static string Process { get; private set; } = "cli";

    /// <summary>Whether lines also go to stderr, which the snap's journal and
    /// the desktop session's log keep.</summary>
    public static bool Echo { get; private set; }

    public static LogLevel Minimum { get; set; } = ParseLevel(Environment.GetEnvironmentVariable("DISPCTRL_LINUX_LOG_LEVEL"));

    public static void Configure(string process, bool echo)
    {
        Process = process;
        Echo = echo;
    }

    /// <summary><c>$DISPCTRL_LINUX_LOG_DIR</c>, else <c>$DISPCTRL_DATA_DIR/logs</c>
    /// (scratch runs), else the snap's own folder, else
    /// <c>$XDG_STATE_HOME/dispctrl-linux/logs</c> (<c>~/.local/state</c>).</summary>
    public static string Directory
    {
        get
        {
            if (Environment.GetEnvironmentVariable("DISPCTRL_LINUX_LOG_DIR") is { Length: > 0 } explicitDir) return explicitDir;
            if (Environment.GetEnvironmentVariable("DISPCTRL_DATA_DIR") is { Length: > 0 } scratch) return Path.Combine(scratch, "logs");
            var snap = Environment.GetEnvironmentVariable("SNAP_USER_COMMON");
            if (string.IsNullOrEmpty(snap)) snap = Environment.GetEnvironmentVariable("SNAP_USER_DATA");
            if (!string.IsNullOrEmpty(snap)) return Path.Combine(snap, "logs");
            var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
            return Path.Combine(state, "dispctrl-linux", "logs");
        }
    }

    public static string FilePath => Path.Combine(Directory, FileName);

    public static void Debug(string component, string message) => Write(LogLevel.Debug, component, message);
    public static void Info(string component, string message) => Write(LogLevel.Info, component, message);
    public static void Warn(string component, string message) => Write(LogLevel.Warn, component, message);
    public static void Error(string component, string message) => Write(LogLevel.Error, component, message);

    /// <summary>For the many plain-text lines the engine and the window have
    /// always written: a line that says something failed or was refused is a
    /// warning, the rest information.</summary>
    public static void Auto(string component, string message) =>
        Write(message.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("refused", StringComparison.OrdinalIgnoreCase)
            || message.Contains("could not", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unhandled", StringComparison.OrdinalIgnoreCase) ? LogLevel.Warn : LogLevel.Info, component, message);

    /// <summary>An error with the whole exception, stack and inner exceptions included.</summary>
    public static void Error(string component, string message, Exception ex) => Write(LogLevel.Error, component, $"{message}: {ex}");

    /// <summary>Queues a line. Never throws and never waits.</summary>
    public static void Write(LogLevel level, string component, string message)
    {
        try
        {
            if (level < Minimum) return;
            EnsureWriter();
            if (message.Length > MaxMessage) message = message[..MaxMessage] + " …(cut)";
            Interlocked.Increment(ref _pending);
            if (!Queue.TryAdd(new Entry(DateTimeOffset.Now, level, Process, component, message)))
                Interlocked.Decrement(ref _pending);
        }
        catch (Exception) { }
    }

    /// <summary>Waits, up to <paramref name="timeoutMs"/>, for queued lines to
    /// reach the file: at shutdown and before the log is read back.</summary>
    public static void Flush(int timeoutMs = 1500)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (Volatile.Read(ref _pending) > 0 && DateTime.UtcNow < deadline) Thread.Sleep(5);
        }
        catch (Exception) { }
    }

    /// <summary>Logs what would otherwise vanish: an exception nothing caught
    /// (flushed at once, since the process is about to end) and a task whose
    /// failure nobody awaited.</summary>
    public static void InstallExceptionHooks()
    {
        lock (Start)
        {
            if (_hooked) return;
            _hooked = true;
        }
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Write(LogLevel.Error, "unhandled", $"{(e.IsTerminating ? "fatal" : "survivable")}: {e.ExceptionObject}");
                Flush(2000);
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Write(LogLevel.Error, "unobserved-task", e.Exception.ToString());
                e.SetObserved();
            };
        }
        catch (Exception) { }
    }

    /// <summary>The log files that exist, oldest first.</summary>
    public static List<string> ExistingFiles()
    {
        var list = new List<string>();
        try
        {
            var main = FilePath;
            for (int i = Files - 1; i >= 1; i--) if (File.Exists($"{main}.{i}")) list.Add($"{main}.{i}");
            if (File.Exists(main)) list.Add(main);
        }
        catch (Exception) { }
        return list;
    }

    /// <summary>The last <paramref name="lines"/> lines, redacted again on the
    /// way out in case an older build wrote them without.</summary>
    public static string[] Tail(int lines)
    {
        Flush();
        var found = new List<string>();
        foreach (var file in Enumerable.Reverse(ExistingFiles()))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                found.InsertRange(0, reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
            catch (Exception) { }
            if (found.Count >= lines) break;
        }
        return found.Skip(Math.Max(0, found.Count - Math.Max(lines, 0))).Select(Redact.Scrub).ToArray();
    }

    /// <summary>Every kept line, oldest first, redacted.</summary>
    public static string ReadAll() => string.Join('\n', Tail(int.MaxValue));

    private static LogLevel ParseLevel(string? text) =>
        Enum.TryParse<LogLevel>(text, ignoreCase: true, out var level) ? level : LogLevel.Info;

    private static void EnsureWriter()
    {
        if (_writer is not null) return;
        lock (Start)
        {
            if (_writer is not null) return;
            _writer = new Thread(Pump) { IsBackground = true, Name = "dispctrl-log" };
            _writer.Start();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush(1500);
        }
    }

    private static void Pump()
    {
        var batch = new StringBuilder();
        while (true)
        {
            int taken = 0;
            try
            {
                var entry = Queue.Take();
                batch.Clear();
                do
                {
                    taken++;
                    Format(batch, entry);
                }
                // A batch is one append, so it stays small beside the cap or rotation could not keep to it.
                while (taken < 200 && batch.Length < MaxBytes / 4 && Queue.TryTake(out entry));
                Persist(batch.ToString());
            }
            catch (Exception) { }
            finally { if (taken > 0) Interlocked.Add(ref _pending, -taken); }
        }
    }

    private static void Format(StringBuilder into, Entry e)
    {
        try
        {
            var text = Redact.Scrub(e.Message).TrimEnd().Replace("\r", "", StringComparison.Ordinal).Replace("\n", "\n    ", StringComparison.Ordinal);
            var level = e.Level switch { LogLevel.Debug => "DEBUG", LogLevel.Info => "INFO ", LogLevel.Warn => "WARN ", _ => "ERROR" };
            into.Append(e.At.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz")).Append(' ').Append(level).Append(' ')
                .Append(e.Process).Append('[').Append(Environment.ProcessId).Append("] ").Append(e.Component).Append(": ").Append(text).Append('\n');
            if (Echo) Console.Error.WriteLine($"dispctrl-linux-{e.Process}: {e.Component}: {text}");
        }
        catch (Exception) { }
    }

    private static void Persist(string text)
    {
        var folder = Directory;
        if (folder == _failedFolder && DateTime.UtcNow < _retryAfter) return;
        try
        {
            System.IO.Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var file = Path.Combine(folder, FileName);
            var bytes = Encoding.UTF8.GetBytes(text);
            Rotate(file, bytes.Length);
            using var stream = new FileStream(file, new FileStreamOptions
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                Share = FileShare.ReadWrite | FileShare.Delete,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            stream.Write(bytes);
            _failedFolder = null;
        }
        catch (Exception)
        {
            // Full disk, read-only or vanished folder: drop these lines, and
            // do not try again for a while.
            _failedFolder = folder;
            _retryAfter = DateTime.UtcNow.AddSeconds(10);
        }
    }

    private static void Rotate(string file, int incoming)
    {
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length + incoming <= MaxBytes) return;
            for (int i = Files - 2; i >= 1; i--)
                if (File.Exists($"{file}.{i}")) File.Move($"{file}.{i}", $"{file}.{i + 1}", overwrite: true);
            File.Move(file, $"{file}.1", overwrite: true);
        }
        catch (Exception) { }
    }
}
