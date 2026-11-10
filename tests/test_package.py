"""Check the distributable, not the live app folder, for required service files and leaks."""
import argparse
import hashlib
import json
import zipfile
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('archive', type=Path)
args = parser.parse_args()
with zipfile.ZipFile(args.archive) as archive:
    names = archive.namelist()
    for required in (
        'app/RegionLauncher.exe', 'app/RegionLauncher.dll',
        'app/RegionLauncher.deps.json', 'app/RegionLauncher.runtimeconfig.json',
        'app/YamlDotNet.dll', 'app/licenses/YamlDotNet-MIT.txt',
        'app/proxy/mihomo.exe', 'app/proxy/LICENSE', 'docs/quick-start.md',
        'docs/validation-lifecycle-20261109.md',
        'docs/proxy-settings.md',
        'docs/subscriptions.md',
        'docs/applications.md',
        'docs/connectivity.md',
        'app/RegionSession.exe', 'app/RegionHook.dll', 'install/request_install.ps1',
        'install/package/install_service.ps1', 'install/package/RegionNetworkService.exe',
        'install/package/ProxyBridgeCore.dll', 'install/package/WinDivert.dll',
        'install/package/WinDivert64.sys', '安装或更新后台服务.cmd', '卸载后台服务.cmd',
    ):
        assert required in names, f'Missing runtime dependency: {required}'
    assert archive.testzip() is None, 'Corrupt ZIP member'
    assert not any('/data/' in '/'+name or '/private/' in '/'+name or name.endswith('/proxy.json') for name in names), 'Private runtime configuration included'
    manifest = json.loads(archive.read('install/package/manifest.json'))
    for name, expected in manifest['files'].items():
        assert hashlib.sha256(archive.read('install/package/'+name)).hexdigest().upper() == expected, name
    for line in archive.read('SHA256SUMS.txt').decode('utf-8-sig').splitlines():
        digest, name = line.split('  ', 1)
        assert hashlib.sha256(archive.read(name)).hexdigest() == digest, name
print('PASS: packaged client, service installer, backend hashes; no private runtime configuration')
