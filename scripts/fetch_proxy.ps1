$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$download = Join-Path $root 'build/downloads'
$target = Join-Path $root 'build/service-bin/proxy'
New-Item -ItemType Directory -Force -Path $download,$target | Out-Null
$url = 'https://github.com/MetaCubeX/mihomo/releases/download/v1.19.33/mihomo-windows-amd64-compatible-v1.19.33.zip'
$zip = Join-Path $download 'mihomo.zip'
$expected = '7aaf636a4f2172dcd8ec34ac4f52797b059195e5afd6409508c963e545f8864d'
if (!(Test-Path $zip) -or (Get-FileHash $zip).Hash -ne $expected) {
    & curl.exe -fL --proxy http://127.0.0.1:7890 --max-time 180 $url -o $zip
    if ($LASTEXITCODE) { throw 'Official proxy download failed' }
}
if ((Get-FileHash $zip).Hash -ne $expected) { throw 'Official proxy archive hash mismatch' }
Expand-Archive -LiteralPath $zip -DestinationPath "$download/mihomo" -Force
Get-ChildItem "$download/mihomo" -Filter '*.exe' | Copy-Item -Destination "$target/mihomo.exe" -Force
& curl.exe -fsSL --proxy http://127.0.0.1:7890 --max-time 30 https://raw.githubusercontent.com/MetaCubeX/mihomo/v1.19.33/LICENSE -o "$target/LICENSE"
if ($LASTEXITCODE) { throw 'License download failed' }
