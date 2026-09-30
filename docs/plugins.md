# Desktop plugins

UEBulkExport can load trusted managed extensions after installation. A plugin can add a page to the
navigation rail and build its UI with Avalonia controls. Open **Plugins** to inspect discovered
packages, enable or disable them, and restart the application to apply the change.

## Security model

Desktop plugins are full-trust code, not scripts or sandboxed extensions. Once enabled, a plugin
can do anything the current Windows account can do. Install only plugins whose source and publisher
you trust.

UEBulkExport reads `plugin.json` and a size-limited icon without loading the DLL. Disabled plugins
never execute. An invalid, duplicate or incompatible manifest is shown as an error and is not
loaded. Every enabled assembly is loaded in its own `AssemblyLoadContext`; the stable contract and
Avalonia runtime are shared with the host. Disabling a loaded plugin requires a restart because its
controls may still be referenced by the desktop window.

## Installation folders

Put each plugin in its own directory under either location:

- `<UEBulkExport installation>\plugins` — packages shipped with a Setup build;
- `%LocalAppData%\UEBulkExport\plugins` — recommended for manually installed plugins.

For example:

```text
plugins/
  example.plugin/
    plugin.json
    Example.Plugin.dll
    Example.Plugin.deps.json
    icon.png
```

After copying a package, restart UEBulkExport, open **Plugins**, enable it, and restart once more.
The plugin page then appears in the navigation rail.

## Manifest version 1

```json
{
  "schemaVersion": 1,
  "apiVersion": 1,
  "id": "example.plugin",
  "name": "Example plugin",
  "description": "Adds an example page.",
  "version": "1.0.0",
  "author": "Example publisher",
  "homepage": "https://example.com/plugin",
  "entryAssembly": "Example.Plugin.dll",
  "entryType": "Example.Plugin.EntryPoint",
  "icon": "icon.png",
  "navigationLabel": "Example",
  "navigationIcon": "M4 4 L20 4 L20 20 L4 20 Z",
  "minimumHostVersion": "2.1.0",
  "maximumHostVersion": "3.0.0",
  "capabilities": [],
  "showPage": true
}
```

The id is a stable lowercase identifier. The entry DLL and icon must be direct children of the
plugin directory; rooted paths and traversal are rejected. The icon may be PNG, JPEG, WEBP or BMP
and must not exceed 2 MiB. `maximumHostVersion`, `homepage`, `icon`, `navigationLabel` and
`navigationIcon`, `capabilities` and `showPage` are optional. `showPage` defaults to `true`.
Capabilities are granted only after the enabled plugin has loaded successfully. A capability-only
extension can set `showPage` to `false` when it does not need a separate navigation page.

## Building a plugin

Reference `src/UEBulkExport.Plugin.Abstractions/UEBulkExport.Plugin.Abstractions.csproj` while
developing in the source tree, or reference the `UEBulkExport.Plugin.Abstractions.dll` shipped with
the installed application. Implement `IUEBulkExportPlugin`:

```csharp
using Avalonia.Controls;
using UEBulkExport.Plugin.Abstractions;

public sealed class EntryPoint : IUEBulkExportPlugin
{
    public Control CreatePage(IUEBulkExportPluginContext context) =>
        new TextBlock { Text = $"Loaded from {context.PluginDirectory}" };
}
```

The context exposes the host version, the plugin's own directory, logging, and a constrained helper
for opening a folder inside that directory. Return one root `Control`; the host keeps it alive for
the application session.

Private packages can be included in a local installer without adding their source to the repository:

```powershell
./installer/build.ps1 -TestIdentity -ExtraPluginsPath "D:\Private\UEBulkExportPlugins"
```

Each immediate subfolder of `-ExtraPluginsPath` must be a ready-to-ship package containing
`plugin.json`.
