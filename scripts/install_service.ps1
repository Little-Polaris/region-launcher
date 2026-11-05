param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$AllowedSid,
    [Parameter(Mandatory)][string]$ResultPath,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$name = 'RegionLauncherNetwork'
$target = Join-Path $env:ProgramFiles $name
$files = @('RegionNetworkService.exe','ProxyBridgeCore.dll','WinDivert.dll','WinDivert64.sys')
$result = [ordered]@{ action = $(if ($Uninstall) {'uninstall'} else {'install'}); success = $false; path = $target }
function Native([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments | Out-Null
    if ($LASTEXITCODE) { throw "$Exe failed ($LASTEXITCODE)" }
}
function AssertPlainPath([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    while ($item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point rejected: $($item.FullName)" }
        $item = $item.Parent
    }
}
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator approval is required to install or remove this service.' }
    $null = [Security.Principal.SecurityIdentifier]::new($AllowedSid)
    AssertPlainPath $env:ProgramFiles
    if (Test-Path -LiteralPath $target) {
        AssertPlainPath $target
        $marker = Join-Path $target 'installation.json'
        if (!(Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json).product -ne $name) { throw 'Unrecognized installation directory; left unchanged.' }
        foreach ($item in Get-ChildItem -LiteralPath $target -Force -Recurse) { AssertPlainPath $item.FullName }
    }
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($service) {
        $registered = Get-CimInstance Win32_Service -Filter "Name='$name'"
        if ($registered.PathName -ne ('"' + (Join-Path $target 'RegionNetworkService.exe') + '"')) { throw 'Unexpected existing service executable; left unchanged.' }
        $workers = @(Get-CimInstance Win32_Process -Filter "Name='RegionNetworkService.exe'" | Where-Object { $_.ExecutablePath -eq (Join-Path $target 'RegionNetworkService.exe') -and $_.CommandLine -match ' --worker ' })
        if ($workers.Count) { throw 'An environment is running. Close it before updating/removing the network service.' }
    }
    if ($Uninstall) {
        if ($service) { Stop-Service $name; Native sc.exe @('delete',$name) }
        Get-NetFirewallRule -Name 'RegionLauncherNetwork-TCP','RegionLauncherNetwork-UDP' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        # Keep binaries and logs for inspection; no recursive deletion or shared-driver removal.
        $result.success = $true
    } else {
        $package = (Resolve-Path -LiteralPath $PackageDirectory).Path
        $manifest = Get-Content -LiteralPath (Join-Path $package 'manifest.json') -Raw | ConvertFrom-Json
        if ($manifest.product -ne $name -or @($manifest.files.PSObject.Properties).Count -ne $files.Count) { throw 'Invalid package manifest' }
        foreach ($file in $files) {
            $source = Join-Path $package $file; AssertPlainPath $source
            if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $manifest.files.$file) { throw "Package checksum mismatch: $file" }
        }
        if ((Get-AuthenticodeSignature -LiteralPath (Join-Path $package 'WinDivert64.sys')).Status -ne 'Valid') { throw 'Network driver signature is not valid' }
        if ($service) { Stop-Service $name }
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetAccessRuleProtection($true,$false)
        $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
        foreach ($entry in @(@('S-1-5-18','FullControl'),@('S-1-5-32-544','FullControl'),@('S-1-5-32-545','ReadAndExecute'))) {
            $rule = [Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($entry[0]),$entry[1],'ContainerInherit,ObjectInherit','None','Allow')
            $acl.AddAccessRule($rule)
        }
        Set-Acl -LiteralPath $target -AclObject $acl
        foreach ($file in $files) {
            $destination = Join-Path $target $file
            # A loaded driver is locked even after the user-mode service stops.
            # Preserve byte-identical dependencies instead of rewriting them.
            if (!(Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $manifest.files.$file) {
                Copy-Item -LiteralPath (Join-Path $package $file) -Destination $destination -Force
            }
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $manifest.files.$file) { throw "Installed checksum mismatch: $file" }
        }
        New-Item -ItemType Directory -Path (Join-Path $target 'logs') -Force | Out-Null
        @{product=$name; installed=(Get-Date -Format o); files=$manifest.files; allowedSid=$AllowedSid} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $target 'installation.json') -Encoding utf8
        if (!$service) {
            New-Service -Name $name -BinaryPathName ('"' + (Join-Path $target 'RegionNetworkService.exe') + '"') -DisplayName 'Region Launcher Network' -StartupType Automatic -Description 'Network routing for explicitly enrolled Region Launcher process groups.' | Out-Null
        }
        $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$name\Parameters"
        New-Item -Path $key -Force | Out-Null
        New-ItemProperty -Path $key -Name AllowedSid -Value $AllowedSid -PropertyType String -Force | Out-Null
        # The bound user may restart this one service, but cannot change its executable,
        # account, configuration or ACL. This keeps routine restart tests unelevated.
        Native sc.exe @('sdset',$name,"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;CCLCRPWPLOCRRC;;;$AllowedSid)")
        # The relay receives WinDivert's injected inbound packets; the previous user-mode
        # launcher had its own Windows prompt-created rules. Keep these new rules local
        # to this protected executable and its two relay ports, never all applications.
        foreach ($protocol in @('TCP','UDP')) {
            $ruleName = "$name-$protocol"
            Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
            New-NetFirewallRule -Name $ruleName -DisplayName "Region Launcher network relay ($protocol)" -Group $name -Direction Inbound -Action Allow -Profile Any -Program (Join-Path $target 'RegionNetworkService.exe') -Protocol $protocol -LocalPort $(if ($protocol -eq 'TCP') {34010} else {34011}) | Out-Null
        }
        Start-Service $name
        (Get-Service $name).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
        $result.success = $true; $result.files = $manifest.files
    }
} catch { $result.error = $_.Exception.Message }
$result.finished = Get-Date -Format o
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ResultPath -Encoding utf8
if (!$result.success) { Write-Error $result.error; exit 1 }
