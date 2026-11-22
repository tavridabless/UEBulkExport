using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.Services;

namespace UEBulkExport.Gui.ViewModels;

public sealed record NavItem(string Key, string LabelKey, string IconData, string? LiteralLabel = null)
{
    public string Label => LiteralLabel ?? Loc.Instance[LabelKey];
    public override string ToString() => Label;

    public Geometry Icon
    {
        get
        {
            try { return Geometry.Parse(IconData); }
            catch (Exception) { return Geometry.Parse(Icons.Plugins); }
        }
    }
}

/// <summary>The shell: navigation rail, the pages, and the status bar.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public ExportViewModel Export { get; }
    public MigrateViewModel Migrate { get; }
    public BrowserViewModel Browser { get; } = new();
    public CompareViewModel Compare { get; }
    public BlueprintViewModel Blueprint { get; }
    public LogViewModel Log { get; }
    public PluginsViewModel Plugins { get; }
    public SettingsViewModel Settings { get; }
    public AboutViewModel About { get; } = new();

    public AppSettings AppSettings { get; }

    public IReadOnlyList<NavItem> Pages { get; }

    [ObservableProperty] private NavItem _selectedPage;

    public string Version => $"v{Cli.Version}";
    // Export and migration run independently; the status line follows whichever is working.
    public string StatusText => Export.IsRunning ? Export.StatusText
        : Migrate.IsBusy || Migrate.IsPreviewing ? Migrate.StatusText
        : Compare.IsBusy ? Compare.StatusText : Blueprint.IsBusy ? Blueprint.StatusText : Export.StatusText;
    public bool IsBusy => Export.IsBusy || Migrate.IsBusy || Migrate.IsPreviewing || Migrate.IsConfirming ||
        Compare.IsBusy || Blueprint.IsBusy;
    public double ProgressFraction => Export.IsRunning ? Export.ProgressFraction
        : Migrate.IsBusy ? Migrate.Progress : Compare.IsBusy ? Compare.Progress : Export.ProgressFraction;
    public bool ShowProgress => Export.ShowProgress || Migrate.IsBusy || Compare.IsBusy;

    /// <summary>Stops whatever is running, before the window closes or restarts.</summary>
    public void CancelAll()
    {
        if (Export.CancelCommand.CanExecute(null)) Export.CancelCommand.Execute(null);
        if (Migrate.CancelCommand.CanExecute(null)) Migrate.CancelCommand.Execute(null);
        if (Compare.CancelCommand.CanExecute(null)) Compare.CancelCommand.Execute(null);
        if (Blueprint.CancelCommand.CanExecute(null)) Blueprint.CancelCommand.Execute(null);
    }

    /// <summary>
    /// Lets exporters flush their checkpoints before the last window ends the process. Bounded, so
    /// a hung tool cannot keep the window open; every checkpoint is already on disk when written.
    /// </summary>
    public async Task CancelAllAndWaitAsync()
    {
        CancelAll();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (IsBusy && DateTime.UtcNow < deadline) await Task.Delay(25);
    }

    public MainViewModel(AppSettings settings, IAppNotificationService notifications)
    {
        AppSettings = settings;
        Export = new ExportViewModel(settings);
        Migrate = new MigrateViewModel(settings);
        Compare = new CompareViewModel(settings, Export.Games, () => Export.CanRun);
        Blueprint = new BlueprintViewModel(Export);
        Log = new LogViewModel(App.LogSink);
        Plugins = new PluginsViewModel(settings);
        Settings = new SettingsViewModel(settings, notifications);
        var pages = new List<NavItem>
        {
            new("export", "Nav.Export", Icons.Export),
            new("migrate", "Nav.Migrate", Icons.Migrate),
            new("browser", "Nav.Browser", Icons.Browser),
            new("compare", "Nav.Compare", Icons.Compare),
            new("blueprint", "Nav.Blueprint", Icons.Blueprint),
            new("log", "Nav.Log", Icons.Log)
        };
        pages.AddRange(Plugins.Catalog.Plugins.Where(plugin => plugin.IsLoaded).Select(plugin =>
            new NavItem(plugin.PageKey, "", plugin.NavigationIcon, plugin.NavigationLabel)));
        pages.Add(new NavItem("plugins", "Nav.Plugins", Icons.Plugins));
        pages.Add(new NavItem("settings", "Nav.Settings", Icons.Settings));
        pages.Add(new NavItem("about", "Nav.About", Icons.About));
        Pages = pages;
        _selectedPage = Pages[0];

        Export.Scanned += Browser.Load;
        Export.SourceChanged += Browser.Clear;
        Export.NotificationRequested += notification =>
        {
            if (AppSettings.NotificationsEnabled) notifications.Show(notification);
        };
        Migrate.NotificationRequested += notification =>
        {
            if (AppSettings.NotificationsEnabled) notifications.Show(notification);
        };
        Browser.Export = Export;
        Browser.ExportReviewRequested += ReviewBrowserExport;
        Compare.ReviewExportRequested += (result, source, paths) =>
        {
            if (Export.PrepareComparedExport(result, source, paths)) Navigate("export");
        };
        Browser.ExportRequested += paths =>
        {
            ReviewBrowserExport(paths);
            if (Export.StartCommand.CanExecute(null)) Export.StartCommand.Execute(null);
        };
        Settings.SettingsReset += Export.LoadFromSettings;
        Settings.SettingsReset += Migrate.LoadFromSettings;
        Settings.SettingsReset += Plugins.SyncFromSettings;

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
                case nameof(ExportViewModel.IsBusy):
                    OnPropertyChanged(nameof(IsBusy));
                    Compare.RefreshExportAvailability();
                    break;
                case nameof(ExportViewModel.ProgressFraction):
                    OnPropertyChanged(nameof(ProgressFraction));
                    break;
            }
        };

        Migrate.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MigrateViewModel.StatusText):
                case nameof(MigrateViewModel.IsBusy):
                case nameof(MigrateViewModel.IsPreviewing):
                case nameof(MigrateViewModel.IsConfirming):
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(IsBusy));
                    OnPropertyChanged(nameof(ShowProgress));
                    OnPropertyChanged(nameof(ProgressFraction));
                    break;
                case nameof(MigrateViewModel.Progress):
                    OnPropertyChanged(nameof(ProgressFraction));
                    break;
            }
        };

        Compare.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(CompareViewModel.IsBusy):
                    OnPropertyChanged(nameof(IsBusy));
                    OnPropertyChanged(nameof(ShowProgress));
                    OnPropertyChanged(nameof(ProgressFraction));
                    OnPropertyChanged(nameof(StatusText));
                    break;
                case nameof(CompareViewModel.Progress):
                    OnPropertyChanged(nameof(ProgressFraction));
                    break;
            }
        };
        Blueprint.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BlueprintViewModel.IsBusy)) OnPropertyChanged(nameof(IsBusy));
            if (e.PropertyName is nameof(BlueprintViewModel.IsBusy) or nameof(BlueprintViewModel.StatusText))
                OnPropertyChanged(nameof(StatusText));
        };

        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public void Navigate(string key) => SelectedPage = Pages.First(p => p.Key == key);

    private void ReviewBrowserExport(IReadOnlySet<string>? paths)
    {
        Export.SetSelection(paths);
        Navigate("export");
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(Pages));
        OnPropertyChanged(nameof(StatusText));
    }

    public void Dispose()
    {
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        Export.Dispose();
        Migrate.Dispose();
        Browser.Dispose();
        Compare.Dispose();
        Blueprint.Dispose();
        Log.Dispose();
        Plugins.Dispose();
    }
}

/// <summary>Simple 24x24 outline glyphs drawn for this application.</summary>
internal static class Icons
{
    public const string Export =
        "M12 3 L12 14 M7 9 L12 14 L17 9 M4 15 L4 19 A2 2 0 0 0 6 21 L18 21 A2 2 0 0 0 20 19 L20 15";

    public const string Migrate =
        "M4 7 L10 4 L16 7 L16 13 L10 16 L4 13 Z M16 10 L21 10 M18 7 L21 10 L18 13 M4 7 L10 10 L16 7 M10 10 L10 16";

    public const string Browser =
        "M3 6 A2 2 0 0 1 5 4 L9 4 L11 6 L19 6 A2 2 0 0 1 21 8 L21 18 A2 2 0 0 1 19 20 L5 20 A2 2 0 0 1 3 18 Z M3 10 L21 10";

    public const string Compare = "M4 7 L20 7 M16 3 L20 7 L16 11 M20 17 L4 17 M8 13 L4 17 L8 21";

    public const string Blueprint = "M2 3 L9 3 L9 9 L2 9 Z M15 15 L22 15 L22 21 L15 21 Z M9 6 L13 6 L13 18 L15 18";

    public const string Log =
        "M6 3 L15 3 L20 8 L20 21 L6 21 Z M15 3 L15 8 L20 8 M9 12 L17 12 M9 16 L17 16";

    public const string Settings =
        "M12 8 A4 4 0 1 0 12 16 A4 4 0 1 0 12 8 M12 2 L12 5 M12 19 L12 22 M2 12 L5 12 M19 12 L22 12 " +
        "M4.9 4.9 L7 7 M17 17 L19.1 19.1 M4.9 19.1 L7 17 M17 7 L19.1 4.9";

    public const string Plugins =
        "M8 3 L16 3 L16 7 L20 7 L20 15 L16 15 L16 21 L8 21 L8 17 L4 17 L4 9 L8 9 Z " +
        "M8 9 L12 9 L12 5 M16 15 L12 15 L12 19";

    public const string About =
        "M12 3 A9 9 0 1 0 12 21 A9 9 0 1 0 12 3 M12 11 L12 16 M12 8 L12 8.01";

    public const string Logo =
        "M4 7 L12 3 L20 7 L20 17 L12 21 L4 17 Z M4 7 L12 11 L20 7 M12 11 L12 21";
}
