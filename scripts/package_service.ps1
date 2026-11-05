param([Parameter(Mandatory)][string]$OutputDirectory, [string]$BinDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$BinDirectory) { $BinDirectory = Join-Path $root 'build/service-bin' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$package = Join-Path $output 'install/package'
New-Item -ItemType Directory -Force -Path $package,(Join-Path $output docs),(Join-Path $output evidence),(Join-Path $output source) | Out-Null
$hashes = [ordered]@{}
foreach ($file in @('RegionNetworkService.exe','ProxyBridgeCore.dll','WinDivert.dll','WinDivert64.sys')) {
    Copy-Item -LiteralPath (Join-Path $BinDirectory $file) -Destination $package -Force
    $hashes[$file] = (Get-FileHash -LiteralPath (Join-Path $package $file) -Algorithm SHA256).Hash
}
@{product='RegionLauncherNetwork'; files=$hashes} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package manifest.json) -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot install_service.ps1) -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot request_install.ps1) -Destination (Join-Path $output install) -Force
$entry = '@echo off' + "`r`n" + 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install\request_install.ps1"' + "`r`n" + 'pause' + "`r`n"
[IO.File]::WriteAllText((Join-Path $output '安装或更新后台服务.cmd'),$entry,[Text.Encoding]::ASCII)
[IO.File]::WriteAllText((Join-Path $output '卸载后台服务.cmd'),$entry.Replace('request_install.ps1"','request_install.ps1" -Uninstall'),[Text.Encoding]::ASCII)
foreach ($file in @('service.md','SOURCES.md')) { Copy-Item -LiteralPath (Join-Path $root "docs/$file") -Destination (Join-Path $output docs) -Force }
Write-Output "Prepared service package: $output"
