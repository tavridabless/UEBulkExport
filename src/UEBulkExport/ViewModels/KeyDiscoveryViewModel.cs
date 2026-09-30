using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.Services;

namespace UEBulkExport.Gui.ViewModels;

public sealed partial class KeyCandidateViewModel : ObservableObject
{
    public AesKeyCandidate Candidate { get; }
    public AesKeyValidation? Validation { get; }
    [ObservableProperty] private bool _showKey;

    public string DisplayKey => ShowKey ? Candidate.Key : $"sha256:{Candidate.Fingerprint}";
    public string Offset => Candidate.Offset >= 0 ? $"0x{Candidate.Offset:X}" : Loc.Instance["Keys.Result.Runtime"];
    public string Origin => Candidate.Origin.ToString();
    public string Confidence => $"{Candidate.Confidence}%";
    public bool IsVerified => Validation?.Verified == true;
    public string ValidationText => Validation is null
        ? Loc.Instance["Keys.Result.Unchecked"]
        : Validation.Verified
            ? Loc.Instance.Format("Keys.Result.Verified", Validation.ContainersUnlocked, Validation.FilesAdded)
            : Loc.Instance["Keys.Result.Rejected"];

    public KeyCandidateViewModel(AesKeyCandidate candidate, AesKeyValidation? validation, bool showKey)
    {
        Candidate = candidate;
        Validation = validation;
        _showKey = showKey;
    }

    partial void OnShowKeyChanged(bool value) => OnPropertyChanged(nameof(DisplayKey));

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(ValidationText));
        OnPropertyChanged(nameof(Offset));
    }
}

/// <summary>GUI over the same bounded offline scanner used by the CLI.</summary>
public sealed partial class KeyDiscoveryViewModel : ObservableObject
{
    private readonly ExportViewModel _export;
    private CancellationTokenSource? _cancellation;
    private static Loc L => Loc.Instance;

    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private string _probeExecutablePath = "";
    [ObservableProperty] private int _probeListenSeconds = 20;
    [ObservableProperty] private string _paksPath = "";
    [ObservableProperty] private bool _scanMachineCode = true;
    [ObservableProperty] private bool _showKeys;
    [ObservableProperty] private bool _verifiedOnly;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _errorHeadline = "";
    [ObservableProperty] private string _errorHint = "";
    [ObservableProperty] private long _bytesScanned;
    [ObservableProperty] private bool _wasTruncated;
    private int? _lastProbeProcessId;

    public ObservableCollection<KeyCandidateViewModel> Results { get; } = [];
    public bool HasResults => Results.Count > 0;
    public bool HasError => !string.IsNullOrEmpty(ErrorHeadline);
    public bool CanScan => !IsBusy && File.Exists(SourcePath.Trim()) && (!VerifiedOnly || !string.IsNullOrWhiteSpace(PaksPath));
    public bool CanProbe => !IsBusy && File.Exists(ProbeExecutablePath.Trim()) &&
                            (!VerifiedOnly || !string.IsNullOrWhiteSpace(PaksPath));
    public string SummaryText => !HasResults
        ? ""
        : _lastProbeProcessId is { } processId
            ? L.Format("Keys.Probe.Summary", Results.Count, processId)
            : L.Format("Keys.Summary", Results.Count, BytesScanned);

    public event Action? KeyApplied;

    public KeyDiscoveryViewModel(ExportViewModel export)
    {
        _export = export;
        _statusText = L["Status.Idle"];
        Loc.Instance.LanguageChanged += RefreshLanguage;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(SourcePath):
            case nameof(ProbeExecutablePath):
            case nameof(PaksPath):
            case nameof(VerifiedOnly):
            case nameof(IsBusy):
                OnPropertyChanged(nameof(CanScan));
                OnPropertyChanged(nameof(CanProbe));
                ScanCommand.NotifyCanExecuteChanged();
                ProbeCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
                break;
            case nameof(ErrorHeadline):
                OnPropertyChanged(nameof(HasError));
                break;
            case nameof(ShowKeys):
                foreach (var item in Results) item.ShowKey = ShowKeys;
                break;
            case nameof(BytesScanned):
                OnPropertyChanged(nameof(SummaryText));
                break;
        }
    }

    [RelayCommand]
    private async Task BrowseSource()
    {
        var selected = await DialogService.PickKeySourceAsync(SourcePath);
        if (selected is not null) SourcePath = selected;
    }

    [RelayCommand]
    private async Task BrowseProbeExecutable()
    {
        var selected = await DialogService.PickExecutableAsync(ProbeExecutablePath);
        if (selected is not null) ProbeExecutablePath = selected;
    }

    [RelayCommand]
    private async Task BrowsePaksFolder()
    {
        var selected = await DialogService.PickFolderAsync("Dialog.PickPaks", PaksPath);
        if (selected is not null) PaksPath = selected;
    }

    [RelayCommand]
    private async Task BrowsePaksFile()
    {
        var selected = await DialogService.PickContainerAsync(PaksPath);
        if (selected is not null) PaksPath = selected;
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task Scan()
    {
        _cancellation = new CancellationTokenSource();
        IsBusy = true;
        StatusText = L["Keys.Status.Scanning"];
        ErrorHeadline = ErrorHint = "";
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
        BytesScanned = 0;
        WasTruncated = false;
        _lastProbeProcessId = null;

        try
        {
            var source = SourcePath.Trim();
            var paks = string.IsNullOrWhiteSpace(PaksPath) ? null : PaksPath.Trim();
            var machineCode = ScanMachineCode;
            var token = _cancellation.Token;

            var outcome = await Task.Run(async () =>
            {
                var scan = await AesKeyScanner.ScanAsync(source, scanMachineCode: machineCode, ct: token);
                IReadOnlyDictionary<string, AesKeyValidation>? validations = null;
                if (paks is not null && scan.Candidates.Count > 0)
                {
                    var game = DetectGame(paks);
                    validations = await AesKeyValidator.ValidateAsync(paks, game, scan.Candidates, token);
                }
                return (Scan: scan, Validations: validations);
            }, token);

            foreach (var candidate in outcome.Scan.Candidates)
            {
                AesKeyValidation? validation = null;
                if (outcome.Validations is not null && outcome.Validations.TryGetValue(candidate.Key, out var found))
                    validation = found;
                if (VerifiedOnly && validation?.Verified != true) continue;
                Results.Add(new KeyCandidateViewModel(candidate, validation, ShowKeys));
            }

            BytesScanned = outcome.Scan.BytesScanned;
            WasTruncated = outcome.Scan.Truncated;
            StatusText = Results.Count > 0 ? L["Keys.Status.Done"] : L["Keys.Status.Empty"];
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(SummaryText));
        }
        catch (OperationCanceledException)
        {
            StatusText = L["Status.Cancelled"];
        }
        catch (UserFacingException e)
        {
            ErrorHeadline = e.Headline;
            ErrorHint = e.Hint ?? "";
            StatusText = L["Status.Failed"];
        }
        catch (Exception e)
        {
            ErrorHeadline = L["Keys.Error.Unexpected"];
            ErrorHint = e.Message;
            StatusText = L["Status.Failed"];
        }
        finally
        {
            IsBusy = false;
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanProbe))]
    private async Task Probe()
    {
        _cancellation = new CancellationTokenSource();
        IsBusy = true;
        StatusText = L["Keys.Status.Probing"];
        ErrorHeadline = ErrorHint = "";
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
        BytesScanned = 0;
        WasTruncated = false;
        _lastProbeProcessId = null;

        try
        {
            var executable = ProbeExecutablePath.Trim();
            var paks = string.IsNullOrWhiteSpace(PaksPath) ? null : PaksPath.Trim();
            var seconds = Math.Clamp(ProbeListenSeconds, 1, 600);
            var token = _cancellation.Token;

            var outcome = await Task.Run(async () =>
            {
                var probe = await CooperativeKeyProbe.CaptureAsync(
                    executable, listenTime: TimeSpan.FromSeconds(seconds), ct: token);
                IReadOnlyDictionary<string, AesKeyValidation>? validations = null;
                if (paks is not null && probe.Candidates.Count > 0)
                    validations = await AesKeyValidator.ValidateAsync(paks, DetectGame(paks), probe.Candidates, token);
                return (Probe: probe, Validations: validations);
            }, token);

            foreach (var candidate in outcome.Probe.Candidates)
            {
                AesKeyValidation? validation = null;
                if (outcome.Validations is not null && outcome.Validations.TryGetValue(candidate.Key, out var found))
                    validation = found;
                if (VerifiedOnly && validation?.Verified != true) continue;
                Results.Add(new KeyCandidateViewModel(candidate, validation, ShowKeys));
            }

            _lastProbeProcessId = outcome.Probe.ProcessId;
            StatusText = !outcome.Probe.Connected
                ? L["Keys.Status.ProbeNotConnected"]
                : Results.Count > 0 ? L["Keys.Status.Done"] : L["Keys.Status.Empty"];
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(SummaryText));
        }
        catch (OperationCanceledException)
        {
            StatusText = L["Status.Cancelled"];
        }
        catch (UserFacingException e)
        {
            ErrorHeadline = e.Headline;
            ErrorHint = e.Hint ?? "";
            StatusText = L["Status.Failed"];
        }
        catch (Exception e)
        {
            ErrorHeadline = L["Keys.Error.Unexpected"];
            ErrorHint = e.Message;
            StatusText = L["Status.Failed"];
        }
        finally
        {
            IsBusy = false;
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand]
    private void UseKey(KeyCandidateViewModel? item)
    {
        if (item is null) return;
        var value = item.Candidate.ContainerGuid is { } guid ? $"{guid}:{item.Candidate.Key}" : item.Candidate.Key;
        var existing = _export.AesText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!existing.Contains(value, StringComparer.OrdinalIgnoreCase))
            _export.AesText = string.Join(Environment.NewLine, [.. existing, value]);
        KeyApplied?.Invoke();
    }

    private static CUE4Parse.UE4.Versions.EGame DetectGame(string paks)
    {
        try
        {
            var resolved = Discovery.ResolvePaksDirectory(paks);
            var detected = EngineDetection.DetectFromContainers(resolved);
            return detected is null ? CUE4Parse.UE4.Versions.EGame.GAME_UE5_LATEST : Cli.ParseGame(detected);
        }
        catch
        {
            return CUE4Parse.UE4.Versions.EGame.GAME_UE5_LATEST;
        }
    }

    private void RefreshLanguage()
    {
        if (!IsBusy) StatusText = L["Status.Idle"];
        foreach (var item in Results) item.RefreshLanguage();
        OnPropertyChanged(nameof(SummaryText));
    }
}
