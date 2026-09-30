# Desktop-плагины

UEBulkExport умеет загружать доверенные managed-расширения уже после установки. Плагин может
добавить страницу в левую навигацию и построить интерфейс на Avalonia. На странице **«Плагины»**
можно посмотреть найденные пакеты, включить или выключить их и перезапустить приложение.

## Модель безопасности

Desktop-плагин — это полнодоверенный код, а не скрипт и не расширение в песочнице. После включения
он может делать всё, что разрешено текущей учётной записи Windows. Устанавливайте только плагины,
исходникам и издателю которых доверяете.

UEBulkExport читает `plugin.json` и ограниченную по размеру иконку без загрузки DLL. Выключенные
плагины не выполняются. Некорректный, дублирующийся или несовместимый manifest отображается с
ошибкой и не загружается. Каждая включённая assembly получает отдельный `AssemblyLoadContext`, а
контракт и Avalonia используются совместно с host-приложением. После выключения нужен перезапуск,
потому что элементы интерфейса плагина могут ещё использоваться окном.

## Папки установки

Каждый плагин должен находиться в собственном подкаталоге одной из папок:

- `<каталог UEBulkExport>\plugins` — пакеты, включённые в конкретную Setup-сборку;
- `%LocalAppData%\UEBulkExport\plugins` — рекомендуемая папка для ручной установки.

Пример:

```text
plugins/
  example.plugin/
    plugin.json
    Example.Plugin.dll
    Example.Plugin.deps.json
    icon.png
```

После копирования перезапустите UEBulkExport, откройте **«Плагины»**, включите пакет и перезапустите
программу ещё раз. После этого его страница появится в навигации.

## Manifest версии 1

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

Id — стабильный идентификатор в нижнем регистре. Entry DLL и иконка должны лежать непосредственно
в каталоге плагина; абсолютные пути и `..` отклоняются. Допустимы PNG, JPEG, WEBP и BMP размером до
2 МиБ. Поля `maximumHostVersion`, `homepage`, `icon`, `navigationLabel` и `navigationIcon`
`capabilities` и `showPage` необязательны; по умолчанию `showPage` равен `true`. Capability
выдаётся host-приложению только после успешной загрузки включённого плагина. Расширение, которому
не нужна отдельная страница навигации, может задать `showPage: false`.

## Разработка плагина

При разработке из исходного дерева подключите
`src/UEBulkExport.Plugin.Abstractions/UEBulkExport.Plugin.Abstractions.csproj`. Для отдельного
проекта можно сослаться на `UEBulkExport.Plugin.Abstractions.dll` из установленной программы.
Реализуйте `IUEBulkExportPlugin`:

```csharp
using Avalonia.Controls;
using UEBulkExport.Plugin.Abstractions;

public sealed class EntryPoint : IUEBulkExportPlugin
{
    public Control CreatePage(IUEBulkExportPluginContext context) =>
        new TextBlock { Text = $"Loaded from {context.PluginDirectory}" };
}
```

Контекст предоставляет версию host, собственный каталог плагина, журналирование и ограниченный
helper для открытия папки внутри каталога плагина. Нужно вернуть один корневой `Control`, который
host хранит до закрытия приложения.

Приватный пакет можно добавить в локальный установщик, не помещая его исходники в репозиторий:

```powershell
./installer/build.ps1 -TestIdentity -ExtraPluginsPath "D:\Private\UEBulkExportPlugins"
```

Каждый непосредственный подкаталог `-ExtraPluginsPath` должен быть готовым пакетом с `plugin.json`.
