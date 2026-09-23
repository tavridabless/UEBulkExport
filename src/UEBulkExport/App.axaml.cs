using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.Services;
using UEBulkExport.Gui.ViewModels;
using UEBulkExport.Gui.Views;

namespace UEBulkExport.Gui;

public sealed class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static UiLogSink LogSink { get; } = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Settings = AppSettings.Load();
        // English and the light theme are the defaults; the OS language is deliberately not consulted.
        Loc.Instance.Language = string.IsNullOrEmpty(Settings.Language) ? Loc.DefaultLanguage : Settings.Language;
        ApplyTheme(Settings.Theme);

        Log.AddSink(LogSink);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = new MainViewModel(Settings);
            desktop.MainWindow = new MainWindow { DataContext = shell };
            desktop.ShutdownRequested += (_, _) => Settings.Save();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(string theme)
    {
        if (Current is null) return;

        Current.RequestedThemeVariant = theme switch
        {
            "Dark" => ThemeVariant.Dark,
            "System" => ThemeVariant.Default,
            _ => ThemeVariant.Light
        };
    }
}
