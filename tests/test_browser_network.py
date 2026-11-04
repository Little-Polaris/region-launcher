"""Chrome joins a running session; all HTTP/SOCKS fixtures stay on loopback."""
import ctypes
import html
import http.server
import json
import re
import socket
import subprocess
import threading
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BIN = ROOT / 'build/bin'
OUT = ROOT / 'build/test-results/browser-network'
OUT.mkdir(parents=True, exist_ok=True)
CHROME = Path(r'C:\Program Files\Google\Chrome\Application\chrome.exe')
html_page = b'''<!doctype html><meta charset="utf-8"><pre id="result"></pre><script>
document.querySelector('#result').textContent=JSON.stringify({
  timezone:Intl.DateTimeFormat().resolvedOptions().timeZone,
  language:navigator.language,
  winter:new Date('2026-01-15T12:00:00Z').getTimezoneOffset(),
  summer:new Date('2026-07-15T12:00:00Z').getTimezoneOffset()
});</script>'''
outside_seen = threading.Event()


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        outside_seen.set()
        self.send_response(200); self.send_header('Content-Type', 'text/html'); self.send_header('Content-Length', str(len(html_page)))
        self.end_headers(); self.wfile.write(html_page)
    def log_message(self, *args):
        pass


direct = http.server.ThreadingHTTPServer(('127.0.0.2', 0), Handler)
threading.Thread(target=direct.serve_forever, daemon=True).start()
proxy = socket.socket(); proxy.bind(('127.0.0.1', 0)); proxy.listen()
proxy_port = proxy.getsockname()[1]
seen_domains = []


def exact(conn, n):
    result = b''
    while len(result) < n:
        part = conn.recv(n - len(result))
        if not part:
            raise EOFError()
        result += part
    return result


def socks(conn):
    with conn:
        conn.settimeout(12)
        try:
            version, count = exact(conn, 2); exact(conn, count); conn.sendall(b'\x05\x00')
            _, command, _, kind = exact(conn, 4)
            address = exact(conn, 4 if kind == 1 else 16 if kind == 4 else exact(conn, 1)[0]); exact(conn, 2)
            if command != 1 or kind != 3 or address != b'region-test.invalid':
                conn.sendall(b'\x05\x07\x00\x01' + b'\x00' * 6); return
            seen_domains.append(address.decode())
            conn.sendall(b'\x05\x00\x00\x01' + b'\x00' * 6)
            request = b''
            while b'\r\n\r\n' not in request:
                request += exact(conn, 1)
            assert outside_seen.wait(10), 'Outside browser did not run concurrently'
            conn.sendall(b'HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nConnection: close\r\nContent-Length: ' + str(len(html_page)).encode() + b'\r\n\r\n' + html_page)
        except (EOFError, TimeoutError, ConnectionError, OSError):
            pass


def proxy_loop():
    while True:
        conn, _ = proxy.accept(); threading.Thread(target=socks, args=(conn,), daemon=True).start()


threading.Thread(target=proxy_loop, daemon=True).start()
job = f'Local\\RegionLauncher.{uuid.uuid4().hex}'
common = f'''[session]
Job={job}
ProxyHost=127.0.0.1
ProxyPort={proxy_port}
Locale=en-US
Region=US
Geo=244
TimeZone=Pacific Standard Time
AutoDst=1
'''
root_config = OUT / 'owner.ini'
root_config.write_text(common + f'Application={BIN / "RegionProbe.exe"}\nArguments=--hold\n', encoding='utf-16')
browser_args = [f'--user-data-dir={OUT / ("inside-" + uuid.uuid4().hex)}', '--headless=new', '--disable-background-networking', '--no-first-run',
                '--no-default-browser-check', '--lang=en-US', '--dump-dom', f'--proxy-server=socks5://127.0.0.1:{proxy_port}',
                '--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1, EXCLUDE localhost', 'http://region-test.invalid/']
join_config = OUT / 'browser.ini'
join_config.write_text(common + f'Application={CHROME}\nArguments={subprocess.list2cmdline(browser_args)}\n', encoding='utf-16')
owner = subprocess.Popen([str(BIN / 'RegionSession.exe'), '--run', str(root_config)], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', errors='replace')
browser = None
try:
    assert owner.stdout.readline().startswith('STARTED PID=')
    browser = subprocess.Popen([str(BIN / 'RegionSession.exe'), '--join', str(join_config)], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', errors='replace')
    outside = subprocess.run([str(CHROME), f'--user-data-dir={OUT / ("outside-" + uuid.uuid4().hex)}', '--headless=new', '--disable-background-networking',
                              '--no-first-run', '--no-default-browser-check', '--dump-dom', '--proxy-server=direct://',
                              f'http://127.0.0.2:{direct.server_port}/'], capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=15)
    output, err = browser.communicate(timeout=20)
    (OUT / 'inside.txt').write_text(output + '\nERROR:\n' + err, encoding='utf-8')
    (OUT / 'outside.txt').write_text(outside.stdout + '\nERROR:\n' + outside.stderr, encoding='utf-8')
    def result(text):
        found = re.findall(r'<pre id="result">(.*?)</pre>', text, re.S)
        assert found, text
        return json.loads(html.unescape(found[-1]))
    inside = result(output); outside_result = result(outside.stdout)
    assert inside['timezone'] == 'America/Los_Angeles' and inside['language'] == 'en-US' and inside['summer'] == 420, inside
    assert outside_result['timezone'] == 'Asia/Shanghai' and outside_result['summer'] ==  -480, outside_result
    assert seen_domains, 'No SOCKS5 domain request observed'
    print('PASS Chrome joins existing session, uses SOCKS5 remote DNS, sees Los Angeles/en-US:', inside)
    print('PASS simultaneous outside Chrome keeps original timezone and direct local access:', outside_result)
finally:
    kernel = ctypes.windll.kernel32
    kernel.OpenEventW.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_wchar_p]; kernel.OpenEventW.restype = ctypes.c_void_p
    kernel.SetEvent.argtypes = [ctypes.c_void_p]; kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    event = kernel.OpenEventW(2, False, job + '.stop')
    if event:
        kernel.SetEvent(event); kernel.CloseHandle(event)
    try:
        owner.communicate(timeout=15)
    except subprocess.TimeoutExpired:
        owner.terminate(); owner.communicate(timeout=5)
    if browser and browser.poll() is None:
        browser.terminate(); browser.communicate(timeout=5)
    direct.shutdown()
