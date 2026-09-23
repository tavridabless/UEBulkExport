namespace UEBulkExport.Tests;

public sealed class LogTests
{
    private sealed class RecordingSink : ILogSink
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_entries) return _entries.ToList(); }
        }

        public void Write(in LogEntry entry)
        {
            lock (_entries) _entries.Add(entry);
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> with a sink attached and returns only the entries it produced.
    /// </summary>
    private static List<LogEntry> Capture(string marker, Action body)
    {
        var sink = new RecordingSink();
        Log.AddSink(sink);
        try
        {
            body();
        }
        finally
        {
            Log.RemoveSink(sink);
        }

        // Log is process-wide, so other tests may interleave; keep only our own entries.
        return sink.Entries.Where(e => e.Message.Contains(marker)).ToList();
    }

    [Fact]
    public void Info_warn_error_raw_reach_the_sink_with_their_levels()
    {
        var marker = Guid.NewGuid().ToString("N");

        var entries = Capture(marker, () =>
        {
            Log.Info($"info {marker}");
            Log.Warn($"warn {marker}");
            Log.Error($"error {marker}");
            Log.Raw($"raw {marker}");
        });

        Assert.Equal(
            [LogLevel.Info, LogLevel.Warn, LogLevel.Error, LogLevel.Raw],
            entries.Select(e => e.Level).ToArray());
        Assert.Equal($"info {marker}", entries[0].Message);
        Assert.Null(entries[0].Detail);
    }

    [Fact]
    public void Problem_carries_headline_and_detail()
    {
        var marker = Guid.NewGuid().ToString("N");

        var entries = Capture(marker, () => Log.Problem($"headline {marker}", "the detail"));

        var entry = Assert.Single(entries);
        Assert.Equal(LogLevel.Problem, entry.Level);
        Assert.Equal($"headline {marker}", entry.Message);
        Assert.Equal("the detail", entry.Detail);
    }

    [Fact]
    public void Removed_sink_receives_nothing_more()
    {
        var marker = Guid.NewGuid().ToString("N");
        var sink = new RecordingSink();

        Log.AddSink(sink);
        Log.RemoveSink(sink);
        Log.Info($"after removal {marker}");

        Assert.DoesNotContain(sink.Entries, e => e.Message.Contains(marker));
    }

    [Fact]
    public void Format_returns_raw_message_verbatim()
    {
        var entry = new LogEntry(new DateTime(2026, 1, 2, 3, 4, 5), LogLevel.Raw, "  table row  ");
        Assert.Equal("  table row  ", entry.Format());
    }

    [Fact]
    public void Format_prefixes_info_with_time_and_tag()
    {
        var entry = new LogEntry(new DateTime(2026, 1, 2, 3, 4, 5), LogLevel.Info, "hello");
        Assert.Equal("03:04:05 [INF] hello", entry.Format());
    }

    [Theory]
    [InlineData(LogLevel.Warn, "[WRN]")]
    [InlineData(LogLevel.Error, "[ERR]")]
    public void Format_uses_the_matching_tag(LogLevel level, string tag)
    {
        var entry = new LogEntry(DateTime.Now, level, "x");
        Assert.Contains(tag, entry.Format());
    }

    [Fact]
    public void Format_problem_with_detail_puts_detail_on_its_own_line()
    {
        var entry = new LogEntry(DateTime.Now, LogLevel.Problem, "Head", "Why");
        var nl = Environment.NewLine;
        Assert.Equal($"{nl}Head{nl}Why{nl}", entry.Format());
    }

    [Fact]
    public void Format_problem_without_detail_is_headline_between_blank_lines()
    {
        var entry = new LogEntry(DateTime.Now, LogLevel.Problem, "Head");
        var nl = Environment.NewLine;
        Assert.Equal($"{nl}Head{nl}", entry.Format());
    }
}
