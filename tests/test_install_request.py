"""Exercise elevation-request results without installing a service or showing UAC."""
import ctypes
import json
import os
from pathlib import Path
import shutil
import subprocess
import uuid

ROOT = Path(__file__).resolve().parents[1]
assert not ctypes.windll.shell32.IsUserAnAdmin(), 'Run at ordinary user privileges'
OUT = ROOT / 'build/test-results/install-request' / uuid.uuid4().hex
POWERSHELL = Path(os.environ['WINDIR']) / 'System32/WindowsPowerShell/v1.0/powershell.exe'
for mode in ('success', 'cancelled', 'installer-failure'):
    case = OUT / mode
    install = case / 'install'
    install.mkdir(parents=True)
    shutil.copy2(ROOT / 'scripts/request_install.ps1', install)
    harness = case / 'harness.ps1'
    harness.write_text(r'''
$ErrorActionPreference = 'Stop'
function Start-Process {
    param($FilePath, $ArgumentList, $Verb, $WindowStyle, [switch]$PassThru, [switch]$Wait)
    if ($Verb -ne 'RunAs' -or !$Wait -or $WindowStyle -ne 'Hidden') { throw 'Incorrect elevation request' }
    if ($env:REGION_INSTALL_TEST -eq 'cancelled') { throw [ComponentModel.Win32Exception]::new(1223) }
    if ($ArgumentList -notmatch '-ResultPath "([^"]+)"') { throw 'Missing result path' }
    $result = @{success=($env:REGION_INSTALL_TEST -eq 'success'); stage='installer'; error='fixture failure'}
    $result | ConvertTo-Json | Set-Content -LiteralPath $Matches[1] -Encoding utf8
    [pscustomobject]@{ExitCode=0}
}
$global:LASTEXITCODE = 0
& (Join-Path $PSScriptRoot 'install/request_install.ps1')
exit $LASTEXITCODE
''', encoding='utf-8-sig')
    env = dict(os.environ, REGION_INSTALL_TEST=mode)
    result = subprocess.run([str(POWERSHELL), '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(harness)],
                            env=env, capture_output=True, timeout=20)
    records = list((case / 'evidence').glob('service-*.json'))
    assert len(records) == 1, (mode, result.stdout, result.stderr)
    record = json.loads(records[0].read_text(encoding='utf-8-sig'))
    assert result.returncode == (0 if mode == 'success' else 1), (mode, result)
    assert record['success'] == (mode == 'success'), record
    assert record['stage'] == ('request' if mode == 'cancelled' else 'installer'), record
    if mode == 'installer-failure':
        assert record['error'] == 'fixture failure', 'Installer evidence must be preserved'
print('PASS: request success, cancelled request evidence, installer failure evidence preserved; no UAC invoked')
