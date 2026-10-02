using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
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
        // The user's own choice wins, then the language picked in the installer, then English. The
        // OS language is deliberately not consulted.
        Loc.Instance.Language = !string.IsNullOrEmpty(Settings.Language) ? Settings.Language
            : AppSettings.InstallerLanguage() ?? Loc.DefaultLanguage;
        ApplyTheme(Settings.Theme);

        Log.AddSink(LogSink);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var notifications = new AppNotificationService();
            var shell = new MainViewModel(Settings, notifications);
            var window = new MainWindow { DataContext = shell };
            desktop.MainWindow = window;
            notifications.Activated += page =>
                Dispatcher.UIThread.Post(() => window.ActivateFromNotification(page));
            desktop.ShutdownRequested += (_, _) =>
            {
                Settings.Save();
                notifications.Dispose();
                shell.Dispose();
            };
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
