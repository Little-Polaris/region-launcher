"""Optional real HTTPS acceptance through the launcher's running dedicated proxy."""
import json
import os
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]
BIN = ROOT/'build/service-bin'
OUT = ROOT/'build/test-results/bitness-live'
OUT.mkdir(parents=True, exist_ok=True)
env = dict(os.environ, REGION_LAUNCHER_DNS_ADDRESS='127.0.0.153')
results = []
for bits in (32, 64):
    for label, url in [('cloudflare', 'https://www.cloudflare.com/cdn-cgi/trace'), ('domestic', 'https://my.ip.cn/')]:
        command = [str(ROOT/'build/ConnectHarness.exe'), str(BIN/'RegionHook.dll'),
                   str(ROOT/f'build/HttpProbe{bits}.exe'), url, os.environ.get('REGION_TEST_PROXY_PORT', '17890')]
        p = subprocess.run(command, capture_output=True, timeout=60, env=env)
        output = (p.stdout+p.stderr).decode('utf8', errors='replace').replace('\r', '')
        (OUT/f'{bits}-{label}.log').write_text(output, encoding='utf8')
        assert p.returncode == 0 and f'BITS={bits} HTTP=200' in output, (bits, label, p.returncode, output)
        if label == 'cloudflare':
            match = re.search(r'^ip=([0-9.]+)$', output, re.M)
        else:
            match = re.search(r'"ip"\s*:\s*"([0-9.]+)"', output)
            if not match:
                match = re.search(r'^ip[：:]\s*((?:\d{1,3}\.){3}\d{1,3})\b', output, re.M)
        assert match, output
        results.append({'bits': bits, 'site': label, 'exit': match[1]})
assert len({r['exit'] for r in results}) == 1, results
(OUT/'results.json').write_text(json.dumps(results, indent=2))
print('PASS: real TLS/HTTPS 200 for x86 and x64, domestic and Cloudflare targets use one exit through the dedicated proxy')
