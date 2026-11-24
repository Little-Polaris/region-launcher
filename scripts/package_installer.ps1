param([Parameter(Mandatory)][string]$Zip, [Parameter(Mandatory)][string]$Version)
# Builds RegionLauncher-Setup-<version>-win-x64.exe around the release ZIP and writes the release checksum file for both.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$zipPath = [IO.Path]::GetFullPath($Zip)
$packages = Split-Path $zipPath -Parent
$out = Join-Path $root 'build/installer-bin'
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
& dotnet publish (Join-Path $root 'installer/RegionSetup.csproj') -c Release -o $out "-p:PayloadZip=$zipPath" "-p:Version=$Version" --nologo -v q
if ($LASTEXITCODE) { throw 'Installer build failed' }
$setup = Join-Path $packages "RegionLauncher-Setup-$Version-win-x64.exe"
Copy-Item -LiteralPath (Join-Path $out 'RegionLauncherSetup.exe') -Destination $setup -Force
$lines = foreach ($file in @($zipPath, $setup)) { "$((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $file -Leaf)" }
[IO.File]::WriteAllLines((Join-Path $packages "RegionLauncher-$Version-SHA256SUMS.txt"), [string[]]$lines)
Write-Output "Installer: $setup"
