"""Install, upgrade and uninstall with the real installer into a temporary folder.

Registry writes go to a throwaway HKCU key and shortcuts to a temporary folder; the network service is skipped.
Usage: python tests/test_installer.py packages/RegionLauncher-Setup-<version>-win-x64.exe [packages/RegionLauncher-<version>-win-x64.zip]
The ZIP (default: next to the installer) is unpacked as a portable copy to test in-place conversion.
"""
import argparse
import os
import shutil
import subprocess
import tempfile
import uuid
import time
import winreg
import zipfile
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('setup', type=Path)
parser.add_argument('zip', type=Path, nargs='?')
args = parser.parse_args()
if args.zip is None:
    version = args.setup.name.removeprefix('RegionLauncher-Setup-').removesuffix('-win-x64.exe')
    args.zip = args.setup.with_name(f'RegionLauncher-{version}-win-x64.zip')

test_key = rf'Software\RegionLauncherInstallerTest\{uuid.uuid4().hex}'
work = Path(tempfile.mkdtemp(prefix='rl-installer-'))
env = dict(os.environ, REGION_LAUNCHER_REGISTRY_ROOT=test_key, REGION_LAUNCHER_SHORTCUT_DIR=str(work / 'shortcuts'),
           REGION_LAUNCHER_SKIP_SERVICE='1')


def read(key, name):
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key) as handle:
            return winreg.QueryValueEx(handle, name)[0]
    except FileNotFoundError:
        return None


def install(root, *extra):
    result = subprocess.run([str(args.setup), '/silent', '/dir', str(root), *extra], env=env, capture_output=True, text=True, timeout=600)
    assert result.returncode == 0, f'install failed: {result.stdout} {result.stderr}'


def uninstall(root, delete_data):
    # Run from a copy outside the installation, as the launcher's --uninstall does.
    runner = work / ('runner-' + uuid.uuid4().hex)
    runner.mkdir()
    for item in (root / 'app').iterdir():
        if item.is_file() and item.suffix in ('.exe', '.dll', '.json'):
            shutil.copy2(item, runner / item.name)
    command = [str(runner / 'RegionLauncher.exe'), '--uninstall-run', str(root), '--silent'] + (['--delete-data'] if delete_data else [])
    result = subprocess.run(command, env=env, capture_output=True, text=True, timeout=300)
    assert result.returncode == 0, f'uninstall failed with {result.returncode}'


product = test_key + r'\RegionLauncher'
uninstall_key = test_key + r'\Microsoft\Windows\CurrentVersion\Uninstall\RegionLauncher'
start_menu = work / 'shortcuts' / 'Programs' / 'Region Launcher.lnk'
desktop = work / 'shortcuts' / 'DesktopDirectory' / 'Region Launcher.lnk'
try:
    root = work / 'Region Launcher'
    install(root)
    assert (root / 'app' / 'RegionLauncher.exe').is_file() and (root / 'install' / 'request_install.ps1').is_file(), 'program files missing'
    assert (root / 'docs' / 'EULA.md').is_file(), 'agreement missing'
    assert (root / 'installation.json').is_file(), 'installation marker missing'
    assert Path(read(product, 'InstallLocation')) == root, 'install location not registered'
    assert read(uninstall_key, 'UninstallString') == f'"{root / "app" / "RegionLauncher.exe"}" --uninstall', 'uninstall entry missing'
    version = read(product, 'Version')
    assert version and read(uninstall_key, 'DisplayVersion') == version, 'version not registered'
    assert start_menu.is_file() and desktop.is_file(), 'shortcuts missing'

    # Upgrade over the same folder keeps user data.
    data = root / 'app' / 'data'
    data.mkdir(parents=True, exist_ok=True)
    (data / 'applications.json').write_text('{"keep": true}', encoding='utf-8')
    (root / 'app' / 'RegionLauncher.dll').write_bytes(b'stale')
    install(root)
    assert (data / 'applications.json').read_text(encoding='utf-8') == '{"keep": true}', 'upgrade changed user data'
    assert (root / 'app' / 'RegionLauncher.dll').stat().st_size > 1000, 'upgrade did not replace program files'

    # A non-empty foreign folder is refused.
    foreign = work / 'foreign'
    foreign.mkdir()
    (foreign / 'notes.txt').write_text('mine', encoding='utf-8')
    refused = subprocess.run([str(args.setup), '/silent', '/dir', str(foreign)], env=env, capture_output=True, text=True, timeout=300)
    assert refused.returncode != 0 and (foreign / 'notes.txt').read_text(encoding='utf-8') == 'mine', 'installer used a foreign folder'

    # Uninstall keeping data.
    uninstall(root, delete_data=False)
    assert (data / 'applications.json').is_file(), 'data removed although kept'
    assert not (root / 'app' / 'RegionLauncher.exe').exists() and not (root / 'install').exists(), 'program files left behind'
    assert read(product, 'InstallLocation') is None and read(uninstall_key, 'DisplayName') is None, 'registry left behind'
    assert not start_menu.exists() and not desktop.exists(), 'shortcuts left behind'

    # Fresh install without a desktop shortcut, then remove everything.
    second = work / 'second'
    install(second, '/nodesktop')
    assert start_menu.is_file() and not desktop.exists(), 'desktop shortcut created although declined'
    (second / 'app' / 'data').mkdir(exist_ok=True)
    (second / 'app' / 'data' / 'secret.json').write_text('x', encoding='utf-8')
    uninstall(second, delete_data=True)
    assert not second.exists(), 'installation folder left behind'
    assert read(product, 'InstallLocation') is None, 'registry left behind'

    # A portable copy unpacked from the release ZIP converts in place and keeps its data.
    portable = work / 'portable'
    with zipfile.ZipFile(args.zip) as archive:
        archive.extractall(portable)
    assert not (portable / 'installation.json').exists()
    (portable / 'app' / 'data').mkdir(exist_ok=True)
    (portable / 'app' / 'data' / 'applications.json').write_text('{"portable": true}', encoding='utf-8')
    (portable / 'evidence').mkdir(exist_ok=True)
    (portable / 'evidence' / 'old.json').write_text('{}', encoding='utf-8')
    # Something still running from the copy blocks the install until it is closed (a stand-in for the proxy engine).
    engine = portable / 'app' / 'proxy' / 'mihomo.exe'
    original_engine = engine.read_bytes()
    shutil.copy2(Path(os.environ['SystemRoot']) / 'System32' / 'PING.EXE', engine)
    holder = subprocess.Popen([str(engine), '-n', '300', '127.0.0.1'], stdout=subprocess.DEVNULL)
    try:
        blocked = subprocess.run([str(args.setup), '/silent', '/dir', str(portable)], env=env, capture_output=True, text=True, timeout=300)
        assert blocked.returncode == 3 and holder.poll() is None, f'install did not stop for a running copy: {blocked.returncode}'
        assert not (portable / 'installation.json').exists(), 'files changed while a copy was running'
        install(portable, '/force')
        time.sleep(1)
        assert holder.poll() is not None, 'running copy was not closed'
    finally:
        if holder.poll() is None:
            holder.kill()
    assert (portable / 'installation.json').is_file(), 'portable copy not converted'
    assert engine.read_bytes() == original_engine, 'program files not replaced'
    assert (portable / 'app' / 'data' / 'applications.json').read_text(encoding='utf-8') == '{"portable": true}', 'portable data changed'
    assert Path(read(product, 'InstallLocation')) == portable, 'converted copy not registered'
    uninstall(portable, delete_data=True)
    assert not portable.exists(), 'converted copy left behind'
    print('PASS: install, registry and shortcuts, upgrade keeps data, foreign folder refused, uninstall keeping or deleting data, '
          'portable copy converted in place after closing what runs from it')
finally:
    def drop(path):
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path, 0, winreg.KEY_ALL_ACCESS) as handle:
                while True:
                    try:
                        drop(path + '\\' + winreg.EnumKey(handle, 0))
                    except OSError:
                        break
            winreg.DeleteKey(winreg.HKEY_CURRENT_USER, path)
        except FileNotFoundError:
            pass
    drop(test_key)
    try:
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, r'Software\RegionLauncherInstallerTest')
    except OSError:
        pass
    shutil.rmtree(work, ignore_errors=True)
