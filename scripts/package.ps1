$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$workspace = Split-Path (Split-Path $root -Parent) -Parent
$output = Join-Path $workspace 'outputs/region-launcher'
$app = Join-Path $output 'app'
$docs = Join-Path $output 'docs'
$source = Join-Path $output 'source'
$packages = Join-Path $output 'packages'
$licenses = Join-Path $app 'licenses'
New-Item -ItemType Directory -Force -Path $app,$docs,$source,$packages,$licenses | Out-Null
$files = @('RegionLauncher.exe','RegionLauncher.dll','RegionLauncher.deps.json','RegionLauncher.runtimeconfig.json','RegionSession.exe','RegionHook.dll','RegionProbe.exe','ProxyBridgeCore.dll','WinDivert.dll','WinDivert64.sys')
foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path "$root/build/bin" $file) -Destination (Join-Path $app $file) -Force }
Copy-Item -LiteralPath "$root/LICENSE" -Destination "$licenses/RegionLauncher-MIT.txt" -Force
Copy-Item -LiteralPath "$root/vendor/detours/LICENSE.md" -Destination "$licenses/Detours-MIT.txt" -Force
Copy-Item -LiteralPath "$root/vendor/proxybridge/LICENSE" -Destination "$licenses/ProxyBridge-MIT.txt" -Force
Copy-Item -LiteralPath "$root/vendor/windivert/WinDivert-2.2.2-A/LICENSE" -Destination "$licenses/WinDivert-LICENSE.txt" -Force
Copy-Item -LiteralPath "$root/README.md" -Destination "$output/使用说明.md" -Force
Copy-Item -LiteralPath "$root/docs/SOURCES.md","$root/docs/validation.md" -Destination $docs -Force
if (Test-Path -LiteralPath "$root/build/test-results/launcher.png") { Copy-Item -LiteralPath "$root/build/test-results/launcher.png" -Destination "$docs/launcher.png" -Force }
Push-Location $root
try {
    $commit = git rev-parse HEAD
    git archive --format=zip --output="$source/RegionLauncher-0.1.0-source.zip" HEAD
    if ($LASTEXITCODE) { throw 'Source archive failed' }
    Set-Content -LiteralPath "$docs/build-commit.txt" -Value $commit -Encoding utf8
} finally { Pop-Location }
$hashes = foreach ($file in $files) { $hash = Get-FileHash -LiteralPath (Join-Path $app $file) -Algorithm SHA256; "$($hash.Hash.ToLowerInvariant())  app/$file" }
Set-Content -LiteralPath "$output/SHA256SUMS.txt" -Value $hashes -Encoding utf8
Compress-Archive -LiteralPath $app,$docs,"$output/使用说明.md","$output/SHA256SUMS.txt" -DestinationPath "$packages/RegionLauncher-0.1.0-win-x64.zip" -Force
Write-Output "Packaged: $output"
