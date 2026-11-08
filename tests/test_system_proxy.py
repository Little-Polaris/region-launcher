"""Public Windows proxy defaults must not escape the selected process group."""
import ctypes as c
from ctypes import wintypes as w
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[1]
BIN = Path(os.environ.get('REGION_TEST_BIN', ROOT/'build/service-bin'))
OUT = ROOT/'build/test-results/system-proxy'


def fetch_windows(url, api):
    """Use each Windows HTTP stack's default settings, without specifying a proxy."""
    lib = c.WinDLL('winhttp' if api == 'winhttp' else 'wininet', use_last_error=True)
    def function(name, args, result=w.BOOL):
        fn = getattr(lib, name); fn.argtypes = args; fn.restype = result; return fn
    if api == 'winhttp':
        opening = function('WinHttpOpen', [w.LPCWSTR,w.DWORD,w.LPCWSTR,w.LPCWSTR,w.DWORD], w.HANDLE)
        close = function('WinHttpCloseHandle', [w.HANDLE])
        session = opening('RegionProbe', 4, None, None, 0)
    else:
        opening = function('InternetOpenW', [w.LPCWSTR,w.DWORD,w.LPCWSTR,w.LPCWSTR,w.DWORD], w.HANDLE)
        close = function('InternetCloseHandle', [w.HANDLE])
        session = opening('RegionProbe', 0, None, None, 0)
    assert session, c.get_last_error()
    handles = [session]
    try:
        if api == 'winhttp':
            assert function('WinHttpSetTimeouts', [w.HANDLE,c.c_int,c.c_int,c.c_int,c.c_int])(session,10000,15000,15000,20000)
            target = urlsplit(url)
            connection = function('WinHttpConnect', [w.HANDLE,w.LPCWSTR,w.WORD,w.DWORD], w.HANDLE)(session,target.hostname,target.port or 443,0)
            assert connection, c.get_last_error(); handles.append(connection)
            request = function('WinHttpOpenRequest', [w.HANDLE,w.LPCWSTR,w.LPCWSTR,w.LPCWSTR,w.LPCWSTR,c.c_void_p,w.DWORD], w.HANDLE)(connection,'GET',target.path or '/',None,None,None,0x800000)
            assert request, c.get_last_error(); handles.append(request)
            assert function('WinHttpSendRequest', [w.HANDLE,w.LPCWSTR,w.DWORD,c.c_void_p,w.DWORD,w.DWORD,c.c_size_t])(request,None,0,None,0,0,0), c.get_last_error()
            assert function('WinHttpReceiveResponse', [w.HANDLE,c.c_void_p])(request,None), c.get_last_error()
            query_name, read_name = 'WinHttpQueryHeaders', 'WinHttpReadData'
        else:
            setting = function('InternetSetOptionW', [w.HANDLE,w.DWORD,c.c_void_p,w.DWORD])
            timeout = w.DWORD(20000)
            for option in (2,5,6): assert setting(session,option,c.byref(timeout),c.sizeof(timeout))
            request = function('InternetOpenUrlW', [w.HANDLE,w.LPCWSTR,w.LPCWSTR,w.DWORD,w.DWORD,c.c_size_t], w.HANDLE)(session,url,None,0,0x80000000|0x04000000,0)
            assert request, c.get_last_error(); handles.append(request)
            query_name, read_name = 'HttpQueryInfoW', 'InternetReadFile'
        status = w.DWORD(); size = w.DWORD(c.sizeof(status))
        if api == 'winhttp':
            query = function(query_name, [w.HANDLE,w.DWORD,w.LPCWSTR,c.c_void_p,c.POINTER(w.DWORD),c.c_void_p])
            ok = query(request,19|0x20000000,None,c.byref(status),c.byref(size),None)
        else:
            query = function(query_name, [w.HANDLE,w.DWORD,c.c_void_p,c.POINTER(w.DWORD),c.c_void_p])
            ok = query(request,19|0x20000000,c.byref(status),c.byref(size),None)
        assert ok and status.value == 200, (api,status.value,c.get_last_error())
        read = function(read_name, [w.HANDLE,c.c_void_p,w.DWORD,c.POINTER(w.DWORD)])
        chunks = []
        while True:
            buffer = c.create_string_buffer(8192); count = w.DWORD()
            assert read(request,buffer,len(buffer),c.byref(count)), c.get_last_error()
            if not count.value: break
            chunks.append(buffer.raw[:count.value])
        return b''.join(chunks).decode('utf8')
    finally:
        for handle in reversed(handles): close(handle)


def observe():
    http = c.WinDLL('winhttp', use_last_error=True)
    inet = c.WinDLL('wininet', use_last_error=True)
    kernel = c.WinDLL('kernel32', use_last_error=True)
    kernel.GlobalFree.argtypes = [c.c_void_p]
    class IE(c.Structure):
        _fields_ = [('auto', w.BOOL), ('pac', c.c_void_p), ('proxy', c.c_void_p), ('bypass', c.c_void_p)]
    class Proxy(c.Structure):
        _fields_ = [('access', w.DWORD), ('proxy', c.c_void_p), ('bypass', c.c_void_p)]
    cfg = IE()
    http.WinHttpGetIEProxyConfigForCurrentUser.argtypes = [c.POINTER(IE)]
    assert http.WinHttpGetIEProxyConfigForCurrentUser(c.byref(cfg)), c.get_last_error()
    result = {'auto': bool(cfg.auto)}
    for name in ('pac', 'proxy', 'bypass'):
        pointer = getattr(cfg, name)
        result[name] = c.wstring_at(pointer) if pointer else None
        if pointer: kernel.GlobalFree(pointer)
    default = Proxy()
    assert http.WinHttpGetDefaultProxyConfiguration(c.byref(default))
    result['default'] = default.access
    for pointer in (default.proxy, default.bypass):
        if pointer: kernel.GlobalFree(pointer)
    result['sessions'] = {}
    for label, lib, opening, query, close, access, wide in (
        ('winhttp-default', http, 'WinHttpOpen', 'WinHttpQueryOption', 'WinHttpCloseHandle', 0, True),
        ('winhttp-auto', http, 'WinHttpOpen', 'WinHttpQueryOption', 'WinHttpCloseHandle', 4, True),
        ('wininet-w', inet, 'InternetOpenW', 'InternetQueryOptionW', 'InternetCloseHandle', 0, True),
        ('wininet-a', inet, 'InternetOpenA', 'InternetQueryOptionA', 'InternetCloseHandle', 4, False),
    ):
        fn = getattr(lib, opening)
        string_type = w.LPCWSTR if wide else c.c_char_p
        fn.argtypes = [string_type, w.DWORD, string_type, string_type, w.DWORD]
        fn.restype = w.HANDLE
        handle = fn('RegionProbe' if wide else b'RegionProbe', access, None, None, 0)
        assert handle, (label, c.get_last_error())
        closing = getattr(lib, close); closing.argtypes = [w.HANDLE]
        try:
            querying = getattr(lib, query)
            querying.argtypes = [w.HANDLE, w.DWORD, c.c_void_p, c.POINTER(w.DWORD)]
            data = c.create_string_buffer(8192); size = w.DWORD(len(data))
            assert querying(handle, 38, data, c.byref(size)), (label, c.get_last_error())
            info = Proxy.from_buffer(data)
            result['sessions'][label] = info.access
            if lib is http:
                for pointer in (info.proxy, info.bypass):
                    if pointer: kernel.GlobalFree(pointer)
        finally: closing(handle)
    result['environment'] = {k: v for k, v in os.environ.items() if k.upper() in ('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY')}
    return result


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    for key in ('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY'): env[key] = 'inherited-test-value'
    command = [sys.executable, '-X', 'utf8', str(Path(__file__).resolve()), '--client']
    def outside():
        p = subprocess.run(command, capture_output=True, text=True, check=True, timeout=30, env=env)
        return json.loads(p.stdout)
    before = outside()
    cfg = OUT/'session.ini'
    cfg.write_text(f'[session]\nJob=Local\\RegionLauncher.{uuid.uuid4().hex}\nApplication={sys.executable}\nArguments={subprocess.list2cmdline(command[1:])}\nProxyHost=127.0.0.1\nProxyPort=17890\nDnsAddress=127.0.0.153\n', encoding='utf-16')
    p = subprocess.run([str(BIN/'RegionSession.exe'), '--run', str(cfg)], capture_output=True, text=True, timeout=40, env=env)
    (OUT/'inside.log').write_text(p.stdout+p.stderr, encoding='utf8')
    assert p.returncode == 0 and p.stdout.startswith('STARTED PID='), p
    inside = json.loads(p.stdout.split('\n', 1)[1])
    after = outside()
    evidence = {'before': before, 'inside': inside, 'after': after}
    (OUT/'results.json').write_text(json.dumps(evidence, indent=2), encoding='utf8')
    assert before == after, 'Outside defaults changed'
    for process in inside:
        assert not process['auto'] and not process['proxy'] and not process['pac'], process
        assert process['default'] == 1 and all(v == 1 for v in process['sessions'].values()), process
        assert not process['environment'], process
    print('PASS: parent/child WinHTTP and WinINet defaults use packet routing; inherited proxy variables removed; outside defaults unchanged')


if __name__ == '__main__':
    if '--leaf' in sys.argv: print(json.dumps(observe()))
    elif '--client' in sys.argv:
        child = subprocess.run([sys.executable, '-X', 'utf8', __file__, '--leaf'], capture_output=True, text=True, check=True, timeout=30)
        print(json.dumps([observe(), json.loads(child.stdout)]))
    else: main()
