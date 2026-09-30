<#
.SYNOPSIS
    Installs a built setup for the current user into a temporary folder, checks the result, runs
    the installed command line program and uninstalls it again.

.DESCRIPTION
    Catches what compiling alone cannot: a script that compiles but fails at install time, files
    missing from the payload, an application that does not start, or an uninstaller that leaves
    things behind. Runs without administrator rights and without any window.

    On a machine where UEBulkExport is really installed, test a build made with
    `build.ps1 -TestIdentity`, so the real installation is left alone.

.EXAMPLE
    ./installer/smoke-test.ps1 -Installer artifacts/installer/UEBulkExport-2.1.0-win-x64-setup.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Installer,
    # Where to install. Defaults to a fresh folder under the temporary directory.
    [string]$Directory = [System.IO.Path]::Combine(
        [System.IO.Path]::GetTempPath(), "uebulkexport-smoke-" + [guid]::NewGuid().ToString('N')),
    # Optional id of a plugin package expected under <install>/plugins after Setup completes.
    [string]$ExpectedPluginId
)

$ErrorActionPreference = 'Stop'
$Installer = (Resolve-Path $Installer).Path
$log = Join-Path ([System.IO.Path]::GetTempPath()) "uebulkexport-smoke-$([guid]::NewGuid().ToString('N')).log"
$failures = [System.Collections.Generic.List[string]]::new()

function Check {
    param([string]$What, [bool]$Ok)
    if ($Ok) { Write-Host "  ok    $What" } else { Write-Host "  FAIL  $What"; $failures.Add($What) }
}

# The version the installer is meant to carry, from its file name.
$expectedVersion = if ((Split-Path $Installer -Leaf) -match 'UEBulkExport-(?<v>\d+\.\d+\.\d+)') { $Matches.v } else { $null }

Write-Host "==> Install $(Split-Path $Installer -Leaf) into $Directory"
$process = Start-Process -FilePath $Installer -Wait -PassThru -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/TASKS=""',
    "/DIR=`"$Directory`"", "/LOG=`"$log`"")
Check "installer exits with 0 (got $($process.ExitCode))" ($process.ExitCode -eq 0)

foreach ($file in 'UEBulkExport.exe', 'UEBulkExport.Cli.exe', 'installer.json', 'unins000.exe', 'docs\mappings.md', 'LICENSE') {
    Check "installed $file" (Test-Path (Join-Path $Directory $file))
}
if ($ExpectedPluginId) {
    Check "installed plugin $ExpectedPluginId" (Test-Path (Join-Path $Directory "plugins\$ExpectedPluginId\plugin.json"))
}

$cli = Join-Path $Directory 'UEBulkExport.Cli.exe'
if (Test-Path $cli) {
    $output = (& $cli --version | Out-String).Trim()
    Check "installed CLI runs (--version: '$output')" ($LASTEXITCODE -eq 0)
    if ($expectedVersion) { Check "installed CLI reports $expectedVersion" ($output -match [regex]::Escape($expectedVersion)) }
}

Write-Host '==> Uninstall'
$uninstaller = Join-Path $Directory 'unins000.exe'
if (Test-Path $uninstaller) {
    Start-Process -FilePath $uninstaller -Wait -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' | Out-Null
    # The uninstaller hands over to a copy of itself and returns at once; wait for the real work.
    $deadline = (Get-Date).AddMinutes(2)
    while ((Test-Path (Join-Path $Directory 'UEBulkExport.exe')) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 2
    Check 'application removed' (-not (Test-Path (Join-Path $Directory 'UEBulkExport.exe')))
    Check 'installer.json removed' (-not (Test-Path (Join-Path $Directory 'installer.json')))
}

if ($failures.Count -gt 0) {
    if (Test-Path $log) {
        Write-Host '==> Setup log (last 40 lines)'
        Get-Content $log -Tail 40 | ForEach-Object { Write-Host "  $_" }
    }
    throw "Smoke test failed: $($failures -join '; ')"
}

if (Test-Path $Directory) { Remove-Item $Directory -Recurse -Force -ErrorAction SilentlyContinue }
Remove-Item $log -Force -ErrorAction SilentlyContinue
Write-Host '==> Smoke test passed'
