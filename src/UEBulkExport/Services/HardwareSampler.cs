using System.Runtime.InteropServices;

namespace UEBulkExport.Gui.Services;

/// <summary>One reading of the whole machine, taken about once a second.</summary>
public sealed record HardwareSample(
    TimeSpan Elapsed,
    double CpuPercent,
    double MemoryUsedBytes,
    double MemoryTotalBytes,
    double DiskReadBytesPerSecond,
    double DiskWriteBytesPerSecond,
    double DiskBusyPercent,
    double? GpuPercent)
{
    public double MemoryPercent => MemoryTotalBytes <= 0 ? 0 : MemoryUsedBytes / MemoryTotalBytes * 100;
}

/// <summary>
/// Reads system-wide hardware load through Windows performance counters (PDH), the same source
/// Task Manager uses. System-wide on purpose: an export also runs retoc, and a migration runs
/// UE Viewer and the Unreal Editor, so the load of this process alone would tell half the story.
/// English counter names keep it working on localised Windows.
/// </summary>
public sealed class HardwareSampler : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const uint PdhMoreData = 0x800007D2;

    private IntPtr _query;
    private IntPtr _cpu;
    private IntPtr _diskRead;
    private IntPtr _diskWrite;
    private IntPtr _diskIdle;
    private IntPtr _gpu;

    /// <summary>Rate counters need two collections; the first reading is only a baseline.</summary>
    private bool _primed;

    public bool HasGpu => _gpu != IntPtr.Zero;

    private HardwareSampler() { }

    /// <summary>Null where performance counters are unavailable (not Windows, or disabled by policy).</summary>
    public static HardwareSampler? TryCreate()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var sampler = new HardwareSampler();
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out sampler._query) != 0) return null;

            // "% Processor Utility" matches Task Manager; older systems only have "% Processor Time".
            sampler._cpu = sampler.Add(@"\Processor Information(_Total)\% Processor Utility")
                           ?? sampler.Add(@"\Processor(_Total)\% Processor Time")
                           ?? IntPtr.Zero;
            sampler._diskRead = sampler.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec") ?? IntPtr.Zero;
            sampler._diskWrite = sampler.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec") ?? IntPtr.Zero;
            sampler._diskIdle = sampler.Add(@"\PhysicalDisk(_Total)\% Idle Time") ?? IntPtr.Zero;
            sampler._gpu = sampler.Add(@"\GPU Engine(*)\Utilization Percentage") ?? IntPtr.Zero;

            if (sampler._cpu == IntPtr.Zero && sampler._diskRead == IntPtr.Zero)
            {
                sampler.Dispose();
                return null;
            }

            PdhCollectQueryData(sampler._query);
            return sampler;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            sampler.Dispose();
            return null;
        }
    }

    private IntPtr? Add(string path) =>
        PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var counter) == 0 ? counter : null;

    /// <summary>Takes a reading; null for the very first call, which only sets the baseline.</summary>
    public HardwareSample? Sample(TimeSpan elapsed)
    {
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return null;
        if (!_primed)
        {
            _primed = true;
            return null;
        }

        var (used, total) = Memory();
        var idle = Value(_diskIdle);
        return new HardwareSample(
            elapsed,
            Math.Clamp(Value(_cpu) ?? 0, 0, 100),
            used,
            total,
            Math.Max(0, Value(_diskRead) ?? 0),
            Math.Max(0, Value(_diskWrite) ?? 0),
            idle is { } i ? Math.Clamp(100 - i, 0, 100) : 0,
            HasGpu ? Gpu() : null);
    }

    private static double? Value(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        return PdhGetFormattedCounterValue(counter, PdhFmtDouble | PdhFmtNoCap100, out _, out var value) == 0 &&
               value.Status == 0
            ? value.Double
            : null;
    }

    /// <summary>
    /// Task Manager's GPU figure: every process's share of one engine is added up, and the busiest
    /// engine of the busiest adapter is the answer.
    /// </summary>
    private double? Gpu()
    {
        uint size = 0;
        var status = PdhGetFormattedCounterArray(_gpu, PdhFmtDouble | PdhFmtNoCap100, ref size, out _, IntPtr.Zero);
        if (status != PdhMoreData || size == 0) return 0;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_gpu, PdhFmtDouble | PdhFmtNoCap100, ref size, out var count, buffer) != 0)
                return null;

            var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var itemSize = Marshal.SizeOf<CounterValueItem>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterValueItem>(buffer + i * itemSize);
                if (item.Value.Status != 0) continue;
                var name = Marshal.PtrToStringUni(item.Name) ?? "";
                // "pid_1234_luid_0x0_0x1_phys_0_eng_3_engtype_3D": drop the process to group by engine.
                var engine = name.Contains("_luid_", StringComparison.Ordinal)
                    ? name[name.IndexOf("_luid_", StringComparison.Ordinal)..]
                    : name;
                engines[engine] = engines.GetValueOrDefault(engine) + item.Value.Double;
            }

            return engines.Count == 0 ? 0 : Math.Clamp(engines.Values.Max(), 0, 100);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (double Used, double Total) Memory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status)
            ? (status.TotalPhysical - status.AvailablePhysical, status.TotalPhysical)
            : (0, 0);
    }

    public void Dispose()
    {
        if (_query == IntPtr.Zero) return;
        PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    // ---------------------------------------------------------------- interop

    [StructLayout(LayoutKind.Explicit)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Double;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValueItem
    {
        public IntPtr Name;
        public CounterValue Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
