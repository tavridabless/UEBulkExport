namespace UEBulkExport;

public static class Log
{
    private static readonly Lock Gate = new();
    private static StreamWriter? _file;

    public static void OpenFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _file = new StreamWriter(path, append: false) { AutoFlush = true };
    }

    public static void Info(string message) => Write("INF", message, ConsoleColor.Gray);
    public static void Warn(string message) => Write("WRN", message, ConsoleColor.Yellow);
    public static void Error(string message) => Write("ERR", message, ConsoleColor.Red);

    /// <summary>Unprefixed output: tables, progress lines, the help text.</summary>
    public static void Raw(string message)
    {
        lock (Gate)
        {
            Console.WriteLine(message);
            _file?.WriteLine(message);
        }
    }

    /// <summary>A headline with an indented explanation under it, for problems worth stopping on.</summary>
    public static void Problem(string headline, string? detail = null)
    {
        lock (Gate)
        {
            var previous = Console.ForegroundColor;

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine(headline);
            _file?.WriteLine();
            _file?.WriteLine(headline);

            if (!string.IsNullOrEmpty(detail))
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine(detail);
                _file?.WriteLine(detail);
            }

            Console.WriteLine();
            _file?.WriteLine();
            Console.ForegroundColor = previous;
        }
    }

    private static void Write(string level, string message, ConsoleColor color)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{level}] {message}";
        lock (Gate)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(line);
            Console.ForegroundColor = previous;
            _file?.WriteLine(line);
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
