# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
