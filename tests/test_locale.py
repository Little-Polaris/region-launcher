import os
from pathlib import Path
import subprocess
import uuid

ROOT = Path(__file__).resolve().parents[1]
BIN = ROOT / 'build/bin'
OUT = ROOT / 'build/test-results'
OUT.mkdir(parents=True, exist_ok=True)


def execute(args, env=None):
    result = subprocess.run([str(x) for x in args], capture_output=True, text=True,
                            encoding='utf-8', errors='replace', timeout=20, env=env)
    assert result.returncode == 0, (result.returncode, result.stdout, result.stderr)
    return result.stdout


outside_before = execute([BIN / 'RegionProbe.exe', '--child'])
for automatic in (True, False):
    config = OUT / f'locale-{int(automatic)}.ini'
    config.write_text(f'''[session]
Job=Local\\RegionLauncher.{uuid.uuid4().hex}
Application={BIN / 'RegionProbe.exe'}
Locale=en-US
Region=US
Geo=244
TimeZone=Pacific Standard Time
AutoDst={int(automatic)}
''', encoding='utf-16')
    env = os.environ.copy()
    env['REGION_PROBE_FILE'] = str(OUT / f'host-access-{int(automatic)}.txt')
    output = execute([BIN / 'RegionSession.exe', '--locale-test', config], env)
    (OUT / f'locale-{int(automatic)}.txt').write_text(output, encoding='utf-8')
    assert output.count('locale=en-US region=US geo=244 timezone=Pacific Standard Time') == 2, output
    expected = 'winterHour=4 summerHour=5' if automatic else 'winterHour=4 summerHour=4'
    assert output.count(expected) == 2, output
    boundary = 'spring=1:59>3:00 fall=1:59>1:00' if automatic else 'spring=1:59>2:00 fall=0:59>1:00'
    assert output.count(boundary) == 2, output
    assert 'FILE_OK' in output, output
    assert (OUT / f'host-access-{int(automatic)}.txt').read_text() == 'host file access verified', 'Host file was not written'
outside_after = execute([BIN / 'RegionProbe.exe', '--child'])
# Ignore PID/time fields; the actual host configuration must be identical.
before = outside_before.split(' locale=', 1)[1].split(' season=', 1)[0]
after = outside_after.split(' locale=', 1)[1].split(' season=', 1)[0]
assert before == after, (before, after)
(OUT / 'outside.txt').write_text(outside_before + outside_after, encoding='utf-8')
print('PASS: parent and child locale/region, automatic DST, fixed standard time, host file writes, unchanged host settings')
