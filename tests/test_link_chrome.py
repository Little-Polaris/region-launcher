"""Real Chrome shell-link handoff against a local fixture; only test job is stopped."""
import ctypes as c
from ctypes import wintypes as w
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import subprocess
import threading
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'build/test-results/link-browser/real-chrome'
k = c.WinDLL('kernel32', use_last_error=True)
u = c.WinDLL('user32', use_last_error=True)
u.FindWindowExW.argtypes = [w.HWND, w.HWND, w.LPCWSTR, w.LPCWSTR]; u.FindWindowExW.restype = w.HWND
u.GetWindowThreadProcessId.argtypes = [w.HWND, c.POINTER(w.DWORD)]
k.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]; k.OpenProcess.restype = w.HANDLE
k.OpenJobObjectW.argtypes = [w.DWORD, w.BOOL, w.LPCWSTR]; k.OpenJobObjectW.restype = w.HANDLE
k.IsProcessInJob.argtypes = [w.HANDLE, w.HANDLE, c.POINTER(w.BOOL)]
k.OpenEventW.argtypes = [w.DWORD, w.BOOL, w.LPCWSTR]; k.OpenEventW.restype = w.HANDLE
k.SetEvent.argtypes = [w.HANDLE]; k.CloseHandle.argtypes = [w.HANDLE]


def main():
    chrome = Path('C:/Program Files/Google/Chrome/Application/chrome.exe')
    assert chrome.is_file(), 'Real Chrome unavailable'
    OUT.mkdir(parents=True, exist_ok=True)
    results = []
    for mode in ('saved', 'temporary', 'foreign'):
        job = 'Local\\RegionLauncher.' + uuid.uuid4().hex
        saved = OUT / ('saved-' + uuid.uuid4().hex)
        temporary = OUT / 'link-browser-temp' / job.split('.')[-1] / 'chrome'
        saved.mkdir(); temporary.mkdir(parents=True)
        (saved / 'keep.txt').write_text('preserve')
        owner = None
        if mode == 'foreign':
            owner = subprocess.Popen([str(ROOT / 'build/LinkProbe.exe'), '--owner', str(saved)], stdout=subprocess.PIPE, text=True)
            assert owner.stdout.readline().strip() == 'OWNER_READY'
        chosen = saved if mode == 'saved' else temporary
        observed = []
        complete = threading.Event()
        class Page(BaseHTTPRequestHandler):
            def do_GET(self):
                if self.path in ('/first', '/second'):
                    hwnd = u.FindWindowExW(w.HWND(-3), None, None, str(chosen))
                    pid = w.DWORD(); u.GetWindowThreadProcessId(hwnd, c.byref(pid))
                    proc = k.OpenProcess(0x1000, False, pid.value)
                    group = k.OpenJobObjectW(4, False, job)
                    inside = w.BOOL()
                    ok = proc and group and k.IsProcessInJob(proc, group, c.byref(inside)) and inside.value
                    if proc: k.CloseHandle(proc)
                    if group: k.CloseHandle(group)
                    observed.append({'path': self.path, 'pid': pid.value, 'inside': bool(ok)})
                    if {item['path'] for item in observed} == {'/first', '/second'}: complete.set()
                body = b'<title>Region Launcher link test</title><p>Browser authorization handoff test.</p>'
                self.send_response(200); self.send_header('Content-Type', 'text/html'); self.send_header('Content-Length', str(len(body))); self.end_headers(); self.wfile.write(body)
            def log_message(self, *args): pass
        server = ThreadingHTTPServer(('127.0.0.1', 0), Page)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        port = server.server_port
        args = subprocess.list2cmdline(['--twice', f'http://127.0.0.1:{port}/first', f'"http://127.0.0.1:{port}/second"'])
        config = OUT / 'session.ini'
        config.write_text(f'''[session]
Job={job}
Application={ROOT / 'build/LinkProbe.exe'}
Arguments={args}
Locale=en-US
Region=US
Geo=244
TimeZone=Pacific Standard Time
AutoDst=1
LinkBrowser_ENABLED=1
LinkBrowser_EXE={chrome}
LinkBrowser_PROFILE={saved if mode != 'temporary' else ''}
LinkBrowser_TEMP={temporary}
''', encoding='utf-16')
        log = (OUT / (mode + '.log')).open('wb')
        session = subprocess.Popen([str(ROOT / 'build/service-bin/RegionSession.exe'), '--locale-test', str(config)], stdout=log, stderr=log)
        try:
            assert complete.wait(35), (mode, observed)
            assert all(item['inside'] for item in observed), observed
            assert len({item['pid'] for item in observed}) == 1, observed
            if owner: assert owner.poll() is None
            results.append({'mode': mode, 'requests': observed})
        finally:
            stop = k.OpenEventW(2, False, job + '.stop')
            if stop: k.SetEvent(stop); k.CloseHandle(stop)
            session.wait(timeout=15); log.close()
            server.shutdown(); server.server_close()
            if owner: owner.terminate(); owner.wait(timeout=5)
        assert not temporary.parent.exists(), 'Session did not remove its temporary profiles'
        assert (saved / 'keep.txt').read_text() == 'preserve', 'Saved data was changed'
    (OUT / 'result.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
    print('PASS: actual Chrome receives plain and quoted URLs in the same scoped process; session cleanup preserves saved data')


if __name__ == '__main__': main()
