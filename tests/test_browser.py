"""Headless Chrome probe with disposable profiles; no accounts or Claude requests."""
from pathlib import Path
import html
import json
import re
import subprocess
import uuid

ROOT = Path(__file__).resolve().parents[1]
BIN = ROOT / 'build/bin'
OUT = ROOT / 'build/test-results/browser'
OUT.mkdir(parents=True, exist_ok=True)
CHROME = Path(r'C:\Program Files\Google\Chrome\Application\chrome.exe')
assert CHROME.is_file()
page = OUT / 'probe.html'
page.write_text('''<!doctype html><meta charset="utf-8"><pre id="result"></pre><script>
document.querySelector('#result').textContent=JSON.stringify({
  timezone:Intl.DateTimeFormat().resolvedOptions().timeZone,
  language:navigator.language,languages:navigator.languages,
  winter:new Date('2026-01-15T12:00:00Z').getTimezoneOffset(),
  summer:new Date('2026-07-15T12:00:00Z').getTimezoneOffset()
});</script>''', encoding='utf-8')
for automatic in (True, False):
    profile = OUT / f'profile-{uuid.uuid4().hex}'
    config = OUT / f'browser-{int(automatic)}.ini'
    args = [f'--user-data-dir={profile}', '--headless=new', '--disable-background-networking',
            '--no-first-run', '--no-default-browser-check', '--lang=en-US', '--dump-dom',
            '--proxy-server=socks5://127.0.0.1:7890',
            '--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1, EXCLUDE localhost', page.as_uri()]
    config.write_text(f'''[session]
Job=Local\\RegionLauncher.{uuid.uuid4().hex}
Application={CHROME}
Arguments={subprocess.list2cmdline(args)}
Locale=en-US
Region=US
Geo=244
TimeZone=Pacific Standard Time
AutoDst={int(automatic)}
''', encoding='utf-16')
    process = subprocess.Popen([str(BIN / 'RegionSession.exe'), '--locale-test', str(config)],
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, encoding='utf-8', errors='replace')
    try:
        output, err = process.communicate(timeout=35)
    except subprocess.TimeoutExpired:
        process.terminate()
        output, err = process.communicate(timeout=10)
    (OUT / f'browser-{int(automatic)}.txt').write_text(output + '\nERROR:\n' + err, encoding='utf-8')
    matches = re.findall(r'<pre id="result">(.*?)</pre>', output, re.S)
    assert matches, (process.returncode, output, err)
    result = json.loads(html.unescape(matches[-1]))
    assert result['language'] == 'en-US', result
    assert result['winter'] == 480, result
    assert result['summer'] == (420 if automatic else 480), result
    if automatic:
        assert result['timezone'] == 'America/Los_Angeles', result
    print('PASS Chrome:', result)
