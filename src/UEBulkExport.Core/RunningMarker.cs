namespace UEBulkExport;

/// <summary>
/// Announces that UEBulkExport is running, so the installer and the uninstaller can ask the user
/// to close it instead of replacing files under a live export. The installer looks for these
/// names (its AppMutex directive); keep the two in sync.
/// </summary>
public static class RunningMarker
{
    public const string MutexName = "UEBulkExport.Running";

    // Held for the lifetime of the process: Windows drops a mutex when its last handle closes.
    private static readonly List<Mutex> Held = [];

    public static void Announce()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The session-local name covers this user; the global one other sessions, such as a
        // second user signed in while an administrator runs the installer.
        foreach (var name in new[] { MutexName, @"Global\" + MutexName })
        {
            try
            {
                lock (Held) Held.Add(new Mutex(initiallyOwned: false, name));
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
            {
                // Another user's instance owns the global name; ours is still announced locally.
            }
        }
    }
}
