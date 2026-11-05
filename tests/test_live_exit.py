"""Opt-in live smoke test: official proxy core, CN/Cloudflare exits and DNS witnesses."""
import json
import os
import re
import socket
import subprocess
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BIN = Path(os.environ.get('REGION_TEST_BIN', ROOT / 'build/service-bin'))
OUT = ROOT / 'build/test-results/live-exit'
OUT.mkdir(parents=True, exist_ok=True)
CONFIG = BIN / 'data/private/proxy.json'


def fetch(url, port=17890, timeout=20):
    result = subprocess.run(['curl.exe', '-sSfL', '--proxy', f'socks5h://127.0.0.1:{port}',
                             '--max-time', str(timeout), url], capture_output=True, timeout=timeout + 5)
    result.check_returncode()
    return result.stdout.decode('utf-8', errors='replace')


def main():
    with socket.socket() as sock:
        assert sock.connect_ex(('127.0.0.1', 17890)) != 0, 'Dedicated test port already occupied'
    with (OUT / 'core.log').open('w', encoding='utf-8') as log:
        proc = subprocess.Popen([str(BIN / 'proxy/mihomo.exe'), '-d', str(CONFIG.parent), '-f', str(CONFIG)],
                                stdout=log, stderr=log, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            for _ in range(50):
                assert proc.poll() is None, 'Core exited'
                with socket.socket() as sock:
                    if sock.connect_ex(('127.0.0.1', 17890)) == 0:
                        break
                time.sleep(.1)
            trace = dict(line.split('=', 1) for line in fetch('https://1.1.1.1/cdn-cgi/trace').splitlines() if '=' in line)
            results = {'cloudflare': {'ip': trace['ip'], 'country': trace['loc']}, 'cn_sites': {}}
            assert trace['loc'] == 'US', results
            for site in ['https://2026.ip138.com/', 'https://my.ip.cn/']:
                try:
                    content = fetch(site)
                except subprocess.CalledProcessError as error:
                    results.setdefault('unavailable', {})[site] = error.stderr.decode(errors='replace')
                    continue
                ip = re.search(r'\b(?:\d{1,3}\.){3}\d{1,3}\b', content).group()
                results['cn_sites'][site] = ip
                assert ip == trace['ip'], results
            assert results['cn_sites'], 'No CN IP source succeeded'
            old = re.search(r'\b(?:\d{1,3}\.){3}\d{1,3}\b', fetch('https://my.ip.cn/', port=7890)).group()
            results['outside_7890_cn_exit'] = old
            token = uuid.uuid4().hex
            for i in (1, 2):
                try:
                    fetch(f'https://{token}-{i}.d.ip.net.coffee/pixel.gif', timeout=6)
                except subprocess.CalledProcessError:
                    pass  # A failed pixel still triggers DNS resolution.
            servers = []
            for _ in range(5):
                time.sleep(2)
                servers = json.loads(fetch(f'https://ip.net.coffee/api/dns/result/{token}')).get('dns_servers', [])
                if servers:
                    break
            assert servers, 'DNS witness returned no results; cannot claim verified absence of leaks'
            results['dns'] = []
            for address in servers:
                geo = json.loads(fetch(f'https://ip.net.coffee/api/geoip/{address}'))
                results['dns'].append({'ip': address, 'country': geo.get('country_code'), 'isp': geo.get('isp')})
                assert geo.get('country_code', '').lower() not in ('', 'cn'), results
            (OUT / 'results.json').write_text(json.dumps(results, indent=2, ensure_ascii=False), encoding='utf-8')
            print(json.dumps(results, ensure_ascii=True))
        finally:
            proc.terminate()
            proc.wait(timeout=10)


if __name__ == '__main__':
    main()
