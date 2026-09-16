# Third-party notices

UEBulkExport is a thin command line front end. Almost all of the work is done by the components
listed here. This file records what they are, where they come from, how they reach your machine,
and under what terms.

## Managed dependencies (NuGet, restored at build time)

| Component | Version | License | Role |
|---|---|---|---|
| [CUE4Parse](https://github.com/FabianFG/CUE4Parse) | 1.2.2.202609 | Apache-2.0 | Mounts `.utoc`/`.ucas`/`.pak`, reads IoStore packages, resolves unversioned properties against a `.usmap` |
| [CUE4Parse-Conversion](https://github.com/FabianFG/CUE4Parse) | 1.2.2.202609 | Apache-2.0 | Decodes textures and audio, writes glTF, ActorX, USD, UEFormat and JSON |
| [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) | 13.0.4 | MIT | JSON serialisation, required by CUE4Parse |

CUE4Parse pulls in further transitive packages of its own — Serilog, SkiaSharp, SharpGLTF,
AssetRipper.TextureDecoder, ZstdSharp, Blake3, BouncyCastle and others. Run
`dotnet list package --include-transitive` for the complete, versioned tree.

## Native libraries (not in this repository)

None of these are committed here. Each is located on your machine or fetched on demand, and the
source is recorded so you can verify or replace it.

| Library | License | How it is obtained | What breaks without it |
|---|---|---|---|
| `CUE4Parse-Natives` | Apache-2.0 | `PackageDownload` of the CUE4Parse **1.2.2** package at build time; downloaded from nuget.org at runtime if still missing | ACL-compressed animations |
| `Detex` | ISC | Unpacked from an embedded resource inside CUE4Parse-Conversion | A few BC/ETC/ASTC textures |
| `zlib-ng` | zlib | Downloaded from [Zlib-ng.NET releases](https://github.com/NotOfficer/Zlib-ng.NET) on first run | zlib-compressed IoStore chunks |
| Oodle Data Compression | **Proprietary** | Downloaded on first run by CUE4Parse's own helper | Oodle-compressed IoStore chunks |

### On `CUE4Parse-Natives`

The library is built from the `CUE4Parse-Natives` directory of the CUE4Parse repository and is
covered by that project's Apache-2.0 licence. Current CUE4Parse packages no longer ship it, so
UEBulkExport takes it from CUE4Parse **1.2.2**, the last package that did. That build still
matches the current managed ABI. You can substitute your own build at any time by placing it
next to the executable.

### On Oodle

Oodle Data Compression is proprietary software owned by Epic Games (RAD Game Tools). It is
**not** redistributed by this project and never will be. Unreal Engine licensees and owners of
Unreal games already have it; the runtime download exists only as a convenience, and you can
point `--oodle` at a copy you already have instead.

## Optional external tools

| Tool | License | Role |
|---|---|---|
| [vgmstream](https://github.com/vgmstream/vgmstream) | ISC (with GPL-licensed optional codecs) | Converts BINKA/ADPCM audio to `.wav`. Never bundled; supply it with `--vgmstream`. |
| [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) | MIT | Generates the `.usmap` mappings file from a running game. A separate program — UEBulkExport neither bundles nor links it. See [docs/mappings.md](docs/mappings.md). |

## Not affiliated

UEBulkExport is not affiliated with, endorsed by, or supported by Epic Games, the CUE4Parse
project, the FModel project, or the UE4SS project. "Unreal" and "Unreal Engine" are trademarks
of Epic Games, Inc., used here only to describe what the tool reads.
