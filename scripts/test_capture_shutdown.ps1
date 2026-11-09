$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'build/capture-shutdown-test'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
& (Join-Path $vs 'Common7/Tools/Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
$wd = Join-Path $root 'vendor/windivert/WinDivert-2.2.2-A'
$sources = @(Get-ChildItem (Join-Path $root 'vendor/proxybridge') -Filter 'pb_*.c' | ForEach-Object FullName)
Push-Location $out
try {
    & cl.exe /nologo /O2 /MT /utf-8 /DPROXYBRIDGE_EXPORTS /D_CRT_SECURE_NO_WARNINGS /D_WINSOCK_DEPRECATED_NO_WARNINGS "/I$wd/include" (Join-Path $root 'tests/CaptureShutdown.c') @sources /link "/LIBPATH:$wd/x64" WinDivert.lib ws2_32.lib iphlpapi.lib advapi32.lib /OUT:CaptureShutdown.exe
    if ($LASTEXITCODE) { throw 'Capture shutdown test compilation failed' }
    Copy-Item "$wd/x64/WinDivert.dll" $out -Force
    & ./CaptureShutdown.exe
    if ($LASTEXITCODE) { throw 'Capture shutdown dropped an outside packet' }
    & ./CaptureShutdown.exe startup-failure
    if ($LASTEXITCODE) { throw 'Capture without a worker dropped an outside packet' }
} finally { Pop-Location }
