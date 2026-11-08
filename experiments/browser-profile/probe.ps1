param(
    [Parameter(Mandatory)][string]$Job,
    [Parameter(Mandatory)][string]$OutputPath,
    [long[]]$WindowIds = @()
)
$ErrorActionPreference = 'Stop'
# Read-only process/job diagnostics. Window IDs must come from the UI tool.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ProfileScopeProbe {
    [DllImport("kernel32", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern IntPtr OpenJobObject(uint access, bool inherit, string name);
    [DllImport("kernel32", SetLastError=true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32", SetLastError=true)]
    public static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool inside);
    [DllImport("kernel32")] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}
'@
$jobHandle = [ProfileScopeProbe]::OpenJobObject(4, $false, $Job)
if ($jobHandle -eq [IntPtr]::Zero) { throw "Cannot query job: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
try {
    $processes = @(Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | ForEach-Object {
        $processHandle = [ProfileScopeProbe]::OpenProcess(0x1000, $false, $_.ProcessId)
        $member = $false
        $known = $false
        if ($processHandle -ne [IntPtr]::Zero) {
            $known = [ProfileScopeProbe]::IsProcessInJob($processHandle, $jobHandle, [ref]$member)
            [void][ProfileScopeProbe]::CloseHandle($processHandle)
        }
        $role = 'browser'
        if ($_.CommandLine -match '--type=([^ ]+)') { $role = $Matches[1] }
        if ($_.CommandLine -match '--utility-sub-type=network.mojom.NetworkService') { $role = 'network' }
        [pscustomobject]@{ pid=$_.ProcessId; parent=$_.ParentProcessId; role=$role; membershipKnown=$known; inJob=($known -and $member); rootCommand=$(if($role -eq 'browser'){$_.CommandLine}else{$null}) }
    })
    $windows = @($WindowIds | ForEach-Object {
        [uint32]$windowPid = 0
        $thread = [ProfileScopeProbe]::GetWindowThreadProcessId([IntPtr]$_, [ref]$windowPid)
        if (!$thread) { throw "Window no longer exists: $_" }
        [pscustomobject]@{window=$_; pid=$windowPid}
    })
    $proxy = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $report = [ordered]@{
        time=(Get-Date).ToString('o'); job=$Job; processes=$processes; windows=$windows
        hostProxy=@{enabled=$proxy.ProxyEnable; server=$proxy.ProxyServer; pac=$proxy.AutoConfigURL}
        hostDns=@(Get-DnsClientServerAddress | Select-Object InterfaceIndex,AddressFamily,ServerAddresses)
    }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding utf8
    $report.processes | Where-Object { $_.role -in @('browser','network') } | Format-Table pid,parent,role,membershipKnown,inJob
    $windows | Format-Table
} finally { [void][ProfileScopeProbe]::CloseHandle($jobHandle) }
