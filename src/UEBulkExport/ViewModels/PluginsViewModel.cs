using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.Services;

namespace UEBulkExport.Gui.ViewModels;

public sealed partial class PluginItemViewModel : ObservableObject, IDisposable
{
    private readonly PluginCatalog _catalog;
    private bool _ready;

    public PluginDescriptor Descriptor { get; }
    public string Name => Descriptor.Manifest.Name;
    public string Description => Descriptor.Manifest.Description;
    public string Version => Descriptor.Manifest.Version;
    public string Author => Descriptor.Manifest.Author;
    public string Directory => Descriptor.Directory;
    public bool HasHomepage => !string.IsNullOrWhiteSpace(Descriptor.Manifest.Homepage);
    public bool CanToggle => Descriptor.IsValid || IsEnabled;
    public bool HasError => Descriptor.ValidationError is not null || Descriptor.LoadError is not null;
    public bool IsLoaded => Descriptor.IsLoaded;
    public bool NeedsRestart => IsEnabled != Descriptor.WasEnabledAtStartup;
    public bool HasWarning => NeedsRestart && !HasError;
    public bool IsSuccess => IsLoaded && !HasError && !HasWarning;
    public Bitmap? Icon { get; }

    public string StatusText => Descriptor.ValidationError is { } validation
        ? Loc.Instance.Format("Plugins.Status.Invalid", validation)
        : Descriptor.LoadError is { } load
            ? Loc.Instance.Format("Plugins.Status.Failed", load)
            : NeedsRestart
                ? Loc.Instance["Plugins.Status.Restart"]
                : IsLoaded
                    ? Loc.Instance[Descriptor.ShowsPage ? "Plugins.Status.Loaded" : "Plugins.Status.LoadedFeature"]
                    : Loc.Instance["Plugins.Status.Disabled"];

    [ObservableProperty] private bool _isEnabled;

    public event Action? EnableChanged;

    public PluginItemViewModel(PluginCatalog catalog, PluginDescriptor descriptor)
    {
        _catalog = catalog;
        Descriptor = descriptor;
        _isEnabled = descriptor.IsEnabled;

        if (!string.IsNullOrWhiteSpace(descriptor.Manifest.Icon))
        {
            try { Icon = new Bitmap(Path.Combine(descriptor.Directory, descriptor.Manifest.Icon)); }
            catch (Exception e)
            {
                Log.Warn($"plugin '{descriptor.Manifest.Id}' icon could not be opened: {e.Message}");
            }
        }

        _ready = true;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_ready) return;
        _catalog.SetEnabled(Descriptor, value);
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(NeedsRestart));
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(IsSuccess));
        OnPropertyChanged(nameof(StatusText));
        EnableChanged?.Invoke();
    }

    public void SetEnabledFromSettings(bool value)
    {
        _ready = false;
        IsEnabled = value;
        Descriptor.IsEnabled = value;
        _ready = true;
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(NeedsRestart));
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(IsSuccess));
        OnPropertyChanged(nameof(StatusText));
    }

    [RelayCommand]
    private void OpenFolder() => ShellHelper.OpenFolder(Directory);

    [RelayCommand]
    private void OpenHomepage()
    {
        if (HasHomepage) ShellHelper.OpenUrl(Descriptor.Manifest.Homepage);
    }

    public void RefreshLanguage() => OnPropertyChanged(nameof(StatusText));
    public void Dispose() => Icon?.Dispose();
}

public sealed class PluginsViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    public PluginCatalog Catalog { get; }
    public ObservableCollection<PluginItemViewModel> Items { get; }
    public string ApplicationFolder => PluginCatalog.ApplicationPluginsDirectory;
    public string UserFolder => PluginCatalog.UserPluginsDirectory;
    public bool HasPlugins => Items.Count > 0;
    public bool RestartRequired => Items.Any(item => item.NeedsRestart);

    public IRelayCommand OpenUserFolderCommand { get; }
    public IRelayCommand OpenApplicationFolderCommand { get; }
    public IRelayCommand RestartCommand { get; }

    public event Action? RestartRequested;

    public PluginsViewModel(AppSettings settings)
    {
        _settings = settings;
        Catalog = new PluginCatalog(settings);
        Items = new ObservableCollection<PluginItemViewModel>(Catalog.Plugins.Select(plugin =>
        {
            var item = new PluginItemViewModel(Catalog, plugin);
            item.EnableChanged += OnEnableChanged;
            return item;
        }));

        OpenUserFolderCommand = new RelayCommand(OpenUserFolder);
        OpenApplicationFolderCommand = new RelayCommand(() => OpenOrCreateFolder(ApplicationFolder));
        RestartCommand = new RelayCommand(() => RestartRequested?.Invoke());
        Loc.Instance.LanguageChanged += RefreshLanguage;
    }

    private void OpenUserFolder() => OpenOrCreateFolder(UserFolder);

    private static void OpenOrCreateFolder(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            ShellHelper.OpenFolder(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"could not create plugin folder '{path}': {e.Message}");
        }
    }

    public void SyncFromSettings()
    {
        var enabled = _settings.EnabledPlugins.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items) item.SetEnabledFromSettings(enabled.Contains(item.Descriptor.Manifest.Id));
        OnPropertyChanged(nameof(RestartRequired));
    }

    private void OnEnableChanged()
    {
        OnPropertyChanged(nameof(RestartRequired));
    }

    private void RefreshLanguage()
    {
        foreach (var item in Items) item.RefreshLanguage();
        OnPropertyChanged(nameof(RestartRequired));
    }

    public void Dispose()
    {
        Loc.Instance.LanguageChanged -= RefreshLanguage;
        foreach (var item in Items) item.Dispose();
    }
}
