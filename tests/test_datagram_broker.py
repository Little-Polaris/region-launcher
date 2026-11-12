"""Native asynchronous UDP APIs, source restoration and association recovery."""
from pathlib import Path
import json
import os
import shutil
import socket
import struct
import subprocess
import sys
import threading
from test_connect_broker import exact

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT/'build/test-results/datagram-broker'


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    app = OUT/'chrome.exe'; shutil.copy2(ROOT/'build'/os.environ.get('REGION_DATAGRAM_BINARY', 'DatagramProbe.exe'), app)
    listener = socket.socket(); listener.bind(('127.0.0.1', 0)); listener.listen(); listener.settimeout(.2)
    stop = threading.Event(); errors = []; requests = []; threads = []

    def handle(stream):
        try:
            with stream, socket.socket(type=socket.SOCK_DGRAM) as udp:
                stream.settimeout(5)
                assert exact(stream, 3) == b'\x05\x01\x00'; stream.sendall(b'\x05\x00')
                assert exact(stream, 10)[:4] == b'\x05\x03\x00\x01'
                udp.bind(('127.0.0.1', 0)); udp.settimeout(.2)
                stream.sendall(b'\x05\x00\x00\x01\x7f\x00\x00\x01'+struct.pack('!H', udp.getsockname()[1]))
                while not stop.is_set():
                    try: data, address = udp.recvfrom(65536)
                    except TimeoutError: continue
                    assert data[:4] == b'\x00\x00\x00\x01'
                    requests.append((socket.inet_ntoa(data[4:8]), struct.unpack('!H', data[8:10])[0]))
                    udp.sendto(data, address)
                    if data[10:] == b'restart': break
        except (ConnectionResetError, EOFError): pass
        except Exception as exc: errors.append(repr(exc))

    def serve():
        while not stop.is_set():
            try: stream, _ = listener.accept()
            except TimeoutError: continue
            thread = threading.Thread(target=handle, args=(stream,)); threads.append(thread); thread.start()

    worker = threading.Thread(target=serve); worker.start()
    results = {}
    def run(label, binary, args, port=None):
        p = subprocess.run([str(ROOT/'build/ConnectHarness.exe'), str(ROOT/'build/service-bin/RegionHook.dll'),
                            str(binary), args, str(port or listener.getsockname()[1])], capture_output=True, timeout=35)
        output = (p.stdout+p.stderr).decode('utf8', errors='replace'); (OUT/(label+'.log')).write_text(output, encoding='utf8')
        assert p.returncode == 0, (label, p.returncode, output)
        results[label] = True
    try:
        for mode in ('sync', 'connected', 'event', 'result', 'iocp', 'iocpex', 'callback', 'recvmsg', 'sendmsg'):
            run(mode, app, mode)
        run('multi-peer-recovery', sys.executable, subprocess.list2cmdline([str(ROOT/'tests/datagram_socket_client.py')]))
        with socket.socket(type=socket.SOCK_DGRAM) as direct, socket.socket() as unavailable:
            direct.bind((socket.gethostbyname(socket.gethostname()), 0)); direct.settimeout(.3)
            unavailable.bind(('127.0.0.1', 0))
            # Prove the target is reachable before checking the selected process.
            with socket.socket(type=socket.SOCK_DGRAM) as baseline:
                baseline.sendto(b'baseline', direct.getsockname()); assert direct.recvfrom(256)[0] == b'baseline'
            run('fail-closed', sys.executable, subprocess.list2cmdline([str(ROOT/'tests/datagram_socket_client.py'),
                direct.getsockname()[0], str(direct.getsockname()[1])]), unavailable.getsockname()[1])
            try: direct.recvfrom(256)
            except TimeoutError: pass
            else: raise AssertionError('UDP escaped to the direct target')
        assert not errors, errors
        (OUT/'results.json').write_text(json.dumps({'results': results, 'requests': requests}, indent=2))
        print('PASS: nine native UDP paths; peer restoration, mapped IPv4, multiple targets, proxy association recovery and IPv6 block')
    finally:
        stop.set(); worker.join(6)
        for thread in threads: thread.join(6)
        listener.close(); app.unlink(missing_ok=True)


if __name__ == '__main__': main()
