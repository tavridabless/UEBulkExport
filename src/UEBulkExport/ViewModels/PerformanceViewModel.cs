using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.Services;

namespace UEBulkExport.Gui.ViewModels;

/// <summary>What held a run back, judged from the last few seconds or from the whole run.</summary>
public enum Bottleneck { None, Disk, Cpu, Memory }

/// <summary>
/// Watches the machine while an export or a migration runs: a reading a second, the whole run
/// kept for the charts, and the figures the tiles show. The history is thinned as it grows, so
/// the charts always show the run from its first second to now.
/// </summary>
public sealed partial class PerformanceViewModel : ObservableObject, IDisposable
{
    /// <summary>More points than a chart is pixels wide buys nothing; two readings merge into one past this.</summary>
    private const int MaxPoints = 720;
    private const double BusyThreshold = 90;
    private const double MemoryThreshold = 92;

    private static Loc L => Loc.Instance;

    private readonly List<HardwareSample> _samples = [];
    private readonly Queue<HardwareSample> _recent = new();
    private readonly Dictionary<Bottleneck, int> _bottleneckSeconds = [];
    private readonly object _gate = new();
    private readonly Stopwatch _clock = new();
    private HardwareSampler? _sampler;
    private Timer? _timer;
    private HardwareSample? _last;
    private int _rawCount;
    private double _totalRead, _totalWrite, _peakCpu, _peakGpu, _peakRead, _peakWrite;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isSupported = OperatingSystem.IsWindows();

    public IReadOnlyList<HardwareSample> Samples => _samples;

    /// <summary>Raised on the UI thread after every reading; the charts redraw on it.</summary>
    public event Action? SamplesChanged;

    public bool HasData => _samples.Count > 0;
    public bool IsVisible => IsSupported && (IsActive || HasData);
    public bool IsWaiting => IsActive && !HasData;
    public bool HasGpu => _last?.GpuPercent is not null;
    public int TileColumns => HasGpu ? 4 : 3;

    // ---------------------------------------------------------------- tiles

    public string CpuValue => _last is { } s ? Percent(s.CpuPercent) : "—";
    public string CpuDetail => L.Format("Perf.Peak", Percent(_peakCpu));
    public double CpuPercent => _last?.CpuPercent ?? 0;
    public bool CpuWarning => CpuPercent is >= 70 and < BusyThreshold;
    public bool CpuCritical => CpuPercent >= BusyThreshold;

    public string MemoryValue => _last is { } s ? Gigabytes(s.MemoryUsedBytes) : "—";
    public string MemoryDetail => _last is { } s
        ? L.Format("Perf.MemoryDetail", Gigabytes(s.MemoryTotalBytes), Percent(s.MemoryPercent))
        : "";
    public double MemoryPercent => _last?.MemoryPercent ?? 0;
    public bool MemoryWarning => MemoryPercent is >= 80 and < MemoryThreshold;
    public bool MemoryCritical => MemoryPercent >= MemoryThreshold;

    // The disk tile shows how busy the disk is, like the other tiles show load; the two
    // directions, their totals and peaks belong to the disk chart and its legend.
    public string DiskValue => _last is { } s ? Percent(s.DiskBusyPercent) : "—";
    public string DiskDetail => _last is { } s
        ? L.Format("Perf.DiskDetail", Rate(s.DiskReadBytesPerSecond), Rate(s.DiskWriteBytesPerSecond))
        : "";
    public double DiskBusyPercent => _last?.DiskBusyPercent ?? 0;
    public bool DiskWarning => DiskBusyPercent is >= 70 and < BusyThreshold;
    public bool DiskCritical => DiskBusyPercent >= BusyThreshold;
    public string ReadLegend => L.Format("Perf.ReadLegend", Gigabytes(_totalRead));
    public string ReadLegendDetail => L.Format("Perf.TotalPeak", Gigabytes(_totalRead), Rate(_peakRead));
    public string WriteLegend => L.Format("Perf.WriteLegend", Gigabytes(_totalWrite));
    public string WriteLegendDetail => L.Format("Perf.TotalPeak", Gigabytes(_totalWrite), Rate(_peakWrite));

    public string GpuValue => _last?.GpuPercent is { } g ? Percent(g) : "—";
    public string GpuDetail => L.Format("Perf.Peak", Percent(_peakGpu));
    public double GpuPercent => _last?.GpuPercent ?? 0;
    public bool GpuWarning => GpuPercent is >= 70 and < BusyThreshold;
    public bool GpuCritical => GpuPercent >= BusyThreshold;

    // ---------------------------------------------------------------- header

    public string StatusText => L.Format(IsActive ? "Perf.Live" : "Perf.Done",
        Converters.Format.Duration(_last?.Elapsed ?? _clock.Elapsed));

    public Bottleneck CurrentBottleneck { get; private set; }

    public string BottleneckText
    {
        get
        {
            if (IsActive)
                return CurrentBottleneck switch
                {
                    Bottleneck.Disk => L.Format("Perf.Busy.Disk", Percent(Average(s => s.DiskBusyPercent))),
                    Bottleneck.Cpu => L.Format("Perf.Busy.Cpu", Percent(Average(s => s.CpuPercent))),
                    Bottleneck.Memory => L.Format("Perf.Busy.Memory", Percent(Average(s => s.MemoryPercent))),
                    _ => L["Perf.Busy.None"]
                };

            // After the run: what held it back most of the time, if anything did for long.
            var (kind, share) = DominantBottleneck();
            return kind switch
            {
                Bottleneck.Disk => L.Format("Perf.Run.Disk", Percent(share * 100)),
                Bottleneck.Cpu => L.Format("Perf.Run.Cpu", Percent(share * 100)),
                Bottleneck.Memory => L.Format("Perf.Run.Memory", Percent(share * 100)),
                _ => L["Perf.Busy.None"]
            };
        }
    }

    public bool HasBottleneck => IsActive ? CurrentBottleneck != Bottleneck.None : DominantBottleneck().Kind != Bottleneck.None;

    public PerformanceViewModel()
    {
        Loc.Instance.LanguageChanged += NotifyAll;
    }

    // ---------------------------------------------------------------- recording

    /// <summary>Starts a fresh recording; the previous run's history is cleared.</summary>
    public void Start()
    {
        Stop();
        lock (_gate)
        {
            _sampler = HardwareSampler.TryCreate();
            IsSupported = _sampler is not null;
            if (_sampler is null) return;

            _samples.Clear();
            _recent.Clear();
            _bottleneckSeconds.Clear();
            _last = null;
            _rawCount = 0;
            _totalRead = _totalWrite = _peakCpu = _peakGpu = _peakRead = _peakWrite = 0;
            CurrentBottleneck = Bottleneck.None;
            _clock.Restart();
            IsActive = true;
            _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        NotifyAll();
        SamplesChanged?.Invoke();
    }

    /// <summary>Stops recording and keeps the history for the charts.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _sampler?.Dispose();
            _sampler = null;
            _clock.Stop();
        }

        if (!IsActive) return;
        IsActive = false;
        NotifyAll();
    }

    private void Tick()
    {
        HardwareSample? sample;
        lock (_gate)
        {
            // The timer can fire once more while Stop is disposing the sampler.
            if (_sampler is null) return;
            sample = _sampler.Sample(_clock.Elapsed);
        }

        if (sample is not null) Dispatcher.UIThread.Post(() => Append(sample));
    }

    private void Append(HardwareSample sample)
    {
        if (!IsActive) return;

        // Rates times the time since the previous reading: what went through the disk in total.
        var seconds = (sample.Elapsed - (_last?.Elapsed ?? TimeSpan.Zero)).TotalSeconds;
        _totalRead += sample.DiskReadBytesPerSecond * seconds;
        _totalWrite += sample.DiskWriteBytesPerSecond * seconds;
        _peakCpu = Math.Max(_peakCpu, sample.CpuPercent);
        _peakGpu = Math.Max(_peakGpu, sample.GpuPercent ?? 0);
        _peakRead = Math.Max(_peakRead, sample.DiskReadBytesPerSecond);
        _peakWrite = Math.Max(_peakWrite, sample.DiskWriteBytesPerSecond);
        _last = sample;
        _rawCount++;

        _recent.Enqueue(sample);
        while (_recent.Count > 5) _recent.Dequeue();
        CurrentBottleneck = Judge();
        _bottleneckSeconds[CurrentBottleneck] = _bottleneckSeconds.GetValueOrDefault(CurrentBottleneck) + 1;

        _samples.Add(sample);
        if (_samples.Count > MaxPoints) Thin();

        NotifyAll();
        SamplesChanged?.Invoke();
    }

    /// <summary>Halves the resolution of the history by averaging neighbours.</summary>
    private void Thin()
    {
        var thinned = new List<HardwareSample>(_samples.Count / 2 + 1);
        for (var i = 0; i + 1 < _samples.Count; i += 2)
        {
            var (a, b) = (_samples[i], _samples[i + 1]);
            thinned.Add(new HardwareSample(
                b.Elapsed,
                (a.CpuPercent + b.CpuPercent) / 2,
                (a.MemoryUsedBytes + b.MemoryUsedBytes) / 2,
                b.MemoryTotalBytes,
                (a.DiskReadBytesPerSecond + b.DiskReadBytesPerSecond) / 2,
                (a.DiskWriteBytesPerSecond + b.DiskWriteBytesPerSecond) / 2,
                (a.DiskBusyPercent + b.DiskBusyPercent) / 2,
                a.GpuPercent is { } ga && b.GpuPercent is { } gb ? (ga + gb) / 2 : b.GpuPercent));
        }

        if (_samples.Count % 2 == 1) thinned.Add(_samples[^1]);
        _samples.Clear();
        _samples.AddRange(thinned);
    }

    /// <summary>A resource counts as the bottleneck when it stays saturated for several seconds.</summary>
    private Bottleneck Judge()
    {
        if (_recent.Count < 3) return Bottleneck.None;
        if (Average(s => s.MemoryPercent) >= MemoryThreshold) return Bottleneck.Memory;
        if (Average(s => s.DiskBusyPercent) >= BusyThreshold) return Bottleneck.Disk;
        if (Average(s => s.CpuPercent) >= BusyThreshold) return Bottleneck.Cpu;
        return Bottleneck.None;
    }

    private double Average(Func<HardwareSample, double> value) => _recent.Count == 0 ? 0 : _recent.Average(value);

    private (Bottleneck Kind, double Share) DominantBottleneck()
    {
        if (_rawCount == 0) return (Bottleneck.None, 0);
        var (kind, seconds) = _bottleneckSeconds
            .Where(p => p.Key != Bottleneck.None)
            .OrderByDescending(p => p.Value)
            .Select(p => (p.Key, p.Value))
            .FirstOrDefault();
        var share = (double)seconds / _rawCount;
        // A bottleneck that held for less than a fifth of the run is not worth a headline.
        return share >= 0.2 ? (kind, share) : (Bottleneck.None, 0);
    }

    private void NotifyAll() => OnPropertyChanged(string.Empty);

    // ---------------------------------------------------------------- formatting

    internal static string Percent(double value) => L.Format("Perf.Percent", Math.Round(value));

    internal static string Rate(double bytesPerSecond)
    {
        var megabytes = bytesPerSecond / 1_000_000;
        return L.Format(megabytes < 10 ? "Perf.RateSmall" : "Perf.Rate", megabytes);
    }

    internal static string Gigabytes(double bytes) => L.Format("Perf.Gigabytes", bytes / 1_000_000_000);

    public void Dispose()
    {
        Stop();
        Loc.Instance.LanguageChanged -= NotifyAll;
    }
}
