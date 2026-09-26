using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UEBulkExport.Gui.Converters;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.Services;

namespace UEBulkExport.Gui.ViewModels;

/// <summary>How far a form is from being runnable; drives the text next to the main action.</summary>
public enum Readiness { Ready, Warning, Blocked }

/// <summary>
/// The Migrate page: rebuilds a UE4 cooked dump as assets of another Unreal project. A workflow of
/// its own, with its own source, result, tools and run, so it lives apart from the export form.
/// </summary>
public sealed partial class MigrateViewModel : ObservableObject
{
    public const string UEViewerUrl = "https://www.gildor.org/en/projects/umodel";

    private readonly AppSettings _settings;
    private readonly DumpConversionService _service = new();
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource<bool>? _decision;
    private static Loc L => Loc.Instance;

    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private EngineVersionOption _sourceVersion;
    [ObservableProperty] private string _uModelPath = "";
    [ObservableProperty] private string _targetProjectPath = "";
    [ObservableProperty] private string _unrealEditorPath = "";
    [ObservableProperty] private string _destinationPath = "/Game/ConvertedDump";
    [ObservableProperty] private bool _overwrite;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDetecting;
    [ObservableProperty] private bool _toolsExpanded;
    [ObservableProperty] private bool _hasPendingIssue;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _stage = "";
    [ObservableProperty] private string _detectionNote = "";
    [ObservableProperty] private string _errorHeadline = "";
    [ObservableProperty] private string _errorHint = "";
    [ObservableProperty] private string _summaryTitle = "";
    [ObservableProperty] private string _lastWorkingDirectory = "";
    [ObservableProperty] private string _lastReportPath = "";

    public IReadOnlyList<EngineVersionOption> SourceVersions { get; } =
        Enumerable.Range(0, 28).Reverse().Select(minor => new EngineVersionOption($"4.{minor}")).ToArray();

    public ObservableCollection<SummaryLine> Summary { get; } = [];

    /// <summary>Set by the view: asks a yes/no question (title, message) and returns the answer.</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }

    public MigrateViewModel(AppSettings settings)
    {
        _settings = settings;
        _sourceVersion = SourceVersions[0];
        LoadFromSettings();

        Loc.Instance.LanguageChanged += RefreshDerived;

        // At startup only the tools are looked up. The target project decides where assets are saved,
        // so it is never picked silently: the user chooses it or asks for a suggestion.
        Dispatcher.UIThread.Post(() => _ = DetectCore(searchProjects: false));
    }

    public void LoadFromSettings()
    {
        SourcePath = _settings.ConversionSourcePath;
        SourceVersion = SourceVersions.FirstOrDefault(v => v.Version == _settings.ConversionSourceVersion)
                        ?? SourceVersions[0];
        UModelPath = _settings.UModelPath;
        TargetProjectPath = _settings.TargetProjectPath;
        UnrealEditorPath = _settings.UnrealEditorPath;
        DestinationPath = string.IsNullOrWhiteSpace(_settings.ConversionDestinationPath)
            ? "/Game/ConvertedDump"
            : _settings.ConversionDestinationPath;
    }

    // ---------------------------------------------------------------- derived state

    private bool UModelFound => File.Exists(UModelPath.Trim());
    private bool EditorFound => File.Exists(UnrealEditorPath.Trim());
    private bool ProjectFound => File.Exists(TargetProjectPath.Trim());

    /// <summary>"5.8" from the project or the editor path, or null when neither tells.</summary>
    public string? TargetVersion
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TargetProjectPath) && string.IsNullOrWhiteSpace(UnrealEditorPath)) return null;
            var detected = DumpConversionService.DetectTargetVersion(TargetProjectPath, UnrealEditorPath);
            return detected == "unknown" ? null : detected;
        }
    }

    public string TargetVersionText => TargetVersion is { } v ? $"Unreal Engine {v}" : L["Migrate.Target.Unknown"];
    public bool HasTargetVersion => TargetVersion is not null;

    /// <summary>One line for the collapsed Tools section, so it need not be opened just to check.</summary>
    public string ToolsSummary => L.Format("Migrate.Tools.Summary",
        EditorFound
            ? TargetVersion is { } v ? L.Format("Migrate.Tools.EditorFoundVersion", v) : L["Migrate.Tools.EditorFound"]
            : L["Migrate.Tools.EditorMissing"],
        UModelFound ? L["Migrate.Tools.UModelFound"] : L["Migrate.Tools.UModelMissing"]);

    public bool ToolsMissing => !UModelFound || !EditorFound;
    public bool ShowUModelHelp => !UModelFound;

    public bool OverwriteWarningVisible => Overwrite;
    public string OverwriteWarning => L.Format("Migrate.Overwrite.Warning", DestinationPath.Trim());

    public (Readiness Level, string Text) ReadinessState
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SourcePath)) return (Readiness.Blocked, L["Migrate.Ready.NoSource"]);
            if (!Directory.Exists(SourcePath.Trim())) return (Readiness.Blocked, L["Migrate.Ready.SourceMissing"]);
            if (string.IsNullOrWhiteSpace(TargetProjectPath)) return (Readiness.Blocked, L["Migrate.Ready.NoProject"]);
            if (!ProjectFound) return (Readiness.Blocked, L["Migrate.Ready.ProjectMissing"]);
            if (!DestinationPath.Trim().StartsWith("/Game", StringComparison.OrdinalIgnoreCase))
                return (Readiness.Blocked, L["Migrate.Ready.BadDestination"]);
            if (!EditorFound) return (Readiness.Blocked, L["Migrate.Ready.NoEditor"]);
            if (!UModelFound) return (Readiness.Blocked, L["Migrate.Ready.NoUModel"]);
            if (Overwrite) return (Readiness.Warning, OverwriteWarning);
            return (Readiness.Ready, L.Format("Migrate.Ready.Ok",
                Path.GetFileNameWithoutExtension(TargetProjectPath.Trim()), DestinationPath.Trim()));
        }
    }

    public string ReadinessText => ReadinessState.Text;
    public bool IsReadinessBlocked => ReadinessState.Level == Readiness.Blocked;
    public bool IsReadinessWarning => ReadinessState.Level == Readiness.Warning;
    public bool IsReadinessOk => ReadinessState.Level == Readiness.Ready;
    public bool ShowReadiness => !IsBusy;

    public bool CanStart => !IsBusy && !IsDetecting && ReadinessState.Level != Readiness.Blocked;
    public bool CanDetect => !IsBusy && !IsDetecting;
    public bool HasError => !string.IsNullOrEmpty(ErrorHeadline);
    public bool HasSummary => Summary.Count > 0;
    public bool HasReport => File.Exists(LastReportPath);
    public bool HasWorkingDirectory => Directory.Exists(LastWorkingDirectory);
    public bool CanOpenContent => ProjectFound && Directory.Exists(ContentDirectory);
    private string ContentDirectory => Path.Combine(Path.GetDirectoryName(TargetProjectPath.Trim()) ?? "", "Content");
    private string ImportLogPath => Path.Combine(LastWorkingDirectory, "unreal-import.log");
    public bool HasImportLog => File.Exists(ImportLogPath);

    public string StatusText => IsBusy
        ? (string.IsNullOrEmpty(Stage) ? L["Migrate.Stage.Preparing"] : Stage)
        : L["Status.Idle"];

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        switch (e.PropertyName)
        {
            case nameof(SourcePath):
            case nameof(UModelPath):
            case nameof(TargetProjectPath):
            case nameof(UnrealEditorPath):
            case nameof(DestinationPath):
            case nameof(Overwrite):
            case nameof(IsBusy):
            case nameof(IsDetecting):
                RefreshDerived();
                break;
            case nameof(Stage):
                OnPropertyChanged(nameof(StatusText));
                break;
            case nameof(ErrorHeadline):
                OnPropertyChanged(nameof(HasError));
                break;
            case nameof(LastWorkingDirectory):
                OnPropertyChanged(nameof(HasWorkingDirectory));
                OnPropertyChanged(nameof(HasImportLog));
                break;
            case nameof(LastReportPath):
                OnPropertyChanged(nameof(HasReport));
                break;
        }
    }

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(TargetVersion));
        OnPropertyChanged(nameof(TargetVersionText));
        OnPropertyChanged(nameof(HasTargetVersion));
        OnPropertyChanged(nameof(ToolsSummary));
        OnPropertyChanged(nameof(ToolsMissing));
        OnPropertyChanged(nameof(ShowUModelHelp));
        OnPropertyChanged(nameof(OverwriteWarningVisible));
        OnPropertyChanged(nameof(OverwriteWarning));
        OnPropertyChanged(nameof(ReadinessText));
        OnPropertyChanged(nameof(IsReadinessBlocked));
        OnPropertyChanged(nameof(IsReadinessWarning));
        OnPropertyChanged(nameof(IsReadinessOk));
        OnPropertyChanged(nameof(ShowReadiness));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanDetect));
        OnPropertyChanged(nameof(CanOpenContent));
        OnPropertyChanged(nameof(StatusText));
        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        DetectCommand.NotifyCanExecuteChanged();
    }

    // ---------------------------------------------------------------- browse

    [RelayCommand]
    private async Task BrowseSource()
    {
        var path = await DialogService.PickFolderAsync("Dialog.PickDump", SourcePath);
        if (path is not null) SourcePath = path;
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

    [RelayCommand]
    private void OpenUEViewerSite() => ShellHelper.OpenUrl(UEViewerUrl);

    // ---------------------------------------------------------------- detection

    [RelayCommand(CanExecute = nameof(CanDetect))]
    private Task Detect() => DetectCore(searchProjects: true);

    private async Task DetectCore(bool searchProjects)
    {
        if (IsDetecting || IsBusy) return;

        IsDetecting = true;
        DetectionNote = L["Migrate.Detect.Searching"];
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

            DetectionNote = "";
            if (searchProjects && result.ProjectPath is null && string.IsNullOrWhiteSpace(TargetProjectPath))
                DetectionNote = L["Migrate.Detect.NoProject"];
            if (result.UModelPath is not null || result.ProjectPath is not null || result.UnrealEditorPath is not null)
                Remember();
        }
        catch (Exception e)
        {
            DetectionNote = L.Format("Migrate.Detect.Error", e.Message);
        }
        finally
        {
            IsDetecting = false;
            // Nothing to check in a section whose tools are all there; open it when something is missing.
            if (ToolsMissing) ToolsExpanded = true;
        }
    }

    // ---------------------------------------------------------------- run

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        // Imported assets are saved into the project; make sure it is the one the user means.
        if (Confirm is not null &&
            !await Confirm(L["Migrate.Confirm.Title"],
                L.Format("Migrate.Confirm.Message", TargetProjectPath.Trim(), DestinationPath.Trim())))
            return;

        ClearOutcome();
        IsBusy = true;
        Progress = 0;
        Stage = L["Migrate.Stage.Preparing"];
        _cancellation = new CancellationTokenSource();
        Remember();

        try
        {
            var progress = new Progress<DumpConversionProgress>(p =>
            {
                Progress = p.Fraction;
                Stage = L[$"Migrate.Stage.{p.Stage}"];
            });
            var options = new DumpConversionOptions(
                SourcePath.Trim(),
                SourceVersion.Version,
                UModelPath.Trim(),
                TargetProjectPath.Trim(),
                UnrealEditorPath.Trim(),
                DestinationPath.Trim(),
                Overwrite: Overwrite);
            var token = _cancellation.Token;
            // The service scans and links large dumps; keep that work off the UI thread. Progress
            // and the Continue prompt marshal back on their own.
            var summary = await Task.Run(() => _service.RunAsync(options, progress, WaitForDecision, token), token);

            LastWorkingDirectory = summary.WorkingDirectory;
            LastReportPath = summary.ErrorReportPath ?? "";
            ShowSummary(summary);
        }
        catch (OperationCanceledException)
        {
            SummaryTitle = "";
            ErrorHeadline = L["Migrate.Stage.Cancelled"];
            ErrorHint = "";
        }
        catch (UserFacingException u)
        {
            ErrorHeadline = u.Headline;
            ErrorHint = u.Hint ?? "";
            Log.Problem(u.Headline, u.Hint);
        }
        catch (Exception e)
        {
            ErrorHeadline = L["Error.Unexpected"];
            ErrorHint = e.Message;
            Log.Error(e.ToString());
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            _decision?.TrySetResult(false);
            _decision = null;
            HasPendingIssue = false;
            Stage = "";
            IsBusy = false;
            OnPropertyChanged(nameof(CanOpenContent));
        }
    }

    private async Task<bool> WaitForDecision(DumpConversionIssue issue, CancellationToken ct)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _decision = decision;
            ErrorHeadline = issue.Headline;
            ErrorHint = $"{issue.SourceFile}{Environment.NewLine}{issue.Details}";
            HasPendingIssue = true;
            Stage = L["Migrate.Error.Waiting"];
        });

        using var registration = ct.Register(() => decision.TrySetCanceled(ct));
        return await decision.Task;
    }

    [RelayCommand]
    private void Continue()
    {
        var decision = _decision;
        _decision = null;
        HasPendingIssue = false;
        ErrorHeadline = "";
        ErrorHint = "";
        Stage = L["Migrate.Error.Continuing"];
        decision?.TrySetResult(true);
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        Stage = L["Migrate.Stage.Cancelling"];
        _decision?.TrySetResult(false);
        _cancellation?.Cancel();
        _service.Cancel();
    }

    [RelayCommand] private void OpenContent() => ShellHelper.OpenFolder(ContentDirectory);
    [RelayCommand] private void OpenReport() => ShellHelper.OpenFile(LastReportPath);
    [RelayCommand] private void OpenLog() => ShellHelper.OpenFile(ImportLogPath);
    [RelayCommand] private void OpenWorkingDirectory() => ShellHelper.OpenFolder(LastWorkingDirectory);

    // ---------------------------------------------------------------- outcome

    private void ClearOutcome()
    {
        ErrorHeadline = "";
        ErrorHint = "";
        SummaryTitle = "";
        Summary.Clear();
        OnPropertyChanged(nameof(HasSummary));
    }

    private void ShowSummary(DumpConversionSummary s)
    {
        SummaryTitle = L.Format("Migrate.Summary.Title", Format.Duration(s.Elapsed));
        Summary.Clear();
        Summary.Add(new SummaryLine(L["Migrate.Summary.SourcePackages"], s.CookedPackages.ToString("N0")));
        Summary.Add(new SummaryLine(L["Migrate.Summary.Importable"], s.ImportableFiles.ToString("N0")));
        Summary.Add(new SummaryLine(L["Migrate.Summary.Imported"], s.ImportedFiles.ToString("N0")));
        if (s.AlreadyPresent > 0)
            Summary.Add(new SummaryLine(L["Migrate.Summary.AlreadyPresent"], s.AlreadyPresent.ToString("N0")));
        Summary.Add(new SummaryLine(L["Migrate.Summary.Failed"], s.FailedFiles.ToString("N0")));
        if (s.SkippedFiles > 0)
            Summary.Add(new SummaryLine(L["Migrate.Summary.Skipped"], s.SkippedFiles.ToString("N0")));
        if (s.UnsupportedActorXFiles > 0)
            Summary.Add(new SummaryLine(L["Migrate.Summary.ActorX"], s.UnsupportedActorXFiles.ToString("N0")));
        Summary.Add(new SummaryLine(L["Migrate.Summary.TargetVersion"], s.TargetVersion));
        Summary.Add(new SummaryLine(L["Migrate.Summary.Destination"], s.DestinationPath));
        OnPropertyChanged(nameof(HasSummary));
    }

    private void Remember()
    {
        _settings.ConversionSourcePath = SourcePath;
        _settings.ConversionSourceVersion = SourceVersion.Version;
        _settings.UModelPath = UModelPath;
        _settings.TargetProjectPath = TargetProjectPath;
        _settings.UnrealEditorPath = UnrealEditorPath;
        _settings.ConversionDestinationPath = DestinationPath;
        _settings.Save();
    }
}
