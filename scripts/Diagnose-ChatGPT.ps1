param([string]$AppDirectory = '', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot ('diagnostic-results/' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$report = Join-Path $OutputDirectory 'connection-report.txt'
function Log([string]$Text) { $Text | Out-File -LiteralPath $report -Append -Encoding utf8; Write-Host $Text }
try {
    if (!$AppDirectory) {
        $AppDirectory = Join-Path $PSScriptRoot 'app'
        if (!(Test-Path -LiteralPath (Join-Path $AppDirectory 'RegionSession.exe'))) {
            $AppDirectory = (Read-Host 'Enter the launcher app folder (the folder containing RegionLauncher.exe)').Trim('"')
        }
    }
    $AppDirectory = (Resolve-Path -LiteralPath $AppDirectory).Path
    $sessionExe = Join-Path $AppDirectory 'RegionSession.exe'
    if (!(Test-Path -LiteralPath $sessionExe)) { throw 'RegionSession.exe not found in the selected folder.' }
    $session = Get-ChildItem -LiteralPath (Join-Path $AppDirectory 'data/sessions') -Filter session.ini -File -Recurse |
        Where-Object { $_.Directory.Name -notlike 'check-*' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (!$session) { throw 'Start ChatGPT with the launcher and keep that session running first.' }
    $settings = @{}
    foreach ($line in Get-Content -LiteralPath $session.FullName -Encoding Unicode) {
        if ($line -match '^([^=]+)=(.*)$') { $settings[$matches[1]] = $matches[2] }
    }
    if ($settings.Job -notmatch '^Local\\RegionLauncher\.[a-f0-9]{32}$' -or
        $settings.ProxyHost -ne '127.0.0.1' -or $settings.ProxyPort -notmatch '^\d{1,5}$') { throw 'Invalid session settings.' }
    $port = [int]$settings.ProxyPort
    if ($port -lt 1 -or $port -gt 65535) { throw 'Invalid proxy port.' }
    $ready = [Threading.EventWaitHandle]::OpenExisting($settings.Job + '.ready')
    try { if (!$ready.WaitOne(0)) { throw 'The launcher session is not ready.' } } finally { $ready.Dispose() }
    $curl = Join-Path $env:SystemRoot 'System32/curl.exe'
    if (![Environment]::Is64BitProcess) { throw 'Please run this script from 64-bit PowerShell.' }
    if (!(Test-Path -LiteralPath $curl)) { throw 'Windows curl.exe is required.' }
    Log ('Time: ' + (Get-Date -Format o))
    Log ('Launcher version: ' + [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $AppDirectory 'RegionLauncher.dll')).ProductVersion)
    Log ('RegionHook SHA256: ' + (Get-FileHash -LiteralPath (Join-Path $AppDirectory 'RegionHook.dll')).Hash)
    Log ('Selected proxy: 127.0.0.1:' + $port)
    Log 'Requests below send no account cookies, tokens, or request bodies. HTTP 401/403 is an HTTP response, not a reset.'
    Log 'Relevant hosts entries (read only):'
    Get-Content -LiteralPath (Join-Path $env:SystemRoot 'System32/drivers/etc/hosts') |
        Where-Object { $_ -match '(?i)localhost|chatgpt\.com|auth\.openai\.com' } | ForEach-Object { Log $_ }
    $common = @('--silent','--show-error','--ipv4','--connect-timeout','8','--max-time','20','--output','NUL',
        '--write-out','http=%{http_code},remote=%{remote_ip},tls_verify=%{ssl_verify_result},seconds=%{time_total}\n')
    $targets = @(
        @('cloudflare','https://www.cloudflare.com/cdn-cgi/trace'),
        @('chatgpt','https://chatgpt.com/cdn-cgi/trace'),
        @('chatgpt-api','https://chatgpt.com/backend-api/models'),
        @('auth','https://auth.openai.com/')
    )
    foreach ($target in $targets) {
        foreach ($mode in @('proxy','environment')) {
            Log ('TEST ' + $mode + ' / ' + $target[0])
            if ($mode -eq 'proxy') {
                $arguments = $common + @('--proxy',"socks5h://127.0.0.1:$port",'--noproxy','no-bypass.invalid',$target[1])
                # Native stderr is diagnostic output, including in Windows PowerShell 5.1.
                $ErrorActionPreference = 'Continue'
                $text = & $curl @arguments 2>&1
                $code = $LASTEXITCODE
                $ErrorActionPreference = 'Stop'
            } else {
                # No application proxy: exercise the existing process environment instead.
                $arguments = $common + @('--noproxy','*',$target[1])
                if ($arguments | Where-Object { $_ -match '\s|"' }) { throw 'Unexpected whitespace in fixed curl arguments.' }
                $config = @('[session]', ('Application=' + $curl), ('Arguments=' + ($arguments -join ' ')))
                foreach ($key in @('Job','OwnerPid','ProxyHost','ProxyPort','DnsAddress','Locale','Region','Geo','TimeZone','AutoDst')) {
                    if ($settings.ContainsKey($key)) { $config += $key + '=' + $settings[$key] }
                }
                $configPath = Join-Path $OutputDirectory 'probe-session.ini'
                $config | Set-Content -LiteralPath $configPath -Encoding Unicode
                $ErrorActionPreference = 'Continue'
                $text = & $sessionExe --join $configPath 2>&1
                $code = $LASTEXITCODE
                $ErrorActionPreference = 'Stop'
            }
            foreach ($line in $text) { Log $line.ToString() }
            Log ('exit=' + $code)
        }
    }
    Log 'DONE. Send connection-report.txt only; no proxy configuration or browser data is needed.'
} catch {
    Log ('Diagnostic stopped: ' + $_.Exception.Message)
    exit 1
}
Write-Host ('Report: ' + $report)
