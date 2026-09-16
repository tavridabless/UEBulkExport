<div align="center">

# UEBulkExport

**Export an entire Unreal Engine container into one folder — in one command.**

[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/download)
[![Built on CUE4Parse](https://img.shields.io/badge/built%20on-CUE4Parse-orange.svg)](https://github.com/FabianFG/CUE4Parse)
[![CI](https://github.com/tavridabless/UEBulkExport/actions/workflows/ci.yml/badge.svg)](https://github.com/tavridabless/UEBulkExport/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/tavridabless/UEBulkExport?include_prereleases)](https://github.com/tavridabless/UEBulkExport/releases)

[Русский](README.ru.md) · [Mappings guide](docs/mappings.md) · [Third-party notices](THIRD-PARTY-NOTICES.md)

</div>

---

## What this is

[FModel](https://github.com/4sval/FModel) is excellent for browsing an Unreal container and
pulling out the handful of assets you need. What it deliberately does not have is a *take
everything* button.

UEBulkExport is that button, as a command line tool. Point it at a game, and every texture, mesh,
animation, sound, level and property table in its containers lands in one folder, in formats you
can actually open, with the original directory tree preserved.

```
UEBulkExport --paks "D:\Games\MyGame" --out "D:\Export"
```

### What it is built on

**All of the difficult work belongs to [CUE4Parse](https://github.com/FabianFG/CUE4Parse)** — the
same library FModel is built on. Mounting `.utoc`/`.ucas`/`.pak`, reading IoStore packages,
resolving unversioned properties against a `.usmap`, decoding every texture format, writing glTF,
ActorX, USD and UEFormat: that is CUE4Parse and CUE4Parse-Conversion, by FabianFG and
contributors, under Apache-2.0.

UEBulkExport is roughly a thousand lines of orchestration on top: path discovery, work scheduling,
the multi-pass export strategy, resumable runs, and diagnostics. It is a front end, and the
[notices](THIRD-PARTY-NOTICES.md) spell out exactly who did what.

**This is not a decompiler.** It unpacks and converts assets. Blueprints come out as serialised
properties in JSON — readable, diffable, and enough to understand how something was configured,
but not source code you can recompile.

---

## Output

The container's directory tree is mirrored exactly, so the result looks like the folder tree in
FModel's left-hand panel.

| Asset | Output |
|---|---|
| any `.uasset` / `.umap` | `.json` with every property of every object; sub-objects under a `<AssetName>/` folder |
| `UTexture2D`, `UTextureCube`, `UTexture2DArray` | `.png` (or `.tga` / `.jpeg` / `.webp`) |
| `UStaticMesh`, `USkeletalMesh` | `.glb` (or ActorX `.psk`/`.pskx`, `.uemodel`, `.usda`) |
| `USkeleton` | `.pskx` — glTF cannot hold a skeleton on its own |
| `UAnimSequence`, `UAnimMontage`, `UAnimComposite` | `.psa` (or `.ueanim`, `.usda`) |
| `USoundWave`, `UAkMediaAssetData` | the original stream (`.binka` / `.ogg` / `.wav`), plus a decoded `.wav` when vgmstream is available |
| `UWorld` (`.umap`) | `.usda` with the full scene graph, plus its referenced meshes |
| materials | `.json`; with `--materials`, a full material export with textures |
| `.ini`, `.png`, `.ttf`, `.svg`, `.locres`, `.mp4`, … | copied verbatim |

---

## Quick start

### 1. Get the tool

Download the latest archive from [Releases](https://github.com/tavridabless/UEBulkExport/releases)
and unpack it. The build is self-contained — no .NET installation required.

### 2. Get a `.usmap`

Shipping UE5 builds store properties **unversioned**: the packages hold hashes where property
names should be. Nothing can read them without a mappings file — not this tool, not FModel, not
anything else.

**[docs/mappings.md](docs/mappings.md) explains how to produce one**, which takes about two
minutes with UE4SS. Drop the resulting `.usmap` next to `UEBulkExport.exe` and it is picked up
automatically.

No mappings available? `--mode raw` gives a byte-exact dump that needs none — see
[Modes](#modes) for the caveat.

### 3. Run it

```bat
UEBulkExport --paks "D:\Games\MyGame" --out "D:\Export"
```

`--paks` accepts the game's root folder, the `Content\Paks` folder, or a single `.utoc` file —
whichever you happen to have to hand.

Add `--dry-run` first if you want to see the plan before anything is written.

---

## Modes

| Mode | Mappings | What it does |
|---|---|---|
| `full` *(default)* | required | Parses every package and converts it to usable formats |
| `json` | required | Property dumps only — fast, small, great for diffing two builds |
| `raw` | not needed | Byte-exact dump of every container entry |
| `list` | not needed | Prints the container's contents and exits |

> **About `raw`:** it always works, but IoStore `.uasset` files come out in their packed form.
> They are readable by CUE4Parse-based tools and will *not* open in the Unreal editor. Use it to
> inspect a container you have no mappings for, not as a substitute for a real export.

---

## Options

Run `UEBulkExport --help` for the authoritative list.

### Paths

| Option | Meaning |
|---|---|
| `--paks <path>` | Paks folder, any folder above it, or a single `.utoc` file |
| `--out <dir>` | Destination folder |
| `--usmap <file>` | Mappings file. Auto-detected when exactly one `.usmap` sits next to the executable, in the working directory, or beside the containers |

### Core

| Option | Meaning |
|---|---|
| `--mode full\|json\|raw\|list` | See [Modes](#modes) |
| `--game <version>` | Engine version, default `GAME_UE5_3`. Accepts `5.3`, `UE5_3`, `GAME_UE5_3` |
| `--aes 0x…` | AES key for encrypted containers. Repeatable; `--aes <guid>:0x…` ties a key to one container |
| `--threads <n>` | Worker threads, default = CPU count − 1 |
| `--dry-run` | Report the plan and write nothing |
| `--version` | Print the version and exit |

### Filtering

| Option | Meaning |
|---|---|
| `--include <regex>` | Only entries whose container path matches |
| `--exclude <regex>` | Skip entries whose container path matches |

### What to write

| Option | Meaning |
|---|---|
| `--no-json` | Skip property dumps |
| `--no-assets` | Skip mesh/texture/audio/animation conversion |
| `--no-raw-misc` | Skip loose non-package files |
| `--no-worlds` | Skip `.umap` levels |
| `--no-audio-convert` | Do not produce `.wav` alongside BINKA/ADPCM |
| `--raw-packages` | Also dump the untouched `.uasset`/`.ubulk` bytes |
| `--materials` | Export materials as assets, and let meshes pull in their materials and textures |
| `--overwrite` | Re-export entries already recorded as done |

### Formats

| Option | Values |
|---|---|
| `--mesh` | `gltf2` *(default)*, `actorx`, `ueformat`, `usd` |
| `--anim` | `actorx` *(default)*, `ueformat`, `usd` |
| `--texture` | `png` *(default)*, `tga`, `jpeg`, `webp` |
| `--quality` | `highest` *(default)*, `lowest`, `all` — which mesh LODs to write |
| `--nanite` | `nonanite` *(default)*, `naniteonly`, `nanitefirst`, `nanitelast` |
| `--sockets` | `bone` *(default)*, `socket`, `none` |
| `--all-mips` | Write every texture mip |
| `--no-morphs` | Skip morph targets |
| `--platform` | `DesktopMobile` *(default)*, `XboxAndPlaystation`, `NintendoSwitch` |

### Helper binaries

| Option | Meaning |
|---|---|
| `--oodle <file>` | Oodle library. Auto-detected, else downloaded |
| `--zlib <file>` | zlib-ng library. Same treatment |
| `--vgmstream <file>` | `vgmstream-cli`, used to turn BINKA/ADPCM audio into `.wav` |

---

## Recipes

**Game content only, skipping stock engine assets and levels** — the usual starting point, and
much faster:

```bat
UEBulkExport --paks "D:\Games\MyGame" --out "D:\Export" --exclude "^Engine/" --no-worlds
```

**Only the textures, as TGA:**

```bat
UEBulkExport --paks "D:\Games\MyGame" --out "D:\Textures" ^
             --no-raw-misc --include "/Textures?/" --texture tga
```

**Meshes and skeletons for Blender's PSK/PSA importer:**

```bat
UEBulkExport --paks "D:\Games\MyGame" --out "D:\Meshes" --mesh actorx --no-json
```

**Diff two builds** — property dumps only, then compare the folders with any diff tool:

```bat
UEBulkExport --paks "D:\Games\MyGame-1.0" --out "D:\diff\1.0" --mode json --usmap "1.0.usmap"
UEBulkExport --paks "D:\Games\MyGame-1.1" --out "D:\diff\1.1" --mode json --usmap "1.1.usmap"
```

**Encrypted containers:**

```bat
UEBulkExport --paks "D:\Games\MyGame" --out "D:\Export" ^
             --aes 0x0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF
```

---

## Resuming, logs and errors

Every processed entry is appended to `_completed.txt` in the output folder. Re-running the same
command picks up where it stopped — handy when an export is interrupted, or when you want to add
`--materials` to a finished run without redoing everything. `--overwrite` starts fresh.

Two more files land in the output folder:

- **`UEBulkExport.log`** — the full run log.
- **`errors.csv`** — one row per entry that could not be converted, with the exception and
  message. The five most common causes are printed at the end of the run.

The summary also reports a **`no converter`** count. Those are objects CUE4Parse has no file
format for — components, anim notifies, blueprint nodes, actors inside levels. Nothing is lost:
their properties are in the `.json` output. Assets that hold no data on disk are skipped
deliberately: `TextureRenderTarget`, `MediaTexture`, `BinkMediaTexture` (filled by the engine at
runtime) and `BlendSpace` (a set of blending rules, not an animation).

---

## How it works

A few details that are not obvious, and cost time to rediscover:

**Three export passes, not one.** CUE4Parse's `ExportSession` keys its queue by object path, so
queuing two exporters for the same object silently drops one. The property dump and the asset
conversion therefore run as separate sessions. Animations, standalone skeletons and levels each
need a format glTF cannot provide, so they run as further passes with their own options —
animations and skeletons as ActorX, levels as USD.

**Levels are expensive.** A `.umap` exported as USD drags in a `.usda` copy of every mesh it
references. Expect hundreds of megabytes and tens of thousands of extra files, partly duplicating
what `.glb` already holds. `--no-worlds` if you only want assets.

**Sound has no exporter.** `USoundWave` is the one asset class CUE4Parse does not export, so
UEBulkExport decodes it directly and, when vgmstream is available, converts Bink to `.wav`.

**Native libraries are fetched, not vendored.** See [Dependencies](#dependencies).

---

## Dependencies

Nothing is committed to this repository except source code. Everything else is restored, unpacked
or downloaded. Full detail, with licences, in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

| Component | Licence | How it arrives |
|---|---|---|
| CUE4Parse, CUE4Parse-Conversion | Apache-2.0 | NuGet, on restore |
| Newtonsoft.Json | MIT | NuGet, on restore |
| `CUE4Parse-Natives` (ACL animations) | Apache-2.0 | Lifted from the CUE4Parse 1.2.2 package at build time; downloaded at runtime if missing |
| `Detex` (BC/ETC/ASTC textures) | ISC | Unpacked from a resource embedded in CUE4Parse-Conversion |
| `zlib-ng` | zlib | Downloaded on first run |
| Oodle Data Compression | **Proprietary** | Downloaded on first run. Never redistributed here — see the notices |
| vgmstream *(optional)* | ISC | You supply it with `--vgmstream` |

---

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/tavridabless/UEBulkExport.git
cd UEBulkExport
dotnet publish src/UEBulkExport -c Release -r win-x64 --self-contained -o artifacts/publish
```

`artifacts/publish/UEBulkExport.exe` runs anywhere, with no runtime installed. For development,
`dotnet build -c Release` is enough; that build needs .NET 10 present.

Releases ship a Windows build only, and that is a deliberate choice rather than an oversight.
Everything a real export leans on outside managed code — the Oodle decompressor that almost every
modern container needs, the ACL native behind animation export, vgmstream — is published by its
upstream for Windows alone.

Other runtime identifiers do build and are covered by CI, so `linux-x64` and `osx-arm64` are
perfectly usable for containers that avoid Oodle compression. Animation export there needs a
native library built from the `CUE4Parse-Natives` sources in the CUE4Parse repository; without
it, animations are skipped and everything else still works.

---

## Legal

This tool reads files you already have. It does not circumvent DRM, does not break encryption
(you supply any AES key yourself), and does not redistribute anyone's assets.

What you may do with what comes out is a separate question, governed by the licence of the game
you extracted it from. Exported assets are the property of their owners. Personal study,
modding where the publisher allows it, and working with your own projects are the intended uses.
Redistributing extracted assets generally is not.

UEBulkExport is not affiliated with Epic Games, the CUE4Parse project, the FModel project, or the
UE4SS project. "Unreal" and "Unreal Engine" are trademarks of Epic Games, Inc.

---

## Credits

- **[FabianFG and the CUE4Parse contributors](https://github.com/FabianFG/CUE4Parse)** — the
  library that does the actual work.
- **[4sval and the FModel contributors](https://github.com/4sval/FModel)** — for the reference
  implementation of how to drive CUE4Parse well.
- **[The UE4SS contributors](https://github.com/UE4SS-RE/RE-UE4SS)** — for making mappings
  obtainable at all.

## License

[Apache-2.0](LICENSE). See [NOTICE](NOTICE) for the attribution this licence requires you to
carry.
