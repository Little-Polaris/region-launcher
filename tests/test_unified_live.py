"""Opt-in end-to-end check: unconfigured curl + Python resolver under the same session."""
import json
import os
import re
import socket
import subprocess
import sys
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BIN = Path(os.environ.get('REGION_TEST_BIN', ROOT / 'build/bin'))
OUT = ROOT / 'build/test-results/unified-live'


def fetch(url):
    # No proxy, DNS, locale, or browser-specific flags are passed to the application.
    p = subprocess.run(['curl.exe', '-sSfL', '--retry', '2', '--retry-all-errors', '--max-time', '25', url], capture_output=True, timeout=85)
    if p.returncode: raise RuntimeError(p.stderr.decode(errors='replace'))
    return p.stdout.decode('utf8', errors='replace')


def client():
    result = {}
    trace = dict(line.split('=', 1) for line in fetch('https://1.1.1.1/cdn-cgi/trace').splitlines() if '=' in line)
    result['cloudflare'] = {'ip': trace['ip'], 'country': trace['loc']}
    result['china_site_ip'] = re.search(r'\b(?:\d{1,3}\.){3}\d{1,3}\b', fetch('https://my.ip.cn/')).group()
    token = uuid.uuid4().hex
    result['python_resolver'] = socket.getaddrinfo('www.cloudflare.com', 443, socket.AF_INET)[0][4][0]
    for i in (1, 2):
        try: socket.getaddrinfo(f'{token}-{i}.d.ip.net.coffee', 443, socket.AF_INET)
        except socket.gaierror: pass
    native_token = uuid.uuid4().hex
    native = subprocess.run([str(BIN/'DnsProbe.exe'), f'{native_token}-3.d.ip.net.coffee', 'leaf', 'raw'],capture_output=True,text=True,timeout=90)
    (OUT/'native-dns.log').write_text(native.stdout+native.stderr,encoding='utf8')
    assert native.returncode == 0, native
    servers = []
    for _ in range(5):
        time.sleep(2)
        servers = json.loads(fetch(f'https://ip.net.coffee/api/dns/result/{token}')).get('dns_servers', [])
        if servers: break
    result['dns'] = []
    native_servers = json.loads(fetch(f'https://ip.net.coffee/api/dns/result/{native_token}')).get('dns_servers', [])
    result['dns_by_path'] = {'python':servers, 'native':native_servers}
    servers = list(set(servers + native_servers))
    for address in servers:
        geo = json.loads(fetch(f'https://ip.net.coffee/api/geoip/{address}'))
        result['dns'].append({'ip': address, 'country': geo.get('country_code'), 'isp': geo.get('isp')})
    (OUT/'inside-results.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf8')
    assert trace['loc'] == 'US' and trace['ip'] == result['china_site_ip'], result
    assert servers and native_servers and result['dns_by_path']['python'] and all(d['country'].lower() == 'us' for d in result['dns']), result
    print('PASS: ordinary curl + Python DNS, US exit for both witnesses, remote US DNS', flush=True)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    config_dir = OUT/'private'; config_dir.mkdir(exist_ok=True)
    for address, port in [('127.0.0.1',17891), ('127.0.0.153',53)]:
        with socket.socket() as sock: sock.bind((address,port))
    settings = json.loads((BIN/'data/private/proxy.json').read_text(encoding='utf-8-sig'))
    settings['mixed-port'] = 17891
    settings['dns']['listen'] = '127.0.0.153:53'
    cfg = config_dir/'proxy.json'
    cfg.write_text(json.dumps(settings, ensure_ascii=False, indent=2), encoding='utf8')
    for name in ['HTTPS_PROXY','HTTP_PROXY','ALL_PROXY','https_proxy','http_proxy','all_proxy']:
        os.environ.pop(name, None)
    outside_before = re.search(r'\b(?:\d{1,3}\.){3}\d{1,3}\b', fetch('https://my.ip.cn/')).group()
    with (OUT/'core.log').open('w', encoding='utf8') as log:
        core = subprocess.Popen([str(BIN/'proxy/mihomo.exe'),'-d',str(config_dir),'-f',str(cfg)], stdout=log, stderr=log, creationflags=subprocess.CREATE_NO_WINDOW)
        session = None
        try:
            for _ in range(80):
                assert core.poll() is None, 'Private core exited'
                with socket.socket() as sock:
                    if sock.connect_ex(('127.0.0.153',53)) == 0: break
                time.sleep(.1)
            else: raise RuntimeError('Private DNS did not start')
            session_cfg = OUT/'session.ini'
            arguments = subprocess.list2cmdline(['-X','utf8',str(Path(__file__).resolve()),'--client'])
            session_cfg.write_text(f'''[session]
Job=Local\\RegionLauncher.{uuid.uuid4().hex}
Application={sys.executable}
Arguments={arguments}
ProxyHost=127.0.0.1
ProxyPort=17891
DnsAddress=127.0.0.153
Locale=en-US
Region=US
Geo=244
TimeZone=Pacific Standard Time
AutoDst=1
''', encoding='utf-16')
            session = subprocess.Popen([str(BIN/'RegionSession.exe'),'--run',str(session_cfg)],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf8',errors='replace')
            first = session.stdout.readline()
            assert first.startswith('STARTED PID='), (first,session.communicate(timeout=10))
            outside_during = re.search(r'\b(?:\d{1,3}\.){3}\d{1,3}\b', fetch('https://my.ip.cn/')).group()
            output, error = session.communicate(timeout=180)
            (OUT/'client.log').write_text(first+output+error,encoding='utf8')
            assert session.returncode == 0, (output,error,session.returncode)
            outside_after = re.search(r'\b(?:\d{1,3}\.){3}\d{1,3}\b', fetch('https://my.ip.cn/')).group()
            outside = {'before':outside_before,'during':outside_during,'after':outside_after}
            (OUT/'outside-results.json').write_text(json.dumps(outside,indent=2),encoding='utf8')
            assert len(set(outside.values())) == 1, outside
            print(output.strip()); print('PASS: ordinary outside curl exit unchanged before/during/after')
        finally:
            if session and session.poll() is None:
                session.terminate(); session.wait(timeout=10)
            core.terminate(); core.wait(timeout=10)


if __name__ == '__main__':
    if '--client' in sys.argv: client()
    else: main()
