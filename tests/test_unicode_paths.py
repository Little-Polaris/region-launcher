"""Exercise DLL paths outside any ANSI codepage; retain reports, remove test binaries."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'build/test-results/unicode-paths'
OUT.mkdir(parents=True, exist_ok=True)
parser = argparse.ArgumentParser()
parser.add_argument('--bin', type=Path, default=ROOT / 'build/service-bin')
parser.add_argument('--expect-reject', action='store_true')
parser.add_argument('--network', action='store_true', help='Also run network/DNS and lifecycle regressions from the Unicode directory')
args = parser.parse_args()
BIN = args.bin.resolve()
with tempfile.TemporaryDirectory(prefix='中文 启动器_日本語_🚀_', dir=OUT) as directory:
    app = Path(directory)
    for name in ('RegionSession.exe', 'RegionHook.dll', 'RegionProbe.exe', 'DnsProbe.exe'):
        shutil.copy2(BIN / name, app / name)
    target = app / '中文测试程序🚀.exe'
    shutil.copy2(BIN / 'RegionProbe.exe', target)
    config = app / '中文会话.ini'
    env = os.environ.copy()
    env['REGION_PROBE_FILE'] = str(app / '文件访问测试.txt')
    env['REGION_PROBE_HOOK_PATH'] = '1'
    def run(target, arguments='', name='normal'):
        config.write_text(f'''[session]
Job=Local\\RegionLauncher.{uuid.uuid4().hex}
Application={target}
Arguments={arguments}
Locale=en-US
Region=US
Geo=244
TimeZone=Pacific Standard Time
AutoDst=1
''', encoding='utf-16')
        result = subprocess.run([str(app/'RegionSession.exe'), '--locale-test', str(config)], capture_output=True, timeout=35, env=env)
        output = (result.stdout + result.stderr).decode('utf-8', errors='replace')
        (OUT / f'{name}.log').write_text(output, encoding='utf-8')
        return result, output
    if args.expect_reject:
        result, output = run(target, name='before')
        assert result.returncode != 0 and 'only English characters' in output, output
        print('PASS: previous version rejects Unicode launcher path')
    else:
        for exe, arguments, name in ((target, '', 'unicode'), (target, '--spawn-suspended', 'suspended'), (BIN/'RegionProbe.exe', '--spawn-ansi', 'ansi-child')):
            result, output = run(exe, arguments, name)
            assert result.returncode == 0, (result.returncode, output)
            assert output.count('locale=en-US region=US geo=244 timezone=Pacific Standard Time') == 2, output
            assert output.count('HOOK_PATH=' + str(app/'RegionHook.dll')) == 2, output
            assert Path(env['REGION_PROBE_FILE']).read_text() == 'host file access verified'
            if arguments == '--spawn-suspended': assert 'SUSPENDED_CHILD_OK' in output
        if args.network:
            network_env = os.environ.copy()
            network_env['REGION_TEST_BIN'] = str(app)
            for name, arguments in (('locale', []), ('system_proxy', []), ('unified_dns', ['--network', '--failure']), ('network', []), ('lifecycle', ['--cycles', '3'])):
                with (OUT / f'regression-{name}.log').open('w', encoding='utf-8') as report:
                    result = subprocess.run([sys.executable, '-X', 'utf8', str(ROOT/'tests'/f'test_{name}.py'), *arguments], env=network_env, stdout=report, stderr=subprocess.STDOUT, timeout=240)
                assert result.returncode == 0, f'{name} failed; see {report.name}'
                print(f'PASS: {name} regression from Unicode directory', flush=True)
        (app/'RegionHook.dll').unlink()
        result, output = run(target, name='missing-module')
        assert result.returncode != 0 and 'locale=en-US' not in output, output
        (app/'RegionHook.dll').write_bytes(b'invalid DLL fixture')
        result, output = run(target, name='invalid-module')
        assert result.returncode != 0 and 'locale=en-US' not in output, output
        print('PASS: Unicode DLL loaded in place in parent/child, ANSI and suspended child callers, Unicode file access; missing/invalid DLL does not run app entry point')
