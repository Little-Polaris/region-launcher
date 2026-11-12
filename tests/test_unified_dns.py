"""Same unmodified DNS client inside/outside, including child inheritance and cache isolation."""
import json
import os
import socket
import socketserver
import struct
import subprocess
import threading
import uuid
import sys
from contextlib import ExitStack
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BIN = Path(os.environ.get('REGION_TEST_BIN', ROOT / 'build/service-bin'))
PROBE = os.environ.get('REGION_DNS_PROBE', 'DnsProbe.exe')
OUT = ROOT / 'build/test-results/unified-dns'
OUT.mkdir(parents=True, exist_ok=True)
seen = []
refuse = False
full_network = '--network' in sys.argv
inside_address = os.environ.get('REGION_TEST_DNS_ADDRESS', '127.0.0.153')
domain = 'region-' + uuid.uuid4().hex + '.invalid'


class Resolver:
    def response(self, packet):
        offset = 12
        labels = []
        while packet[offset]:
            size = packet[offset]; offset += 1
            labels.append(packet[offset:offset+size].decode()); offset += size
        offset += 1
        name = '.'.join(labels)
        kind, _ = struct.unpack('!HH', packet[offset:offset+4])
        seen.append((self.server.server_address[0], name, kind))
        assert name == domain, name
        answer_ip = '203.0.113.77' if self.server.server_address[0] == inside_address else '203.0.113.88'
        answer = b'\xc0\x0c' + struct.pack('!HHIH', 1, 1, 30, 4) + socket.inet_aton(answer_ip) if kind == 1 else b''
        if kind == 28: answer = b'\xc0\x0c' + struct.pack('!HHIH', 28, 1, 30, 16) + socket.inet_pton(socket.AF_INET6, '2001:db8::77')
        refused = refuse and self.server.server_address[0] == inside_address
        if refused: answer = b''
        reply = packet[:2] + struct.pack('!HHHHH', 0x8185 if refused else 0x8180, 1, int(bool(answer)), 0, 0) + packet[12:offset+4] + answer
        return reply

class DNS(Resolver, socketserver.BaseRequestHandler):
    def handle(self):
        packet, sock = self.request
        sock.sendto(self.response(packet), self.client_address)

def exact(sock, n):
    data = b''
    while len(data) < n:
        part = sock.recv(n-len(data))
        if not part: raise EOFError()
        data += part
    return data

class TCPDNS(Resolver, socketserver.BaseRequestHandler):
    def handle(self):
        self.request.settimeout(5)
        try:
            size = struct.unpack('!H', exact(self.request, 2))[0]
            reply = self.response(exact(self.request, size))
            self.request.sendall(struct.pack('!H', len(reply)) + reply)
        except (EOFError, OSError): pass

class UDP6(socketserver.ThreadingUDPServer):
    address_family = socket.AF_INET6
class TCP6(socketserver.ThreadingTCPServer):
    address_family = socket.AF_INET6


def outside(label, env=None):
    p = subprocess.run([str(BIN/PROBE), domain, 'leaf'] + (['raw'] if full_network else []), capture_output=True, text=True, timeout=45, env=env)
    (OUT/(label+'.txt')).write_text(p.stdout+p.stderr, encoding='utf8')
    assert p.returncode == 0, p
    assert 'servers=0' in p.stdout and '203.0.113.77' not in p.stdout, p.stdout
    assert 'IPv6 WSASocketW=available WSASocketA=available Icmp6=available' in p.stdout, p.stdout
    if full_network: assert p.stdout.count('ip=203.0.113.88') == 4, p.stdout
    return p.stdout


with ExitStack() as stack:
    for address in [inside_address] + (['127.0.0.2', '::1'] if full_network else []):
        for server_class, handler in [(UDP6 if ':' in address else socketserver.ThreadingUDPServer, DNS), (TCP6 if ':' in address else socketserver.ThreadingTCPServer, TCPDNS)]:
            server = stack.enter_context(server_class((address, 53), handler))
            threading.Thread(target=server.serve_forever, daemon=True).start()
            stack.callback(server.shutdown)
    before = outside('outside-before')
    cfg = OUT/'session.ini'
    cfg.write_text(f'''[session]
Job=Local\\RegionLauncher.{uuid.uuid4().hex}
Application={BIN/PROBE}
Arguments={domain} child {'raw' if full_network else ''}
DnsAddress={inside_address}
ProxyHost=127.0.0.1
ProxyPort=17890
Locale=en-US
Region=US
Geo=244
TimeZone=Pacific Standard Time
AutoDst=1
''', encoding='utf-16')
    inside = subprocess.Popen([str(BIN/'RegionSession.exe'), '--run' if full_network else '--locale-test', str(cfg)], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    stack.callback(lambda: inside.poll() is None and inside.kill())
    first = inside.stdout.readline()
    assert first.startswith('STARTED PID='), (first, inside.communicate(timeout=10))
    during = outside('outside-during')
    output, errors = inside.communicate(timeout=45)
    output = first + output
    (OUT/'inside.txt').write_text(output+errors, encoding='utf8')
    (OUT/'requests.json').write_text(json.dumps(seen, indent=2), encoding='utf8')
    assert inside.returncode == 0, (output, errors, inside.returncode)
    assert output.count('servers=0') == 2, output
    assert output.count('ip=203.0.113.77') == (24 if full_network else 20), output
    assert 'ip=2001:db8::77' not in output, output
    assert output.count('DnsQueryAAAA status=9501') == 2, output
    assert output.count('IPv6 WSASocketW=available WSASocketA=available Icmp6=blocked') == 2, output
    if full_network:
        assert 'RawDNS-UDP-v6 status=0' not in output, output
        assert 'RawDNS-TCP-v6 status=0' not in output, output
    assert output.count('DnsQueryEx-async pending=1') == 2, output
    after = outside('outside-after')
    if full_network:
        driver_cfg = OUT/'driver-only.ini'
        lines = cfg.read_text(encoding='utf-16').splitlines()
        lines = [f'Arguments={domain} raw-only' if line.startswith('Arguments=') else
                 f'Job=Local\\RegionLauncher.{uuid.uuid4().hex}' if line.startswith('Job=') else line for line in lines]
        driver_cfg.write_text('\n'.join(lines),encoding='utf-16')
        driver = subprocess.run([str(BIN/'RegionSession.exe'),'--network-test',str(driver_cfg)],capture_output=True,text=True,timeout=45)
        (OUT/'driver-only.txt').write_text(driver.stdout+driver.stderr,encoding='utf8')
        assert driver.returncode == 0 and driver.stdout.count('ip=203.0.113.77') == 2 and '203.0.113.88' not in driver.stdout, driver.stdout
        assert 'RawDNS-UDP-v6 status=0' not in driver.stdout and 'RawDNS-TCP-v6 status=0' not in driver.stdout, driver.stdout
        print('PASS: packet engine independently blocks inside IPv6 TCP/UDP with no injected hooks')
        addresses = json.loads(subprocess.check_output(['powershell.exe','-NoProfile','-Command',
            "@(Get-NetIPAddress -AddressFamily IPv6 | Where-Object {$_.IPAddress -like 'fe80:*' -and $_.AddressState -eq 'Preferred'} | Select-Object IPAddress,InterfaceIndex) | ConvertTo-Json -Compress"],text=True))
        if isinstance(addresses, dict): addresses = [addresses]
        assert addresses, 'This host acceptance run requires a usable link-local IPv6 address'
        link = addresses[0]; address = link['IPAddress'].split('%')[0]; scope = link['InterfaceIndex']
        for server_class, handler in [(UDP6,DNS),(TCP6,TCPDNS)]:
            server = stack.enter_context(server_class((address,53,0,scope),handler))
            threading.Thread(target=server.serve_forever,daemon=True).start(); stack.callback(server.shutdown)
        env = os.environ.copy(); env['DNS_PROBE_IPV6_TARGET'] = f'{address}%{scope}'
        outside('outside-linklocal',env)
        before_link = sum(item[0] == address for item in seen)
        assert before_link >= 2
        driver_cfg.write_text(driver_cfg.read_text(encoding='utf-16').replace(next(line for line in lines if line.startswith('Job=')),f'Job=Local\\RegionLauncher.{uuid.uuid4().hex}'),encoding='utf-16')
        link_result = subprocess.run([str(BIN/'RegionSession.exe'),'--network-test',str(driver_cfg)],capture_output=True,text=True,timeout=45,env=env)
        (OUT/'driver-linklocal.txt').write_text(link_result.stdout+link_result.stderr,encoding='utf8')
        assert link_result.returncode == 0 and link_result.stdout.count('ip=203.0.113.77') == 2, link_result.stdout+link_result.stderr
        assert 'RawDNS-UDP-v6 status=0' not in link_result.stdout and 'RawDNS-TCP-v6 status=0' not in link_result.stdout, link_result.stdout
        assert sum(item[0] == address for item in seen) == before_link, 'Selected process sent data to the IPv6 link-local server'
        print('PASS: IPv6 link-local TCP/UDP blocked inside with no hooks; outside same endpoints work')
    if '--failure' in sys.argv:
        domain = 'www.example.com'
        baseline = subprocess.run([str(BIN/PROBE),domain],capture_output=True,text=True,timeout=45)
        assert 'getaddrinfo status=0 ip=' in baseline.stdout, baseline.stdout
        refuse = True
        # A valid name is already in the host cache; the private DNS refusal must win.
        text = cfg.read_text(encoding='utf-16').splitlines()
        text = [f'Arguments={domain}' if line.startswith('Arguments=') else line for line in text]
        cfg.write_text('\n'.join(text), encoding='utf-16')
        denied = subprocess.run([str(BIN/'RegionSession.exe'),'--locale-test',str(cfg)],capture_output=True,text=True,timeout=90)
        (OUT/'dns-refused.txt').write_text(denied.stdout+denied.stderr,encoding='utf8')
        assert denied.returncode == 0 and ' ip=' not in denied.stdout, denied.stdout
        print('PASS: private resolver refusal does not fall back to the host DNS/cache')
(OUT/'requests.json').write_text(json.dumps(seen, indent=2), encoding='utf8')
print('PASS: 10 Windows DNS paths + async; inside AAAA/ICMPv6 disabled, outside unchanged' + ('; inside IPv6 TCP/UDP blocked; raw IPv4 DNS redirected only inside' if full_network else ''))
