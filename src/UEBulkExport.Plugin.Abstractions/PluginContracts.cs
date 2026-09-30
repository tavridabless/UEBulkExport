using Avalonia.Controls;

namespace UEBulkExport.Plugin.Abstractions;

/// <summary>The stable contract version understood by this UEBulkExport build.</summary>
public static class PluginApi
{
    public const int Version = 1;
}

/// <summary>
/// Entry point implemented by a trusted desktop plugin. The host creates exactly one instance at
/// startup and keeps the returned page alive until the application exits.
/// </summary>
public interface IUEBulkExportPlugin
{
    Control CreatePage(IUEBulkExportPluginContext context);
}

/// <summary>A deliberately small host surface. Plugins receive no private application services.</summary>
public interface IUEBulkExportPluginContext
{
    string HostVersion { get; }
    string PluginDirectory { get; }

    void Log(PluginLogLevel level, string message);
    void OpenFolder(string path);
}

public enum PluginLogLevel
{
    Debug,
    Information,
    Warning,
    Error
}
