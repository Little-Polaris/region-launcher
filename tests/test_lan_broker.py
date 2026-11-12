"""LAN without SOCKS, DNS precedence, outside connectivity, and both process bitnesses."""
from contextlib import ExitStack
import ipaddress
import json
import os
from pathlib import Path
import socket
import socketserver
import subprocess
import sys
import threading

ROOT = Path(__file__).resolve().parents[1]
BIN = ROOT/'build/service-bin'
OUT = ROOT/'build/test-results/lan'
OUT.mkdir(parents=True, exist_ok=True)
host = socket.gethostbyname(socket.gethostname())
assert any(ipaddress.ip_address(host) in ipaddress.ip_network(cidr) for cidr in
           ('10.0.0.0/8','172.16.0.0/12','192.168.0.0/16','169.254.0.0/16')), host

class TCP(socketserver.BaseRequestHandler):
    def handle(self):
        self.request.settimeout(5)
        assert self.request.recv(5) == b'probe'
        self.request.sendall(self.server.reply)

class UDP(socketserver.BaseRequestHandler):
    def handle(self):
        packet, s = self.request
        assert packet == b'probe', packet
        s.sendto(self.server.reply, self.client_address)

results = {}
with ExitStack() as stack:
    def serve(cls, handler, address, reply):
        server = stack.enter_context(cls(address, handler)); server.reply = reply
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        stack.callback(server.shutdown)
        return server.server_address[1]
    tcp_port = serve(socketserver.ThreadingTCPServer, TCP, (host,0), b'lan-ok')
    udp_port = serve(socketserver.ThreadingUDPServer, UDP, (host,0), b'lan-ok')
    serve(socketserver.ThreadingTCPServer, TCP, ('127.0.0.154',53), b'private-dns')
    serve(socketserver.ThreadingUDPServer, UDP, ('127.0.0.154',53), b'private-dns')
    unavailable = stack.enter_context(socket.socket()); unavailable.bind(('127.0.0.1',0))
    env = dict(os.environ, REGION_LAUNCHER_DNS_ADDRESS='127.0.0.154')
    def run(label, binary, args):
        p = subprocess.run([str(ROOT/'build/ConnectHarness.exe'), str(BIN/'RegionHook.dll'), str(binary),
                            subprocess.list2cmdline(args), str(unavailable.getsockname()[1])],
                           capture_output=True, timeout=35, env=env)
        output = (p.stdout+p.stderr).decode('utf8', errors='replace')
        (OUT/(label+'.log')).write_text(output, encoding='utf8')
        assert p.returncode == 0, (label, output)
        results[label] = True
        return output
    def outside():
        with socket.create_connection((host,tcp_port), timeout=3) as s:
            s.sendall(b'probe'); assert s.recv(64) == b'lan-ok'
    outside()
    for bits in (32,64):
        binary = BIN/('RegionProbe32.exe' if bits == 32 else 'RegionProbe.exe')
        for mode in ('--network','--wsaconnect','--connectex','--nonblocking','--spawn-network','--udp'):
            port = udp_port if mode == '--udp' else tcp_port
            output = run(f'{bits}-{mode[2:]}', binary, [mode,host,str(port)])
            assert 'NETWORK=lan-ok' in output and 'DIRECT LAN' in output and 'SOCKS5' not in output, output
            outside()
        for mode in ('--network','--udp'):
            output = run(f'{bits}-dns-{mode[2:]}', binary, [mode,host,'53'])
            assert 'NETWORK=private-dns' in output and 'PROXY DNS 127.0.0.154:53' in output and 'DIRECT LAN' not in output, output
            no_dns = env.copy(); no_dns.pop('REGION_LAUNCHER_DNS_ADDRESS', None)
            p = subprocess.run([str(ROOT/'build/ConnectHarness.exe'),str(BIN/'RegionHook.dll'),str(binary),
                                f'{mode} {host} 53',str(unavailable.getsockname()[1])],
                               capture_output=True,timeout=20,env=no_dns)
            output = (p.stdout+p.stderr).decode('utf8',errors='replace')
            (OUT/f'{bits}-no-resolver-{mode[2:]}.log').write_text(output,encoding='utf8')
            assert p.returncode != 0 and 'FAILED SOCKS5' in output and 'DIRECT LAN' not in output, output
    output = run('mapped-bound', sys.executable, [str(ROOT/'tests/lan_socket_client.py'),host,str(tcp_port),str(udp_port)])
    assert 'LAN_SOCKET_OK' in output and 'SOCKS5' not in output, output
    outside()
(OUT/'results.json').write_text(json.dumps(results, indent=2))
print('PASS: x86/x64 LAN TCP/UDP works with proxy unavailable; DNS priority, NIC binding, mapped IPv4, child inheritance, outside connectivity')
