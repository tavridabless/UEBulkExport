using System.IO.Pipes;
using System.Text;

namespace UEBulkExport.Tests;

public sealed class CooperativeKeyProbeServerTests
{
    private const string Key = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string GuidText = "12345678-1234-5678-9abc-def012345678";

    [Fact]
    public async Task Authenticated_client_reports_guid_key_and_deduplicates()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var server = new CooperativeKeyProbeServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receive = server.ReceiveAsync(Environment.ProcessId, timeout.Token);

        await using (var client = new NamedPipeClientStream(
                         ".", server.PipeName, PipeDirection.Out, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(timeout.Token);
            await using var writer = new StreamWriter(client, Encoding.ASCII, 1024, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            await writer.WriteLineAsync($"HELLO|1|{server.TokenHex}|{Environment.ProcessId}");
            await writer.WriteLineAsync($"KEY|{GuidText}|{Key}");
            await writer.WriteLineAsync($"KEY|{GuidText}|{Key}");
            await writer.WriteLineAsync("DONE");
        }

        var capture = await receive;
        var candidate = Assert.Single(capture.Candidates);
        Assert.True(capture.Connected);
        Assert.Equal("0x" + Key, candidate.Key);
        Assert.Equal(GuidText, candidate.ContainerGuid);
        Assert.Equal(AesKeyOrigin.CooperativeProbe, candidate.Origin);
        Assert.Equal(100, candidate.Confidence);
        Assert.Equal(-1, candidate.Offset);
    }

    [Fact]
    public async Task Wrong_one_time_token_is_rejected()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var server = new CooperativeKeyProbeServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receive = server.ReceiveAsync(Environment.ProcessId, timeout.Token);

        await using (var client = new NamedPipeClientStream(
                         ".", server.PipeName, PipeDirection.Out, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(timeout.Token);
            var wrong = new string('0', 64);
            var bytes = Encoding.ASCII.GetBytes($"HELLO|1|{wrong}|{Environment.ProcessId}\n");
            await client.WriteAsync(bytes, timeout.Token);
            await client.FlushAsync(timeout.Token);
        }

        var error = await Assert.ThrowsAsync<UserFacingException>(async () => await receive);
        Assert.Contains("handshake was rejected", error.Headline);
        Assert.False(server.Snapshot.Connected);
        Assert.Empty(server.Snapshot.Candidates);
    }

    [Fact]
    public async Task Unexpected_process_id_is_rejected()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var server = new CooperativeKeyProbeServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var claimedProcessId = Environment.ProcessId + 1;
        var receive = server.ReceiveAsync(claimedProcessId, timeout.Token);

        await using (var client = new NamedPipeClientStream(
                         ".", server.PipeName, PipeDirection.Out, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(timeout.Token);
            // The frame claims the expected PID, but the kernel reports this test process as the
            // actual pipe client. The OS-backed peer check must reject the spoofed text field.
            var bytes = Encoding.ASCII.GetBytes($"HELLO|1|{server.TokenHex}|{claimedProcessId}\n");
            await client.WriteAsync(bytes, timeout.Token);
            await client.FlushAsync(timeout.Token);
        }

        await Assert.ThrowsAsync<UserFacingException>(async () => await receive);
    }
}

public sealed class CooperativeKeyProbeLaunchTests
{
    [Fact]
    public async Task Launcher_passes_one_time_environment_and_accepts_only_its_child_pid()
    {
        if (!OperatingSystem.IsWindows()) return;

        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "FakeCooperativeProbe.ps1");
        Assert.True(File.Exists(powershell));
        Assert.True(File.Exists(fixture));

        var result = await CooperativeKeyProbe.CaptureAsync(
            powershell,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", fixture],
            TimeSpan.FromSeconds(5));

        var candidate = Assert.Single(result.Candidates);
        Assert.True(result.Connected);
        Assert.False(result.TimedOut);
        Assert.True(result.ProcessId > 0);
        Assert.Equal(AesKeyOrigin.CooperativeProbe, candidate.Origin);
        Assert.Equal("12345678-1234-5678-9abc-def012345678", candidate.ContainerGuid);
    }
}

public sealed class CooperativeKeyProbeCliTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Probe_parser_preserves_repeatable_exact_arguments()
    {
        var executable = _tmp.File("OwnedTestBuild.exe");

        var options = KeyDiscoveryCli.ParseProbe([
            "keys", "probe", "--exe", executable,
            "--arg", "-log", "--arg", "-SomeValue=with spaces",
            "--wait-seconds", "45", "--show-keys", "--json"]);

        Assert.NotNull(options);
        Assert.Equal(executable, options.ExecutablePath);
        Assert.Equal(["-log", "-SomeValue=with spaces"], options.Arguments);
        Assert.Equal(45, options.ListenSeconds);
        Assert.True(options.ShowKeys);
        Assert.True(options.Json);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("601")]
    [InlineData("forever")]
    public void Probe_parser_rejects_invalid_listen_window(string value)
    {
        var executable = _tmp.File("OwnedTestBuild.exe");
        Assert.Throws<UserFacingException>(() => KeyDiscoveryCli.ParseProbe([
            "keys", "probe", "--exe", executable, "--wait-seconds", value]));
    }
}
