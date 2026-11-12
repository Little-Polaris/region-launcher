"""Cross-bitness inheritance, Unicode paths, suspend semantics, and fail-closed loading."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[1]
BIN = ROOT / 'build/service-bin'
OUT = ROOT / 'build/test-results/bitness'
OUT.mkdir(parents=True, exist_ok=True)

def execute(args, env=None):
    p = subprocess.run(list(map(str, args)), capture_output=True, timeout=45, env=env)
    return p.returncode, (p.stdout + p.stderr).decode('utf8', errors='replace')

outside = [execute([BIN / name, '--child'])[1] for name in ('RegionProbe.exe', 'RegionProbe32.exe')]
results = {}
with tempfile.TemporaryDirectory(prefix='位数 验证🚀', dir=OUT) as directory:
    app = Path(directory)
    for name in ('RegionSession.exe', 'RegionHook.dll', 'RegionHook32.dll', 'RegionInject32.exe',
                 'RegionInject64.exe', 'RegionProbe.exe', 'RegionProbe32.exe'):
        shutil.copy2(BIN / name, app / name)
    def run(label, binary, args, ok=True):
        config = app / 'session.ini'
        config.write_text(f'[session]\nJob=Local\\RegionLauncher.{uuid.uuid4().hex}\n'
                          f'Application={app/binary}\nArguments={subprocess.list2cmdline(args)}\n'
                          'Locale=en-US\nRegion=US\nGeo=244\nTimeZone=Pacific Standard Time\nAutoDst=1\n', encoding='utf-16')
        env = dict(os.environ, REGION_PROBE_HOOK_PATH='1', REGION_PROBE_FILE=str(app/'host-file.txt'))
        code, output = execute([app/'RegionSession.exe', '--locale-test', config], env)
        (OUT/(label+'.log')).write_text(output, encoding='utf8')
        if ok:
            assert code == 0, (label, code, output)
            count = output.count('BITS=')
            assert count >= 2 and output.count('locale=en-US region=US geo=244 timezone=Pacific Standard Time') == count, output
            assert output.count('winterHour=4 summerHour=5') == count, output
            assert output.count('REGISTRY=en-US|Pacific Standard Time status=0,0') == count, output
            assert (app/'host-file.txt').read_text() == 'host file access verified'
        else:
            assert code != 0, (label, output)
        results[label] = True
        return output
    for root, child in [('RegionProbe32.exe', 'RegionProbe.exe'), ('RegionProbe.exe', 'RegionProbe32.exe')]:
        text = run(root+'-chain', root, ['--spawn-other', str(app/child), str(app/root)])
        assert text.count('BITS=32') + text.count('BITS=64') == 3, text
        assert 'RegionHook32.dll' in text and 'RegionHook.dll' in text, text
    run('x86-ansi', str(BIN/'RegionProbe32.exe'), ['--spawn-ansi'])
    for args in ([], ['--spawn-suspended']):
        run('x86'+('-suspended' if args else '-child'), 'RegionProbe32.exe', args)
    (app/'RegionInject32.exe').unlink()
    text = run('missing-helper', 'RegionProbe32.exe', [], False)
    assert 'BITS=' not in text, text
    shutil.copy2(BIN/'RegionInject32.exe', app/'RegionInject32.exe')
    (app/'RegionHook32.dll').write_bytes(b'invalid module')
    text = run('bad-x86-module', 'RegionProbe32.exe', [], False)
    assert 'BITS=' not in text, text
    text = run('failed-x64-child', 'RegionProbe.exe', ['--spawn-other', str(app/'RegionProbe32.exe')], False)
    assert text.count('BITS=') == 1 and 'CHILD_ERROR=' in text, text
    shutil.copy2(BIN/'RegionHook32.dll', app/'RegionHook32.dll')
    (app/'RegionInject64.exe').unlink()
    text = run('failed-x86-child', 'RegionProbe32.exe', ['--spawn-other', str(app/'RegionProbe.exe')], False)
    assert text.count('BITS=') == 1 and 'CHILD_ERROR=' in text, text
after = [execute([BIN/name, '--child'])[1] for name in ('RegionProbe.exe', 'RegionProbe32.exe')]
signature = lambda s: s.split(' locale=', 1)[1].split(' season=', 1)[0]
assert list(map(signature, outside)) == list(map(signature, after))
assert all('HOOK_PATH=' not in text for text in outside+after)
(OUT/'results.json').write_text(json.dumps(results, indent=2))
print('PASS: x86/x64 mixed three-generation trees, locale/DST, Unicode modules, host files, suspended x86 child, fail-closed, unchanged outside processes')
