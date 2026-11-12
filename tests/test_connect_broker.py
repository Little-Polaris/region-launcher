"""Real Winsock calls and child injection, with a local SOCKS fixture and UU left running."""
import json
import os
from pathlib import Path
import shutil
import socket
import struct
import subprocess
import sys
import threading

ROOT = Path(__file__).resolve().parents[1]
BIN = ROOT / 'build/service-bin'
OUT = ROOT / 'build/test-results/connect-broker'


def exact(stream, size):
    data = b''
    while len(data) < size:
        part = stream.recv(size - len(data))
        if not part:
            raise EOFError()
        data += part
    return data


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    app = OUT / 'chrome.exe'
    shutil.copy2(BIN / os.environ.get('REGION_PROBE_BINARY', 'RegionProbe.exe'), app)
    listener = socket.socket()
    listener.bind(('127.0.0.1', 0))
    listener.listen()
    listener.settimeout(.2)
    stop = threading.Event()
    requests, errors = [], []

    def serve():
        while not stop.is_set():
            try:
                stream, _ = listener.accept()
            except TimeoutError:
                continue
            try:
                with stream:
                    stream.settimeout(5)
                    assert exact(stream, 3) == b'\x05\x01\x00'
                    stream.sendall(b'\x05\x00')
                    request = exact(stream, 10)
                    assert request[:4] == b'\x05\x01\x00\x01'
                    target = (socket.inet_ntoa(request[4:8]), struct.unpack('!H', request[8:])[0])
                    assert target == ('203.0.113.42', 443), target
                    requests.append(target)
                    stream.sendall(b'\x05\x00\x00\x01\x7f\x00\x00\x01\x00\x01')
                    assert exact(stream, 5) == b'probe'
                    stream.sendall(b'broker-ok')
            except Exception as exc:
                errors.append(repr(exc))

    worker = threading.Thread(target=serve)
    worker.start()
    results = []
    try:
        for mode in ('--network', '--wsaconnect', '--nonblocking', '--connectex', '--spawn-network'):
            p = subprocess.run([str(ROOT/'build/ConnectHarness.exe'), str(BIN/'RegionHook.dll'), str(app),
                                mode+' 203.0.113.42 443', str(listener.getsockname()[1])], capture_output=True, timeout=25)
            output = (p.stdout+p.stderr).decode('utf-8', errors='replace')
            (OUT/(mode[2:]+'.log')).write_text(output, encoding='utf-8')
            results.append({'mode': mode, 'exit': p.returncode, 'ok': 'NETWORK=broker-ok' in output})
            assert p.returncode == 0 and results[-1]['ok'], (results[-1], output)
        assert len(requests) == 5 and not errors, (requests, errors)
        command = subprocess.list2cmdline([str(ROOT/'tests/broker_socket_client.py')])
        p = subprocess.run([str(ROOT/'build/ConnectHarness.exe'), str(BIN/'RegionHook.dll'), sys.executable,
                            command, str(listener.getsockname()[1])], capture_output=True, timeout=35)
        output = (p.stdout+p.stderr).decode('utf-8', errors='replace')
        (OUT/'socket-semantics.log').write_text(output, encoding='utf-8')
        assert p.returncode == 0 and 'SOCKET_SEMANTICS_OK' in output, output
        assert len(requests) == 55 and not errors, (len(requests), errors)
        # A usable direct target exists. Losing SOCKS must not fall back to it.
        direct = socket.socket()
        host = socket.gethostbyname(socket.gethostname())
        direct.bind((host, 0)); direct.listen(); direct.settimeout(.3)
        closed = socket.socket(); closed.bind(('127.0.0.1', 0))
        closed_port = closed.getsockname()[1]  # Bound but deliberately not listening.
        try:
            command = subprocess.list2cmdline([str(ROOT/'tests/broker_socket_client.py'), '--failure', host, str(direct.getsockname()[1])])
            p = subprocess.run([str(ROOT/'build/ConnectHarness.exe'), str(BIN/'RegionHook.dll'), sys.executable,
                                command, str(closed_port)], capture_output=True, timeout=20)
            output = (p.stdout+p.stderr).decode('utf-8', errors='replace')
            (OUT/'fail-closed.log').write_text(output, encoding='utf-8')
            assert p.returncode == 0 and 'FAIL_CLOSED' in output, output
            try:
                escaped, _ = direct.accept()
            except TimeoutError:
                pass
            else:
                escaped.close()
                raise AssertionError('Proxy failure reached the direct target')
        finally:
            closed.close(); direct.close()
        (OUT/'results.json').write_text(json.dumps({'results': results, 'requests': requests}, indent=2))
        print('PASS: chrome.exe five connection paths; Python 48 concurrent IPv4/mapped requests, peer/half-close semantics; IPv6 blocked; proxy failure cannot reach direct listener')
    finally:
        stop.set()
        worker.join(6)
        listener.close()
        app.unlink(missing_ok=True)


if __name__ == '__main__':
    main()
