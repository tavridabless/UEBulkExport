using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.Services;

namespace UEBulkExport.Gui.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly string _startupLanguage;

    public IReadOnlyList<LanguageInfo> Languages => Loc.Languages;
    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new("Light", "Theme.Light"),
        new("Dark", "Theme.Dark"),
        new("System", "Theme.System")
    ];

    [ObservableProperty] private LanguageInfo _language;
    [ObservableProperty] private bool _restartRequired;
    [ObservableProperty] private ThemeOption _theme;
    [ObservableProperty] private bool _rememberPaths;
    [ObservableProperty] private bool _confirmClose;
    [ObservableProperty] private bool _transparencyEnabled;
    [ObservableProperty] private int _defaultThreads;
    [ObservableProperty] private string _retocPath;
    [ObservableProperty] private string _oodlePath;
    [ObservableProperty] private string _zlibPath;
    [ObservableProperty] private string _vgmStreamPath;

    public string SettingsFile => AppSettings.FilePath;
    public int MaxThreads => Math.Max(1, Environment.ProcessorCount * 2);

    /// <summary>Raised after a reset so the export form can re-read its defaults.</summary>
    public event Action? SettingsReset;
    /// <summary>Handled by the main window, which owns shutdown and running-export confirmation.</summary>
    public event Action? RestartRequested;

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        _startupLanguage = Loc.Instance.Language;
        _language = Languages.FirstOrDefault(l => l.Code == Loc.Instance.Language) ?? Languages[0];
        _theme = Themes.FirstOrDefault(t => t.Code == settings.Theme) ?? Themes[0];
        _rememberPaths = settings.RememberPaths;
        _confirmClose = settings.ConfirmCloseWhileRunning;
        _transparencyEnabled = settings.TransparencyEnabled;
        _defaultThreads = settings.DefaultThreads;
        _retocPath = settings.RetocPath;
        _oodlePath = settings.OodlePath;
        _zlibPath = settings.ZlibPath;
        _vgmStreamPath = settings.VgmStreamPath;
    }

    partial void OnLanguageChanged(LanguageInfo value)
    {
        _settings.Language = value.Code;
        _settings.Save();
        RestartRequired = value.Code != _startupLanguage;
    }

    partial void OnThemeChanged(ThemeOption value)
    {
        App.ApplyTheme(value.Code);
        _settings.Theme = value.Code;
        _settings.Save();
    }

    partial void OnRememberPathsChanged(bool value) { _settings.RememberPaths = value; _settings.Save(); }
    partial void OnConfirmCloseChanged(bool value) { _settings.ConfirmCloseWhileRunning = value; _settings.Save(); }
    partial void OnTransparencyEnabledChanged(bool value) { _settings.TransparencyEnabled = value; _settings.Save(); }
    partial void OnDefaultThreadsChanged(int value) { _settings.DefaultThreads = Math.Max(1, value); _settings.Save(); }
    partial void OnRetocPathChanged(string value) { _settings.RetocPath = value; _settings.Save(); }
    partial void OnOodlePathChanged(string value) { _settings.OodlePath = value; _settings.Save(); }
    partial void OnZlibPathChanged(string value) { _settings.ZlibPath = value; _settings.Save(); }
    partial void OnVgmStreamPathChanged(string value) { _settings.VgmStreamPath = value; _settings.Save(); }

    [RelayCommand]
    private async Task BrowseRetoc() => RetocPath = await DialogService.PickExecutableAsync(RetocPath) ?? RetocPath;

    [RelayCommand]
    private async Task BrowseOodle() => OodlePath = await DialogService.PickLibraryAsync(OodlePath) ?? OodlePath;

    [RelayCommand]
    private async Task BrowseZlib() => ZlibPath = await DialogService.PickLibraryAsync(ZlibPath) ?? ZlibPath;

    [RelayCommand]
    private async Task BrowseVgmStream() => VgmStreamPath = await DialogService.PickExecutableAsync(VgmStreamPath) ?? VgmStreamPath;

    [RelayCommand]
    private void OpenSettingsFolder() => ShellHelper.OpenFolder(Path.GetDirectoryName(AppSettings.FilePath)!);

    [RelayCommand]
    private void Restart() => RestartRequested?.Invoke();

    [RelayCommand]
    private void Reset()
    {
        var fresh = new AppSettings();
        _settings.Language = "";
        _settings.Theme = fresh.Theme;
        _settings.RememberPaths = fresh.RememberPaths;
        _settings.TransparencyEnabled = fresh.TransparencyEnabled;
        _settings.ConfirmCloseWhileRunning = fresh.ConfirmCloseWhileRunning;
        _settings.DefaultThreads = fresh.DefaultThreads;
        _settings.LastPaksPath = "";
        _settings.LastOutputPath = "";
        _settings.LastGame = "";
        _settings.RetocPath = _settings.OodlePath = _settings.ZlibPath = _settings.VgmStreamPath = "";
        _settings.Recent.Clear();
        _settings.Save();

        Language = Languages.First(l => l.Code == Loc.DefaultLanguage);
        Theme = Themes[0];
        RememberPaths = fresh.RememberPaths;
        ConfirmClose = fresh.ConfirmCloseWhileRunning;
        TransparencyEnabled = fresh.TransparencyEnabled;
        DefaultThreads = fresh.DefaultThreads;
        RetocPath = OodlePath = ZlibPath = VgmStreamPath = "";

        SettingsReset?.Invoke();
    }
}

public sealed record ThemeOption(string Code, string LabelKey)
{
    public string Label => Loc.Instance[LabelKey];
    public override string ToString() => Label;
}
