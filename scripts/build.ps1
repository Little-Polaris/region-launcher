param([string]$BinDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$build = Join-Path $root 'build'
$bin = if ($BinDirectory) { [IO.Path]::GetFullPath($BinDirectory) } else { Join-Path $build 'service-bin' }
if (!$bin.StartsWith($build + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Build output must stay under build/' }
New-Item -ItemType Directory -Force -Path $build,$bin | Out-Null
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Visual Studio C++ compiler not found' }
& (Join-Path $vs 'Common7/Tools/Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
if (!(Get-Command cl.exe -ErrorAction SilentlyContinue)) { throw 'Compiler environment initialization failed' }
$detours = Join-Path $root 'vendor/detours'
$pb = Join-Path $root 'vendor/proxybridge'
$wd = Join-Path $root 'vendor/windivert/WinDivert-2.2.2-A'
if (!(Test-Path -LiteralPath $wd)) {
    $archive = Join-Path $root 'vendor/WinDivert-2.2.2-A.zip'
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '63CB41763BB4B20F600B6DE04E991A9C2BE73279E317D4D82F237B150C5F3F15') { throw 'WinDivert package checksum mismatch' }
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $root 'vendor/windivert')
}
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
    RunCompiler @('/nologo','/O2','/MT','/EHsc','/std:c++17','/utf-8',"/I$detours",(Join-Path $root 'src/RegionHook.cpp'),(Join-Path $root 'src/UnifiedDns.cpp'),(Join-Path $root 'src/SystemProxy.cpp'),(Join-Path $root 'src/SystemProxyWinInet.cpp'),(Join-Path $root 'src/ConnectHooks.cpp'),(Join-Path $root 'src/DatagramHooks.cpp'),'/LD','/link','detours.lib','advapi32.lib','dnsapi.lib','ws2_32.lib','iphlpapi.lib','winhttp.lib','wininet.lib',"/DEF:$(Join-Path $root 'src/RegionHook.def')",'/DYNAMICBASE','/NXCOMPAT',"/OUT:$bin/RegionHook.dll")
    $sources = Get-ChildItem -LiteralPath $pb -Filter '*.c' | ForEach-Object FullName
    RunCompiler (@('/nologo','/O2','/MT','/utf-8','/DPROXYBRIDGE_EXPORTS','/D_CRT_SECURE_NO_WARNINGS','/D_WINSOCK_DEPRECATED_NO_WARNINGS',"/I$wd/include") + $sources + @('/LD','/link',"/LIBPATH:$wd/x64",'WinDivert.lib','ws2_32.lib','iphlpapi.lib','advapi32.lib','/DYNAMICBASE','/NXCOMPAT',"/OUT:$bin/ProxyBridgeCore.dll"))
    RunCompiler @('/nologo','/O2','/MT','/EHsc','/std:c++17','/utf-8',"/I$detours", "/I$pb",(Join-Path $root 'src/Session.cpp'),(Join-Path $root 'src/ConnectBroker.cpp'),'/link','detours.lib','shell32.lib','ws2_32.lib','iphlpapi.lib','advapi32.lib','/DYNAMICBASE','/NXCOMPAT',"/OUT:$bin/RegionSession.exe")
    RunCompiler @('/nologo','/O2','/MT','/EHsc','/std:c++17','/utf-8',"/I$pb",(Join-Path $root 'src/NetworkService.cpp'),'/link','advapi32.lib','ws2_32.lib','/DYNAMICBASE','/NXCOMPAT',"/OUT:$bin/RegionNetworkService.exe")
    RunCompiler @('/nologo','/O2','/MT','/EHsc','/std:c++17','/utf-8',(Join-Path $root 'tests/Probe.cpp'),'/link','ws2_32.lib','/DYNAMICBASE','/NXCOMPAT',"/OUT:$bin/RegionProbe.exe")
    RunCompiler @('/nologo','/O2','/MT','/EHsc','/std:c++17','/utf-8',(Join-Path $root 'tests/DnsProbe.cpp'),'/link','ws2_32.lib','dnsapi.lib','iphlpapi.lib',"/OUT:$bin/DnsProbe.exe")
    RunCompiler @('/nologo','/O2','/MT','/EHsc','/std:c++17','/utf-8',(Join-Path $root 'tests/DatagramProbe.cpp'),'/link','ws2_32.lib',"/OUT:$build/DatagramProbe.exe")
    RunCompiler @('/nologo','/O2','/MT','/EHsc','/std:c++17','/utf-8',"/I$detours","/I$root/src",(Join-Path $root 'tests/ConnectHarness.cpp'),(Join-Path $root 'src/ConnectBroker.cpp'),'/link','detours.lib','ws2_32.lib','iphlpapi.lib','advapi32.lib',"/OUT:$build/ConnectHarness.exe")
    foreach ($file in @('WinDivert.dll','WinDivert64.sys')) {
        $source = Join-Path "$wd/x64" $file; $target = Join-Path $bin $file
        if (!(Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
            Copy-Item -LiteralPath $source -Destination $target -Force
        }
    }
    & dotnet publish (Join-Path $root 'src/RegionLauncher.csproj') -c Release -o $bin --nologo
    if ($LASTEXITCODE) { throw 'Launcher compilation failed' }
} finally { Pop-Location }
Write-Output "Built: $bin"
