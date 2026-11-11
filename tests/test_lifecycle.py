"""Keep outside traffic alive across real installed-service start/stop cycles."""
import argparse
import ctypes as c
from ctypes import wintypes as w
import http.client
import json
import os
from pathlib import Path
import queue
import socket
import ssl
import subprocess
import sys
import threading
import time
import traceback
import uuid

ROOT = Path(__file__).resolve().parents[1]
BIN = Path(os.environ.get('REGION_TEST_BIN', ROOT/'build/service-bin'))
k = c.WinDLL('kernel32', use_last_error=True)
k.OpenEventW.argtypes = [w.DWORD, w.BOOL, w.LPCWSTR]
k.OpenEventW.restype = w.HANDLE
k.SetEvent.argtypes = [w.HANDLE]
k.CloseHandle.argtypes = [w.HANDLE]
k.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
k.OpenProcess.restype = w.HANDLE
k.WaitForSingleObject.argtypes = [w.HANDLE, w.DWORD]


def exact(sock, count):
    data = b''
    while len(data) < count:
        part = sock.recv(count-len(data))
        if not part:
            raise EOFError('outside TCP connection closed')
        data += part
    return data


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--cycles', type=int, default=12)
    parser.add_argument('--online', action='store_true')
    parser.add_argument('--direct-https', action='store_true', help='Use outside HTTPS directly when no host proxy is running')
    parser.add_argument('--https-host', default='www.baidu.com', help='HTTPS witness hostname (certificate validation stays enabled)')
    parser.add_argument('--https-path', default='/')
    parser.add_argument('--selftest', action='store_true')
    parser.add_argument('--observe-seconds', type=float, default=1,
                        help='Outside-only observation duration when --selftest is used')
    args = parser.parse_args()
    assert args.cycles > 0
    assert 0 < args.observe_seconds <= 600
    assert not c.windll.shell32.IsUserAnAdmin(), 'Use ordinary user privileges'
    out = ROOT/'build/test-results'/('lifecycle-'+time.strftime('%Y%m%d-%H%M%S'))
    out.mkdir(parents=True)
    stop = threading.Event()
    phase = 'before'
    observations, cycles = [], []
    fatal = None

    tcp = socket.socket()
    tcp.bind(('127.0.0.2', 0)); tcp.listen(); tcp.settimeout(.2)
    address = tcp.getsockname()
    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp.bind(address); udp.settimeout(.2)

    def echo(conn):
        with conn:
            conn.settimeout(.2)
            while not stop.is_set():
                try:
                    data = conn.recv(8192)
                    if not data: return
                    conn.sendall(data)
                except socket.timeout: continue
                except OSError: return

    def tcp_server():
        while not stop.is_set():
            try: conn, _ = tcp.accept()
            except socket.timeout: continue
            threading.Thread(target=echo, args=(conn,), daemon=True).start()

    def udp_server():
        while not stop.is_set():
            try: data, sender = udp.recvfrom(8192)
            except socket.timeout: continue
            udp.sendto(data, sender)

    def monitor(label, operation, interval):
        while not stop.is_set():
            started = time.monotonic()
            entry = {'kind': label, 'phase': phase, 'start': started}
            try:
                operation()
                entry['ok'] = True
            except Exception as ex:
                entry.update(ok=False, error=repr(ex))
            entry['endPhase'] = phase
            entry['elapsedMs'] = round((time.monotonic()-started)*1000, 2)
            observations.append(entry)
            stop.wait(interval)

    for serve in (tcp_server, udp_server):
        threading.Thread(target=serve, daemon=True).start()
    persistent = socket.create_connection(address, timeout=2)
    udp_client = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp_client.settimeout(2)

    def persistent_tcp():
        payload = uuid.uuid4().bytes * 64
        persistent.sendall(payload)
        assert exact(persistent, len(payload)) == payload

    def new_tcp():
        with socket.create_connection(address, timeout=2) as conn:
            payload = uuid.uuid4().bytes
            conn.sendall(payload)
            assert exact(conn, len(payload)) == payload

    def persistent_udp():
        payload = uuid.uuid4().bytes
        udp_client.sendto(payload, address)
        data, sender = udp_client.recvfrom(8192)
        assert data == payload and sender == address

    def new_udp():
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as conn:
            conn.settimeout(2)
            payload = uuid.uuid4().bytes
            conn.sendto(payload, address)
            data, sender = conn.recvfrom(8192)
            assert data == payload and sender == address

    monitors = [('persistent-tcp', persistent_tcp, .005), ('new-tcp', new_tcp, .02),
                ('persistent-udp', persistent_udp, .005), ('new-udp', new_udp, .02)]
    https = None
    if args.online:
        # Choose a route for this witness only; no user setting changes.
        https = http.client.HTTPSConnection(args.https_host if args.direct_https else '127.0.0.1',
                                            443 if args.direct_https else 7890, timeout=12,
                                            context=ssl.create_default_context())
        if not args.direct_https: https.set_tunnel(args.https_host, 443)
        def persistent_https():
            https.request('GET', args.https_path, headers={'Connection': 'keep-alive'})
            response = https.getresponse()
            body = response.read()
            assert response.status == 200 and body, response.status
        def new_https():
            route = ['--noproxy', '*'] if args.direct_https else ['--proxy', 'http://127.0.0.1:7890']
            p = subprocess.run(['curl.exe', '-sSf', *route,
                                '--max-time', '12', 'https://'+args.https_host+args.https_path],
                               capture_output=True, timeout=15)
            assert p.returncode == 0 and p.stdout, p.stderr.decode(errors='replace')
        route_name = 'direct' if args.direct_https else 'proxy'
        monitors += [(route_name+'-https-keepalive', persistent_https, .25),
                     (route_name+'-https-new', new_https, .25)]
    threads = []
    for label, operation, interval in monitors:
        thread = threading.Thread(target=monitor, args=(label, operation, interval), daemon=True)
        threads.append(thread); thread.start()

    try:
        deadline = time.monotonic()+20
        while not all(any(x['kind'] == kind and x['ok'] for x in observations) for kind, _, _ in monitors):
            if time.monotonic() > deadline: raise AssertionError('Outside baseline not healthy')
            time.sleep(.1)
        if args.selftest:
            time.sleep(args.observe_seconds)
        else:
            for index in range(args.cycles):
                mode = ('stop-event', 'natural-exit', 'owner-exit')[index % 3]
                phase = f'{index+1}:starting:{mode}'
                job = 'Local\\RegionLauncher.'+uuid.uuid4().hex
                cfg = out/f'{index+1}.ini'
                # The target makes no requests; outside probes must never be enrolled.
                target_args = subprocess.list2cmdline(['-c', 'import time; time.sleep('+('1' if mode == 'natural-exit' else '60')+')'])
                cfg.write_text(f'[session]\nJob={job}\nApplication={sys.executable}\nArguments={target_args}\nProxyHost=127.0.0.1\nProxyPort=17891\nProxyLoopback=1\nDnsAddress=127.0.0.153\n', encoding='utf-16')
                process = subprocess.Popen([str(BIN/'RegionSession.exe'), '--run', str(cfg)],
                                           stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                           text=True, encoding='utf8', errors='replace')
                target = None
                try:
                    lines = queue.Queue()
                    threading.Thread(target=lambda: lines.put(process.stdout.readline()), daemon=True).start()
                    first = lines.get(timeout=35)
                    assert first.startswith('STARTED PID='), (first, process.communicate(timeout=10))
                    target = k.OpenProcess(0x100000, False, int(first.split('=')[1]))
                    assert target, c.get_last_error()
                    phase = f'{index+1}:running:{mode}'
                    time.sleep(.35)
                    phase = f'{index+1}:stopping:{mode}'
                    if mode == 'stop-event':
                        event = k.OpenEventW(2, False, job+'.stop')
                        assert event, c.get_last_error()
                        try: assert k.SetEvent(event), c.get_last_error()
                        finally: k.CloseHandle(event)
                    elif mode == 'owner-exit':
                        process.terminate()
                    output, error = process.communicate(timeout=20)
                    assert k.WaitForSingleObject(target, 10000) == 0, 'Target outlived environment'
                    if mode == 'natural-exit': assert process.returncode == 0, error
                    (out/f'{index+1}.log').write_text(first+output+error, encoding='utf8')
                    # Preserve each worker log before the next worker truncates it.
                    logfile = Path(os.environ['ProgramFiles'])/'RegionLauncherNetwork/logs/network.log'
                    deadline = time.monotonic()+12
                    while True:
                        try: log = logfile.read_text(errors='replace')
                        except PermissionError: log = ''  # Worker still owns its write handle.
                        if 'ProxyBridge stopped' in log: break
                        if time.monotonic() > deadline: raise AssertionError('Capture worker did not finish stopping')
                        time.sleep(.1)
                    (out/f'{index+1}-network.log').write_text(log, encoding='utf8')
                    assert 'WinDivertSend failed' not in log and 'shutdown failed' not in log, log
                    cycles.append({'index': index+1, 'mode': mode, 'exitCode': process.returncode})
                    phase = f'{index+1}:after:{mode}'
                    time.sleep(.35)
                    print(f'PASS lifecycle {index+1}/{args.cycles}: {mode}', flush=True)
                finally:
                    if process.poll() is None:
                        process.terminate(); process.wait(timeout=10)
                    if target: k.CloseHandle(target)
        phase = 'after'
        time.sleep(2)
    except Exception as ex:
        fatal = repr(ex)
        (out/'failure.txt').write_text(traceback.format_exc(), encoding='utf8')
    finally:
        stop.set()
        for thread in threads: thread.join(timeout=16)
        persistent.close(); udp_client.close()
        if https: https.close()
        # Servers own their sockets until their timeout observes stop.
        time.sleep(.25); tcp.close(); udp.close()
        summary = {kind: {'count': sum(x['kind'] == kind for x in observations),
                          'failed': sum(x['kind'] == kind and not x['ok'] for x in observations),
                          'maxMs': max((x['elapsedMs'] for x in observations if x['kind'] == kind), default=0)}
                   for kind, _, _ in monitors}
        result = {'cycles': cycles, 'traffic': summary, 'fatal': fatal, 'observations': observations}
        (out/'results.json').write_text(json.dumps(result, indent=2), encoding='utf8')
        print(json.dumps({'output': str(out), 'cycles': len(cycles), 'traffic': summary, 'fatal': fatal}), flush=True)
    assert fatal is None, fatal
    assert not any(x['failed'] for x in summary.values()), 'Outside traffic failed; inspect results.json'
    if not args.selftest: assert len(cycles) == args.cycles
    print('PASS: outside monitor baseline' if args.selftest else
          'PASS: outside connections survive all environment transitions', flush=True)


if __name__ == '__main__': main()
