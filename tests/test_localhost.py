"""Local callbacks must work without sending their names to the private DNS server."""
import json
import os
from pathlib import Path
import socket
import socketserver
import struct
import subprocess
import sys
import threading
import uuid

ROOT = Path(__file__).resolve().parents[1]
BIN = ROOT / 'build/service-bin'
OUT = ROOT / 'build/test-results/localhost'
seen = []


class RefusingDns(socketserver.BaseRequestHandler):
    def handle(self):
        packet, sock = self.request
        offset, labels = 12, []
        while packet[offset]:
            size = packet[offset]; offset += 1
            labels.append(packet[offset:offset + size].decode('ascii')); offset += size
        seen.append('.'.join(labels).lower())
        # NXDOMAIN: localhost may not depend on any DNS server being willing to resolve it.
        sock.sendto(packet[:2] + struct.pack('!HHHHH', 0x8183, 1, 0, 0, 0) + packet[12:offset + 5], self.client_address)


def launch(app, args):
    config = OUT / 'session.ini'
    config.write_text(f'''[session]
Job=Local\\RegionLauncher.{uuid.uuid4().hex}
Application={app}
Arguments={subprocess.list2cmdline(args)}
DnsAddress=127.0.0.154
Locale=en-US
Region=US
Geo=244
TimeZone=Pacific Standard Time
AutoDst=1
''', encoding='utf-16')
    p = subprocess.run([str(BIN / 'RegionSession.exe'), '--locale-test', str(config)],
                       capture_output=True, text=True, timeout=45)
    assert p.returncode == 0, (p.returncode, p.stdout, p.stderr)
    return p.stdout


def outside():
    code = "import socket,json; print(json.dumps(sorted(set((r[0],r[4][0]) for r in socket.getaddrinfo('localhost',443)))))"
    return json.loads(subprocess.check_output([sys.executable, '-c', code], text=True))


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    before = outside()
    with socketserver.ThreadingUDPServer(('127.0.0.154', 53), RefusingDns) as server:
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            for name in ['localhost', 'LOCALHOST.', 'callback.localhost']:
                text = launch(BIN / 'DnsProbe.exe', [name, 'local'])
                (OUT / (name + '.log')).write_text(text, encoding='utf-8')
                assert text.count('ip=127.0.0.1') == 11, text
                assert 'DnsQueryAAAA status=9501' in text, text
                assert 'DnsQueryEx-async pending=0' in text, text
            assert not seen, seen
            node = Path(os.environ.get('REGION_TEST_NODE', Path.home() / '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe'))
            text = launch(node, [str(ROOT / 'tests/localhost_callback.cjs'), '--probe'])
            (OUT / 'node.log').write_text(text, encoding='utf-8')
            assert 'PASS: Node localhost' in text, text
            assert not seen, seen
            for name in ['notlocalhost.invalid', 'localhost.example.invalid']:
                text = launch(BIN / 'DnsProbe.exe', [name, 'local'])
                assert 'ip=127.0.0.1' not in text, text
                assert name in seen, seen
        finally:
            server.shutdown()
    assert outside() == before, 'Outside localhost resolver changed'
    (OUT / 'dns-requests.json').write_text(json.dumps(seen, indent=2), encoding='utf-8')
    print('PASS: Windows DNS APIs, Node callback, no localhost DNS traffic, similar public names not bypassed, outside resolver unchanged')


if __name__ == '__main__': main()
