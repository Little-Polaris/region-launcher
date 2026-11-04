"""Local-only integration: no requests to Claude or any external destination.

The native session elevates only its network engine and changes no system routes/DNS.
"""
import ctypes
import socket
import struct
import subprocess
import threading
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BIN = ROOT / 'build/bin'
OUT = ROOT / 'build/test-results/network'
OUT.mkdir(parents=True, exist_ok=True)


def exact(sock, n):
    result = b''
    while len(result) < n:
        part = sock.recv(n - len(result))
        if not part:
            raise EOFError()
        result += part
    return result


tcp_direct = socket.socket()
tcp_direct.bind(('127.0.0.2', 0))
port = tcp_direct.getsockname()[1]
tcp_direct.listen()
udp_direct = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
udp_direct.bind(('127.0.0.2', port))
proxy = socket.socket()
proxy.bind(('127.0.0.1', 0))
proxy_port = proxy.getsockname()[1]
proxy.listen()
requests = []
reject_tcp = False


def direct_tcp():
    while True:
        conn, _ = tcp_direct.accept()
        with conn:
            conn.recv(100)
            conn.sendall(b'DIRECT')


def direct_udp():
    while True:
        _, address = udp_direct.recvfrom(4096)
        udp_direct.sendto(b'DIRECT', address)


def serve_socks(conn):
    with conn:
        conn.settimeout(15)
        try:
            version, count = exact(conn, 2)
            assert version == 5 and 0 in exact(conn, count)
            conn.sendall(b'\x05\x00')
            version, command, _, kind = exact(conn, 4)
            address = exact(conn, 4 if kind == 1 else 16 if kind == 4 else exact(conn, 1)[0])
            destination_port = struct.unpack('!H', exact(conn, 2))[0]
            requests.append((command, kind, address.hex(), destination_port))
            if command == 1:
                assert kind == 1 and address == socket.inet_aton('127.0.0.2') and destination_port == port
                if reject_tcp:
                    conn.sendall(b'\x05\x05\x00\x01' + b'\x00' * 6)
                    return
                conn.sendall(b'\x05\x00\x00\x01' + socket.inet_aton('127.0.0.1') + b'\x00\x00')
                conn.recv(100)
                conn.sendall(b'PROXY')
            elif command == 3:
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as relay:
                    relay.bind(('127.0.0.1', 0)); relay.settimeout(15)
                    conn.sendall(b'\x05\x00\x00\x01' + socket.inet_aton('127.0.0.1') + struct.pack('!H', relay.getsockname()[1]))
                    packet, sender = relay.recvfrom(4096)
                    assert packet[3] == 1 and packet[4:8] == socket.inet_aton('127.0.0.2')
                    relay.sendto(packet[:10] + b'PROXY', sender)
                    conn.recv(1)
        except (EOFError, ConnectionError, TimeoutError, OSError):
            pass


def proxy_loop():
    while True:
        conn, _ = proxy.accept()
        threading.Thread(target=serve_socks, args=(conn,), daemon=True).start()


for func in (direct_tcp, direct_udp, proxy_loop):
    threading.Thread(target=func, daemon=True).start()

for protocol, flag in [('tcp', '--network'), ('udp', '--udp'), ('child', '--spawn-network'), ('rejected', '--network')]:
    reject_tcp = protocol == 'rejected'
    config = OUT / f'{protocol}.ini'
    config.write_text(f'''[session]
Job=Local\\RegionLauncher.{uuid.uuid4().hex}
Application={BIN / 'RegionProbe.exe'}
Arguments={flag} 127.0.0.2 {port} 2000
ProxyHost=127.0.0.1
ProxyPort={proxy_port}
ProxyLoopback=1
Locale=en-US
Region=US
Geo=244
TimeZone=Pacific Standard Time
AutoDst=1
''', encoding='utf-16')
    launched = subprocess.Popen([str(BIN / 'RegionSession.exe'), '--run', str(config)],
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                               encoding='utf-8', errors='replace')
    try:
        # The engine is ready and the selected process is enrolled before STARTED is emitted.
        first = launched.stdout.readline()
        assert first.startswith('STARTED PID='), (first, launched.communicate(timeout=10))
        outside = subprocess.run([str(BIN / 'RegionProbe.exe'), '--udp' if protocol == 'udp' else '--network', '127.0.0.2', str(port)],
                                 capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=15)
        assert launched.poll() is None, 'Selected process must still be running during the outside-instance test'
        output, err = launched.communicate(timeout=20)
        (OUT / f'{protocol}.txt').write_text(first + output + '\nOUTSIDE:\n' + outside.stdout + '\nERROR:\n' + err, encoding='utf-8')
        if protocol == 'rejected':
            assert 'NETWORK=PROXY' not in output and 'NETWORK=DIRECT' not in output and launched.returncode != 0, (output, err)
        else:
            assert 'NETWORK=PROXY' in output and launched.returncode == 0, (output, err, launched.returncode)
        assert 'NETWORK=DIRECT' in outside.stdout and outside.returncode == 0, outside
        print(f'PASS {protocol}: launched instance {"BLOCKED" if reject_tcp else "PROXY"}, simultaneous outside instance DIRECT')
    finally:
        if launched.poll() is None:
            launched.terminate()
            launched.wait(timeout=10)
(OUT / 'requests.txt').write_text(repr(requests), encoding='utf-8')
print('PASS: local-only TCP/UDP instance routing')
