using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using CUE4Parse.UE4.Versions;

namespace UEBulkExport;

public sealed record CooperativeKeyProbeResult(
    IReadOnlyList<AesKeyCandidate> Candidates,
    int ProcessId,
    bool Connected,
    bool TimedOut);

/// <summary>
/// Launches an explicitly selected, probe-enabled test build and receives keys that the build
/// voluntarily reports. This class does not open, inspect, debug, or modify the child process.
/// </summary>
public static class CooperativeKeyProbe
{
    public const string EnableArgument = "-UEBEKeyProbe";
    public const string PipeEnvironmentVariable = "UEBE_KEY_PROBE_PIPE";
    public const string TokenEnvironmentVariable = "UEBE_KEY_PROBE_TOKEN";

    public static async Task<CooperativeKeyProbeResult> CaptureAsync(
        string executablePath,
        IReadOnlyList<string>? arguments = null,
        TimeSpan? listenTime = null,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new UserFacingException("The cooperative Unreal key probe currently supports Windows only.");

        var executable = Path.GetFullPath(executablePath.Trim().Trim('"'));
        if (!File.Exists(executable))
            throw new UserFacingException($"Probe-enabled test executable not found: {executable}");
        if (!Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("The cooperative key probe expects a Windows .exe test build.");

        var duration = listenTime ?? TimeSpan.FromSeconds(20);
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromMinutes(10))
            throw new UserFacingException("Probe listen time must be between 1 second and 10 minutes.");

        await using var server = new CooperativeKeyProbeServer();
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false
        };
        foreach (var argument in arguments ?? []) start.ArgumentList.Add(argument);
        // Unreal accepts switches anywhere. Appending ours avoids changing the meaning of a
        // caller's first positional argument while keeping argument boundaries injection-safe.
        start.ArgumentList.Add(EnableArgument);
        start.Environment[PipeEnvironmentVariable] = server.PipeName;
        start.Environment[TokenEnvironmentVariable] = server.TokenHex;

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new UserFacingException("The probe-enabled test build could not be started.", e.Message);
        }

        using (process)
        using (var window = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var receive = server.ReceiveAsync(process.Id, window.Token);
            var exited = process.WaitForExitAsync(ct);
            var timeout = Task.Delay(duration, ct);
            var first = await Task.WhenAny(receive, exited, timeout);

            var timedOut = first == timeout;
            if (first != receive)
            {
                // Give a normally exiting module a moment to flush the final pipe frame.
                if (first == exited) await Task.WhenAny(receive, Task.Delay(250, ct));
                window.Cancel();
            }

            CooperativeProbeCapture capture;
            try { capture = await receive; }
            catch (OperationCanceledException) when (window.IsCancellationRequested)
            {
                capture = server.Snapshot;
            }

            ct.ThrowIfCancellationRequested();
            return new CooperativeKeyProbeResult(capture.Candidates, process.Id, capture.Connected, timedOut);
        }
    }
}

internal sealed record CooperativeProbeCapture(
    IReadOnlyList<AesKeyCandidate> Candidates,
    bool Connected);

/// <summary>One authenticated, bounded named-pipe session. Internal so tests can use a fake UE client.</summary>
internal sealed class CooperativeKeyProbeServer : IAsyncDisposable
{
    private const int MaximumLineBytes = 256;
    private readonly byte[] _token = RandomNumberGenerator.GetBytes(32);
    private readonly NamedPipeServerStream _pipe;
    private readonly List<AesKeyCandidate> _candidates = [];
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private bool _connected;

    public string PipeName { get; } =
        $"UEBulkExport.KeyProbe.{Environment.ProcessId}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(12))}";

    public string TokenHex => Convert.ToHexString(_token);
    public CooperativeProbeCapture Snapshot => new(_candidates.ToList(), _connected);

    public CooperativeKeyProbeServer()
    {
        _pipe = new NamedPipeServerStream(
            PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 4096,
            outBufferSize: 4096);
    }

    public async Task<CooperativeProbeCapture> ReceiveAsync(int expectedProcessId, CancellationToken ct)
    {
        try
        {
            await _pipe.WaitForConnectionAsync(ct);
            ValidateClientProcess(expectedProcessId);
            var hello = await ReadBoundedLineAsync(_pipe, ct);
            ValidateHello(hello, expectedProcessId);
            _connected = true;

            while (!ct.IsCancellationRequested)
            {
                var line = await ReadBoundedLineAsync(_pipe, ct);
                if (line is null || line.Equals("DONE", StringComparison.Ordinal)) break;
                ParseKey(line);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The listening window is intentionally bounded; return whatever was authenticated.
        }
        catch (IOException) when (_connected)
        {
            // A game exit closes the pipe without a DONE frame. Authenticated frames remain valid.
        }

        return Snapshot;
    }

    private void ValidateClientProcess(int expectedProcessId)
    {
        if (!NativeMethods.GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var actualProcessId) ||
            actualProcessId != (uint)expectedProcessId)
            throw new UserFacingException("The cooperative key-probe handshake was rejected.",
                "The named-pipe client was not the test build started for this probe session.");
    }

    private void ValidateHello(string? line, int expectedProcessId)
    {
        var fields = line?.Split('|');
        if (fields is not ["HELLO", "1", _, _] ||
            !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
            pid != expectedProcessId || !TokenMatches(fields[2]))
            throw new UserFacingException("The cooperative key-probe handshake was rejected.",
                "The plugin protocol, process ID, or one-time token did not match this launch.");
    }

    private bool TokenMatches(string presented)
    {
        if (presented.Length != _token.Length * 2) return false;

        byte[] received;
        try { received = Convert.FromHexString(presented); }
        catch (FormatException) { return false; }

        try { return CryptographicOperations.FixedTimeEquals(received, _token); }
        finally { CryptographicOperations.ZeroMemory(received); }
    }

    private void ParseKey(string line)
    {
        var fields = line.Split('|');
        if (fields is not ["KEY", _, _] || !Guid.TryParse(fields[1], out var guid))
            throw new UserFacingException("The cooperative key probe sent an invalid frame.");

        Cli.ValidateAesKey(fields[2]);
        var key = fields[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? fields[2]
            : "0x" + fields[2];
        key = "0x" + key[2..].ToUpperInvariant();

        var identity = guid.ToString("D") + ":" + key;
        if (!_seen.Add(identity)) return;

        _candidates.Add(new AesKeyCandidate(
            key,
            -1,
            AesKeyOrigin.CooperativeProbe,
            "authenticated key-registration event from the explicitly launched Unreal test build",
            100,
            guid == Guid.Empty ? null : guid.ToString("D")));
    }

    private static async Task<string?> ReadBoundedLineAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new byte[MaximumLineBytes];
        var length = 0;
        var one = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(one, ct);
            if (read == 0) return length == 0 ? null : Encoding.ASCII.GetString(bytes, 0, length);
            if (one[0] == (byte)'\n') return Encoding.ASCII.GetString(bytes, 0, length);
            if (one[0] == (byte)'\r') continue;
            if (one[0] > 0x7F || length == bytes.Length)
                throw new UserFacingException("The cooperative key probe exceeded its protocol frame limit.");
            bytes[length++] = one[0];
        }
    }

    public async ValueTask DisposeAsync()
    {
        CryptographicOperations.ZeroMemory(_token);
        await _pipe.DisposeAsync();
    }
}

internal static class NativeMethods
{
#pragma warning disable SYSLIB1054 // This one SafeHandle-only import avoids enabling unsafe code project-wide.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
#pragma warning restore SYSLIB1054
}
