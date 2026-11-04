$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$build = Join-Path $root 'build'
$bin = Join-Path $build 'bin'
New-Item -ItemType Directory -Force -Path $build,$bin | Out-Null
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Visual Studio C++ compiler not found' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvarsall.bat'
$lines = & cmd.exe /d /s /c "`"`"$vcvars`" x64 >nul && set`""
if ($LASTEXITCODE) { throw 'Compiler environment initialization failed' }
foreach ($line in $lines) { if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process') } }
$detours = Join-Path $root 'vendor/detours'
$pb = Join-Path $root 'vendor/proxybridge'
$wd = Join-Path $root 'vendor/windivert/WinDivert-2.2.2-A'
function RunCompiler([string[]]$CompilerArgs) {
    & cl.exe @CompilerArgs
    if ($LASTEXITCODE) { throw 'C++ compilation failed' }
}
Push-Location $build
try {
    $sources = @('detours.cpp','modules.cpp','disasm.cpp','image.cpp','creatwth.cpp','disolx86.cpp','disolx64.cpp','disolia64.cpp','disolarm.cpp','disolarm64.cpp') | ForEach-Object { Join-Path $detours $_ }
    RunCompiler (@('/nologo','/c','/O2','/MT','/EHsc','/DDETOURS_X64',"/I$detours") + $sources)
    & lib.exe /nologo /OUT:detours.lib detours.obj modules.obj disasm.obj image.obj creatwth.obj disolx86.obj disolx64.obj disolia64.obj disolarm.obj disolarm64.obj
    if ($LASTEXITCODE) { throw 'Detours library failed' }
    RunCompiler @('/nologo','/O2','/MT','/EHsc','/std:c++17','/utf-8',"/I$detours",(Join-Path $root 'src/RegionHook.cpp'),'/LD','/link','detours.lib',"/DEF:$(Join-Path $root 'src/RegionHook.def')",'/DYNAMICBASE','/NXCOMPAT',"/OUT:$bin/RegionHook.dll")
    $sources = Get-ChildItem -LiteralPath $pb -Filter '*.c' | ForEach-Object FullName
    RunCompiler (@('/nologo','/O2','/MT','/utf-8','/DPROXYBRIDGE_EXPORTS','/D_CRT_SECURE_NO_WARNINGS','/D_WINSOCK_DEPRECATED_NO_WARNINGS',"/I$wd/include") + $sources + @('/LD','/link',"/LIBPATH:$wd/x64",'WinDivert.lib','ws2_32.lib','iphlpapi.lib','advapi32.lib','/DYNAMICBASE','/NXCOMPAT',"/OUT:$bin/ProxyBridgeCore.dll"))
    RunCompiler @('/nologo','/O2','/MT','/EHsc','/std:c++17','/utf-8',"/I$detours", "/I$pb",(Join-Path $root 'src/Session.cpp'),'/link','detours.lib','shell32.lib','ws2_32.lib','/DYNAMICBASE','/NXCOMPAT',"/OUT:$bin/RegionSession.exe")
    RunCompiler @('/nologo','/O2','/MT','/EHsc','/std:c++17','/utf-8',(Join-Path $root 'tests/Probe.cpp'),'/link','ws2_32.lib','/DYNAMICBASE','/NXCOMPAT',"/OUT:$bin/RegionProbe.exe")
    Copy-Item -LiteralPath "$wd/x64/WinDivert.dll","$wd/x64/WinDivert64.sys" -Destination $bin -Force
    & dotnet publish (Join-Path $root 'src/RegionLauncher.csproj') -c Release -o $bin --nologo
    if ($LASTEXITCODE) { throw 'Launcher compilation failed' }
} finally { Pop-Location }
Write-Output "Built: $bin"
