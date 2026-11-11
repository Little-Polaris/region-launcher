"""Different region settings in one live network Job, including child inheritance."""
import json
import os
from pathlib import Path
import socket
import struct
import subprocess
import sys
import threading
import uuid

ROOT = Path(__file__).resolve().parents[1]
BIN = ROOT/'build/service-bin'
OUT = ROOT/'build/test-results/app-environments'
OUT.mkdir(parents=True, exist_ok=True)

def exact(s, size):
    data = b''
    while len(data) < size:
        part = s.recv(size-len(data))
        if not part: raise EOFError()
        data += part
    return data

stop = threading.Event()
proxy = socket.socket(); proxy.bind(('127.0.0.1', 0)); proxy.listen(); proxy.settimeout(.2)
def serve(client):
    with client, socket.socket(type=socket.SOCK_DGRAM) as relay:
        try:
            client.settimeout(1)
            assert exact(client, 3) == b'\x05\x01\x00'; client.sendall(b'\x05\x00')
            request = exact(client, 10); assert request[:4] == b'\x05\x03\x00\x01'
            relay.bind(('127.0.0.1', 0))
            client.sendall(b'\x05\x00\x00\x01'+socket.inet_aton('127.0.0.1')+struct.pack('!H', relay.getsockname()[1]))
            while not stop.is_set():
                try:
                    if not client.recv(1): break
                except TimeoutError: continue
        except (OSError, EOFError): pass
def accept():
    while not stop.is_set():
        try: client, _ = proxy.accept()
        except TimeoutError: continue
        threading.Thread(target=serve, args=(client,), daemon=True).start()
worker = threading.Thread(target=accept); worker.start()
job = 'Local\\RegionLauncher.'+uuid.uuid4().hex
signal = OUT/'finish-root'; signal.unlink(missing_ok=True)
script = OUT/'hold-root.py'
script.write_text('''import pathlib, subprocess, sys, time
def probe():
    p = subprocess.run([sys.argv[1]], capture_output=True)
    print(p.stdout.decode('utf8', errors='replace'), flush=True)
    assert p.returncode == 0
probe()
print('ROOT_READY', flush=True)
deadline = time.monotonic() + 60
while not pathlib.Path(sys.argv[2]).exists():
    assert time.monotonic() < deadline, 'join test did not finish'
    time.sleep(.05)
probe()
''', encoding='utf8')
def config(name, app, args, locale, region, geo, zone, dst):
    path = OUT/(name+'.ini')
    path.write_text(f'[session]\nJob={job}\nOwnerPid={os.getpid()}\nApplication={app}\nArguments={subprocess.list2cmdline(args)}\nProxyHost=127.0.0.1\nProxyPort={proxy.getsockname()[1]}\nLocale={locale}\nRegion={region}\nGeo={geo}\nTimeZone={zone}\nAutoDst={dst}\n', encoding='utf-16')
    return path
root = None
try:
    before = subprocess.check_output([str(BIN/'RegionProbe.exe'), '--child']).decode('utf8', errors='replace')
    ini = config('us-root', sys.executable, ['-u', str(script), str(BIN/'RegionProbe.exe'), str(signal)], 'en-US', 'US', 244, 'Pacific Standard Time', 1)
    root = subprocess.Popen([str(BIN/'RegionSession.exe'), '--run', str(ini)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    lines = []; ready = threading.Event()
    def read():
        for line in root.stdout:
            text = line.decode('utf8', errors='replace'); lines.append(text)
            if 'ROOT_READY' in text: ready.set()
    reader = threading.Thread(target=read); reader.start()
    assert ready.wait(20), ''.join(lines)
    for name, locale, region, geo, zone, dst, hours in [
        ('jp', 'ja-JP', 'JP', 122, 'Tokyo Standard Time', 1, 'winterHour=21 summerHour=21'),
        ('de', 'de-DE', 'DE', 94, 'W. Europe Standard Time', 0, 'winterHour=13 summerHour=13')]:
        ini = config(name, BIN/'RegionProbe.exe', [], locale, region, geo, zone, dst)
        p = subprocess.run([str(BIN/'RegionSession.exe'), '--join', str(ini)], capture_output=True, timeout=25)
        text = (p.stdout+p.stderr).decode('utf8', errors='replace'); (OUT/(name+'.log')).write_text(text, encoding='utf8')
        assert p.returncode == 0, text
        assert text.count(f'locale={locale} region={region} geo={geo} timezone={zone}') == 2, text
        assert text.count(hours) == 2, text
        assert root.poll() is None, 'Joining ended the original program'
    signal.touch(); root.wait(timeout=20); reader.join(5)
    text = ''.join(lines); (OUT/'us-root.log').write_text(text, encoding='utf8')
    assert root.returncode == 0 and text.count('locale=en-US region=US geo=244 timezone=Pacific Standard Time') == 4, text
    assert text.count('winterHour=4 summerHour=5') == 4, text
    after = subprocess.check_output([str(BIN/'RegionProbe.exe'), '--child']).decode('utf8', errors='replace')
    signature = lambda text: text.split(' locale=', 1)[1].split(' season=', 1)[0]
    assert signature(before) == signature(after), (before, after)
    (OUT/'native-results.json').write_text(json.dumps({'sameJob':True,'usBeforeAfter':True,'japanChildren':True,'germanyFixedDstChildren':True,'hostUnchanged':True}, indent=2))
    print('PASS: US root with independent Japan/Germany joins in one Job, children inherit, root unchanged after joins, fixed/automatic DST, host unchanged')
finally:
    if root and root.poll() is None:
        signal.touch()
        try: root.wait(timeout=10)
        except subprocess.TimeoutExpired: root.terminate(); root.wait(5)
    stop.set(); worker.join(2); proxy.close(); signal.unlink(missing_ok=True)
