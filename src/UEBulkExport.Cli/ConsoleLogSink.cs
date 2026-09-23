namespace UEBulkExport.CommandLine;

/// <summary>Coloured console output: grey for information, yellow for warnings, red for errors.</summary>
public sealed class ConsoleLogSink : ILogSink
{
    private readonly Lock _gate = new();

    public void Write(in LogEntry entry)
    {
        lock (_gate)
        {
            switch (entry.Level)
            {
                case LogLevel.Raw:
                    Console.WriteLine(entry.Message);
                    break;

                case LogLevel.Problem:
                    Console.WriteLine();
                    WriteColoured(entry.Message, ConsoleColor.Red);
                    if (!string.IsNullOrEmpty(entry.Detail)) WriteColoured(entry.Detail, ConsoleColor.Gray);
                    Console.WriteLine();
                    break;

                default:
                    WriteColoured(entry.Format(), entry.Level switch
                    {
                        LogLevel.Warn => ConsoleColor.Yellow,
                        LogLevel.Error => ConsoleColor.Red,
                        _ => ConsoleColor.Gray
                    });
                    break;
            }
        }
    }

    private static void WriteColoured(string text, ConsoleColor colour)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = colour;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }
}
