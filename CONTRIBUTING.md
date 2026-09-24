# Contributing

Thanks for looking. This is a small project with a narrow purpose, which keeps things simple.

## Scope

UEBulkExport is a front end for [CUE4Parse](https://github.com/FabianFG/CUE4Parse). That boundary
decides where a change belongs:

- **Parsing a container, decoding a format, exporting an asset type** → that is CUE4Parse. Report
  it, or contribute it, [there](https://github.com/FabianFG/CUE4Parse/issues). Everyone using the
  library benefits, this tool included.
- **Which assets get exported, in what order, where they land, what the CLI and the window look
  like, how failures are reported** → that is here.

If you are unsure, open an issue and we can work it out.

## Reporting a problem

An export failure is far easier to act on with:

- the exact command you ran (the window shows it in the "Equivalent command line" box);
- the engine version (`--game`, or what the game's `.uproject` says);
- the summary block at the end of the run;
- the relevant rows from `errors.csv`;
- whether the same asset works in FModel with the same `.usmap`.

That last one matters: if FModel fails too, the issue is in CUE4Parse, and the report should go
there.

Please do not attach game assets, `.usmap` files, or containers. They are not yours to
redistribute, and the paths and error messages are enough.

## Building

```bash
dotnet build UEBulkExport.slnx -c Release
dotnet test tests/UEBulkExport.Tests -c Release
```

Requires the .NET 10 SDK. The first build downloads a NuGet package to extract
`CUE4Parse-Natives` from; after that it works offline. The tests need neither network access nor
game files.

The solution has three source projects and one test project:

- `src/UEBulkExport.Core` — the library with all export logic. Anything that is not presentation
  belongs here, so both front ends behave the same.
- `src/UEBulkExport.Cli` — the console front end, `UEBulkExport.Cli.exe`.
- `src/UEBulkExport` — the desktop window, `UEBulkExport.exe`.
- `tests/UEBulkExport.Tests` — xunit tests for the core.

To produce the release layout, publish both executables into one folder:

```bash
dotnet publish src/UEBulkExport     -c Release -r win-x64 --self-contained -o artifacts/publish
dotnet publish src/UEBulkExport.Cli -c Release -r win-x64 --self-contained -o artifacts/publish
```

### Release packaging

Windows releases must be distributed as
`UEBulkExport-<version>-win-x64-setup.exe`, built from `installer/UEBulkExport.iss`; do not replace
the installer with a ZIP archive. Publish the GUI and CLI into the same staging directory before
compiling the installer so they continue to share Core, the self-contained .NET runtime and native
libraries. The default Full installation must include every export dependency and let the user
choose the destination directory. Whenever installation or release behaviour changes, update
`README.md` and `README.ru.md` together.

To build the installer locally, publish both executables into one folder, generate the wizard
artwork and compile with Inno Setup 6.6 or later (the release workflow uses 6.7.3):

```bat
dotnet publish src/UEBulkExport     -c Release -r win-x64 --self-contained -o artifacts/publish
dotnet publish src/UEBulkExport.Cli -c Release -r win-x64 --self-contained -o artifacts/publish
dotnet run --project installer/branding/generator -- installer/branding src/UEBulkExport/Assets docs/images
ISCC.exe /DAppVersion=2.0.0 /DSourceDir="artifacts\publish" /DOutputDir="artifacts\installer" installer\UEBulkExport.iss
```

The artwork sources live in `installer/branding`: `icon-source.webp` for the application icon and
`design/*.webp` for the wizard and the README logo. The generated `wizard-*.png` files are not
committed; `app.ico`, `logo.png` and `docs/images/logo*.png` are. After replacing a source image,
run the generator and commit the regenerated icon and logo files. The README describes installing from
`setup.exe` only; build instructions for the installer belong here.

### GUI code

The window lives under `src/UEBulkExport` and is built with Avalonia, MVVM style, using
CommunityToolkit.Mvvm for observable properties and commands. User-visible strings go into
`Localization/Strings.en.json` and `Localization/Strings.ru.json` with the same keys in both
files — never hard-coded in XAML or view models. Never copy code, XAML, icons or text from FModel:
it is GPL-3.0 and this project is Apache-2.0.

## Code style

`.editorconfig` covers the mechanics. Beyond it:

- The build is warning-free. CI runs with `-warnaserror`, so keep it that way.
- Comment the *why*, not the *what*. The tricky parts of this codebase are all
  "why is it done in this roundabout way", and the answer is usually a CUE4Parse behaviour worth
  writing down — see the three-pass export in `BulkExporter` for the pattern.
- Errors a user can fix are `UserFacingException`, with a headline and a hint. Everything else is
  a bug and should keep its stack trace.
- No new third-party binaries in the repository. Fetch, unpack or locate them at build or run
  time, and record them in `THIRD-PARTY-NOTICES.md`.

## Licensing

Contributions are accepted under [Apache-2.0](LICENSE), the project's licence. By opening a pull
request you confirm you have the right to submit the code under those terms.
