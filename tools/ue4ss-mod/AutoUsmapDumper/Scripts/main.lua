--[[
    AutoUsmapDumper - a UE4SS mod for UEBulkExport
    https://github.com/tavridabless/UEBulkExport

    UE4SS can write a .usmap mappings file out of the engine's live reflection data. Its built-in
    binding for that is Ctrl+Numpad6, which is unreachable on a keyboard without a numeric keypad.
    This mod fires the same dump on a timer shortly after the game starts, and keeps Ctrl+F6 as a
    manual re-trigger.

    Install:
      1. Copy this AutoUsmapDumper folder into  <Game>\Binaries\Win64\ue4ss\Mods\
      2. Add the following line to ue4ss\Mods\mods.txt, above the Keybinds entry:
             AutoUsmapDumper : 1
      3. Launch the game, wait for the delay below, then close it.

    The file lands in the ue4ss folder, named after the game and engine version. Older UE4SS
    builds put it beside the game executable instead - check both.

    Licensed under the Apache License, Version 2.0.
--]]

-- Long enough for the engine to finish registering its types. Raise it if the dump comes out
-- short on a slow machine or a game with a long startup.
local AutoDumpDelayMs = 20000

local dumped = false

local function Dump(reason)
    if dumped then return end
    dumped = true

    print(string.format("[AutoUsmapDumper] dumping mappings (%s), this can take a minute...\n", reason))

    local ok, err = pcall(DumpUSMAP)
    if ok then
        print("[AutoUsmapDumper] done - look for the .usmap file in the ue4ss folder\n")
    else
        -- Let the user try again by hand rather than leaving them with nothing.
        dumped = false
        print(string.format("[AutoUsmapDumper] failed: %s\n", tostring(err)))
    end
end

RegisterKeyBindAsync(Key.F6, { ModifierKey.CONTROL }, function()
    dumped = false
    Dump("Ctrl+F6")
end)

ExecuteWithDelay(AutoDumpDelayMs, function()
    Dump("automatic")
end)

print(string.format("[AutoUsmapDumper] loaded - automatic dump in %d seconds, or press Ctrl+F6\n",
    AutoDumpDelayMs / 1000))
