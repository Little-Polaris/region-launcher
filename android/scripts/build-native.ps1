param([string]$Proxy = '', [string[]]$Architectures = @('arm64', 'amd64'), [switch]$Trace)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$tools = Join-Path $root 'build/android-tools'
$go = Join-Path $tools 'go/go/bin/go.exe'
$clang = Join-Path $tools 'ndk/android-ndk-r28c/toolchains/llvm/prebuilt/windows-x86_64/bin/clang.exe'
if (!(Test-Path $go) -or !(Test-Path $clang)) { throw 'Run prepare-tools.py go ndk first' }
$env:GOMODCACHE = Join-Path $tools 'gomod'
$env:GOCACHE = Join-Path $tools 'go-cache'
$env:CGO_ENABLED = '1'
$env:GOOS = 'android'
$env:CGO_LDFLAGS = '-Wl,-z,max-page-size=16384'
if ($Proxy) { $env:HTTPS_PROXY=$Proxy; $env:HTTP_PROXY=$Proxy }
Push-Location (Join-Path $root 'android/native')
try {
    foreach ($architecture in $Architectures) {
        if ($architecture -eq 'arm64') { $abi='arm64-v8a'; $target='aarch64-linux-android28' }
        elseif ($architecture -eq 'amd64') { $abi='x86_64'; $target='x86_64-linux-android28' }
        else { throw "Unsupported architecture: $architecture" }
        $env:GOARCH=$architecture
        $env:CC='"'+$clang+'" --target='+$target
        $out=Join-Path $root "android/app/src/main/jniLibs/$abi"
        New-Item -ItemType Directory -Force $out | Out-Null
        $traceArgs = @(); if ($Trace) { $traceArgs = @('-x', '-work') }
        & $go build @traceArgs -mod=readonly -tags 'with_gvisor,cmfa' -buildmode=c-shared -trimpath -ldflags '-s -w -checklinkname=0 -X github.com/metacubex/mihomo/constant.Version=v1.19.33' -o "$out/libregioncore.so" .
        if ($LASTEXITCODE) { throw "Native build failed: $abi" }
    }
} finally { Pop-Location }
