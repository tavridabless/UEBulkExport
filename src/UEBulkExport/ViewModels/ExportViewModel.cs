using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Options;
using UEBulkExport.Gui.Converters;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.Services;

namespace UEBulkExport.Gui.ViewModels;

public enum RunState { Idle, Scanning, Preparing, Running, Cancelling, Done, DoneWithErrors, Failed, Cancelled }

/// <summary>Where the engine version on the form came from.</summary>
public enum EngineSource { None, Detecting, Detected, Unsupported, Manual, NotDetected }

/// <summary>What the automatic look at the source has found so far.</summary>
public enum ScanPhase { None, Missing, Pending, Ready, Failed }

public sealed record GameOption(EGame Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An export mode, named after what the user gets rather than how it is produced.</summary>
public sealed record ModeOption(ExportMode Mode, string Key)
{
    public string Title => Loc.Instance[$"Mode.{Key}.Title"];
    public string Description => Loc.Instance[$"Mode.{Key}.Desc"];
    public string Details => Loc.Instance[$"Mode.{Key}.Details"];
    public string CliName => "--mode " + Mode.ToString().ToLowerInvariant();
}

public sealed record EnumOption<T>(T Value, string Label) where T : struct, Enum
{
    public override string ToString() => Label;
}

public sealed record SummaryLine(string Label, string Value);

public sealed record EngineVersionOption(string Version)
{
    public override string ToString() => $"Unreal Engine {Version}";
}

/// <summary>
/// The Export page. Only the decisions a user must make are on the surface: the source, the
/// result, the destination. The engine version, encryption and mappings are worked out from the
/// source, and everything else waits in collapsed sections that say what state they are in.
/// </summary>
public sealed partial class ExportViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ExportService _service = new();
    private readonly DispatcherTimer _scanTimer = new();
    private static Loc L => Loc.Instance;

    // ---------------------------------------------------------------- source

    [ObservableProperty] private string _paksPath = "";
    [ObservableProperty] private GameOption _selectedGame;
    [ObservableProperty] private string _aesText = "";

    [ObservableProperty] private ScanPhase _scanPhase;
    [ObservableProperty] private string _scanError = "";
    [ObservableProperty] private int _containerCount;
    [ObservableProperty] private int _entryCount;
    [ObservableProperty] private int _lockedCount;

    [ObservableProperty] private EngineSource _engineSource;
    [ObservableProperty] private string _detectedEngine = "";
    [ObservableProperty] private bool _engineEditorOpen;
    [ObservableProperty] private bool _aesRequested;

    /// <summary>Bumped by every change that makes a running scan's answer stale.</summary>
    private int _scanGeneration;
    private bool _scanQueued;
    /// <summary>The container folder the engine version was last detected for.</summary>
    private string? _detectedFor;
    /// <summary>The source the user picked the engine version for by hand; detection leaves it alone.</summary>
    private string? _gameChosenFor;
    private bool _settingGame;
    private string? _autoMappings;
    private bool _mappingsAmbiguous;
    private bool _hasIoStore;

    public IReadOnlyList<GameOption> Games { get; } = BuildGames();

    // ---------------------------------------------------------------- destination

    [ObservableProperty] private string _outputPath = "";
    [ObservableProperty] private ModeOption _selectedMode;
    [ObservableProperty] private string _usmapPath = "";

    public IReadOnlyList<ModeOption> Modes { get; } =
    [
        new(ExportMode.Legacy, "Legacy"),
        new(ExportMode.Full, "Full"),
        new(ExportMode.Json, "Json"),
        new(ExportMode.Raw, "Raw")
    ];

    public int MaxThreads => Math.Max(2, Environment.ProcessorCount * 2);

    public bool NeedsMappings => SelectedMode.Mode is ExportMode.Full or ExportMode.Json;
    public bool IsFullMode => SelectedMode.Mode == ExportMode.Full;
    public bool IsLegacyMode => SelectedMode.Mode == ExportMode.Legacy;

    // ---------------------------------------------------------------- options

    [ObservableProperty] private int _threads;
    [ObservableProperty] private string _includeRegex = "";
    [ObservableProperty] private string _excludeRegex = "";
    [ObservableProperty] private bool _skipWorlds;
    [ObservableProperty] private bool _exportMaterials;
    [ObservableProperty] private bool _rawPackages;
    [ObservableProperty] private bool _skipJson;
    [ObservableProperty] private bool _skipAssets;
    [ObservableProperty] private bool _skipRawMisc;
    [ObservableProperty] private bool _skipAudioConvert;
    [ObservableProperty] private bool _allMips;
    [ObservableProperty] private bool _skipMorphs;
    [ObservableProperty] private bool _overwrite;
    [ObservableProperty] private bool _verbose;

    [ObservableProperty] private EnumOption<EMeshFormat> _meshFormat;
    [ObservableProperty] private EnumOption<EMeshFormat> _animFormat;
    [ObservableProperty] private EnumOption<ETextureFormat> _textureFormat;
    [ObservableProperty] private EnumOption<EMeshQuality> _meshQuality;
    [ObservableProperty] private EnumOption<ENaniteMeshFormat> _naniteFormat;
    [ObservableProperty] private EnumOption<ESocketFormat> _socketFormat;
    [ObservableProperty] private EnumOption<ETexturePlatform> _platform;

    public IReadOnlyList<EnumOption<EMeshFormat>> MeshFormats { get; } =
    [
        new(EMeshFormat.Gltf2, "glTF 2.0 (.glb)"),
        new(EMeshFormat.ActorX, "ActorX (.psk / .pskx)"),
        new(EMeshFormat.UEFormat, "UEFormat (.uemodel)"),
        new(EMeshFormat.USD, "USD (.usda)")
    ];

    public IReadOnlyList<EnumOption<EMeshFormat>> AnimFormats { get; } =
    [
        new(EMeshFormat.ActorX, "ActorX (.psa / .pskx)"),
        new(EMeshFormat.UEFormat, "UEFormat (.ueanim)"),
        new(EMeshFormat.USD, "USD (.usda)")
    ];

    public IReadOnlyList<EnumOption<ETextureFormat>> TextureFormats { get; } =
    [
        new(ETextureFormat.Png, "PNG"),
        new(ETextureFormat.Tga, "TGA"),
        new(ETextureFormat.Jpeg, "JPEG"),
        new(ETextureFormat.Webp, "WebP")
    ];

    public IReadOnlyList<EnumOption<EMeshQuality>> MeshQualities { get; } =
    [
        new(EMeshQuality.Highest, "Highest LOD only"),
        new(EMeshQuality.Lowest, "Lowest LOD only"),
        new(EMeshQuality.All, "All LODs")
    ];

    public IReadOnlyList<EnumOption<ENaniteMeshFormat>> NaniteFormats { get; } =
    [
        new(ENaniteMeshFormat.NoNanite, "Skip Nanite meshes"),
        new(ENaniteMeshFormat.NaniteOnly, "Nanite only"),
        new(ENaniteMeshFormat.NaniteFirst, "Nanite first"),
        new(ENaniteMeshFormat.NaniteLast, "Nanite last")
    ];

    public IReadOnlyList<EnumOption<ESocketFormat>> SocketFormats { get; } =
    [
        new(ESocketFormat.Bone, "As bones"),
        new(ESocketFormat.Socket, "As sockets"),
        new(ESocketFormat.None, "None")
    ];

    public IReadOnlyList<EnumOption<ETexturePlatform>> Platforms { get; } =
    [
        new(ETexturePlatform.DesktopMobile, "Desktop / Mobile"),
        new(ETexturePlatform.XboxAndPlaystation4, "Xbox / PlayStation 4"),
        new(ETexturePlatform.Playstation5, "PlayStation 5"),
        new(ETexturePlatform.NintendoSwitch, "Nintendo Switch")
    ];

    // ---------------------------------------------------------------- tools

    [ObservableProperty] private string _retocPath = "";
    [ObservableProperty] private string _oodlePath = "";
    [ObservableProperty] private string _zlibPath = "";
    [ObservableProperty] private string _vgmStreamPath = "";

    // ---------------------------------------------------------------- run state

    [ObservableProperty] private RunState _state = RunState.Idle;
    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private string _progressProcessed = "";
    [ObservableProperty] private string _progressWritten = "";
    [ObservableProperty] private string _progressFailed = "";
    [ObservableProperty] private string _progressRate = "";
    [ObservableProperty] private string _progressEta = "";
    [ObservableProperty] private string _progressElapsed = "";
    [ObservableProperty] private string _errorHeadline = "";
    [ObservableProperty] private string _errorHint = "";
    [ObservableProperty] private string _summaryTitle = "";
    [ObservableProperty] private string _commandLine = "";
    [ObservableProperty] private string _lastOutputDirectory = "";

    /// <summary>Paths ticked in the Browser, or null to export the whole container.</summary>
    [ObservableProperty] private IReadOnlySet<string>? _selectedPaths;

    public bool HasSelection => SelectedPaths is not null;
    public string SelectionText => L.Format("Export.Selection.Text", SelectedPaths?.Count ?? 0);

    public ObservableCollection<SummaryLine> Summary { get; } = [];
    public ObservableCollection<RecentGame> Recent { get; } = [];

    public bool IsBusy => State is RunState.Scanning or RunState.Preparing or RunState.Running or RunState.Cancelling;
    /// <summary>A scan runs by itself and is quick; only a real run shows progress and blocks the form.</summary>
    public bool IsRunning => State is RunState.Preparing or RunState.Running or RunState.Cancelling;
    public bool CanRun => !IsBusy;
    public bool CanStart => !IsBusy && ReadinessState.Level != Readiness.Blocked;
    public bool CanCancel => State is RunState.Running or RunState.Preparing;
    public bool HasError => !string.IsNullOrEmpty(ErrorHeadline);
    public bool HasSummary => Summary.Count > 0;
    public bool ShowProgress => IsRunning;
    public bool ShowReadiness => !IsRunning;
    public bool HasRecent => Recent.Count > 0;
    public bool CanOpenOutput => !string.IsNullOrEmpty(LastOutputDirectory) && Directory.Exists(LastOutputDirectory);
    public bool HasErrorsFile => CanOpenOutput && File.Exists(Path.Combine(LastOutputDirectory, "errors.csv"));
    public bool CanOpenLog => File.Exists(LogPath);
    private string LogPath => Path.Combine(LastOutputDirectory, "UEBulkExport.log");

    public string StatusText => State switch
    {
        RunState.Scanning => L["Status.Scanning"],
        RunState.Preparing => L["Status.Preparing"],
        RunState.Running => L["Status.Running"],
        RunState.Cancelling => L["Status.Cancelling"],
        RunState.Done => L["Status.Done"],
        RunState.DoneWithErrors => L["Status.DoneWithErrors"],
        RunState.Failed => L["Status.Failed"],
        RunState.Cancelled => L["Status.Cancelled"],
        _ => L["Status.Idle"]
    };

    /// <summary>Raised after a successful scan; the shell hands the result to the Browser tab.</summary>
    public event Action<ScanResult>? Scanned;

    /// <summary>Set by the view: copies text to the clipboard.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    public ExportViewModel(AppSettings settings)
    {
        _settings = settings;

        _selectedGame = Games.FirstOrDefault(g => g.Value == EGame.GAME_UE5_3) ?? Games[0];
        _selectedMode = Modes[0];
        _meshFormat = MeshFormats[0];
        _animFormat = AnimFormats[0];
        _textureFormat = TextureFormats[0];
        _meshQuality = MeshQualities[0];
        _naniteFormat = NaniteFormats[0];
        _socketFormat = SocketFormats[0];
        _platform = Platforms[0];
        _threads = Math.Max(1, settings.DefaultThreads);

        _scanTimer.Tick += (_, _) =>
        {
            _scanTimer.Stop();
            _ = AutoScanAsync();
        };

        LoadFromSettings();

        _service.ProgressChanged += p => Dispatcher.UIThread.Post(() => ApplyProgress(p));
        Loc.Instance.LanguageChanged += () =>
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(Modes));
            RefreshSourceStatus();
            RefreshSummaries();
        };

        RefreshCommandLine();
    }

    public void LoadFromSettings()
    {
        if (_settings.RememberPaths)
        {
            if (Enum.TryParse<EGame>(_settings.LastGame, out var game))
                SetGame(Games.FirstOrDefault(g => g.Value == game));
            OutputPath = _settings.LastOutputPath;
            PaksPath = _settings.LastPaksPath;
        }

        Threads = Math.Max(1, _settings.DefaultThreads);
        RetocPath = _settings.RetocPath;
        OodlePath = _settings.OodlePath;
        ZlibPath = _settings.ZlibPath;
        VgmStreamPath = _settings.VgmStreamPath;

        Recent.Clear();
        foreach (var r in _settings.Recent) Recent.Add(r);
        OnPropertyChanged(nameof(HasRecent));
    }

    // ---------------------------------------------------------------- change tracking

    private static readonly HashSet<string> OptionProperties =
    [
        nameof(Threads), nameof(IncludeRegex), nameof(ExcludeRegex), nameof(SkipWorlds), nameof(ExportMaterials),
        nameof(RawPackages), nameof(SkipJson), nameof(SkipAssets), nameof(SkipRawMisc), nameof(SkipAudioConvert),
        nameof(AllMips), nameof(SkipMorphs), nameof(Overwrite), nameof(Verbose), nameof(MeshFormat),
        nameof(AnimFormat), nameof(TextureFormat), nameof(MeshQuality), nameof(NaniteFormat), nameof(SocketFormat),
        nameof(Platform)
    ];

    private static readonly HashSet<string> ToolProperties =
        [nameof(RetocPath), nameof(OodlePath), nameof(ZlibPath), nameof(VgmStreamPath)];

    /// <summary>Changes that only report on the form and never alter the command it describes.</summary>
    private static readonly HashSet<string> PresentationProperties =
    [
        nameof(CommandLine), nameof(State), nameof(ScanPhase), nameof(ScanError), nameof(ContainerCount),
        nameof(EntryCount), nameof(LockedCount), nameof(EngineSource), nameof(DetectedEngine),
        nameof(EngineEditorOpen), nameof(AesRequested), nameof(ProgressFraction), nameof(ProgressIndeterminate),
        nameof(ProgressProcessed), nameof(ProgressWritten), nameof(ProgressFailed), nameof(ProgressRate),
        nameof(ProgressEta), nameof(ProgressElapsed), nameof(ErrorHeadline), nameof(ErrorHint),
        nameof(SummaryTitle), nameof(LastOutputDirectory)
    ];

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        var name = e.PropertyName ?? "";

        switch (name)
        {
            case nameof(State):
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(CanRun));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(ShowProgress));
                OnPropertyChanged(nameof(ShowReadiness));
                OnPropertyChanged(nameof(StatusText));
                RefreshReadiness();
                CancelCommand.NotifyCanExecuteChanged();
                RescanCommand.NotifyCanExecuteChanged();
                break;

            case nameof(PaksPath):
            case nameof(OutputPath):
            case nameof(UsmapPath):
            case nameof(ScanPhase):
            case nameof(ScanError):
            case nameof(LockedCount):
            case nameof(ContainerCount):
            case nameof(EntryCount):
            case nameof(SelectedGame):
            case nameof(AesText):
            case nameof(AesRequested):
            case nameof(EngineSource):
            case nameof(DetectedEngine):
            case nameof(EngineEditorOpen):
            case nameof(SelectedPaths):
                RefreshSourceStatus();
                RefreshReadiness();
                break;

            case nameof(SelectedMode):
                OnPropertyChanged(nameof(NeedsMappings));
                OnPropertyChanged(nameof(IsFullMode));
                OnPropertyChanged(nameof(IsLegacyMode));
                RefreshSourceStatus();
                RefreshReadiness();
                break;

            case nameof(ErrorHeadline):
                OnPropertyChanged(nameof(HasError));
                break;

            case nameof(LastOutputDirectory):
                OnPropertyChanged(nameof(CanOpenOutput));
                OnPropertyChanged(nameof(HasErrorsFile));
                OnPropertyChanged(nameof(CanOpenLog));
                break;
        }

        if (name == nameof(SelectedPaths))
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectionText));
        }

        if (OptionProperties.Contains(name) || ToolProperties.Contains(name)) RefreshSummaries();
        if (name == nameof(Overwrite)) RefreshReadiness();
        if (!PresentationProperties.Contains(name)) RefreshCommandLine();
    }

    // ---------------------------------------------------------------- automatic source check

    private bool SourceExists
    {
        get
        {
            var path = PaksPath.Trim().Trim('"');
            return path.Length > 0 && (Directory.Exists(path) || File.Exists(path));
        }
    }

    partial void OnPaksPathChanged(string value)
    {
        _detectedFor = null;
        _autoMappings = null;
        _mappingsAmbiguous = false;
        _hasIoStore = false;
        ContainerCount = EntryCount = LockedCount = 0;
        ScanError = "";
        AesRequested = false;
        EngineEditorOpen = false;
        if (!string.Equals(_gameChosenFor, value.Trim(), StringComparison.OrdinalIgnoreCase)) _gameChosenFor = null;
        EngineSource = _gameChosenFor is not null ? EngineSource.Manual : EngineSource.None;
        ScheduleScan(TimeSpan.FromMilliseconds(500));
    }

    partial void OnSelectedGameChanged(GameOption value)
    {
        if (_settingGame) return;

        // The user chose the version: it stands for this source until the source changes.
        _gameChosenFor = PaksPath.Trim();
        if (EngineSource != EngineSource.None) EngineSource = EngineSource.Manual;
        ScheduleScan(TimeSpan.FromMilliseconds(300));
    }

    partial void OnAesTextChanged(string value) => ScheduleScan(TimeSpan.FromMilliseconds(1200));

    private void SetGame(GameOption? game)
    {
        if (game is null) return;
        _settingGame = true;
        try { SelectedGame = game; }
        finally { _settingGame = false; }
    }

    private void ScheduleScan(TimeSpan delay)
    {
        _scanGeneration++;
        _scanTimer.Stop();

        if (string.IsNullOrWhiteSpace(PaksPath))
        {
            ScanPhase = ScanPhase.None;
            return;
        }

        if (!SourceExists)
        {
            ScanPhase = ScanPhase.Missing;
            return;
        }

        ScanPhase = ScanPhase.Pending;
        _scanTimer.Interval = delay;
        _scanTimer.Start();
    }

    /// <summary>
    /// Resolves the container folder, reads the engine version from the game, looks for mappings and
    /// mounts the containers, so the user sees what they pointed at before deciding anything.
    /// </summary>
    private async Task AutoScanAsync()
    {
        if (!SourceExists) return;
        if (IsBusy)
        {
            _scanQueued = true;
            return;
        }

        var generation = _scanGeneration;
        var paks = PaksPath.Trim().Trim('"');
        var output = NullIfEmpty(OutputPath);
        ScanPhase = ScanPhase.Pending;

        string resolved;
        try
        {
            resolved = await Task.Run(() => Discovery.ResolvePaksDirectory(paks));
        }
        catch (UserFacingException e)
        {
            if (generation == _scanGeneration) FailScan(e.Headline);
            return;
        }

        if (generation != _scanGeneration) return;

        if (!string.Equals(_detectedFor, resolved, StringComparison.OrdinalIgnoreCase))
        {
            _detectedFor = resolved;
            if (_gameChosenFor is null)
            {
                EngineSource = EngineSource.Detecting;
                var version = await Task.Run(() => EngineDetection.DetectFromContainers(resolved));
                if (generation != _scanGeneration) return;
                ApplyDetectedEngine(version);
            }
        }

        try
        {
            _hasIoStore = await Task.Run(() => Discovery.HasIoStoreContainers(resolved));
        }
        catch (UserFacingException)
        {
            _hasIoStore = false;
        }

        try
        {
            _autoMappings = await Task.Run(() => Discovery.FindMappings(resolved, output));
            _mappingsAmbiguous = false;
        }
        catch (UserFacingException)
        {
            _autoMappings = null;
            _mappingsAmbiguous = true;
        }

        if (generation != _scanGeneration) return;
        if (IsBusy)
        {
            _scanQueued = true;
            return;
        }

        var previous = State;
        State = RunState.Scanning;
        try
        {
            var result = await _service.ScanAsync(ToOptions());
            if (generation == _scanGeneration)
            {
                ContainerCount = result.Containers.Count(c => !c.IsLocked);
                LockedCount = result.Containers.Count(c => c.IsLocked);
                EntryCount = result.Files.Count;
                ScanError = "";
                ScanPhase = ScanPhase.Ready;
                Scanned?.Invoke(result);
            }
        }
        catch (Exception e)
        {
            if (generation == _scanGeneration)
            {
                FailScan(e is UserFacingException u ? u.Headline : e.Message);
                if (e is not UserFacingException) Log.Error(e.ToString());
            }
        }
        finally
        {
            // A finished run keeps its outcome on the status line; a scan is not news.
            State = previous is RunState.Done or RunState.DoneWithErrors or RunState.Failed or RunState.Cancelled
                ? previous
                : RunState.Idle;
            if (generation != _scanGeneration) ScheduleScan(TimeSpan.FromMilliseconds(100));
        }
    }

    private void FailScan(string message)
    {
        ScanError = message;
        ScanPhase = ScanPhase.Failed;
    }

    private void ApplyDetectedEngine(string? version)
    {
        DetectedEngine = version ?? "";
        if (version is null)
        {
            EngineSource = EngineSource.NotDetected;
            return;
        }

        var match = Games.FirstOrDefault(g => g.Label == $"Unreal Engine {version}");
        if (match is null)
        {
            EngineSource = EngineSource.Unsupported;
            return;
        }

        SetGame(match);
        EngineSource = EngineSource.Detected;
    }

    /// <summary>A run that had to wait for the form to settle; checked once it is over.</summary>
    private void RunQueuedScan()
    {
        if (!_scanQueued) return;
        _scanQueued = false;
        ScheduleScan(TimeSpan.FromMilliseconds(100));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Rescan()
    {
        _detectedFor = null;
        ScheduleScan(TimeSpan.Zero);
    }

    [RelayCommand]
    private void ToggleEngineEditor() => EngineEditorOpen = !EngineEditorOpen;

    [RelayCommand]
    private void AddAesKey() => AesRequested = true;

    [RelayCommand]
    private void OpenMappingsGuide() =>
        ShellHelper.OpenUrl(Loc.Instance.Language == "ru" ? AboutViewModel.MappingsUrlRu : AboutViewModel.MappingsUrl);

    // ---------------------------------------------------------------- what the form shows about the source

    public bool HasSource => !string.IsNullOrWhiteSpace(PaksPath);

    public string EngineText => EngineSource switch
    {
        EngineSource.Detecting => L["Export.Engine.Detecting"],
        EngineSource.Detected => L.Format("Export.Engine.Detected", SelectedGame.Label),
        EngineSource.Unsupported => L.Format("Export.Engine.Unsupported", DetectedEngine),
        EngineSource.Manual => L.Format("Export.Engine.Manual", SelectedGame.Label),
        EngineSource.NotDetected => L["Export.Engine.NotDetected"],
        _ => L.Format("Export.Engine.Manual", SelectedGame.Label)
    };

    public bool ShowEngineLine => HasSource && ScanPhase != ScanPhase.Missing;
    public bool EngineNeedsAttention => EngineSource is EngineSource.Unsupported or EngineSource.NotDetected;
    public bool ShowEngineSelector => EngineEditorOpen || EngineNeedsAttention;

    public string ScanText => ScanPhase switch
    {
        ScanPhase.Missing => L["Export.Scan.Missing"],
        ScanPhase.Pending => L["Export.Scan.Running"],
        ScanPhase.Failed => ScanError,
        ScanPhase.Ready => L.Format("Export.Scan.Result", ContainerCount + LockedCount, EntryCount,
            LockedCount > 0 ? L.Format("Export.Scan.Locked", LockedCount) : L["Export.Scan.NoneLocked"]),
        _ => ""
    };

    public bool ShowScanLine => ScanPhase != ScanPhase.None;
    public bool ScanIsProblem => ScanPhase is ScanPhase.Missing or ScanPhase.Failed;
    public bool ScanIsWarning => ScanPhase == ScanPhase.Ready && LockedCount > 0;
    public bool ScanIsNormal => !ScanIsProblem && !ScanIsWarning;
    public bool CanRescan => ScanPhase is ScanPhase.Ready or ScanPhase.Failed;

    /// <summary>AES keys are asked for only when the source turned out to be encrypted, or on request.</summary>
    public bool ShowAes => LockedCount > 0 || AesRequested || !string.IsNullOrWhiteSpace(AesText);
    /// <summary>Offered while encryption is unknown; a read that found none needs no key.</summary>
    public bool CanAddAesKey => !ShowAes && HasSource && ScanPhase != ScanPhase.Ready;
    public string AesPrompt => LockedCount > 0 ? L.Format("Export.Aes.Required", LockedCount) : "";
    public bool HasAesPrompt => LockedCount > 0;

    private bool IsUE5 => SelectedGame.Value >= EGame.GAME_UE5_0;

    public string MappingsText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(UsmapPath))
                return File.Exists(UsmapPath.Trim())
                    ? L.Format("Export.Usmap.Using", Path.GetFileName(UsmapPath.Trim()))
                    : L["Export.Usmap.NotFound"];
            if (_autoMappings is not null) return L.Format("Export.Usmap.Found", Path.GetFileName(_autoMappings));
            if (_mappingsAmbiguous) return L["Export.Usmap.Ambiguous"];
            return IsUE5 ? L["Export.Usmap.Missing"] : L["Export.Usmap.Optional"];
        }
    }

    private bool MappingsProblem =>
        NeedsMappings && (
            (!string.IsNullOrWhiteSpace(UsmapPath) && !File.Exists(UsmapPath.Trim())) ||
            (string.IsNullOrWhiteSpace(UsmapPath) && _autoMappings is null && (_mappingsAmbiguous || IsUE5)));

    public bool MappingsIsWarning => MappingsProblem;
    public bool MappingsIsNormal => !MappingsProblem;

    public string OverwriteWarning => L["Export.Out.OverwriteWarning"];

    private void RefreshSourceStatus()
    {
        foreach (var property in new[]
                 {
                     nameof(HasSource), nameof(EngineText), nameof(ShowEngineLine), nameof(EngineNeedsAttention),
                     nameof(ShowEngineSelector), nameof(ScanText), nameof(ShowScanLine), nameof(ScanIsProblem),
                     nameof(ScanIsWarning), nameof(ScanIsNormal), nameof(CanRescan), nameof(ShowAes),
                     nameof(CanAddAesKey), nameof(AesPrompt), nameof(HasAesPrompt), nameof(MappingsText),
                     nameof(MappingsIsWarning), nameof(MappingsIsNormal)
                 })
            OnPropertyChanged(property);
    }

    // ---------------------------------------------------------------- collapsed sections

    /// <summary>How many export options differ from their defaults.</summary>
    private int ChangedOptionCount =>
        new[]
        {
            Threads != Math.Max(1, _settings.DefaultThreads),
            !string.IsNullOrWhiteSpace(IncludeRegex), !string.IsNullOrWhiteSpace(ExcludeRegex),
            SkipWorlds, ExportMaterials, RawPackages, SkipJson, SkipAssets, SkipRawMisc, SkipAudioConvert,
            AllMips, SkipMorphs, Overwrite, Verbose,
            MeshFormat != MeshFormats[0], AnimFormat != AnimFormats[0], TextureFormat != TextureFormats[0],
            MeshQuality != MeshQualities[0], NaniteFormat != NaniteFormats[0], SocketFormat != SocketFormats[0],
            Platform != Platforms[0]
        }.Count(changed => changed);

    public string OptionsSummary => ChangedOptionCount is var n and > 0
        ? L.Format("Export.Options.Changed", n)
        : L["Export.Options.Defaults"];

    public bool OptionsChanged => ChangedOptionCount > 0;

    public string ToolsSummary =>
        new[] { RetocPath, OodlePath, ZlibPath, VgmStreamPath }.Count(p => !string.IsNullOrWhiteSpace(p)) is var n and > 0
            ? L.Format("Export.Tools.Custom", n)
            : L["Export.Tools.Automatic"];

    private void RefreshSummaries()
    {
        OnPropertyChanged(nameof(OptionsSummary));
        OnPropertyChanged(nameof(OptionsChanged));
        OnPropertyChanged(nameof(ToolsSummary));
    }

    // ---------------------------------------------------------------- readiness

    /// <summary>What stands between the form and a run, most important first.</summary>
    public (Readiness Level, string Text) ReadinessState
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PaksPath)) return (Readiness.Blocked, L["Export.Ready.NoSource"]);
            if (ScanPhase == ScanPhase.Missing) return (Readiness.Blocked, L["Export.Scan.Missing"]);
            if (string.IsNullOrWhiteSpace(OutputPath)) return (Readiness.Blocked, L["Export.Ready.NoOutput"]);
            if (ScanPhase == ScanPhase.Pending) return (Readiness.Warning, L["Export.Scan.Running"]);
            if (ScanPhase == ScanPhase.Failed) return (Readiness.Warning, L.Format("Export.Ready.ScanFailed", ScanError));
            if (LockedCount > 0) return (Readiness.Warning, L.Format("Export.Ready.Locked", LockedCount));
            if (EngineNeedsAttention) return (Readiness.Warning, L["Export.Ready.Engine"]);
            if (IsLegacyMode && _hasIoStore && !Retoc.SupportsEngine(SelectedGame.Value.ToString()))
                return (Readiness.Warning, L.Format("Export.Ready.RetocUnsupported", SelectedGame.Label));
            if (MappingsProblem) return (Readiness.Warning, L["Export.Ready.Mappings"]);
            if (Overwrite) return (Readiness.Warning, OverwriteWarning);
            if (SelectedPaths is { } selected) return (Readiness.Ready, L.Format("Export.Ready.Selection", selected.Count));
            return ScanPhase == ScanPhase.Ready
                ? (Readiness.Ready, L.Format("Export.Ready.Entries", EntryCount))
                : (Readiness.Ready, L["Export.Ready.Ok"]);
        }
    }

    public string ReadinessText => ReadinessState.Text;
    public bool IsReadinessBlocked => ReadinessState.Level == Readiness.Blocked;
    public bool IsReadinessWarning => ReadinessState.Level == Readiness.Warning;
    public bool IsReadinessOk => ReadinessState.Level == Readiness.Ready;

    private void RefreshReadiness()
    {
        OnPropertyChanged(nameof(ReadinessText));
        OnPropertyChanged(nameof(IsReadinessBlocked));
        OnPropertyChanged(nameof(IsReadinessWarning));
        OnPropertyChanged(nameof(IsReadinessOk));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
        DryRunCommand.NotifyCanExecuteChanged();
    }

    // ---------------------------------------------------------------- options <-> form

    public Options ToOptions() => new()
    {
        PaksDirectory = PaksPath.Trim(),
        OutputDirectory = OutputPath.Trim(),
        UsmapPath = NullIfEmpty(UsmapPath),
        Game = SelectedGame.Value,
        Mode = SelectedMode.Mode,
        AesKeys = AesText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        Threads = Math.Max(1, Threads),
        IncludeRegex = NullIfEmpty(IncludeRegex),
        ExcludeRegex = NullIfEmpty(ExcludeRegex),
        SelectedPaths = SelectedPaths,
        PathsFile = SelectedPaths is null || string.IsNullOrWhiteSpace(OutputPath) ? null : SelectionFilePath,
        ExportWorlds = !SkipWorlds,
        ExportMaterials = ExportMaterials,
        WriteRawPackages = RawPackages,
        WriteJson = !SkipJson,
        WriteAssets = !SkipAssets,
        WriteRawMisc = !SkipRawMisc,
        ConvertAudio = !SkipAudioConvert,
        ExportAllTextureMips = AllMips,
        ExportMorphTargets = !SkipMorphs,
        Resume = !Overwrite,
        Verbose = Verbose,
        MeshFormat = MeshFormat.Value,
        AnimFormatOverride = AnimFormat.Value == EMeshFormat.ActorX && MeshFormat.Value == EMeshFormat.Gltf2 ? null : AnimFormat.Value,
        TextureFormat = TextureFormat.Value,
        MeshQuality = MeshQuality.Value,
        NaniteMeshFormat = NaniteFormat.Value,
        SocketFormat = SocketFormat.Value,
        Platform = Platform.Value,
        RetocPath = NullIfEmpty(RetocPath) ?? NullIfEmpty(_settings.RetocPath),
        OodlePath = NullIfEmpty(OodlePath) ?? NullIfEmpty(_settings.OodlePath),
        ZlibPath = NullIfEmpty(ZlibPath) ?? NullIfEmpty(_settings.ZlibPath),
        VgmStreamPath = NullIfEmpty(VgmStreamPath) ?? NullIfEmpty(_settings.VgmStreamPath)
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void RefreshCommandLine()
    {
        try { CommandLine = Cli.FormatCommand(ToOptions()); }
        catch { CommandLine = ""; }
    }

    private string SelectionFilePath => Path.Combine(OutputPath.Trim(), "_selection.txt");

    public void SetSelection(IReadOnlySet<string>? paths) => SelectedPaths = paths;

    [RelayCommand]
    private void ClearSelection() => SelectedPaths = null;

    /// <summary>The selection is persisted next to the output, so the run is reproducible from the command line.</summary>
    private void WriteSelectionFile()
    {
        if (SelectedPaths is null || string.IsNullOrWhiteSpace(OutputPath)) return;
        Directory.CreateDirectory(OutputPath.Trim());
        File.WriteAllLines(SelectionFilePath, SelectedPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
    }

    public void SetFilter(string? include, string? exclude)
    {
        if (include is not null) IncludeRegex = include;
        if (exclude is not null) ExcludeRegex = exclude;
    }

    public void SetPaksPath(string path) => PaksPath = path;

    // ---------------------------------------------------------------- browse commands

    [RelayCommand]
    private async Task BrowsePaksFolder()
    {
        var path = await DialogService.PickFolderAsync("Dialog.PickPaks", PaksPath);
        if (path is not null) SetPaksPath(path);
    }

    [RelayCommand]
    private async Task BrowsePaksFile()
    {
        var path = await DialogService.PickContainerAsync(PaksPath);
        if (path is not null) SetPaksPath(path);
    }

    [RelayCommand]
    private async Task BrowseOutput()
    {
        var path = await DialogService.PickFolderAsync("Dialog.PickOut", OutputPath);
        if (path is not null) OutputPath = path;
    }

    [RelayCommand]
    private async Task BrowseUsmap()
    {
        var picked = await DialogService.PickUsmapAsync(string.IsNullOrEmpty(UsmapPath) ? PaksPath : UsmapPath);
        if (picked is not null) UsmapPath = picked;
    }

    [RelayCommand]
    private async Task BrowseRetoc() => RetocPath = await DialogService.PickExecutableAsync(RetocPath) ?? RetocPath;

    [RelayCommand]
    private async Task BrowseOodle() => OodlePath = await DialogService.PickLibraryAsync(OodlePath) ?? OodlePath;

    [RelayCommand]
    private async Task BrowseZlib() => ZlibPath = await DialogService.PickLibraryAsync(ZlibPath) ?? ZlibPath;

    [RelayCommand]
    private async Task BrowseVgmStream() => VgmStreamPath = await DialogService.PickExecutableAsync(VgmStreamPath) ?? VgmStreamPath;

    [RelayCommand]
    private async Task CopyCommand()
    {
        if (CopyToClipboard is not null && !string.IsNullOrEmpty(CommandLine)) await CopyToClipboard(CommandLine);
    }

    [RelayCommand]
    private void OpenOutput() => ShellHelper.OpenFolder(LastOutputDirectory);

    [RelayCommand]
    private void OpenErrors() => ShellHelper.OpenFile(Path.Combine(LastOutputDirectory, "errors.csv"));

    [RelayCommand]
    private void OpenLog() => ShellHelper.OpenFile(LogPath);

    [RelayCommand]
    private void ApplyRecent(RecentGame? game)
    {
        if (game is null) return;

        OutputPath = game.OutputPath;
        AesText = string.Join(Environment.NewLine, game.AesKeys);
        UsmapPath = game.UsmapPath;
        if (Enum.TryParse<ExportMode>(game.Mode, out var m))
            SelectedMode = Modes.FirstOrDefault(x => x.Mode == m) ?? SelectedMode;
        PaksPath = game.PaksPath;

        // A game-specific profile cannot be detected, only remembered; a plain engine version is
        // detected again, which also corrects an old wrong guess.
        if (Enum.TryParse<EGame>(game.Game, out var g) && Games.FirstOrDefault(x => x.Value == g) is { } remembered)
        {
            SetGame(remembered);
            if (!remembered.Label.StartsWith("Unreal Engine ", StringComparison.Ordinal))
            {
                _gameChosenFor = PaksPath.Trim();
                EngineSource = EngineSource.Manual;
            }
        }
    }

    [RelayCommand]
    private void RemoveRecent(RecentGame? game)
    {
        if (game is null) return;
        _settings.Recent.Remove(game);
        _settings.Save();
        Recent.Remove(game);
        OnPropertyChanged(nameof(HasRecent));
    }

    // ---------------------------------------------------------------- run commands

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task DryRun()
    {
        ClearOutcome();
        State = RunState.Preparing;
        ProgressIndeterminate = true;
        try
        {
            WriteSelectionFile();
            var plan = await _service.DryRunAsync(ToOptions());
            ShowPlan(plan);
            State = RunState.Idle;
        }
        catch (Exception e)
        {
            Fail(e);
        }
        finally
        {
            ProgressIndeterminate = false;
            RunQueuedScan();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        ClearOutcome();
        RememberRun();
        State = RunState.Preparing;
        ProgressIndeterminate = true;
        LastOutputDirectory = "";

        try
        {
            WriteSelectionFile();
            var summary = await _service.ExportAsync(ToOptions());
            LastOutputDirectory = summary.OutputDirectory;
            ShowSummary(summary);
            State = summary.Cancelled ? RunState.Cancelled
                : summary.ExitCode == 0 ? RunState.Done
                : RunState.DoneWithErrors;
        }
        catch (Exception e)
        {
            Fail(e);
        }
        finally
        {
            ProgressIndeterminate = false;
            OnPropertyChanged(nameof(HasErrorsFile));
            RunQueuedScan();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        State = RunState.Cancelling;
        _service.Cancel();
    }

    // ---------------------------------------------------------------- outcome presentation

    private void ClearOutcome()
    {
        ErrorHeadline = "";
        ErrorHint = "";
        SummaryTitle = "";
        Summary.Clear();
        OnPropertyChanged(nameof(HasSummary));
        ProgressFraction = 0;
        ProgressProcessed = ProgressWritten = ProgressFailed = ProgressRate = ProgressEta = ProgressElapsed = "";
    }

    private void Fail(Exception e)
    {
        switch (e)
        {
            case OperationCanceledException:
                State = RunState.Cancelled;
                return;

            case UserFacingException u:
                ErrorHeadline = u.Headline;
                ErrorHint = u.Hint ?? "";
                Log.Problem(u.Headline, u.Hint);
                break;

            default:
                ErrorHeadline = L["Error.Unexpected"];
                ErrorHint = e.Message;
                Log.Error(e.ToString());
                break;
        }

        State = RunState.Failed;
    }

    private void ApplyProgress(ExportProgress p)
    {
        if (State == RunState.Preparing) State = RunState.Running;
        ProgressIndeterminate = false;
        ProgressFraction = p.Fraction;
        ProgressProcessed = $"{p.Processed:N0} / {p.Total:N0}";
        ProgressWritten = p.Written.ToString("N0");
        ProgressFailed = p.Failed.ToString("N0");
        ProgressRate = L.Format("Progress.PerSecond", p.RatePerSecond);
        ProgressEta = p.Eta is { } eta ? Format.Duration(eta) : "—";
        ProgressElapsed = Format.Duration(p.Elapsed);
    }

    private void ShowSummary(ExportSummary s)
    {
        SummaryTitle = s.Cancelled
            ? L.Format("Summary.Cancelled", Format.Duration(s.Elapsed))
            : L.Format("Summary.Done", Format.Duration(s.Elapsed));

        Summary.Clear();
        Summary.Add(new SummaryLine(L["Summary.Processed"], s.Processed.ToString("N0")));
        Summary.Add(new SummaryLine(L["Summary.Exported"], s.Exported.ToString("N0")));
        Summary.Add(new SummaryLine(L["Summary.Written"], s.Written.ToString("N0")));
        if (s.IoStoreConverted > 0) Summary.Add(new SummaryLine(L["Summary.IoStore"], s.IoStoreConverted.ToString("N0")));
        Summary.Add(new SummaryLine(L["Summary.NothingToDo"], s.NothingToDo.ToString("N0")));
        if (IsFullMode) Summary.Add(new SummaryLine(L["Summary.NoConverter"], s.NoConverter.ToString("N0")));
        if (s.FailedEntries > 0) Summary.Add(new SummaryLine(L["Summary.FailedEntries"], s.FailedEntries.ToString("N0")));
        if (s.FailedObjects > 0) Summary.Add(new SummaryLine(L["Summary.FailedObjects"], s.FailedObjects.ToString("N0")));
        Summary.Add(new SummaryLine(L["Summary.Output"], s.OutputDirectory));
        OnPropertyChanged(nameof(HasSummary));
    }

    private void ShowPlan(ExportPlan p)
    {
        SummaryTitle = L["Plan.Title"];
        Summary.Clear();
        Summary.Add(new SummaryLine(L["Plan.Selected"], p.Selected.ToString("N0")));
        Summary.Add(new SummaryLine(L["Plan.AlreadyDone"], p.AlreadyDone.ToString("N0")));
        Summary.Add(new SummaryLine(L["Plan.SkippedByMode"], p.SkippedByMode.ToString("N0")));
        Summary.Add(new SummaryLine(L["Plan.Packages"], p.Packages.ToString("N0")));
        Summary.Add(new SummaryLine(L["Plan.LooseFiles"], p.LooseFiles.ToString("N0")));
        if (p.Mode != ExportMode.Legacy) Summary.Add(new SummaryLine(L["Plan.Payloads"], p.Payloads.ToString("N0")));
        if (p.Mode == ExportMode.Legacy) Summary.Add(new SummaryLine(L["Plan.IoStore"], p.IoStorePackages.ToString("N0")));
        Summary.Add(new SummaryLine(L["Plan.Mappings"], p.MappingsPath ?? L["Common.None"]));
        Summary.Add(new SummaryLine(L["Summary.Output"], p.OutputDirectory));
        OnPropertyChanged(nameof(HasSummary));
    }

    private void RememberRun()
    {
        if (_settings.RememberPaths)
        {
            _settings.LastPaksPath = PaksPath;
            _settings.LastOutputPath = OutputPath;
            _settings.LastGame = SelectedGame.Value.ToString();
        }

        var options = ToOptions();
        _settings.Remember(new RecentGame
        {
            // The resolved container folder names the game even when the user picked its root.
            Name = GuessGameName(_detectedFor ?? PaksPath),
            PaksPath = PaksPath,
            OutputPath = OutputPath,
            Game = options.Game.ToString(),
            Mode = options.Mode.ToString(),
            AesKeys = options.AesKeys,
            UsmapPath = UsmapPath ?? "",
            LastUsed = DateTime.Now
        });
        _settings.Save();

        Recent.Clear();
        foreach (var r in _settings.Recent) Recent.Add(r);
        OnPropertyChanged(nameof(HasRecent));
    }

    /// <summary>"D:\Games\MyGame\MyGame\Content\Paks" → "MyGame": the first folder above Content.</summary>
    private static string GuessGameName(string path)
    {
        var full = path.Trim().TrimEnd('\\', '/');
        if (File.Exists(full)) full = Path.GetDirectoryName(full) ?? full;

        var parts = full.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var content = Array.FindLastIndex(parts, p => p.Equals("Content", StringComparison.OrdinalIgnoreCase));
        if (content > 0) return parts[content - 1];

        return parts.Length > 0 ? parts[^1] : full;
    }

    private static List<GameOption> BuildGames()
    {
        var all = Enum.GetValues<EGame>().Distinct().ToList();

        // Plain engine versions first, newest at the top; game-specific overrides after them.
        var engine = all
            .Where(g => g.ToString().StartsWith("GAME_UE", StringComparison.Ordinal))
            .Select(g => (Game: g, Version: ParseVersion(g.ToString())))
            .Where(t => t.Version is not null)
            .OrderByDescending(t => t.Version!.Value.Major).ThenByDescending(t => t.Version!.Value.Minor)
            .Select(t => new GameOption(t.Game, $"Unreal Engine {t.Version!.Value.Major}.{t.Version.Value.Minor}"))
            .ToList();

        var specific = all
            .Where(g => !g.ToString().StartsWith("GAME_UE", StringComparison.Ordinal) && g.ToString().StartsWith("GAME_", StringComparison.Ordinal))
            .OrderBy(g => g.ToString(), StringComparer.Ordinal)
            .Select(g => new GameOption(g, g.ToString()[5..]));

        return [.. engine, .. specific];
    }

    private static (int Major, int Minor)? ParseVersion(string name)
    {
        var body = name["GAME_UE".Length..].Split('_');
        return body.Length == 2 && int.TryParse(body[0], out var major) && int.TryParse(body[1], out var minor)
            ? (major, minor)
            : null;
    }
}
