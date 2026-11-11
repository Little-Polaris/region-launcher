"""A caller-supplied child environment must not drop the launch context."""
import ctypes
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'build/test-results/child-environment'
BIN = ROOT / 'build/service-bin'


def child():
    name = ctypes.create_unicode_buffer(85)
    assert ctypes.windll.kernel32.GetUserDefaultLocaleName(name, len(name))
    assert name.value == 'en-US', name.value
    assert os.environ['REGION_LAUNCHER_TIMEZONE'] == 'Pacific Standard Time'
    assert os.environ['LC_ALL'] == 'en_US.UTF-8'
    assert os.environ['CUSTOM_VALUE'] == '中文 空格 🚀'
    assert 'PARENT_ONLY_TEST_SECRET' not in os.environ
    print('CHILD_ENV_OK', flush=True)


def parent():
    os.environ['PARENT_ONLY_TEST_SECRET'] = 'must-not-be-merged'
    for override in (False, True):
        env = {'SystemRoot': os.environ['SystemRoot'], 'CUSTOM_VALUE': '中文 空格 🚀'}
        if override:
            env.update({'region_launcher_locale': 'ja-JP', 'REGION_LAUNCHER_TIMEZONE': 'Tokyo Standard Time', 'LC_ALL': 'ja_JP.UTF-8'})
        p = subprocess.run([sys.executable, __file__, '--child'], env=env, capture_output=True, timeout=20)
        assert p.returncode == 0 and b'CHILD_ENV_OK' in p.stdout, (p.returncode, p.stdout, p.stderr)
    print('CUSTOM_ENVIRONMENT_OK', flush=True)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    config = OUT / 'session.ini'
    config.write_text(f'''[session]
Job=Local\\RegionLauncher.{uuid.uuid4().hex}
Application={sys.executable}
Arguments={subprocess.list2cmdline([str(Path(__file__).resolve()), '--parent'])}
Locale=en-US
Region=US
Geo=244
TimeZone=Pacific Standard Time
AutoDst=1
''', encoding='utf-16')
    p = subprocess.run([str(BIN/'RegionSession.exe'), '--locale-test', str(config)], capture_output=True, timeout=60)
    text = (p.stdout + p.stderr).decode('utf-8', errors='replace')
    (OUT/'result.log').write_text(text, encoding='utf-8')
    assert p.returncode == 0 and 'CUSTOM_ENVIRONMENT_OK' in text, (p.returncode, text)
    print('PASS: custom child environment starts, launch settings retained, child Unicode values preserved, unrelated parent values excluded')


if __name__ == '__main__':
    if '--child' in sys.argv: child()
    elif '--parent' in sys.argv: parent()
    else: main()
