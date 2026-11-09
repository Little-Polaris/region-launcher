param([string]$BinDirectory = '', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$bin = if ($BinDirectory) { [IO.Path]::GetFullPath($BinDirectory) } else { Join-Path $root 'build/service-bin' }
$workspace = Split-Path (Split-Path $root -Parent) -Parent
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $workspace 'outputs/region-launcher-unified' }
$app = Join-Path $output 'app'
$docs = Join-Path $output 'docs'
$source = Join-Path $output 'source'
$packages = Join-Path $output 'packages'
$licenses = Join-Path $app 'licenses'
$version = ([xml](Get-Content "$root/src/RegionLauncher.csproj" -Raw)).Project.PropertyGroup.Version
New-Item -ItemType Directory -Force -Path $app,$docs,$source,$packages,$licenses | Out-Null
$files = @('RegionLauncher.exe','RegionLauncher.dll','RegionLauncher.deps.json','RegionLauncher.runtimeconfig.json','RegionSession.exe','RegionHook.dll','RegionProbe.exe','DnsProbe.exe')
foreach ($file in $files) {
    $from = Join-Path $bin $file; $to = Join-Path $app $file
    if (!(Test-Path $to) -or (Get-FileHash $from).Hash -ne (Get-FileHash $to).Hash) {
        Copy-Item -LiteralPath $from -Destination $to -Force
    }
}
if (Test-Path "$bin/proxy") {
    New-Item -ItemType Directory -Force -Path "$app/proxy" | Out-Null
    foreach ($item in Get-ChildItem "$bin/proxy" -File) {
        $to = Join-Path "$app/proxy" $item.Name
        if (!(Test-Path $to) -or (Get-FileHash $item.FullName).Hash -ne (Get-FileHash $to).Hash) {
            Copy-Item -LiteralPath $item.FullName -Destination $to -Force
        }
    }
}
Copy-Item -LiteralPath "$root/LICENSE" -Destination "$licenses/RegionLauncher-MIT.txt" -Force
Copy-Item -LiteralPath "$root/vendor/detours/LICENSE.md" -Destination "$licenses/Detours-MIT.txt" -Force
Copy-Item -LiteralPath "$root/vendor/proxybridge/LICENSE" -Destination "$licenses/ProxyBridge-MIT.txt" -Force
Copy-Item -LiteralPath "$root/vendor/windivert/WinDivert-2.2.2-A/LICENSE" -Destination "$licenses/WinDivert-LICENSE.txt" -Force
Copy-Item -LiteralPath "$root/README.md" -Destination "$output/使用说明.md" -Force
Get-ChildItem -LiteralPath "$root/docs" -Filter '*.md' -File | Copy-Item -Destination $docs -Force
& (Join-Path $PSScriptRoot 'package_service.ps1') -BinDirectory $bin -OutputDirectory $output
if (Test-Path -LiteralPath "$root/build/test-results/launcher.png") { Copy-Item -LiteralPath "$root/build/test-results/launcher.png" -Destination "$docs/launcher.png" -Force }
Push-Location $root
try {
    $commit = git rev-parse HEAD
    git archive --format=zip --output="$source/RegionLauncher-$version-source.zip" HEAD
    if ($LASTEXITCODE) { throw 'Source archive failed' }
    Set-Content -LiteralPath "$docs/build-commit.txt" -Value $commit -Encoding utf8
} finally { Pop-Location }
$hashFiles = @($files)
if (Test-Path "$app/proxy/mihomo.exe") { $hashFiles += 'proxy/mihomo.exe' }
$hashes = foreach ($file in $hashFiles) { $hash = Get-FileHash -LiteralPath (Join-Path $app $file) -Algorithm SHA256; "$($hash.Hash.ToLowerInvariant())  app/$file" }
$serviceFiles = @(Get-ChildItem -LiteralPath (Join-Path $output 'install') -File -Recurse) + @(Get-ChildItem -LiteralPath $output -Filter '*.cmd' -File)
$hashes += foreach ($file in $serviceFiles) { $hash = Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256; "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetRelativePath($output,$file.FullName).Replace('\','/'))" }
Set-Content -LiteralPath "$output/SHA256SUMS.txt" -Value $hashes -Encoding utf8
# Assemble a clean package. Never zip the live app/data (browser sessions or node secrets).
$stage = Join-Path "$root/build" ('package-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$stage/app" -Force | Out-Null
foreach ($file in $files) { Copy-Item -LiteralPath "$app/$file" -Destination "$stage/app" }
Copy-Item -LiteralPath $licenses -Destination "$stage/app" -Recurse
if (Test-Path "$app/proxy") { Copy-Item -LiteralPath "$app/proxy" -Destination "$stage/app" -Recurse }
Copy-Item -LiteralPath $docs -Destination $stage -Recurse
Copy-Item -LiteralPath (Join-Path $output 'install') -Destination $stage -Recurse
Get-ChildItem -LiteralPath $output -Filter '*.cmd' -File | Copy-Item -Destination $stage
Copy-Item -LiteralPath "$output/使用说明.md","$output/SHA256SUMS.txt" -Destination $stage
Compress-Archive -LiteralPath "$stage/app","$stage/docs","$stage/install","$stage/安装或更新后台服务.cmd","$stage/卸载后台服务.cmd","$stage/使用说明.md","$stage/SHA256SUMS.txt" -DestinationPath "$packages/RegionLauncher-$version-win-x64.zip" -Force
Write-Output "Packaged: $output"
