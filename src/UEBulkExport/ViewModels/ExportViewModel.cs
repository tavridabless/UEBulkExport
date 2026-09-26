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

public sealed record GameOption(EGame Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record ModeOption(ExportMode Mode, string TitleKey, string DescriptionKey)
{
    public string Title => Loc.Instance[TitleKey];
    public string Description => Loc.Instance[DescriptionKey];
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

/// <summary>The Export page: everything the CLI can do, as a form, plus the run itself.</summary>
public sealed partial class ExportViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ExportService _service = new();
    private readonly DumpConversionService _conversionService = new();
    private CancellationTokenSource? _conversionCancellation;
    private TaskCompletionSource<bool>? _conversionDecision;
    private static Loc L => Loc.Instance;

    // ---------------------------------------------------------------- source

    [ObservableProperty] private string _paksPath = "";
    [ObservableProperty] private GameOption _selectedGame;
    [ObservableProperty] private string _aesText = "";
    [ObservableProperty] private string _scanSummary = "";
    [ObservableProperty] private bool _scanHasLocked;

    public IReadOnlyList<GameOption> Games { get; } = BuildGames();

    // ---------------------------------------------------------------- destination

    [ObservableProperty] private string _outputPath = "";
    [ObservableProperty] private ModeOption _selectedMode;
    [ObservableProperty] private string _usmapPath = "";

    public IReadOnlyList<ModeOption> Modes { get; } =
    [
        new(ExportMode.Legacy, "Mode.Legacy.Title", "Mode.Legacy.Desc"),
        new(ExportMode.Full, "Mode.Full.Title", "Mode.Full.Desc"),
        new(ExportMode.Json, "Mode.Json.Title", "Mode.Json.Desc"),
        new(ExportMode.Raw, "Mode.Raw.Title", "Mode.Raw.Desc")
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

    // ------------------------------------------------------- dump conversion

    [ObservableProperty] private string _conversionSourcePath = "";
    [ObservableProperty] private EngineVersionOption _conversionSourceVersion;
    [ObservableProperty] private string _uModelPath = "";
    [ObservableProperty] private string _targetProjectPath = "";
    [ObservableProperty] private string _unrealEditorPath = "";
    [ObservableProperty] private string _conversionDestinationPath = "/Game/ConvertedDump";
    [ObservableProperty] private bool _conversionOverwrite;
    [ObservableProperty] private bool _conversionIsBusy;
    [ObservableProperty] private bool _conversionAutoDetectBusy;
    [ObservableProperty] private bool _hasPendingConversionIssue;
    [ObservableProperty] private double _conversionProgress;
    [ObservableProperty] private string _conversionStatus = "";

    public IReadOnlyList<EngineVersionOption> ConversionSourceVersions { get; } =
        Enumerable.Range(0, 28).Reverse().Select(minor => new EngineVersionOption($"4.{minor}")).ToArray();

    public bool CanStartConversion => !IsBusy && !ConversionAutoDetectBusy;
    public bool CanCancelConversion => ConversionIsBusy;
    public bool CanDetectConversionPaths => !IsBusy && !ConversionAutoDetectBusy;
    public string ConversionTargetVersion
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TargetProjectPath) && string.IsNullOrWhiteSpace(UnrealEditorPath))
                return L["Convert.Target.Unknown"];
            var detected = DumpConversionService.DetectTargetVersion(TargetProjectPath, UnrealEditorPath);
            return detected == "unknown" ? L["Convert.Target.Unknown"] : detected;
        }
    }

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
    [ObservableProperty] private string _lastLogPath = "";

    /// <summary>Paths ticked in the Browser, or null to export the whole container.</summary>
    [ObservableProperty] private IReadOnlySet<string>? _selectedPaths;

    public bool HasSelection => SelectedPaths is not null;
    public string SelectionText => L.Format("Export.Selection.Text", SelectedPaths?.Count ?? 0);

    public ObservableCollection<SummaryLine> Summary { get; } = [];
    public ObservableCollection<RecentGame> Recent { get; } = [];

    public bool IsBusy => ConversionIsBusy ||
        State is RunState.Scanning or RunState.Preparing or RunState.Running or RunState.Cancelling;
    public bool CanRun => !IsBusy;
    public bool CanCancel => ConversionIsBusy ||
        State is RunState.Running or RunState.Preparing or RunState.Scanning;
    public bool HasError => !string.IsNullOrEmpty(ErrorHeadline);
    public bool HasSummary => Summary.Count > 0;
    public bool ShowProgress => State is RunState.Running or RunState.Preparing or RunState.Cancelling;
    public bool HasRecent => Recent.Count > 0;
    public bool CanOpenOutput => !string.IsNullOrEmpty(LastOutputDirectory) && Directory.Exists(LastOutputDirectory);
    public bool HasErrorsFile => CanOpenOutput && File.Exists(Path.Combine(LastOutputDirectory, "errors.csv"));
    public bool CanOpenLog => File.Exists(EffectiveLogPath);
    private string EffectiveLogPath => !string.IsNullOrWhiteSpace(LastLogPath)
        ? LastLogPath
        : Path.Combine(LastOutputDirectory, "UEBulkExport.log");

    public string StatusText => ConversionIsBusy
        ? (string.IsNullOrEmpty(ConversionStatus) ? L["Convert.Stage.Preparing"] : ConversionStatus)
        : State switch
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

    /// <summary>Set by the view: asks a yes/no question (title, message) and returns the answer.</summary>
    public Func<string, string, Task<bool>>? ConfirmConversion { get; set; }

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
        _conversionSourceVersion = ConversionSourceVersions[0];
        _threads = Math.Max(1, settings.DefaultThreads);

        LoadFromSettings();

        _service.ProgressChanged += p => Dispatcher.UIThread.Post(() => ApplyProgress(p));
        Loc.Instance.LanguageChanged += () =>
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(Modes));
        };

        RefreshCommandLine();
        // At startup only the tools are looked up. The target project decides where assets are saved,
        // so it is never picked silently: the user chooses it or asks for a suggestion.
        Dispatcher.UIThread.Post(() => _ = DetectConversionPathsCore(searchProjects: false));
    }

    public void LoadFromSettings()
    {
        if (_settings.RememberPaths)
        {
            PaksPath = _settings.LastPaksPath;
            OutputPath = _settings.LastOutputPath;
            if (Enum.TryParse<EGame>(_settings.LastGame, out var game))
                SelectedGame = Games.FirstOrDefault(g => g.Value == game) ?? SelectedGame;
        }

        Threads = Math.Max(1, _settings.DefaultThreads);
        RetocPath = _settings.RetocPath;
        OodlePath = _settings.OodlePath;
        ZlibPath = _settings.ZlibPath;
        VgmStreamPath = _settings.VgmStreamPath;
        ConversionSourcePath = _settings.ConversionSourcePath;
        ConversionSourceVersion = ConversionSourceVersions.FirstOrDefault(v =>
            v.Version == _settings.ConversionSourceVersion) ?? ConversionSourceVersions[0];
        UModelPath = _settings.UModelPath;
        TargetProjectPath = _settings.TargetProjectPath;
        UnrealEditorPath = _settings.UnrealEditorPath;
        ConversionDestinationPath = string.IsNullOrWhiteSpace(_settings.ConversionDestinationPath)
            ? "/Game/ConvertedDump"
            : _settings.ConversionDestinationPath;

        Recent.Clear();
        foreach (var r in _settings.Recent) Recent.Add(r);
        OnPropertyChanged(nameof(HasRecent));
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        switch (e.PropertyName)
        {
            case nameof(State):
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanRun));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(ShowProgress));
                OnPropertyChanged(nameof(StatusText));
                StartCommand.NotifyCanExecuteChanged();
                DryRunCommand.NotifyCanExecuteChanged();
                ScanCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
                StartConversionCommand.NotifyCanExecuteChanged();
                break;

            case nameof(ConversionIsBusy):
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanRun));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(CanStartConversion));
                OnPropertyChanged(nameof(CanCancelConversion));
                StartConversionCommand.NotifyCanExecuteChanged();
                CancelConversionCommand.NotifyCanExecuteChanged();
                StartCommand.NotifyCanExecuteChanged();
                DryRunCommand.NotifyCanExecuteChanged();
                ScanCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
                break;

            case nameof(ConversionAutoDetectBusy):
                OnPropertyChanged(nameof(CanStartConversion));
                OnPropertyChanged(nameof(CanDetectConversionPaths));
                StartConversionCommand.NotifyCanExecuteChanged();
                DetectConversionPathsCommand.NotifyCanExecuteChanged();
                break;

            case nameof(ConversionStatus):
                OnPropertyChanged(nameof(StatusText));
                break;

            case nameof(TargetProjectPath):
            case nameof(UnrealEditorPath):
                OnPropertyChanged(nameof(ConversionTargetVersion));
                break;

            case nameof(SelectedMode):
                OnPropertyChanged(nameof(NeedsMappings));
                OnPropertyChanged(nameof(IsFullMode));
                OnPropertyChanged(nameof(IsLegacyMode));
                break;

            case nameof(ErrorHeadline):
                OnPropertyChanged(nameof(HasError));
                break;

            case nameof(LastOutputDirectory):
                OnPropertyChanged(nameof(CanOpenOutput));
                OnPropertyChanged(nameof(HasErrorsFile));
                OnPropertyChanged(nameof(CanOpenLog));
                break;

            case nameof(LastLogPath):
                OnPropertyChanged(nameof(CanOpenLog));
                break;

            case nameof(SelectedPaths):
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectionText));
                break;
        }

        if (e.PropertyName is not (nameof(CommandLine) or nameof(State) or nameof(ScanSummary)
            or nameof(ProgressFraction) or nameof(ProgressProcessed) or nameof(ProgressWritten)
            or nameof(ProgressFailed) or nameof(ProgressRate) or nameof(ProgressEta) or nameof(ProgressElapsed)
            or nameof(ErrorHeadline) or nameof(ErrorHint) or nameof(SummaryTitle) or nameof(LastOutputDirectory)
            or nameof(LastLogPath)))
            RefreshCommandLine();
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
    private async Task BrowseConversionSource()
    {
        var path = await DialogService.PickFolderAsync("Dialog.PickDump", ConversionSourcePath);
        if (path is not null) ConversionSourcePath = path;
    }

    [RelayCommand]
    private async Task BrowseUModel() => UModelPath = await DialogService.PickExecutableAsync(UModelPath) ?? UModelPath;

    [RelayCommand]
    private async Task BrowseTargetProject()
    {
        var path = await DialogService.PickUnrealProjectAsync(TargetProjectPath);
        if (path is not null) TargetProjectPath = path;
    }

    [RelayCommand]
    private async Task BrowseUnrealEditor() =>
        UnrealEditorPath = await DialogService.PickExecutableAsync(UnrealEditorPath) ?? UnrealEditorPath;

    [RelayCommand(CanExecute = nameof(CanDetectConversionPaths))]
    private Task DetectConversionPaths() => DetectConversionPathsCore(searchProjects: true);

    private async Task DetectConversionPathsCore(bool searchProjects)
    {
        if (ConversionAutoDetectBusy || IsBusy) return;

        ConversionAutoDetectBusy = true;
        ConversionStatus = L["Convert.AutoDetect.Searching"];
        try
        {
            // A field the user edits while the search runs keeps what they typed.
            var (umodelBefore, projectBefore, editorBefore) = (UModelPath, TargetProjectPath, UnrealEditorPath);
            var result = await Task.Run(() => ConversionPathDiscovery.Find(
                umodelBefore, projectBefore, editorBefore, searchProjects));

            if (result.UModelPath is not null && UModelPath == umodelBefore) UModelPath = result.UModelPath;
            if (result.ProjectPath is not null && TargetProjectPath == projectBefore) TargetProjectPath = result.ProjectPath;
            if (result.UnrealEditorPath is not null && UnrealEditorPath == editorBefore)
                UnrealEditorPath = result.UnrealEditorPath;

            ConversionStatus = L.Format("Convert.AutoDetect.Result",
                result.UModelPath is not null ? L["Common.Yes"] : L["Common.No"],
                result.ProjectPath is not null ? L["Common.Yes"] : L["Common.No"],
                result.UnrealEditorPath is not null ? L["Common.Yes"] : L["Common.No"]);
            if (result.UModelPath is not null || result.ProjectPath is not null || result.UnrealEditorPath is not null)
                RememberConversionSettings();
        }
        catch (Exception e)
        {
            ConversionStatus = L.Format("Convert.AutoDetect.Error", e.Message);
        }
        finally
        {
            ConversionAutoDetectBusy = false;
        }
    }

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
    private void OpenLog() => ShellHelper.OpenFile(EffectiveLogPath);

    [RelayCommand]
    private void ApplyRecent(RecentGame? game)
    {
        if (game is null) return;

        PaksPath = game.PaksPath;
        OutputPath = game.OutputPath;
        AesText = string.Join(Environment.NewLine, game.AesKeys);
        UsmapPath = game.UsmapPath;
        if (Enum.TryParse<EGame>(game.Game, out var g))
            SelectedGame = Games.FirstOrDefault(x => x.Value == g) ?? SelectedGame;
        if (Enum.TryParse<ExportMode>(game.Mode, out var m))
            SelectedMode = Modes.FirstOrDefault(x => x.Mode == m) ?? SelectedMode;
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

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Scan()
    {
        if (!Require(paks: true, output: false)) return;

        ClearOutcome();
        State = RunState.Scanning;
        try
        {
            var result = await _service.ScanAsync(ToOptions());
            var locked = result.Containers.Count(c => c.IsLocked);
            ScanHasLocked = locked > 0;
            ScanSummary = L.Format("Export.Scan.Result",
                result.Containers.Count(c => !c.IsLocked), result.Files.Count,
                locked > 0 ? L.Format("Export.Scan.Locked", locked) : L["Export.Scan.NoneLocked"]);
            Scanned?.Invoke(result);
            State = RunState.Idle;
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DryRun()
    {
        if (!Require(paks: true, output: true)) return;

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
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Start()
    {
        if (!Require(paks: true, output: true)) return;

        ClearOutcome();
        RememberRun();
        State = RunState.Preparing;
        ProgressIndeterminate = true;
        LastOutputDirectory = "";
        LastLogPath = "";

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
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (ConversionIsBusy)
        {
            CancelConversion();
            return;
        }
        State = RunState.Cancelling;
        _service.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanStartConversion))]
    private async Task StartConversion()
    {
        if (!RequireConversion()) return;

        // Imported assets are saved into the project; make sure it is the one the user means.
        if (ConfirmConversion is not null &&
            !await ConfirmConversion(L["Convert.Confirm.Title"],
                L.Format("Convert.Confirm.Message", TargetProjectPath.Trim(), ConversionDestinationPath.Trim())))
            return;

        ClearOutcome();
        State = RunState.Idle;
        ConversionIsBusy = true;
        ConversionProgress = 0;
        ConversionStatus = L["Convert.Stage.Preparing"];
        _conversionCancellation = new CancellationTokenSource();
        RememberConversionSettings();

        try
        {
            var progress = new Progress<DumpConversionProgress>(p =>
            {
                ConversionProgress = p.Fraction;
                ConversionStatus = L[$"Convert.Stage.{p.Stage}"];
            });
            // The service scans and links large dumps; keep that work off the UI thread. Progress
            // and the Continue prompt marshal back on their own.
            var options = new DumpConversionOptions(
                ConversionSourcePath.Trim(),
                ConversionSourceVersion.Version,
                UModelPath.Trim(),
                TargetProjectPath.Trim(),
                UnrealEditorPath.Trim(),
                ConversionDestinationPath.Trim(),
                Overwrite: ConversionOverwrite);
            var token = _conversionCancellation.Token;
            var summary = await Task.Run(
                () => _conversionService.RunAsync(options, progress, WaitForConversionDecision, token), token);

            ShowConversionSummary(summary);
            LastOutputDirectory = Path.Combine(Path.GetDirectoryName(TargetProjectPath)!, "Content");
            LastLogPath = Path.Combine(summary.WorkingDirectory, "unreal-import.log");
            ConversionStatus = L["Convert.Stage.Done"];
            State = summary.FailedFiles == 0 && summary.SkippedFiles == 0
                ? RunState.Done
                : RunState.DoneWithErrors;
        }
        catch (OperationCanceledException)
        {
            ConversionStatus = L["Convert.Stage.Cancelled"];
            State = RunState.Cancelled;
        }
        catch (Exception e)
        {
            ConversionStatus = L["Convert.Stage.Failed"];
            Fail(e);
        }
        finally
        {
            _conversionCancellation?.Dispose();
            _conversionCancellation = null;
            _conversionDecision?.TrySetResult(false);
            _conversionDecision = null;
            HasPendingConversionIssue = false;
            ConversionIsBusy = false;
        }
    }

    private async Task<bool> WaitForConversionDecision(DumpConversionIssue issue, CancellationToken ct)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _conversionDecision = decision;
            ErrorHeadline = issue.Headline;
            ErrorHint = $"{issue.SourceFile}{Environment.NewLine}{issue.Details}";
            HasPendingConversionIssue = true;
            ConversionStatus = L["Convert.Error.Waiting"];
        });

        using var registration = ct.Register(() => decision.TrySetCanceled(ct));
        return await decision.Task;
    }

    [RelayCommand]
    private void ContinueConversion()
    {
        var decision = _conversionDecision;
        _conversionDecision = null;
        HasPendingConversionIssue = false;
        ErrorHeadline = "";
        ErrorHint = "";
        ConversionStatus = L["Convert.Error.Continuing"];
        decision?.TrySetResult(true);
    }

    [RelayCommand(CanExecute = nameof(CanCancelConversion))]
    private void CancelConversion()
    {
        ConversionStatus = L["Convert.Stage.Cancelling"];
        _conversionDecision?.TrySetResult(false);
        _conversionCancellation?.Cancel();
        _conversionService.Cancel();
    }

    // ---------------------------------------------------------------- outcome presentation

    private bool Require(bool paks, bool output)
    {
        if (paks && string.IsNullOrWhiteSpace(PaksPath))
        {
            ErrorHeadline = L["Validate.PaksMissing"];
            ErrorHint = "";
            return false;
        }

        if (output && string.IsNullOrWhiteSpace(OutputPath))
        {
            ErrorHeadline = L["Validate.OutMissing"];
            ErrorHint = "";
            return false;
        }

        return true;
    }

    private bool RequireConversion()
    {
        (string Value, string Key)[] required =
        [
            (ConversionSourcePath, "Validate.DumpMissing"),
            (UModelPath, "Validate.UModelMissing"),
            (TargetProjectPath, "Validate.ProjectMissing"),
            (UnrealEditorPath, "Validate.EditorMissing")
        ];
        foreach (var item in required)
        {
            if (string.IsNullOrWhiteSpace(item.Value))
            {
                ErrorHeadline = L[item.Key];
                ErrorHint = "";
                return false;
            }
        }
        return true;
    }

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

    private void ShowConversionSummary(DumpConversionSummary s)
    {
        SummaryTitle = L.Format("Convert.Summary.Title", Format.Duration(s.Elapsed));
        Summary.Clear();
        Summary.Add(new SummaryLine(L["Convert.Summary.SourcePackages"], s.CookedPackages.ToString("N0")));
        Summary.Add(new SummaryLine(L["Convert.Summary.Importable"], s.ImportableFiles.ToString("N0")));
        Summary.Add(new SummaryLine(L["Convert.Summary.Imported"], s.ImportedFiles.ToString("N0")));
        Summary.Add(new SummaryLine(L["Convert.Summary.Failed"], s.FailedFiles.ToString("N0")));
        if (s.UnsupportedActorXFiles > 0)
            Summary.Add(new SummaryLine(L["Convert.Summary.ActorX"], s.UnsupportedActorXFiles.ToString("N0")));
        if (s.AlreadyPresent > 0)
            Summary.Add(new SummaryLine(L["Convert.Summary.AlreadyPresent"], s.AlreadyPresent.ToString("N0")));
        if (s.SkippedFiles > 0)
            Summary.Add(new SummaryLine(L["Convert.Summary.Skipped"], s.SkippedFiles.ToString("N0")));
        if (s.ErrorReportPath is not null)
            Summary.Add(new SummaryLine(L["Convert.Summary.Report"], s.ErrorReportPath));
        Summary.Add(new SummaryLine(L["Convert.Summary.TargetVersion"], s.TargetVersion));
        Summary.Add(new SummaryLine(L["Convert.Summary.Destination"], s.DestinationPath));
        Summary.Add(new SummaryLine(L["Convert.Summary.Working"], s.WorkingDirectory));
        OnPropertyChanged(nameof(HasSummary));
    }

    private void RememberConversionSettings()
    {
        _settings.ConversionSourcePath = ConversionSourcePath;
        _settings.ConversionSourceVersion = ConversionSourceVersion.Version;
        _settings.UModelPath = UModelPath;
        _settings.TargetProjectPath = TargetProjectPath;
        _settings.UnrealEditorPath = UnrealEditorPath;
        _settings.ConversionDestinationPath = ConversionDestinationPath;
        _settings.Save();
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
            Name = GuessGameName(PaksPath),
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
