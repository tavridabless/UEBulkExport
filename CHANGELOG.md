# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [2.1.0] - 2026-09-27

### Added

- A **Migrate to UE** page that migrates assets between Unreal Engine versions. It uses a
  user-selected UE Viewer executable to export recoverable assets from loose UE4.0–4.27 cooked
  dumps, then starts the selected target `UnrealEditor-Cmd` to create new assets inside a target
  project. The source version, target project, editor, content destination, overwrite policy and
  progress are available in the English and Russian interfaces and remembered between runs.
- UE Viewer and the matching `UnrealEditor-Cmd` are detected automatically. A target project is
  suggested only when **Suggest** is pressed with the project field empty, and a path
  typed by the user is never replaced. Before anything is imported, a confirmation window shows
  the target project and content folder.
- A UE Viewer failure no longer has to abort the whole conversion. The window displays the failing
  package and a **Continue** button; continuing skips that package, resumes the export, and writes
  every skipped or failed file to `conversion-errors.csv` at the end. Packages with empty `.ubulk`
  or `.uexp` payloads are detected and skipped up front.
- The source dump is only read. Packages that UE Viewer must skip are left out of a temporary
  hard-linked working copy (plain copies across drives), which is removed when the run ends.
- Conversion reports and the UE Viewer/Unreal Editor logs are retained under
  `Saved/UEBulkExport/DumpConversion` in the target project, in a separate folder for each dump.
  Interchange files that are already in a dump can be imported without exporting them again. An
  import ledger lets a repeated run without **Replace existing target assets** leave already
  imported assets untouched and report them as already present rather than as failures, so
  interrupted runs can be resumed.
- UE Viewer and the editor are closed after a long period without output (15 and 30 minutes), and
  a failing editor Python script is reported with the error from `unreal-import.log` even though
  the editor exits successfully.
- The editor receives the import script path in the form Unreal expects, so projects in folders
  with spaces or names that begin with digits import correctly.
- The installer recognises an installed copy. A newer version runs as an update (*Update
  2.0.0 → 2.1.0*) that reuses the licence, folder, components and shortcuts; the same version
  runs as a reinstall where the components can be changed; an older version asks before it
  replaces a newer one, and unattended installs need `/ALLOWDOWNGRADE` for that.
- UEBulkExport can be installed for the current user only, without administrator rights (a choice
  at the start of the wizard, or `/CURRENTUSER`). An update keeps the mode of the existing
  installation, so no second copy appears.
- An optional installer task adds the command line program to `PATH`; uninstalling removes it
  again and leaves every other `PATH` entry untouched.
- The uninstaller offers to delete the user's settings, recent games and downloaded helper
  libraries; by default they are kept.
- Releases carry `SHA256SUMS.txt`. The release workflow signs the executables, the installer and
  the uninstaller when a signing certificate is configured as a repository secret.
- Setup and uninstall write logs to `%TEMP%`.

### Changed

- The Export page was reorganised around the three decisions a user has to make: the game, the
  result and where to save it. Numbered steps are gone, and migration has a page and an action of
  its own, so two different Start buttons are never on screen together.
- The game is read as soon as it is chosen; the separate **Scan container** button is replaced by
  a one-line finding (containers, entries, encryption) with **Read again**. The engine version is
  read from the game executable, and the selector appears only when that fails or on request.
  The AES key field appears only for an encrypted game or on request.
- Results are named after what the user gets (**Game packages**, **Converted files**, **Data for
  comparison**, **Exact copy**), each with one line of description, the full explanation in a
  tooltip and the matching `--mode` switch.
- Mappings, filters, formats and tool paths explain themselves through `?` tooltips instead of
  permanent text. Collapsed sections show their state (*Recommended defaults*, *2 settings
  changed*, *Found automatically*), and the equivalent command line moved into a collapsed
  section.
- The bar at the bottom of each page says whether the operation is ready and, when it is not,
  why; the Start button stays disabled until the required fields are filled. Warnings about
  overwriting or lost editor data stay visible next to the setting they concern.
- The **UEBulkExport CLI** Start menu shortcut opens a command prompt with the command line help,
  ready for the next command, instead of a console that can only show the help and close.
- The application starts in the language chosen in the installer until a language is picked in
  Settings.

### Fixed

- An update removes program files the new version no longer ships, and deselecting a component
  in a reinstall removes its files; a core-only installation no longer leaves empty `docs` and
  `tools` folders behind.
- The installer offers only the native components the build actually contains; the others are
  downloaded by the application on first use and no longer appear as empty checkboxes.
- The installer refuses to run on Windows versions that .NET 10 does not support (desktop
  Windows older than 10 version 1607, servers older than 2012), and both the installer and the
  uninstaller ask to close UEBulkExport while it is running.
- Game packages export of a game built with an engine newer than the bundled retoc knows (UE 5.8
  and later) no longer fails with an unexplained `invalid value` error. The version is left for
  retoc to read from the containers; if retoc still cannot convert them, the error names the
  cause and the alternatives, and the Export page warns about it before the run starts.

### Known limitations

- Conversion reconstructs supported meshes, textures and audio; cooking has already discarded
  editor-only data such as Blueprint, material, Niagara and level graphs. UE Viewer ActorX animation
  files (`.psa`) are reported but cannot be imported natively by Unreal Engine 5.

## [2.0.0] - 2026-09-24

### Added

- A Windows installer. Releases ship as `UEBulkExport-<version>-win-x64-setup.exe` instead of a
  ZIP archive. The wizard, in English or Russian, lets the user choose the destination folder and
  the components, creates Start menu shortcuts for the window and the command line program, can add
  a desktop shortcut, and registers an uninstaller. Full installation is selected by default.
- Settings gain a **Transparency effects** switch. With it off, or where the system offers no blur,
  the window uses a solid background.

### Changed

- A new "glass" look for the window, in the style of Windows 11 Fluent: the window is translucent
  with Acrylic or Mica blur behind it, a soft blue-and-lavender glow sits under frosted panels,
  cards have light rims and soft shadows, primary buttons carry a blue-to-violet sheen, and the
  active page is marked with an accent pill. The title bar is part of the window. Both the light and
  the dark theme follow the same style.
- A new application icon: the glyph set in frosted glass. It is used for both executables, the
  window, the About page and `setup.exe`.
- The installer wizard carries dedicated artwork: an illustrated panel on the Welcome and Finished
  pages, a light glass background on every page, the icon in the header, and a hero scene while
  files are copied.
- Changing the interface language now takes effect after a restart; the Settings page says so and
  offers to restart right away.
- The README opens with the new logo.
- The mappings guide is available in Russian, `docs/mappings.ru.md`; the Russian README and the
  About page in Russian link to it, and it ships with the offline documentation.

### Fixed

- The Export page no longer opens scrolled down: the mode picker scrolled its selected tile into
  view and dragged the whole page with it.

## [1.2.0] - 2026-09-23

### Added

- `--paths-file <file>`: export only the container paths listed in a text file. The Browser page
  of the window uses it: tick folders or files, or tick and press "Exclude" to export everything
  else; the selection is saved as `_selection.txt` in the output folder.
- The window ships with its own icon and a green visual theme; the Browser page has a breadcrumb,
  type filter chips, a details panel and a selection bar.
- A graphical interface. `UEBulkExport.exe` is now a desktop application (Avalonia UI): pages for
  Export (source, destination and mode, options, helper binaries; Start, Dry run and Cancel;
  progress with ETA; result summary; an "equivalent command line" box with Copy; recent games),
  Browser (folder tree of the mounted containers, file list with search, by-type statistics,
  container list with locked status; "Export only this folder" / "Exclude this folder" generate
  include/exclude filters), Log (filter by level, copy, clear, follow), Settings (language
  English/Russian with live switching, theme Light/Dark/System, remember last paths, confirm close
  while running, default threads, default helper binary paths, reset) and About. A game folder or
  a `.utoc`/`.pak` dropped onto the window fills in the source. A fresh install starts in English
  with the light theme. Settings live in `%LOCALAPPDATA%\UEBulkExport\settings.json`; a startup crash
  is written to `%LOCALAPPDATA%\UEBulkExport\crash.log`. Nothing leaves the machine.
- A unit test project, `tests/UEBulkExport.Tests` (xunit, 119 tests), that needs neither network
  access nor game files.
- `--verbose` (log every file written) is now listed in the README options tables, together with
  the exit codes: 0 success, 1 could not start or crashed, 2 finished with failed entries or
  cancelled.

### Changed

- The console program is now `UEBulkExport.Cli.exe`; all flags are unchanged. `UEBulkExport.exe`
  (the GUI build) accepts the same arguments and then runs in command-line mode attached to the
  calling terminal, but scripts and CI should call `UEBulkExport.Cli.exe`, since a GUI-subsystem
  executable does not block the shell or return its exit code reliably from an interactive
  cmd/PowerShell prompt.
- The solution is split into `src/UEBulkExport.Core` (library with all export logic),
  `src/UEBulkExport.Cli` (console front end) and `src/UEBulkExport` (GUI). The release archive
  contains both executables side by side.
- The summary separates "failed entries" from "failed objects" instead of one mixed count.
- `UEBulkExport.log` in the output folder is appended to across runs, each run starting with a
  "run started" separator, instead of being overwritten.
- Using several `--aes` keys with IoStore containers in the default mode is rejected before
  mounting (retoc supports one key), not after.

### Fixed

- Ctrl+C in the CLI cancels cleanly: the resume index is flushed and a summary printed, so the
  next run continues where this one stopped.
- Invalid `--aes` values (not 64 hex characters, malformed GUID) are reported with a hint instead
  of a stack trace.
- The `--platform` help text listed `XboxAndPlaystation`; the accepted values are `DesktopMobile`,
  `XboxAndPlaystation4`, `Playstation5` and `NintendoSwitch`. The README options tables are fixed
  as well.

## [1.1.0] - 2026-09-20

### Changed

- The default mode extracts cooked `.uasset`/`.umap` packages and payloads instead of writing
  JSON. IoStore packages are converted to legacy cooked layout with retoc; no `.usmap` is needed.
  Cooked packages do not restore editor-only data and open only where Unreal Editor supports the
  cooked asset type, usually read-only.
- Completion indexes are mode-specific, so earlier JSON runs cannot skip package extraction.
  JSON and converted output remain available with `--mode json` and `--mode full`.
- Legacy IoStore conversion now passes the selected `--game` engine version to retoc instead of
  relying on container-header inference.
- Cooked packages are documented as requiring the same engine version that produced them.
- Partially failed packages are no longer recorded as completed, so shared material/texture write
  races from a parallel pass can be resumed safely with `--threads 1`; a successful retry also
  removes the stale error report.

## [1.0.0] - 2026-09-17

First release.

### Added

- Bulk export of an entire Unreal Engine container (`.utoc`/`.ucas`/`.pak`) into a single folder,
  mirroring the container's directory tree.
- Four modes: `full` (parse and convert), `json` (property dumps only), `raw` (byte-exact dump,
  no mappings needed) and `list` (inspect the container).
- Conversion of textures to PNG/TGA/JPEG/WebP, meshes to glTF/ActorX/UEFormat/USD, animations and
  standalone skeletons to ActorX/UEFormat/USD, levels to USD, and audio to its original stream
  plus a decoded `.wav` when vgmstream is available.
- Property dumps for every object in every package, including sub-objects.
- Path discovery: `--paks` accepts a game root, the Paks folder, or a single `.utoc` file, and a
  lone `.usmap` nearby is picked up without being named.
- An early check for missing mappings that stops the run with instructions, rather than failing
  once per package.
- Resumable runs via `_completed.txt`, `--dry-run`, `--version`, regex include/exclude filters,
  per-container AES keys, and an `errors.csv` report with the most common failure causes.
- `CUE4Parse-Natives` is lifted out of a downloaded NuGet package at build time and fetched at
  runtime if still missing, so no third-party binary lives in the repository.
- A UE4SS mod under `tools/` that dumps a `.usmap` on a timer, for keyboards without a numeric
  keypad.

[1.0.0]: https://github.com/tavridabless/UEBulkExport/releases/tag/v1.0.0
[1.1.0]: https://github.com/tavridabless/UEBulkExport/compare/v1.0.0...v1.1.0
[1.2.0]: https://github.com/tavridabless/UEBulkExport/compare/v1.1.0...v1.2.0
[2.0.0]: https://github.com/tavridabless/UEBulkExport/compare/v1.2.0...v2.0.0
[2.1.0]: https://github.com/tavridabless/UEBulkExport/compare/v2.0.0...v2.1.0
