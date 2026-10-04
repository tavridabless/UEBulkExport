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

public sealed record MigrationDiagnosticViewModel(string Text, bool IsError);

/// <summary>
/// The Migrate page: rebuilds a UE4 or UE5 cooked dump as assets of another Unreal project. A workflow of
/// its own, with its own source, result, tools and run, so it lives apart from the export form.
/// </summary>
public sealed partial class MigrateViewModel : ObservableObject, IDisposable
{
    public const string UEViewerUrl = "https://www.gildor.org/en/projects/umodel";

    private readonly AppSettings _settings;
    private readonly DumpConversionService _service = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Func<DumpConversionOptions, CancellationToken, Task<DumpConversionPreflight>> _inspect;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _confirmationCancellation;
    private CancellationTokenSource? _preflightCancellation;
    private TaskCompletionSource<bool>? _decision;
    private DumpConversionPreflight? _preflight;
    private string? _targetVersion;
    private int _inputRevision;
    private bool _disposed;
    private static Loc L => Loc.Instance;

    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private EngineVersionOption _sourceVersion;
    [ObservableProperty] private string _mappingsPath = "";
    [ObservableProperty] private string _uModelPath = "";
    [ObservableProperty] private string _targetProjectPath = "";
    [ObservableProperty] private string _unrealEditorPath = "";
    [ObservableProperty] private string _destinationPath = "/Game/ConvertedDump";
    [ObservableProperty] private bool _overwrite;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDetecting;
    [ObservableProperty] private bool _isPreviewing;
    [ObservableProperty] private bool _isConfirming;
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
    [ObservableProperty] private string _preflightNote = "";

    public IReadOnlyList<EngineVersionOption> SourceVersions { get; } =
        Enumerable.Range(0, 9).Reverse().Select(minor => new EngineVersionOption($"5.{minor}"))
            .Concat(Enumerable.Range(0, 28).Reverse().Select(minor => new EngineVersionOption($"4.{minor}")))
            .ToArray();

    public ObservableCollection<SummaryLine> Summary { get; } = [];
    public ObservableCollection<SummaryLine> PreflightSummary { get; } = [];
    public ObservableCollection<MigrationDiagnosticViewModel> PreflightDiagnostics { get; } = [];
    public bool HasPreflight => _preflight is not null;
    public bool PreflightCanProceed => _preflight?.CanProceed == true;
    public string PreflightTitle => L[PreflightCanProceed ? "Migrate.Preflight.Ready" : "Migrate.Preflight.Blocked"];

    /// <summary>Hardware load recorded while a migration runs.</summary>
    public PerformanceViewModel Performance { get; } = new();

    /// <summary>Set by the view: asks a yes/no question (title, message) and returns the answer.</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }
    /// <summary>Raised for a completed conversion or an error that needs a decision.</summary>
    public event Action<AppNotification>? NotificationRequested;

    public MigrateViewModel(AppSettings settings, bool autoDetect = true) : this(settings, null, autoDetect) { }

    internal MigrateViewModel(AppSettings settings,
        Func<DumpConversionOptions, CancellationToken, Task<DumpConversionPreflight>>? inspect, bool autoDetect = false)
    {
        _settings = settings;
        _inspect = inspect ?? _service.PreflightAsync;
        _sourceVersion = SourceVersions[0];
        LoadFromSettings();

        Loc.Instance.LanguageChanged += RefreshLanguage;

        // At startup only the tools are looked up. The target project decides where assets are saved,
        // so it is never picked silently: the user chooses it or asks for a suggestion.
        if (autoDetect) Dispatcher.UIThread.Post(() => _ = DetectCore(searchProjects: false));
    }

    public void LoadFromSettings()
    {
        if (_disposed) return;
        SourcePath = _settings.ConversionSourcePath;
        SourceVersion = SourceVersions.FirstOrDefault(v => v.Version == _settings.ConversionSourceVersion)
                        ?? SourceVersions.First(v => v.Version == "4.27");
        MappingsPath = _settings.ConversionUsmapPath;
        UModelPath = _settings.UModelPath;
        TargetProjectPath = _settings.TargetProjectPath;
        UnrealEditorPath = _settings.UnrealEditorPath;
        DestinationPath = string.IsNullOrWhiteSpace(_settings.ConversionDestinationPath)
            ? "/Game/ConvertedDump"
            : _settings.ConversionDestinationPath;
    }

    // ---------------------------------------------------------------- derived state

    private bool UModelFound => File.Exists(UModelPath.Trim());
    private bool MappingsProvided => !string.IsNullOrWhiteSpace(MappingsPath);
    private bool MappingsFound => File.Exists(MappingsPath.Trim());
    private bool EditorFound => File.Exists(UnrealEditorPath.Trim());
    private bool ProjectFound => File.Exists(TargetProjectPath.Trim());

    /// <summary>Cached descriptor/path hint. Path changes and a fresh preflight refresh it.</summary>
    public string? TargetVersion => _targetVersion;

    public string TargetVersionText => TargetVersion is { } v ? $"Unreal Engine {v}" : L["Migrate.Target.Unknown"];
    public bool HasTargetVersion => TargetVersion is not null;

    /// <summary>One line for the collapsed Tools section, so it need not be opened just to check.</summary>
    public bool IsUE5Source => DumpConversionService.IsUE5Source(SourceVersion.Version);

    public string ToolsSummary => L.Format("Migrate.Tools.Summary",
        EditorFound
            ? TargetVersion is { } v ? L.Format("Migrate.Tools.EditorFoundVersion", v) : L["Migrate.Tools.EditorFound"]
            : L["Migrate.Tools.EditorMissing"],
        IsUE5Source
            ? MappingsFound ? L["Migrate.Tools.MappingsFound"]
                : MappingsProvided ? L["Migrate.Tools.MappingsInvalid"] : L["Migrate.Tools.MappingsMissing"]
            : UModelFound ? L["Migrate.Tools.UModelFound"] : L["Migrate.Tools.UModelMissing"]);

    public bool ToolsMissing => !EditorFound ||
                                (IsUE5Source ? MappingsProvided && !MappingsFound : !UModelFound);
    public bool ShowUModelHelp => !IsUE5Source && !UModelFound;

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
            var destination = DestinationPath.Trim().Replace('\\', '/').TrimEnd('/');
            if (!destination.Equals("/Game", StringComparison.OrdinalIgnoreCase) &&
                !destination.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
                return (Readiness.Blocked, L["Migrate.Ready.BadDestination"]);
            if (!EditorFound) return (Readiness.Blocked, L["Migrate.Ready.NoEditor"]);
            if (IsUE5Source && MappingsProvided && !MappingsFound)
                return (Readiness.Blocked, L["Migrate.Ready.BadMappings"]);
            if (!IsUE5Source && !UModelFound) return (Readiness.Blocked, L["Migrate.Ready.NoUModel"]);
            if (_preflight?.Diagnostics.FirstOrDefault(item => item.Level == DumpConversionDiagnosticLevel.Error) is { } error)
                return (Readiness.Blocked, DiagnosticText(error));
            // Overwriting target assets is the more costly surprise, so it wins the single line;
            // missing mappings are still reported in the Tools summary.
            if (Overwrite) return (Readiness.Warning, OverwriteWarning);
            if (IsUE5Source && !MappingsProvided) return (Readiness.Warning, L["Migrate.Ready.NoMappings"]);
            return (Readiness.Ready, L.Format("Migrate.Ready.Ok",
                Path.GetFileNameWithoutExtension(TargetProjectPath.Trim()), DestinationPath.Trim()));
        }
    }

    public string ReadinessText => ReadinessState.Text;
    public bool IsReadinessBlocked => ReadinessState.Level == Readiness.Blocked;
    public bool IsReadinessWarning => ReadinessState.Level == Readiness.Warning;
    public bool IsReadinessOk => ReadinessState.Level == Readiness.Ready;
    public bool ShowReadiness => !IsBusy && !IsPreviewing;

    public bool CanEditInputs => !_disposed && !IsBusy && !IsPreviewing && !IsConfirming;
    public bool CanStart => CanEditInputs && !IsDetecting && ReadinessState.Level != Readiness.Blocked;
    public bool CanDetect => CanEditInputs && !IsDetecting;
    public bool CanPreview => CanEditInputs && !IsDetecting;
    public bool CanCancel => !_disposed && (IsBusy || IsPreviewing || IsConfirming);
    public bool HasError => !string.IsNullOrEmpty(ErrorHeadline);
    public bool HasSummary => Summary.Count > 0;
    public bool HasReport => File.Exists(LastReportPath);
    public bool HasWorkingDirectory => Directory.Exists(LastWorkingDirectory);
    public bool CanOpenContent => ProjectFound && Directory.Exists(ContentDirectory);
    private string ContentDirectory => Path.Combine(Path.GetDirectoryName(TargetProjectPath.Trim()) ?? "", "Content");
    private string ImportLogPath => Path.Combine(LastWorkingDirectory, "unreal-import.log");
    public bool HasImportLog => File.Exists(ImportLogPath);

    public string StatusText => IsPreviewing ? L["Migrate.Preflight.Checking"] : IsBusy
        ? (string.IsNullOrEmpty(Stage) ? L["Migrate.Stage.Preparing"] : Stage)
        : L["Status.Idle"];

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        switch (e.PropertyName)
        {
            case nameof(TargetProjectPath):
            case nameof(UnrealEditorPath):
                RefreshTargetVersion();
                InvalidatePreflight();
                RefreshDerived();
                break;
            case nameof(SourcePath):
            case nameof(SourceVersion):
            case nameof(MappingsPath):
            case nameof(UModelPath):
            case nameof(DestinationPath):
            case nameof(Overwrite):
                InvalidatePreflight();
                RefreshDerived();
                break;
            case nameof(IsBusy):
            case nameof(IsDetecting):
            case nameof(IsPreviewing):
            case nameof(IsConfirming):
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
        if (_disposed) return;
        OnPropertyChanged(nameof(TargetVersion));
        OnPropertyChanged(nameof(TargetVersionText));
        OnPropertyChanged(nameof(HasTargetVersion));
        OnPropertyChanged(nameof(ToolsSummary));
        OnPropertyChanged(nameof(ToolsMissing));
        OnPropertyChanged(nameof(ShowUModelHelp));
        OnPropertyChanged(nameof(IsUE5Source));
        OnPropertyChanged(nameof(OverwriteWarningVisible));
        OnPropertyChanged(nameof(OverwriteWarning));
        OnPropertyChanged(nameof(ReadinessText));
        OnPropertyChanged(nameof(IsReadinessBlocked));
        OnPropertyChanged(nameof(IsReadinessWarning));
        OnPropertyChanged(nameof(IsReadinessOk));
        OnPropertyChanged(nameof(ShowReadiness));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanDetect));
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanEditInputs));
        OnPropertyChanged(nameof(CanOpenContent));
        OnPropertyChanged(nameof(StatusText));
        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        DetectCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged();
        BrowseSourceCommand.NotifyCanExecuteChanged();
        BrowseUModelCommand.NotifyCanExecuteChanged();
        BrowseMappingsCommand.NotifyCanExecuteChanged();
        BrowseTargetProjectCommand.NotifyCanExecuteChanged();
        BrowseUnrealEditorCommand.NotifyCanExecuteChanged();
    }

    private void RefreshLanguage()
    {
        RefreshDerived();
        if (!_disposed && _preflight is not null) ShowPreflight(_preflight);
    }

    private void RefreshTargetVersion()
    {
        var detected = DumpConversionService.DetectTargetVersion(TargetProjectPath.Trim(), UnrealEditorPath.Trim());
        _targetVersion = detected == "unknown" ? null : detected;
    }

    private void InvalidatePreflight()
    {
        _inputRevision++;
        _preflightCancellation?.Cancel();
        if (_preflight is null) return;
        _preflight = null;
        PreflightSummary.Clear();
        PreflightDiagnostics.Clear();
        NotifyPreflightState();
    }

    private void NotifyPreflightState()
    {
        OnPropertyChanged(nameof(HasPreflight));
        OnPropertyChanged(nameof(PreflightCanProceed));
        OnPropertyChanged(nameof(PreflightTitle));
    }

    // ---------------------------------------------------------------- browse

    [RelayCommand(CanExecute = nameof(CanEditInputs))]
    private async Task BrowseSource()
    {
        var path = await DialogService.PickFolderAsync("Dialog.PickDump", SourcePath);
        if (path is not null && CanEditInputs) SourcePath = path;
    }

    [RelayCommand(CanExecute = nameof(CanEditInputs))]
    private async Task BrowseUModel()
    {
        var path = await DialogService.PickExecutableAsync(UModelPath);
        if (path is not null && CanEditInputs) UModelPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanEditInputs))]
    private async Task BrowseMappings()
    {
        var path = await DialogService.PickUsmapAsync(MappingsPath);
        if (path is not null && CanEditInputs) MappingsPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanEditInputs))]
    private async Task BrowseTargetProject()
    {
        var path = await DialogService.PickUnrealProjectAsync(TargetProjectPath);
        if (path is not null && CanEditInputs) TargetProjectPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanEditInputs))]
    private async Task BrowseUnrealEditor()
    {
        var path = await DialogService.PickExecutableAsync(UnrealEditorPath);
        if (path is not null && CanEditInputs) UnrealEditorPath = path;
    }

    [RelayCommand]
    private void OpenUEViewerSite() => ShellHelper.OpenUrl(UEViewerUrl);

    // ---------------------------------------------------------------- detection

    [RelayCommand(CanExecute = nameof(CanDetect))]
    private Task Detect() => DetectCore(searchProjects: true);

    private async Task DetectCore(bool searchProjects)
    {
        if (!CanDetect) return;

        IsDetecting = true;
        DetectionNote = L["Migrate.Detect.Searching"];
        try
        {
            // A field the user edits while the search runs keeps what they typed.
            var (umodelBefore, projectBefore, editorBefore) = (UModelPath, TargetProjectPath, UnrealEditorPath);
            var result = await Task.Run(() => ConversionPathDiscovery.Find(
                umodelBefore, projectBefore, editorBefore, searchProjects));

            if (_disposed) return;

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
            if (_disposed) return;
            DetectionNote = L.Format("Migrate.Detect.Error", e.Message);
            if (searchProjects)
                Notify("Notification.Detect.Failed.Title", e.Message, AppNotificationSeverity.Error);
        }
        finally
        {
            if (!_disposed)
            {
                IsDetecting = false;
                // Nothing to check in a section whose tools are all there; open it when something is missing.
                if (ToolsMissing) ToolsExpanded = true;
            }
        }
    }

    // ---------------------------------------------------------------- read-only plan

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task Preview()
    {
        if (!CanPreview) return;
        var options = BuildOptions();
        var revision = _inputRevision;
        var cancellation = new CancellationTokenSource();
        _preflightCancellation = cancellation;
        IsPreviewing = true;
        PreflightNote = L["Migrate.Preflight.Checking"];
        try
        {
            var result = await _inspect(options, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed || revision != _inputRevision) return;
            _preflight = result;
            _targetVersion = result.TargetVersion == "unknown" ? null : result.TargetVersion;
            PreflightNote = "";
            ShowPreflight(result);
            RefreshDerived();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_disposed) PreflightNote = L["Migrate.Preflight.Cancelled"];
        }
        catch (Exception e)
        {
            if (!_disposed) PreflightNote = L.Format("Migrate.Preflight.Failed", e.Message);
        }
        finally
        {
            if (ReferenceEquals(_preflightCancellation, cancellation)) _preflightCancellation = null;
            cancellation.Dispose();
            if (!_disposed) IsPreviewing = false;
        }
    }

    private static string DiagnosticText(DumpConversionDiagnostic diagnostic) => diagnostic.Detail is { } detail
        ? L.Format("Migrate.Preflight.Diagnostic." + diagnostic.Code, detail)
        : L["Migrate.Preflight.Diagnostic." + diagnostic.Code];

    private void ShowPreflight(DumpConversionPreflight plan)
    {
        PreflightSummary.Clear();
        PreflightSummary.Add(new(L["Migrate.Preflight.Packages"], plan.CookedPackages.ToString("N0")));
        PreflightSummary.Add(new(L["Migrate.Preflight.Maps"], plan.MapPackages.ToString("N0")));
        PreflightSummary.Add(new(L["Migrate.Preflight.Payloads"], plan.PayloadFiles.ToString("N0")));
        PreflightSummary.Add(new(L["Migrate.Preflight.EmptyPayloads"], plan.EmptyPayloadFiles.ToString("N0")));
        PreflightSummary.Add(new(L["Migrate.Preflight.Interchange"], plan.ImportableFiles.ToString("N0")));
        PreflightSummary.Add(new(L["Migrate.Preflight.SourceBytes"], Format.Bytes(plan.SourceBytes)));
        PreflightSummary.Add(new(L["Migrate.Summary.TargetVersion"], plan.TargetVersion == "unknown" ? L["Migrate.Target.Unknown"] : plan.TargetVersion));
        PreflightSummary.Add(new(L["Migrate.Summary.Destination"], plan.DestinationPath));
        if (plan.WorkingDirectory.Length > 0)
            PreflightSummary.Add(new(L["Migrate.Preflight.WorkingDirectory"], plan.WorkingDirectory));
        PreflightDiagnostics.Clear();
        foreach (var diagnostic in plan.Diagnostics)
            PreflightDiagnostics.Add(new(DiagnosticText(diagnostic), diagnostic.Level == DumpConversionDiagnosticLevel.Error));
        NotifyPreflightState();
    }

    private DumpConversionOptions BuildOptions() => new(
        SourcePath.Trim(), SourceVersion.Version, UModelPath.Trim(), TargetProjectPath.Trim(), UnrealEditorPath.Trim(),
        DestinationPath.Trim(), Overwrite: Overwrite, MappingsPath: IsUE5Source ? MappingsPath.Trim() : null,
        VgmStreamPath: IsUE5Source ? _settings.VgmStreamPath : null);

    // ---------------------------------------------------------------- run

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        if (!CanStart) return;
        // Reserve this request before awaiting the dialog, and reject changed inputs. The approved
        // project/destination must be the exact snapshot passed to the service.
        var options = BuildOptions();
        var revision = _inputRevision;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _confirmationCancellation = cancellation;
        var approved = false;
        IsConfirming = true;
        try
        {
            var accepted = Confirm is null || await Confirm(L["Migrate.Confirm.Title"],
                L.Format("Migrate.Confirm.Message", options.TargetProject, options.DestinationPath))
                .WaitAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (accepted && !_disposed && revision == _inputRevision)
            {
                // Transfer the same token before the busy notification. A close/cancel triggered
                // by that notification cannot slip between confirmation and the actual run.
                _cancellation = cancellation;
                IsBusy = true;
                cancellation.Token.ThrowIfCancellationRequested();
                approved = true;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (!_disposed)
            {
                ErrorHeadline = L["Error.Unexpected"];
                ErrorHint = e.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_confirmationCancellation, cancellation)) _confirmationCancellation = null;
            if (!approved)
            {
                if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
                cancellation.Dispose();
                if (!_disposed) IsBusy = false;
            }
            if (!_disposed) IsConfirming = false;
        }
        if (!approved || _disposed)
        {
            if (approved)
            {
                if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
                cancellation.Dispose();
            }
            return;
        }

        try
        {
            ClearOutcome();
            Progress = 0;
            Stage = L["Migrate.Stage.Preparing"];
            Remember();
            Performance.Start();
            var progress = new Progress<DumpConversionProgress>(p =>
            {
                if (_disposed || cancellation.IsCancellationRequested || !ReferenceEquals(_cancellation, cancellation)) return;
                Progress = p.Fraction;
                Stage = L[$"Migrate.Stage.{p.Stage}"];
            });
            var token = cancellation.Token;
            // The service scans and links large dumps; keep that work off the UI thread. Progress
            // and the Continue prompt marshal back on their own.
            var summary = await Task.Run(() => _service.RunAsync(options, progress, WaitForDecision, token), token);
            token.ThrowIfCancellationRequested();
            if (_disposed) return;

            LastWorkingDirectory = summary.WorkingDirectory;
            LastReportPath = summary.ErrorReportPath ?? "";
            ShowSummary(summary);
            var unavailable = summary.FailedFiles + summary.SkippedFiles;
            if (unavailable > 0)
                Notify("Notification.Migrate.Errors.Title",
                    L.Format("Notification.Migrate.Errors.Body", summary.ImportedFiles, unavailable),
                    AppNotificationSeverity.Warning);
            else
                Notify("Notification.Migrate.Done.Title",
                    L.Format("Notification.Migrate.Done.Body", summary.ImportedFiles));
        }
        catch (OperationCanceledException)
        {
            if (_disposed) return;
            SummaryTitle = "";
            ErrorHeadline = L["Migrate.Stage.Cancelled"];
            ErrorHint = "";
            Notify("Notification.Migrate.Cancelled.Title", L["Notification.Migrate.Cancelled.Body"]);
        }
        catch (UserFacingException u)
        {
            if (_disposed) return;
            ErrorHeadline = u.Headline;
            ErrorHint = u.Hint ?? "";
            Log.Problem(u.Headline, u.Hint);
            Notify("Notification.Migrate.Failed.Title", ErrorNotificationText(), AppNotificationSeverity.Error);
        }
        catch (Exception e)
        {
            if (_disposed) return;
            ErrorHeadline = L["Error.Unexpected"];
            ErrorHint = e.Message;
            Log.Error(e.ToString());
            Notify("Notification.Migrate.Failed.Title", ErrorNotificationText(), AppNotificationSeverity.Error);
        }
        finally
        {
            Performance.Stop();
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            cancellation.Dispose();
            _decision?.TrySetResult(false);
            _decision = null;
            if (!_disposed)
            {
                HasPendingIssue = false;
                Stage = "";
                IsBusy = false;
                OnPropertyChanged(nameof(CanOpenContent));
            }
        }
    }

    private async Task<bool> WaitForDecision(DumpConversionIssue issue, CancellationToken ct)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed || ct.IsCancellationRequested)
            {
                decision.TrySetResult(false);
                return;
            }
            _decision = decision;
            ErrorHeadline = issue.Headline;
            ErrorHint = $"{issue.SourceFile}{Environment.NewLine}{issue.Details}";
            HasPendingIssue = true;
            Stage = L["Migrate.Error.Waiting"];
            Notify("Notification.Migrate.Attention.Title", issue.Headline,
                AppNotificationSeverity.Warning);
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

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (IsConfirming)
        {
            _confirmationCancellation?.Cancel();
            return;
        }
        if (IsPreviewing)
        {
            _preflightCancellation?.Cancel();
            return;
        }
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

    private void Notify(string titleKey, string message,
        AppNotificationSeverity severity = AppNotificationSeverity.Information)
    {
        if (!_disposed) NotificationRequested?.Invoke(new AppNotification(L[titleKey], message, "migrate", severity));
    }

    private string ErrorNotificationText() => string.IsNullOrWhiteSpace(ErrorHint)
        ? ErrorHeadline
        : $"{ErrorHeadline}: {ErrorHint}";

    private void Remember()
    {
        _settings.ConversionSourcePath = SourcePath;
        _settings.ConversionSourceVersion = SourceVersion.Version;
        _settings.ConversionUsmapPath = MappingsPath;
        _settings.UModelPath = UModelPath;
        _settings.TargetProjectPath = TargetProjectPath;
        _settings.UnrealEditorPath = UnrealEditorPath;
        _settings.ConversionDestinationPath = DestinationPath;
        _settings.Save();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        Loc.Instance.LanguageChanged -= RefreshLanguage;
        _preflightCancellation?.Cancel();
        _confirmationCancellation?.Cancel();
        _cancellation?.Cancel();
        _decision?.TrySetResult(false);
        _service.Cancel();
        Performance.Dispose();
        StartCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged();
        DetectCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }
}
