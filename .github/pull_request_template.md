## What and why

<!-- What does this change do, and what problem does it solve? Link the issue if there is one. -->

## How it was checked

<!-- Tests added or run, games or sample files tried, screenshots for interface changes. -->

## Checklist

- [ ] `dotnet build UEBulkExport.slnx -c Release -warnaserror` passes without warnings.
- [ ] `dotnet test tests/UEBulkExport.Tests -c Release` passes.
- [ ] New interface text is in both `Strings.en.json` and `Strings.ru.json`.
- [ ] `README.md` and `README.ru.md` are updated together if behaviour changed.
- [ ] `CHANGELOG.md` has an entry under the unreleased version.
- [ ] No game files, AES keys or personal data in the change.
