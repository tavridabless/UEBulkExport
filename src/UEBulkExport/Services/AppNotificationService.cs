using System.Runtime.InteropServices;
#if WINDOWS
using CommunityToolkit.WinUI.Notifications;
#endif

namespace UEBulkExport.Gui.Services;

public enum AppNotificationSeverity { Information, Warning, Error }

/// <summary>A short operating-system notification and the page opened when the user clicks it.</summary>
public sealed record AppNotification(
    string Title,
    string Message,
    string Page,
    AppNotificationSeverity Severity = AppNotificationSeverity.Information);

public interface IAppNotificationService : IDisposable
{
    event Action<string>? Activated;
    void Show(AppNotification notification);
}

/// <summary>
/// Uses a real Windows toast on current Windows releases and falls back to the notification-area
/// API for portable and older installations. On other systems both backends are harmless no-ops.
/// </summary>
public sealed class AppNotificationService : IAppNotificationService
{
    private readonly IAppNotificationService _fallback = new TrayBalloonNotificationService();
    private readonly IAppNotificationService? _primary = null;
    private bool _disposed;

    public event Action<string>? Activated;

    public AppNotificationService()
    {
        _fallback.Activated += OnActivated;
#if WINDOWS
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
        {
            try
            {
                _primary = new WindowsToastNotificationService();
                _primary.Activated += OnActivated;
            }
            catch (Exception e)
            {
                Log.Warn($"Windows toast notifications are unavailable; using the tray fallback: {e.Message}");
            }
        }
#endif
    }

    public void Show(AppNotification notification)
    {
        if (_disposed) return;
        if (_primary is not null)
        {
            try
            {
                _primary.Show(notification);
                return;
            }
            catch (Exception e)
            {
                Log.Warn($"Windows toast notification failed; using the tray fallback: {e.Message}");
            }
        }

        _fallback.Show(notification);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_primary is not null)
        {
            _primary.Activated -= OnActivated;
            _primary.Dispose();
        }
        _fallback.Activated -= OnActivated;
        _fallback.Dispose();
    }

    private void OnActivated(string page) => Activated?.Invoke(page);
}

#if WINDOWS
/// <summary>Real Windows toast notifications that are retained in Action Center.</summary>
internal sealed class WindowsToastNotificationService : IAppNotificationService
{
    private bool _disposed;

    public event Action<string>? Activated;

    public WindowsToastNotificationService() => ToastNotificationManagerCompat.OnActivated += OnActivated;

    public void Show(AppNotification notification)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        new ToastContentBuilder()
            .AddArgument("page", notification.Page)
            .AddText(notification.Title)
            .AddText(notification.Message)
            .Show();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ToastNotificationManagerCompat.OnActivated -= OnActivated;
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat arguments)
    {
        var parsed = ToastArguments.Parse(arguments.Argument);
        if (parsed.TryGetValue("page", out var page) && !string.IsNullOrWhiteSpace(page))
            Activated?.Invoke(page);
    }
}
#endif

/// <summary>Legacy notification-area fallback for portable and older Windows installations.</summary>
internal sealed class TrayBalloonNotificationService : IAppNotificationService
{
    private const uint WmApp = 0x8000;
    private const uint WmShow = WmApp + 1;
    private const uint WmTray = WmApp + 2;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmLButtonUp = 0x0202;
    private const uint NinBalloonHide = 0x0403;
    private const uint NinBalloonTimeout = 0x0404;
    private const uint NinBalloonUserClick = 0x0405;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NotifyIconVersion4 = 4;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NiifInfo = 0x00000001;
    private const uint NiifWarning = 0x00000002;
    private const uint NiifError = 0x00000003;
    private const int IdApplication = 32512;

    private readonly Lock _gate = new();
    private readonly Queue<AppNotification> _pending = new();
    private readonly Dictionary<uint, string> _active = [];
    private readonly Thread? _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WndProc? _windowProcedure;
    private nint _window;
    private nint _icon;
    private bool _ownsIcon;
    private bool _disposed;
    private uint _nextIconId;
    private uint _taskbarCreated;

    public event Action<string>? Activated;

    public TrayBalloonNotificationService()
    {
        if (!OperatingSystem.IsWindows()) return;

        _windowProcedure = WindowProcedure;
        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "UEBulkExport notifications"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    public void Show(AppNotification notification)
    {
        if (_disposed || _window == 0) return;
        lock (_gate) _pending.Enqueue(notification);
        _ = PostMessage(_window, WmShow, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_window != 0) _ = PostMessage(_window, WmClose, 0, 0);
        if (_thread is { IsAlive: true }) _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void MessageLoop()
    {
        try
        {
            var className = $"UEBulkExport.Notification.{Environment.ProcessId}";
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure!),
                Instance = GetModuleHandle(null),
                ClassName = className
            };
            if (RegisterClassEx(ref windowClass) == 0)
            {
                Log.Warn($"system notifications could not register a callback window ({Marshal.GetLastPInvokeError()})");
                return;
            }

            _window = CreateWindowEx(0, className, "UEBulkExport notifications", 0,
                0, 0, 0, 0, 0, 0, windowClass.Instance, 0);
            if (_window == 0)
            {
                Log.Warn($"system notifications could not create a callback window ({Marshal.GetLastPInvokeError()})");
                return;
            }

            if (_disposed)
            {
                DestroyWindow(_window);
                return;
            }

            if (Environment.ProcessPath is { } executable &&
                ExtractIconEx(executable, 0, out var large, out var small, 1) > 0)
            {
                _icon = small != 0 ? small : large;
                _ownsIcon = _icon != 0;
                if (small != 0 && large != 0) _ = DestroyIcon(large);
            }
            if (_icon == 0) _icon = LoadIcon(0, (nint)IdApplication);
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            _ready.Set();

            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"system notifications are unavailable: {e.Message}");
        }
        finally
        {
            RemoveIcon();
            if (_ownsIcon && _icon != 0) _ = DestroyIcon(_icon);
            _window = 0;
            _ready.Set();
        }
    }

    private nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        if (_taskbarCreated != 0 && message == _taskbarCreated)
        {
            _active.Clear();
            return 0;
        }

        switch (message)
        {
            case WmShow:
                ShowPending();
                return 0;
            case WmTray:
                var action = (uint)lParam & 0xffff;
                var iconId = ((uint)lParam >> 16) & 0xffff;
                if (action is NinBalloonUserClick or WmLButtonUp &&
                    _active.TryGetValue(iconId, out var page))
                    Activated?.Invoke(page);
                if (action is NinBalloonUserClick or NinBalloonHide or NinBalloonTimeout)
                    RemoveIcon(iconId);
                return 0;
            case WmClose:
                DestroyWindow(window);
                return 0;
            case WmDestroy:
                RemoveIcon();
                PostQuitMessage(0);
                return 0;
            default:
                return DefWindowProc(window, message, wParam, lParam);
        }
    }

    private void ShowPending()
    {
        while (true)
        {
            AppNotification? notification;
            lock (_gate)
                notification = _pending.Count > 0 ? _pending.Dequeue() : null;
            if (notification is null) return;
            ShowNotification(notification);
        }
    }

    private void ShowNotification(AppNotification notification)
    {
        var iconId = NextIconId();
        var add = NewIconData(iconId);
        add.Flags = NifMessage | NifIcon | NifTip | NifInfo;
        add.CallbackMessage = WmTray;
        add.Icon = _icon;
        add.ToolTip = "UEBulkExport";
        add.InfoTitle = Truncate(notification.Title, 63);
        add.Info = Truncate(notification.Message, 255);
        add.InfoFlags = notification.Severity switch
        {
            AppNotificationSeverity.Error => NiifError,
            AppNotificationSeverity.Warning => NiifWarning,
            _ => NiifInfo
        };
        add.VersionOrTimeout = 10_000;
        if (!ShellNotifyIcon(NimAdd, ref add))
        {
            Log.Warn($"system notification could not be displayed ({Marshal.GetLastPInvokeError()})");
            return;
        }
        add.VersionOrTimeout = NotifyIconVersion4;
        _ = ShellNotifyIcon(NimSetVersion, ref add);
        _active[iconId] = notification.Page;
    }

    private uint NextIconId()
    {
        do _nextIconId = _nextIconId == ushort.MaxValue ? 1 : _nextIconId + 1;
        while (_active.ContainsKey(_nextIconId));
        return _nextIconId;
    }

    private NotifyIconData NewIconData(uint id) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _window,
        Id = id,
        ToolTip = "",
        Info = "",
        InfoTitle = ""
    };

    private void RemoveIcon()
    {
        foreach (var iconId in _active.Keys.ToArray()) RemoveIcon(iconId);
    }

    private void RemoveIcon(uint iconId)
    {
        if (_window != 0)
        {
            var data = NewIconData(iconId);
            _ = ShellNotifyIcon(NimDelete, ref data);
        }
        _active.Remove(iconId);
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..Math.Max(0, maximum - 1)] + "…";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ToolTip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIcon(nint instance, nint iconName);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int iconIndex, out nint largeIcon, out nint smallIcon,
        uint iconCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
