# Getting a `.usmap` mappings file

**English** · [Русский](mappings.ru.md)

## Why it is needed

Unreal Engine can serialise object properties in two ways. Editor and development builds write
**versioned** properties: every value is preceded by its property name and type. Shipping builds
write **unversioned** properties: the names are gone, and what remains is a bare stream of values
whose meaning depends on the exact layout of each class at compile time.

Every shipping UE5 build does this. It is not obfuscation and there is no trick around it — the
information genuinely is not in the package. Without a description of the class layouts, a
`.uasset` is an undifferentiated blob.

A `.usmap` file is exactly that description: a dump of every class, struct and enum in the build,
with their properties in declaration order. Given one, the byte stream can be walked and every
value put back under its proper name.

You will know you need one when you see:

```
This build stores properties unversioned and cannot be read without a mappings file.
```

Or, from other tools:

```
Package has unversioned properties but mapping file is missing, can't serialize
```

## A mappings file is build-specific

The layouts change whenever the code changes. A `.usmap` is valid for the build it was taken
from, and usually for patches that do not touch class layouts. After a game update, regenerate
it. Using a stale file does not fail loudly — it silently produces wrong values — so regenerate
rather than guess.

## Generating one with UE4SS

The class layouts exist in memory whenever the game runs, in Unreal's reflection system.
[UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) (MIT) reads them out and writes a `.usmap`.

UE4SS is a separate project. UEBulkExport does not bundle it, link against it, or require it —
any `.usmap` from any source works.

> Only do this with software you are entitled to run and modify. Injecting a library into a
> program with anti-cheat, or one whose terms forbid modification, is your problem to assess.

### Steps

1. Download `UE4SS_*.zip` from the UE4SS [releases page](https://github.com/UE4SS-RE/RE-UE4SS/releases).
   The `experimental-latest` build covers newer engine versions than the last tagged release.

2. Extract `dwmapi.dll` and the `ue4ss` folder next to the game's shipping executable — the one
   under `<Game>/Binaries/Win64/`, not the launcher in the game's root.

3. Launch the game and wait for the main menu.

4. Press <kbd>Ctrl</kbd> + <kbd>Numpad 6</kbd>. That is UE4SS's default binding for `DumpUSMAP()`.

5. A file named after the game and engine version appears in the `ue4ss` folder. Close the game.

6. Pick it in the **Mappings** field of the Export page, or pass it with `--usmap`:

   ```
   UEBulkExport.Cli --paks "D:\Games\MyGame" --out "D:\Export" --mode full --usmap "...\ue4ss\MyGame-5.3.2.usmap"
   ```

   A single `.usmap` placed next to the game's containers (in the `Paks` folder or one level
   above it) or in the output folder is found without being named.

### No numeric keypad?

Laptops usually have no numpad, and the default binding is unreachable. This repository ships a
small UE4SS mod that calls `DumpUSMAP()` on a timer instead, so the dump happens on its own a few
seconds after the game starts, with <kbd>Ctrl</kbd> + <kbd>F6</kbd> as a manual re-trigger.

Copy [`tools/ue4ss-mod/AutoUsmapDumper`](../tools/ue4ss-mod/AutoUsmapDumper) into
`ue4ss/Mods/`, then add this line to `ue4ss/Mods/mods.txt`, above the `Keybinds` entry at the
bottom:

```
AutoUsmapDumper : 1
```

If UEBulkExport was installed with the *UE4SS mappings helper tools* component, the mod is
already in the installation folder, under `tools\ue4ss-mod\AutoUsmapDumper`.

Launch the game, wait about twenty seconds, close it. The `.usmap` will be in the `ue4ss` folder.

To watch it happen, set `ConsoleEnabled = 1` under `[Debug]` in `ue4ss/UE4SS-settings.ini`; UE4SS
then opens a console window and logs the dump.

### Cleaning up

UE4SS only adds files. To remove it, delete `dwmapi.dll` and the `ue4ss` folder from the
`Binaries/Win64` directory. Keeping it around is convenient if you expect to regenerate mappings
after the next patch.

## Other sources

- Some games ship a `.usmap` inside their own containers. Run `--mode list` and look for one.
- Developers can have the engine emit one at cook time; if you are exporting your own project,
  that is by far the easiest route.
- Any other reflection dumper that writes the `.usmap` format works equally well.

## Format versions

UEBulkExport reads whatever version CUE4Parse supports, which currently includes version 5
(`EngineVersioning`). Files produced by current UE4SS builds are version 4
(`ExplicitEnumValues`). Older tools pinned to earlier CUE4Parse releases may reject these with
`Usmap has invalid version` — that is a limitation of those tools, not of the file.
