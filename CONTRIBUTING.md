# Contributing

Thanks for looking. This is a small project with a narrow purpose, which keeps things simple.

## Scope

UEBulkExport is a front end for [CUE4Parse](https://github.com/FabianFG/CUE4Parse). That boundary
decides where a change belongs:

- **Parsing a container, decoding a format, exporting an asset type** → that is CUE4Parse. Report
  it, or contribute it, [there](https://github.com/FabianFG/CUE4Parse/issues). Everyone using the
  library benefits, this tool included.
- **Which assets get exported, in what order, where they land, what the CLI looks like, how
  failures are reported** → that is here.

If you are unsure, open an issue and we can work it out.

## Reporting a problem

An export failure is far easier to act on with:

- the exact command you ran;
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
dotnet build src/UEBulkExport -c Release
```

Requires the .NET 10 SDK. The first build downloads a NuGet package to extract
`CUE4Parse-Natives` from; after that it works offline.

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
