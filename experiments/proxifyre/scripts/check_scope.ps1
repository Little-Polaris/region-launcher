$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$source = Join-Path $root 'build/research/network-backend/source/proxifyre-5e3bc845d73a47a0931b1233c745580cb916f4ba'
$policy = Join-Path $source 'netlib/src/proxy/process_routing_policy.h'
if ((Get-FileHash -LiteralPath $policy).Hash -ne '6AA9E7DC770643DFEFCD98C598A5F0F3F82DEBEF9810B2D2816F99DB98EFA200') {
    throw 'Expected the reviewed upstream policy header; source was changed or is missing.'
}
$out = Join-Path $root 'build/research/network-backend/scope-probe'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Visual Studio C++ compiler not found' }
& (Join-Path $vs 'Common7/Tools/Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
Push-Location $out
try {
    & cl.exe /nologo /EHsc /std:c++17 /W4 /utf-8 /MT "/I$(Split-Path $policy -Parent)" (Join-Path $PSScriptRoot '../src/ScopeProbe.cpp') /Fe:ScopeProbe.exe
    if ($LASTEXITCODE) { throw 'Scope probe build failed' }
    & ./ScopeProbe.exe | Tee-Object -FilePath results.txt
    if ($LASTEXITCODE) { throw 'Scope probe failed' }
} finally { Pop-Location }
