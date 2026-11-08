param([string]$BinDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$BinDirectory) { $BinDirectory = Join-Path $root 'build/service-bin' }
$env:REGION_TEST_BIN = (Resolve-Path -LiteralPath $BinDirectory).Path
$service = Get-Service -Name RegionLauncherNetwork -ErrorAction SilentlyContinue
if (!$service -or $service.Status -ne 'Running') {
    throw 'Network service is not running. Complete service installation first; no test applications were started.'
}
$relayRules = @(Get-NetFirewallRule -Name 'RegionLauncherNetwork-TCP','RegionLauncherNetwork-UDP' -ErrorAction SilentlyContinue | Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' })
if ($relayRules.Count -ne 2) { throw 'Service relay permissions are missing. Install the service update before network verification.' }
$out = Join-Path $root 'build/test-results'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$statePath = Join-Path $out 'unified-suite.json'
$logPath = Join-Path $out 'unified-suite.log'
function Snapshot {
    [ordered]@{
        dns = @(Get-DnsClientServerAddress | Sort-Object InterfaceIndex,AddressFamily | Select-Object InterfaceIndex,AddressFamily,ServerAddresses)
        routes = @(Get-NetRoute | Sort-Object InterfaceIndex,DestinationPrefix,NextHop | Select-Object InterfaceIndex,DestinationPrefix,NextHop,RouteMetric)
        ipv6 = @(Get-NetAdapterBinding -ComponentID ms_tcpip6 | Sort-Object Name | Select-Object Name,Enabled)
        proxy = Get-ItemProperty 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Internet Settings' | Select-Object ProxyEnable,ProxyServer,AutoConfigURL
    } | ConvertTo-Json -Depth 6
}
Push-Location $root
$passed = @()
try {
    $before = Snapshot
    Set-Content -LiteralPath $logPath -Value 'Unified network / IPv4-only verification' -Encoding utf8
    foreach ($test in @('test_service.py','test_system_proxy.py','test_unified_dns.py','test_network.py','test_unified_live.py')) {
        $testArgs = @('-X','utf8',"tests/$test")
        if ($test -eq 'test_unified_dns.py') { $testArgs += @('--network','--failure') }
        & python @testArgs *>> $logPath
        if ($LASTEXITCODE) { throw "$test failed; see $logPath" }
        $passed += $test
    }
    $after = Snapshot
    if ($before -ne $after) { throw 'Host network configuration changed during the test; inspect snapshots.' }
    [ordered]@{passed=$passed; hostUnchanged=$true; finished=(Get-Date -Format o)} | ConvertTo-Json | Set-Content $statePath -Encoding utf8
} catch {
    [ordered]@{passed=$passed; error=$_.Exception.Message; finished=(Get-Date -Format o)} | ConvertTo-Json | Set-Content $statePath -Encoding utf8
    throw
} finally {
    if ($before) { $before | Set-Content (Join-Path $out 'host-unified-before.json') -Encoding utf8 }
    Snapshot | Set-Content (Join-Path $out 'host-unified-after.json') -Encoding utf8
    Pop-Location
}
