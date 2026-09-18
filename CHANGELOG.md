# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- The default mode extracts cooked `.uasset`/`.umap` packages and payloads instead of writing
  JSON. IoStore packages are converted to legacy cooked layout with retoc; no `.usmap` is needed.
  Cooked packages do not restore editor-only data and open only where Unreal Editor supports the
  cooked asset type, usually read-only.
- Completion indexes are mode-specific, so earlier JSON runs cannot skip package extraction.
  JSON and converted output remain available with `--mode json` and `--mode full`.

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
