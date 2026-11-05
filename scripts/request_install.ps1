param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$package = Join-Path $PSScriptRoot 'package'
$evidence = Join-Path (Split-Path $PSScriptRoot -Parent) 'evidence'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$resultPath = Join-Path $evidence ('service-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$installer = Join-Path $package 'install_service.ps1'
$arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $installer + '" -PackageDirectory "' + $package + '" -AllowedSid "' + $sid + '" -ResultPath "' + $resultPath + '"'
if ($Uninstall) { $arguments += ' -Uninstall' }
try {
    $process = Start-Process -FilePath (Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe') -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru -Wait
    if (!(Test-Path -LiteralPath $resultPath)) { throw 'The installer did not produce a result.' }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if (!$result.success) { throw $result.error }
    Write-Host 'Completed. Result:' $resultPath
} catch { Write-Host 'Not completed:' $_.Exception.Message -ForegroundColor Red; exit 1 }
