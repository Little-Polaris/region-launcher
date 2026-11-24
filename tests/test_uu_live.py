"""Opt-in live UU coexistence: selected-process TCP, UDP STUN and DNS witnesses."""
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
BIN = ROOT / 'build/service-bin'
OUT = ROOT / 'build/test-results/uu-live'


def fetch(url):
    p = subprocess.run(['curl.exe', '--silent', '--show-error', '--fail', '--proxy', 'socks5h://127.0.0.1:17890',
                        '--max-time', '20', url], capture_output=True, timeout=25)
    p.check_returncode()
    return p.stdout.decode('utf8')


def session(label, app, args):
    config = OUT / (label + '.ini')
    config.write_text(f'[session]\nJob=Local\\RegionLauncher.{uuid.uuid4().hex}\nOwnerPid={os.getpid()}\nApplication={app}\nArguments={subprocess.list2cmdline(args)}\nProxyHost=127.0.0.1\nProxyPort=17890\nDnsAddress=127.0.0.153\n', encoding='utf-16')
    p = subprocess.run([str(BIN/'RegionSession.exe'), '--run', str(config)], capture_output=True, timeout=80)
    text = (p.stdout+p.stderr).decode('utf8', errors='replace')
    (OUT/(label+'.log')).write_text(text, encoding='utf8')
    assert p.returncode == 0, (label, p.returncode, text[-3000:])
    return text


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    private = OUT/'private'; private.mkdir(exist_ok=True)
    config = json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
    assert config['mixed-port'] == 17890 and config['dns']['listen'] == '127.0.0.153:53'
    for address, port in [('127.0.0.1', 17890), ('127.0.0.153', 53)]:
        with socket.socket() as sock:
            sock.bind((address, port))
    runtime = private/'runtime.json'; config['log-level'] = 'warning'
    runtime.write_text(json.dumps(config, ensure_ascii=False), encoding='utf8')
    generated = []
    results = {}
    with (OUT/'core.log').open('w', encoding='utf8') as log:
        core = subprocess.Popen([str(BIN/'proxy/mihomo.exe'), '-d', str(private), '-f', str(runtime)],
                                stdout=log, stderr=log, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            for _ in range(50):
                assert core.poll() is None, 'Owned core exited'
                with socket.socket() as sock:
                    if sock.connect_ex(('127.0.0.1', 17890)) == 0:
                        break
                time.sleep(.1)
            trace = dict(line.split('=', 1) for line in fetch('https://www.cloudflare.com/cdn-cgi/trace').splitlines() if '=' in line)
            results['expected_exit'] = trace['ip']
            app = OUT/'chrome.exe'; generated.append(app)
            shutil.copy2(BIN/'RegionProbe.exe', app)
            target = socket.gethostbyname('stun.cloudflare.com')
            results['udp'] = {}
            for label in ('unconnected', 'connected', 'bound-unconnected', 'bound-connected'):
                mode = label.removeprefix('bound-')
                args = ['--stun', target, '3478', mode]
                if label.startswith('bound-'): args.append(socket.gethostbyname(socket.gethostname()))
                text = session('udp-'+label, app, args)
                exit_ip = re.search(r'STUN_EXIT=([0-9.]+)', text).group(1)
                results['udp'][label] = exit_ip
                assert exit_ip == trace['ip'], results
            print('PASS: chrome.exe connected and unconnected UDP share selected proxy exit', flush=True)
            shutil.copy2(BIN/'DnsProbe.exe', app)
            token = uuid.uuid4().hex
            text = session('dns-native', app, [token+'-1.d.ip.net.coffee', 'leaf', 'raw'])
            assert 'DnsQueryAAAA status=9501' in text and 'Icmp6=blocked' in text, text
            assert 'RawDNS-UDP-v4 status=0' in text and 'RawDNS-TCP-v4 status=0' in text, text
            servers = []
            for _ in range(5):
                servers = json.loads(fetch('https://ip.net.coffee/api/dns/result/'+token)).get('dns_servers', [])
                if servers:
                    break
                time.sleep(1)
            assert servers, 'DNS witness did not return a result'
            results['dns'] = [json.loads(fetch('https://ip.net.coffee/api/geoip/'+ip)) for ip in servers]
            assert all(x.get('country_code', '').lower() == 'us' for x in results['dns']), results['dns']
            print('PASS: chrome.exe native DNS witness reports US resolvers', flush=True)
            shutil.copy2(BIN/'RegionLauncher.exe', app)
            for name in ('RegionLauncher.dll','RegionLauncher.deps.json','RegionLauncher.runtimeconfig.json','YamlDotNet.dll','AntdUI.dll'):
                destination = OUT/name; generated.append(destination); shutil.copy2(BIN/name, destination)
            report = OUT/'https.json'
            session('https', app, ['--network-probe', str(report)])
            results['https'] = json.loads(report.read_text())
            assert all(x['Ok'] and x['Ip'] == trace['ip'] for x in results['https']), results
            text = session('curl', Path(os.environ['SystemRoot'])/'System32/curl.exe', ['-sSf', '--max-time', '20', 'https://my.ip.cn/'])
            assert trace['ip'] in text, text
            results['curl'] = trace['ip']
            print('PASS: selected chrome.exe HTTPS and unconfigured curl use selected exit', flush=True)
        finally:
            (OUT/'results.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf8')
            core.terminate(); core.wait(timeout=10)
            runtime.unlink(missing_ok=True)
            for path in generated:
                path.unlink(missing_ok=True)


if __name__ == '__main__':
    main()
