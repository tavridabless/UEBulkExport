using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.Services;
using UEBulkExport.Gui.ViewModels;

namespace UEBulkExport.Gui.Views;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, Control> _pages = new();
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Bind();
        Opened += (_, _) => DialogService.Owner = this;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ActualTransparencyLevelProperty) UpdateBackdrop();
        };

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private MainViewModel? Shell => DataContext as MainViewModel;

    private void Bind()
    {
        if (Shell is not { } shell) return;

        Width = Math.Max(MinWidth, shell.AppSettings.WindowWidth);
        Height = Math.Max(MinHeight, shell.AppSettings.WindowHeight);

        _pages["export"] = new ExportView { DataContext = shell.Export };
        _pages["browser"] = new BrowserView { DataContext = shell.Browser };
        _pages["log"] = new LogView { DataContext = shell.Log };
        _pages["settings"] = new SettingsView { DataContext = shell.Settings };
        _pages["about"] = new AboutView { DataContext = shell.About };

        shell.Export.CopyToClipboard = async text =>
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        };
        shell.Settings.RestartRequested += OnRestartRequested;
        shell.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.TransparencyEnabled)) ApplyTransparency();
        };
        ApplyTransparency();

        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedPage)) ShowPage(shell.SelectedPage.Key);
        };
        ShowPage(shell.SelectedPage.Key);
    }

    // ------------------------------------------------------------------ glass

    /// <summary>Asks the system for blur behind the window, or for none when the user turned it off.</summary>
    private void ApplyTransparency()
    {
        TransparencyLevelHint = App.Settings.TransparencyEnabled
            ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Mica, WindowTransparencyLevel.None]
            : [WindowTransparencyLevel.None];
        UpdateBackdrop();
    }

    /// <summary>
    /// The tint is translucent only while the system actually blurs what is behind the window:
    /// without blur (Windows without composition, remote sessions, transparency off) the desktop
    /// would show through unblurred, so the opaque twin is used instead.
    /// </summary>
    private void UpdateBackdrop()
    {
        var blurred = ActualTransparencyLevel != WindowTransparencyLevel.None
                      && ActualTransparencyLevel != WindowTransparencyLevel.Transparent;
        Backdrop[!Border.BackgroundProperty] = this.GetResourceObservable(
            blurred ? "Brush.Backdrop" : "Brush.BackdropOpaque").ToBinding();
    }

    // ------------------------------------------------------------------ title bar

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1) BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void ShowPage(string key)
    {
        if (_pages.TryGetValue(key, out var page)) PageHost.Content = page;
    }

    private async void OnRestartRequested()
    {
        if (Shell is not { } shell) return;

        if (shell.Export.IsBusy && shell.AppSettings.ConfirmCloseWhileRunning)
        {
            var confirmed = await ConfirmAsync(
                "Restart.Running.Title",
                "Restart.Running.Message",
                "Restart.Running.Confirm",
                "Restart.Running.Stay");
            if (!confirmed) return;
        }

        shell.AppSettings.WindowWidth = Width;
        shell.AppSettings.WindowHeight = Height;
        shell.AppSettings.Save();

        if (!ShellHelper.TryRestartApplication()) return;

        _closeConfirmed = true;
        if (shell.Export.IsBusy) shell.Export.CancelCommand.Execute(null);
        Close();
    }

    // ------------------------------------------------------------------ drag and drop

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Link;
        DropOverlay.IsVisible = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => DropOverlay.IsVisible = false;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        if (Shell is not { } shell) return;

        var path = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null);
        if (path is null) return;

        shell.Export.SetPaksPath(path);
        shell.Navigate("export");
    }

    // ------------------------------------------------------------------ closing

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (Shell is { } shell)
        {
            shell.AppSettings.WindowWidth = Width;
            shell.AppSettings.WindowHeight = Height;

            if (!_closeConfirmed && shell.Export.IsBusy && shell.AppSettings.ConfirmCloseWhileRunning)
            {
                e.Cancel = true;
                if (await ConfirmAsync("Close.Running.Title", "Close.Running.Message", "Close.Running.Confirm", "Close.Running.Stay"))
                {
                    _closeConfirmed = true;
                    shell.Export.CancelCommand.Execute(null);
                    Close();
                }

                return;
            }

            if (shell.Export.IsBusy) shell.Export.CancelCommand.Execute(null);
            shell.AppSettings.Save();
        }

        base.OnClosing(e);
    }

    /// <summary>A two-button question. Small enough not to deserve its own XAML file.</summary>
    private async Task<bool> ConfirmAsync(string titleKey, string messageKey, string yesKey, string noKey)
    {
        var loc = Loc.Instance;
        var result = false;

        var dialog = new Window
        {
            Title = loc[titleKey],
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            // The main window is see-through; a dialog needs a solid surface to stay readable.
            [!BackgroundProperty] = this.GetResourceObservable("Brush.SurfaceSolid").ToBinding()
        };

        var yes = new Button { Content = loc[yesKey], Classes = { "Primary" } };
        var no = new Button { Content = loc[noKey] };
        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = loc[messageKey], TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { no, yes }
                }
            }
        };

        await dialog.ShowDialog(this);
        return result;
    }
}
