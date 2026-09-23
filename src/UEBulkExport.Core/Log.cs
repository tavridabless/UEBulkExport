namespace UEBulkExport;

public enum LogLevel
{
    /// <summary>Unprefixed output: tables, progress lines, help text.</summary>
    Raw,
    Info,
    Warn,
    Error,

    /// <summary>A headline with an explanation under it, for problems worth stopping on.</summary>
    Problem
}

public readonly record struct LogEntry(DateTime Time, LogLevel Level, string Message, string? Detail = null)
{
    /// <summary>The line as it appears in the log file and on a plain console.</summary>
    public string Format()
    {
        switch (Level)
        {
            case LogLevel.Raw:
                return Message;
            case LogLevel.Problem:
                return string.IsNullOrEmpty(Detail)
                    ? Environment.NewLine + Message + Environment.NewLine
                    : Environment.NewLine + Message + Environment.NewLine + Detail + Environment.NewLine;
            default:
                var tag = Level switch { LogLevel.Info => "INF", LogLevel.Warn => "WRN", _ => "ERR" };
                return $"{Time:HH:mm:ss} [{tag}] {Message}";
        }
    }
}

/// <summary>Receives every log entry. Implementations must tolerate calls from several threads.</summary>
public interface ILogSink
{
    void Write(in LogEntry entry);
}

/// <summary>Appends formatted lines to a file. Opened by the front end once the output folder is known.</summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
    private readonly StreamWriter _writer;

    public FileLogSink(string path, bool append)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append) { AutoFlush = true };
        if (append) _writer.WriteLine($"----- run started {DateTime.Now:yyyy-MM-dd HH:mm:ss} -----");
    }

    public void Write(in LogEntry entry)
    {
        lock (_writer) _writer.WriteLine(entry.Format());
    }

    public void Dispose()
    {
        lock (_writer) _writer.Dispose();
    }
}

/// <summary>
/// Process-wide log. The exporter only ever talks to this class; front ends decide where the
/// entries go by registering sinks - a coloured console for the CLI, a list view for the GUI.
/// </summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private static readonly List<ILogSink> Sinks = [];
    private static FileLogSink? _file;

    public static void AddSink(ILogSink sink)
    {
        lock (Gate) Sinks.Add(sink);
    }

    public static void RemoveSink(ILogSink sink)
    {
        lock (Gate) Sinks.Remove(sink);
    }

    /// <summary>Starts (or replaces) the file log. Earlier runs are kept when <paramref name="append"/> is set.</summary>
    public static void OpenFile(string path, bool append = true)
    {
        lock (Gate)
        {
            _file?.Dispose();
            _file = new FileLogSink(path, append);
        }
    }

    public static void Info(string message) => Emit(LogLevel.Info, message);
    public static void Warn(string message) => Emit(LogLevel.Warn, message);
    public static void Error(string message) => Emit(LogLevel.Error, message);
    public static void Raw(string message) => Emit(LogLevel.Raw, message);

    /// <summary>A headline with an indented explanation under it, for problems worth stopping on.</summary>
    public static void Problem(string headline, string? detail = null) => Emit(LogLevel.Problem, headline, detail);

    private static void Emit(LogLevel level, string message, string? detail = null)
    {
        var entry = new LogEntry(DateTime.Now, level, message, detail);

        lock (Gate)
        {
            _file?.Write(entry);
            foreach (var sink in Sinks) sink.Write(entry);
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }
}
