<#
.SYNOPSIS
    Downloads Inno Setup, checks it against a pinned SHA-256 hash and installs it portably.

.DESCRIPTION
    Used by the CI and release workflows, and handy on a fresh development machine. Prints the path
    of ISCC.exe and, on GitHub Actions, also exports it as ISCC_PATH for the following steps.

.EXAMPLE
    ./installer/install-inno.ps1 -Destination "$env:TEMP\inno-setup"
#>
[CmdletBinding()]
param(
    # Where Inno Setup is installed. Defaults to the runner's temporary folder, or %TEMP% locally.
    [string]$Destination = (Join-Path ($(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP })) 'inno-setup')
)

$ErrorActionPreference = 'Stop'

# Update both together; the hash is of the official installer from the Inno Setup GitHub release.
$version = '6.7.3'
$url = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'
$expectedHash = '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'

$iscc = Join-Path $Destination 'ISCC.exe'
if (Test-Path $iscc) {
    Write-Host "Inno Setup is already installed at $Destination"
}
else {
    $installer = Join-Path ([System.IO.Path]::GetTempPath()) "innosetup-$version.exe"
    Write-Host "Downloading Inno Setup $version"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -OutFile $installer -UseBasicParsing

    $actualHash = (Get-FileHash $installer -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) {
        Remove-Item $installer -Force
        throw "Inno Setup checksum mismatch: expected $expectedHash, got $actualHash"
    }

    $process = Start-Process -FilePath $installer -Wait -PassThru -WindowStyle Hidden -ArgumentList @(
        '/PORTABLE=1', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/CURRENTUSER', '/NORESTART', "/DIR=`"$Destination`"")
    Remove-Item $installer -Force
    if ($process.ExitCode -ne 0) { throw "Inno Setup installation failed with exit code $($process.ExitCode)" }
    if (-not (Test-Path $iscc)) { throw "ISCC.exe was not found in $Destination after installation" }
    Write-Host "Installed Inno Setup $version to $Destination"
}

# UTF-8 without a byte order mark, which Out-File in Windows PowerShell would add.
if ($env:GITHUB_ENV) { [IO.File]::AppendAllText($env:GITHUB_ENV, "ISCC_PATH=$iscc`n") }
$iscc
