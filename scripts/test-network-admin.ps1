$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$result = Join-Path $root 'build/test-results/network-admin.txt'
Push-Location $root
try {
    & 'G:\python314\python.exe' (Join-Path $root 'tests/test_network.py') *> $result
    $code = $LASTEXITCODE
    Add-Content -LiteralPath $result -Value "EXIT_CODE=$code"
    if ($code -eq 0) {
        & 'G:\python314\python.exe' (Join-Path $root 'tests/test_browser_network.py') *>> $result
        Add-Content -LiteralPath $result -Value "BROWSER_EXIT_CODE=$LASTEXITCODE"
    }
} catch {
    $_ | Out-String | Add-Content -LiteralPath $result
    Add-Content -LiteralPath $result -Value 'EXIT_CODE=1'
} finally { Pop-Location }
