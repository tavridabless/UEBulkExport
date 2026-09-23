using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.Services;

namespace UEBulkExport.Gui.ViewModels;

public sealed record NavItem(string Key, string LabelKey, string IconData)
{
    public string Label => Loc.Instance[LabelKey];
    public Geometry Icon => Geometry.Parse(IconData);
}

/// <summary>The shell: navigation rail, the five pages, and the status bar.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public ExportViewModel Export { get; }
    public BrowserViewModel Browser { get; } = new();
    public LogViewModel Log { get; }
    public SettingsViewModel Settings { get; }
    public AboutViewModel About { get; } = new();

    public AppSettings AppSettings { get; }

    public IReadOnlyList<NavItem> Pages { get; } =
    [
        new("export", "Nav.Export", Icons.Export),
        new("browser", "Nav.Browser", Icons.Browser),
        new("log", "Nav.Log", Icons.Log),
        new("settings", "Nav.Settings", Icons.Settings),
        new("about", "Nav.About", Icons.About)
    ];

    [ObservableProperty] private NavItem _selectedPage;

    public string Version => $"v{Cli.Version}";
    public string StatusText => Export.StatusText;
    public bool IsBusy => Export.IsBusy;
    public double ProgressFraction => Export.ProgressFraction;
    public bool ShowProgress => Export.ShowProgress;

    public MainViewModel(AppSettings settings)
    {
        AppSettings = settings;
        Export = new ExportViewModel(settings);
        Log = new LogViewModel(App.LogSink);
        Settings = new SettingsViewModel(settings);
        _selectedPage = Pages[0];

        Export.Scanned += Browser.Load;
        Browser.Export = Export;
        Browser.ExportRequested += paths =>
        {
            Export.SetSelection(paths);
            Navigate("export");
            if (Export.StartCommand.CanExecute(null)) Export.StartCommand.Execute(null);
        };
        Settings.SettingsReset += Export.LoadFromSettings;

        Export.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ExportViewModel.StatusText):
                case nameof(ExportViewModel.State):
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(IsBusy));
                    OnPropertyChanged(nameof(ShowProgress));
                    break;
                case nameof(ExportViewModel.ProgressFraction):
                    OnPropertyChanged(nameof(ProgressFraction));
                    break;
            }
        };

        Loc.Instance.LanguageChanged += () =>
        {
            OnPropertyChanged(nameof(Pages));
            OnPropertyChanged(nameof(StatusText));
        };
    }

    public void Navigate(string key) => SelectedPage = Pages.First(p => p.Key == key);
}

/// <summary>Simple 24x24 outline glyphs drawn for this application.</summary>
internal static class Icons
{
    public const string Export =
        "M12 3 L12 14 M7 9 L12 14 L17 9 M4 15 L4 19 A2 2 0 0 0 6 21 L18 21 A2 2 0 0 0 20 19 L20 15";

    public const string Browser =
        "M3 6 A2 2 0 0 1 5 4 L9 4 L11 6 L19 6 A2 2 0 0 1 21 8 L21 18 A2 2 0 0 1 19 20 L5 20 A2 2 0 0 1 3 18 Z M3 10 L21 10";

    public const string Log =
        "M6 3 L15 3 L20 8 L20 21 L6 21 Z M15 3 L15 8 L20 8 M9 12 L17 12 M9 16 L17 16";

    public const string Settings =
        "M12 8 A4 4 0 1 0 12 16 A4 4 0 1 0 12 8 M12 2 L12 5 M12 19 L12 22 M2 12 L5 12 M19 12 L22 12 " +
        "M4.9 4.9 L7 7 M17 17 L19.1 19.1 M4.9 19.1 L7 17 M17 7 L19.1 4.9";

    public const string About =
        "M12 3 A9 9 0 1 0 12 21 A9 9 0 1 0 12 3 M12 11 L12 16 M12 8 L12 8.01";

    public const string Logo =
        "M4 7 L12 3 L20 7 L20 17 L12 21 L4 17 Z M4 7 L12 11 L20 7 M12 11 L12 21";
}
