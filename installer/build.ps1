<#
.SYNOPSIS
    Builds the Windows installer the same way everywhere: on a developer machine, in CI and for
    a release.

.DESCRIPTION
    1. Publishes the window and the command line program, self-contained, into one staging folder.
    2. Adds the offline documentation, UE4SS mappings helper and optional external plugin packages.
    3. Generates the wizard artwork from installer/branding.
    4. Optionally signs both executables.
    5. Compiles installer/UEBulkExport.iss (and lets Inno Setup sign the installer and its
       uninstaller) and writes SHA256SUMS.txt next to it.

    Needs the .NET SDK from global.json and Inno Setup 6.6 or later (installer/install-inno.ps1).

.EXAMPLE
    ./installer/build.ps1
    Builds artifacts/installer/UEBulkExport-<version>-win-x64-setup.exe from the version in
    Directory.Build.props.

.EXAMPLE
    ./installer/build.ps1 -Version 2.2.0 -TestIdentity
    Builds a copy with its own identity ("UEBulkExport Test"), which installs next to a real
    installation instead of updating it.
#>
[CmdletBinding()]
param(
    # Version for the installer and the executables. Defaults to <Version> in Directory.Build.props.
    [string]$Version,

    # Path to ISCC.exe. Defaults to ISCC_PATH, then to a standard Inno Setup 6 installation.
    [string]$Iscc = $env:ISCC_PATH,

    [string]$Staging = 'artifacts/staging/UEBulkExport',
    [string]$Output = 'artifacts/installer',
    [string]$Runtime = 'win-x64',

    # Optional folder whose immediate subfolders are ready-to-ship plugin packages. This keeps
    # private plugin sources outside the repository while allowing local/test installers to carry them.
    [string]$ExtraPluginsPath,

    # A signing command in Inno Setup's sign tool syntax: $f is the file to sign, $q a double quote.
    # For example: $qC:\Kits\signtool.exe$q sign /fd sha256 /f $qcert.pfx$q /p secret $f
    [string]$SignCommand,

    # Gives the build its own AppId and name, for trying installs and updates safely.
    [switch]$TestIdentity,

    # Reuses an existing staging folder instead of publishing again.
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

function Invoke-Native {
    param([string]$Description, [scriptblock]$Command)
    Write-Host "==> $Description"
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Description failed with exit code $LASTEXITCODE" }
}

function Invoke-Sign {
    param([string]$File)
    $command = $SignCommand.Replace('$q', '"').Replace('$f', "`"$File`"")
    # cmd /s strips only the outer quotes, so the command runs exactly as written.
    $process = Start-Process -FilePath 'cmd.exe' -ArgumentList "/d /s /c `"$command`"" -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -ne 0) { throw "Signing $(Split-Path $File -Leaf) failed with exit code $($process.ExitCode)" }
}

Push-Location $root
try {
    if (-not $Version) {
        $Version = ([xml](Get-Content 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version |
            Where-Object { $_ } | Select-Object -First 1
        if (-not $Version) { throw 'No -Version given and none found in Directory.Build.props.' }
    }

    if (-not $Iscc) {
        $Iscc = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
        ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    }
    if (-not $Iscc -or -not (Test-Path $Iscc)) {
        throw 'ISCC.exe not found. Run installer/install-inno.ps1, set ISCC_PATH, or pass -Iscc.'
    }

    # Relative paths are relative to the repository, absolute ones are taken as they are.
    function Resolve-Full([string]$Path) {
        if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
        return [System.IO.Path]::GetFullPath((Join-Path $root $Path))
    }
    $stagingPath = Resolve-Full $Staging
    $outputPath = Resolve-Full $Output
    Write-Host "UEBulkExport $Version -> $outputPath"

    if (-not $SkipPublish) {
        if (Test-Path $stagingPath) { Remove-Item $stagingPath -Recurse -Force }
        foreach ($project in 'src/UEBulkExport', 'src/UEBulkExport.Cli') {
            Invoke-Native "Publish $project" {
                dotnet publish $project -c Release -r $Runtime --self-contained true "-p:Version=$Version" -o $stagingPath -warnaserror --nologo
            }
        }
    }
    elseif (-not (Test-Path (Join-Path $stagingPath 'UEBulkExport.exe'))) {
        throw "-SkipPublish was given, but $stagingPath holds no published build."
    }

    Write-Host '==> Add the offline documentation'
    foreach ($file in 'README.md', 'README.ru.md', 'LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md') {
        Copy-Item $file $stagingPath -Force
    }
    $docs = New-Item -ItemType Directory -Force (Join-Path $stagingPath 'docs')
    foreach ($file in 'docs/mappings.md', 'docs/mappings.ru.md', 'docs/plugins.md', 'docs/plugins.ru.md',
        'docs/screenshot.png') {
        Copy-Item $file $docs.FullName -Force
    }
    Copy-Item 'docs/images' $docs.FullName -Recurse -Force
    Copy-Item 'tools' $stagingPath -Recurse -Force

    if ($ExtraPluginsPath) {
        $pluginSource = Resolve-Full $ExtraPluginsPath
        if (-not (Test-Path -LiteralPath $pluginSource -PathType Container)) {
            throw "Plugin package root not found: $pluginSource"
        }
        $pluginDestination = New-Item -ItemType Directory -Force (Join-Path $stagingPath 'plugins')
        foreach ($plugin in Get-ChildItem -LiteralPath $pluginSource -Directory) {
            if (-not (Test-Path -LiteralPath (Join-Path $plugin.FullName 'plugin.json') -PathType Leaf)) {
                throw "Plugin package has no plugin.json: $($plugin.FullName)"
            }
            Copy-Item -LiteralPath $plugin.FullName -Destination $pluginDestination.FullName -Recurse -Force
        }
    }

    Invoke-Native 'Generate the wizard artwork' {
        # No --nologo here: dotnet run does not know it and would hand it to the generator.
        dotnet run --project (Join-Path $root 'installer/branding/generator') -c Release -- `
            (Join-Path $root 'installer/branding') (Join-Path $root 'src/UEBulkExport/Assets') (Join-Path $root 'docs/images')
    }

    if ($SignCommand) {
        Write-Host '==> Sign the executables'
        foreach ($exe in 'UEBulkExport.exe', 'UEBulkExport.Cli.exe') { Invoke-Sign (Join-Path $stagingPath $exe) }
    }

    $arguments = @('/Q', "/DAppVersion=$Version", "/DSourceDir=$stagingPath", "/DOutputDir=$outputPath")
    if ($TestIdentity) {
        $arguments += '/DAppGuid=0B7F2E54-3C1D-4E0A-9D61-7A5C2B9E8F10'
        $arguments += '/DAppName=UEBulkExport Test'
    }
    if ($SignCommand) {
        $arguments += '/DSignInstaller'
        $arguments += "/Ssigntool=$SignCommand"
    }
    Invoke-Native 'Compile the installer' { & $Iscc @arguments 'installer/UEBulkExport.iss' }

    $setup = Join-Path $outputPath "UEBulkExport-$Version-$Runtime-setup.exe"
    if (-not (Test-Path $setup)) { throw "The compiler reported success, but $setup does not exist." }

    # Lets anyone check that the file they downloaded is the one that was built.
    $hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $setup -Leaf)" | Set-Content (Join-Path $outputPath 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "==> $(Split-Path $setup -Leaf)  $([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB  sha256 $hash"

    # UTF-8 without a byte order mark, which Out-File in Windows PowerShell would add.
    if ($env:GITHUB_OUTPUT) { [IO.File]::AppendAllText($env:GITHUB_OUTPUT, "installer=$setup`n") }
    $setup
}
finally {
    Pop-Location
}
