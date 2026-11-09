param([Parameter(Mandatory)][string]$BinDirectory, [int]$Cycles = 12)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$env:REGION_TEST_BIN = (Resolve-Path -LiteralPath $BinDirectory).Path
foreach ($file in @('RegionNetworkService.exe','ProxyBridgeCore.dll','WinDivert.dll','WinDivert64.sys')) {
    if ((Get-FileHash (Join-Path $env:REGION_TEST_BIN $file)).Hash -ne
        (Get-FileHash (Join-Path "$env:ProgramFiles/RegionLauncherNetwork" $file)).Hash) {
        throw "Installed backend differs from the tested build: $file"
    }
}
if ((Get-Service RegionLauncherNetwork).Status -ne 'Running') { throw 'Network service not running' }
$out = Join-Path $root ('build/test-results/lifecycle-host-' + (Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory -Path $out -Force | Out-Null
function Snapshot {
    [ordered]@{
        dns = @(Get-DnsClientServerAddress | Sort-Object InterfaceIndex,AddressFamily | Select-Object InterfaceIndex,AddressFamily,ServerAddresses)
        routes = @(Get-NetRoute | Sort-Object InterfaceIndex,DestinationPrefix,NextHop | Select-Object InterfaceIndex,DestinationPrefix,NextHop,RouteMetric)
        ipv6 = @(Get-NetAdapterBinding -ComponentID ms_tcpip6 | Sort-Object Name | Select-Object Name,Enabled)
        proxy = Get-ItemProperty 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Internet Settings' | Select-Object ProxyEnable,ProxyServer,AutoConfigURL
        outsideProxyProcesses = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -in @('clashmi.exe','clashmiService.exe') } | Sort-Object ProcessId | Select-Object Name,ProcessId,CreationDate)
    } | ConvertTo-Json -Depth 6
}
$result = [ordered]@{passed=$false; cycles=$Cycles; bin=$env:REGION_TEST_BIN}
$before = Snapshot
Set-Content -LiteralPath (Join-Path $out before.json) -Value $before -Encoding utf8
try {
    & python -X utf8 (Join-Path $root 'tests/test_lifecycle.py') --cycles $Cycles --online 2>&1 | Tee-Object -FilePath (Join-Path $out traffic.log)
    if ($LASTEXITCODE) { throw 'Outside traffic or environment lifecycle failed' }
    $after = Snapshot
    if ($before -ne $after) { throw 'Host network settings or outside proxy processes changed' }
    $result.passed = $true
    Write-Output 'PASS: host proxy, DNS, routes, IPv6 bindings and outside proxy processes unchanged'
} catch {
    $result.error = $_.Exception.Message
    throw
} finally {
    Snapshot | Set-Content -LiteralPath (Join-Path $out after.json) -Encoding utf8
    $result.finished = Get-Date -Format o
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out results.json) -Encoding utf8
    Write-Output "Lifecycle evidence: $out"
}
