"""Compare real registry reads inside independent launch groups and outside them."""
import concurrent.futures
import ctypes as c
from ctypes import wintypes as w
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import time
import uuid
import winreg as reg

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'build/test-results/registry-region'
BIN = ROOT / 'build/service-bin'
KEYS = [(reg.HKEY_CURRENT_USER, r'Control Panel\International'),
        (reg.HKEY_CURRENT_USER, r'Control Panel\International\Geo'),
        (reg.HKEY_CURRENT_USER, r'Control Panel\International\User Profile'),
        (reg.HKEY_LOCAL_MACHINE, r'SYSTEM\CurrentControlSet\Control\TimeZoneInformation'),
        (reg.HKEY_LOCAL_MACHINE, r'SYSTEM\CurrentControlSet\Control\Nls\Language')]


def snapshot():
    result = {}
    for root, path in KEYS:
        with reg.OpenKey(root, path) as key:
            values = {}
            for index in range(reg.QueryInfoKey(key)[1]):
                name, value, kind = reg.EnumValue(key, index)
                values[name] = [value.hex() if isinstance(value, bytes) else value, kind]
            result[path] = values
    return result


def child(locale, region, geo, zone, automatic):
    intl = r'Control Panel\International'
    tzpath = r'SYSTEM\CurrentControlSet\Control\TimeZoneInformation'
    state = snapshot()
    assert state[intl]['LocaleName'][0] == locale, state[intl]
    assert state[intl + r'\Geo']['Nation'][0] == geo
    assert state[tzpath]['TimeZoneKeyName'][0] == zone
    assert state[tzpath]['DynamicDaylightTimeDisabled'][0] == 1 - int(automatic)
    api = c.WinDLL('advapi32', use_last_error=True)
    get = api.RegGetValueW
    get.argtypes = [w.HKEY, w.LPCWSTR, w.LPCWSTR, w.DWORD, c.POINTER(w.DWORD), c.c_void_p, c.POINTER(w.DWORD)]
    get.restype = w.LONG
    with reg.OpenKey(reg.HKEY_CURRENT_USER, intl) as key:
        expected = (locale + '\0').encode('utf-16-le')
        assert reg.QueryValueEx(key, 'localename') == (locale, reg.REG_SZ)
        size, kind = w.DWORD(), w.DWORD()
        assert get(int(key), None, 'LocaleName', 2, c.byref(kind), None, c.byref(size)) == 0
        # RegGetValue may reserve one extra terminator in its size-only query.
        assert size.value >= len(expected) and kind.value == reg.REG_SZ
        data = c.create_string_buffer(size.value)
        assert get(int(key), None, 'LocaleName', 2, c.byref(kind), data, c.byref(size)) == 0
        assert data.raw[:size.value] == expected
        size = w.DWORD(2)
        assert get(int(key), None, 'LocaleName', 2, None, data, c.byref(size)) == 234
        size = w.DWORD(len(data)); c.memset(data, 0x55, size.value)
        assert get(int(key), None, 'LocaleName', 0x20000008, None, data, c.byref(size)) == 1630
        assert data.raw == bytes(len(data)), 'RRF_ZEROONFAILURE failed'
        ansi = api.RegQueryValueExA
        ansi.argtypes = [w.HKEY, c.c_char_p, c.c_void_p, c.POINTER(w.DWORD), c.c_void_p, c.POINTER(w.DWORD)]
        ansi.restype = w.LONG
        data = c.create_string_buffer(128); size = w.DWORD(128)
        assert ansi(int(key), b'LocaleName', None, c.byref(kind), data, c.byref(size)) == 0
        assert data.value.decode('ascii') == locale and size.value == len(locale) + 1
        get_a = api.RegGetValueA
        get_a.argtypes = [w.HKEY, c.c_char_p, c.c_char_p, w.DWORD, c.POINTER(w.DWORD), c.c_void_p, c.POINTER(w.DWORD)]
        get_a.restype = w.LONG
        size = w.DWORD(128)
        assert get_a(int(key), None, b'LocaleName', 2, None, data, c.byref(size)) == 0
        assert data.value.decode('ascii') == locale
        enum_a = api.RegEnumValueA
        enum_a.argtypes = [w.HKEY, w.DWORD, c.c_char_p, c.POINTER(w.DWORD), c.c_void_p, c.POINTER(w.DWORD), c.c_void_p, c.POINTER(w.DWORD)]
        enum_a.restype = w.LONG
        found = False
        for index in range(reg.QueryInfoKey(key)[1]):
            label = c.create_string_buffer(256); label_size = w.DWORD(256)
            size = w.DWORD(128)
            status = enum_a(int(key), index, label, c.byref(label_size), None, c.byref(kind), data, c.byref(size))
            if status == 0 and label.value == b'LocaleName':
                assert data.value.decode('ascii') == locale
                found = True
        assert found, 'ANSI enumeration did not expose LocaleName'
        # Direct ntdll reads, including full/partial and 64-bit aligned records.
        class String(c.Structure):
            _fields_ = [('Length', w.USHORT), ('MaximumLength', w.USHORT), ('Buffer', c.c_void_p)]
        text = c.create_unicode_buffer('LocaleName')
        name = String(20, 22, c.cast(text, c.c_void_p))
        native = c.WinDLL('ntdll').NtQueryValueKey
        native.argtypes = [w.HANDLE, c.POINTER(String), c.c_int, c.c_void_p, w.ULONG, c.POINTER(w.ULONG)]
        native.restype = w.LONG
        for cls, header in [(0, 12), (1, 20), (2, 12), (3, 20), (4, 8)]:
            needed = w.ULONG()
            status = native(int(key), c.byref(name), cls, None, 0, c.byref(needed))
            assert status & 0xffffffff == 0xc0000023, (cls, hex(status & 0xffffffff))
            buffer = c.create_string_buffer(needed.value)
            assert native(int(key), c.byref(name), cls, buffer, len(buffer), c.byref(needed)) == 0
            raw = buffer.raw
            assert struct.unpack_from('<I', raw, 0 if cls == 4 else 4)[0] == reg.REG_SZ
            if cls != 0:
                offset = struct.unpack_from('<I', raw, 8)[0] if cls in (1, 3) else header
                assert raw[offset:offset + len(expected)] == expected, (cls, raw)
            status = native(int(key), c.byref(name), cls, buffer, header - 1, c.byref(needed))
            assert status & 0xffffffff == 0xc0000023
            status = native(int(key), c.byref(name), cls, buffer, header, c.byref(needed))
            assert status & 0xffffffff == 0x80000005
        needed = w.ULONG()
        assert native(0, c.byref(name), 2, None, 0, c.byref(needed)) != 0
    # Explicit non-default locale requests and unrelated registry reads retain their meaning.
    kernel = c.WinDLL('kernel32')
    kernel.GetLocaleInfoEx.argtypes = [w.LPCWSTR, w.DWORD, w.LPWSTR, c.c_int]
    fmt = c.create_unicode_buffer(128)
    assert kernel.GetLocaleInfoEx(locale, 0x8000001f, fmt, len(fmt))
    assert state[intl]['sShortDate'][0] == fmt.value
    with reg.OpenKey(reg.HKEY_LOCAL_MACHINE, r'SOFTWARE\Microsoft\Windows NT\CurrentVersion') as key:
        outside = json.loads((OUT / 'unrelated.json').read_text(encoding='utf8'))
        assert reg.QueryValueEx(key, 'ProductName')[0] == outside['ProductName']
    with reg.OpenKey(reg.HKEY_CURRENT_USER, intl + r'\Geo') as key:
        assert reg.QueryValueEx(key, 'Name')[0] == region
    # Alternate opens/duplicated handles resolve to the same key, not a stale handle map.
    with reg.OpenKey(reg.HKEY_CURRENT_USER, intl) as key:
        duplicate = w.HANDLE()
        kernel.DuplicateHandle.argtypes = [w.HANDLE, w.HANDLE, w.HANDLE, c.POINTER(w.HANDLE), w.DWORD, w.BOOL, w.DWORD]
        assert kernel.DuplicateHandle(w.HANDLE(-1), int(key), w.HANDLE(-1), c.byref(duplicate), 0, False, 2)
        try:
            assert reg.QueryValueEx(duplicate.value, 'LocaleName')[0] == locale
        finally: reg.CloseKey(duplicate.value)
    if not int(automatic):
        assert state[tzpath]['DaylightBias'][0] == 0
        assert state[tzpath]['DaylightStart'][0] == '00' * 16
    if '--leaf' not in sys.argv:
        result = subprocess.run([sys.executable, __file__, '--child', locale, region, geo, zone, automatic, '--leaf'], capture_output=True, timeout=30)
        assert result.returncode == 0, (result.stdout, result.stderr)
    print('REGISTRY_OK', locale, zone, automatic, flush=True)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    before = snapshot()
    with reg.OpenKey(reg.HKEY_LOCAL_MACHINE, r'SOFTWARE\Microsoft\Windows NT\CurrentVersion') as key:
        (OUT / 'unrelated.json').write_text(json.dumps({'ProductName': reg.QueryValueEx(key, 'ProductName')[0]}), encoding='utf8')
    (OUT / 'outside-before.json').write_text(json.dumps(before, ensure_ascii=False, indent=2), encoding='utf8')
    def run(settings):
        locale, region, geo, zone, automatic = settings
        config = OUT / f'{locale}-{automatic}.ini'
        arguments = subprocess.list2cmdline([str(Path(__file__).resolve()), '--child', *settings])
        config.write_text(f'[session]\nJob=Local\\RegionLauncher.{uuid.uuid4().hex}\nApplication={sys.executable}\nArguments={arguments}\nLocale={locale}\nRegion={region}\nGeo={geo}\nTimeZone={zone}\nAutoDst={automatic}\n', encoding='utf-16')
        p = subprocess.run([str(BIN / 'RegionSession.exe'), '--locale-test', str(config)], capture_output=True, timeout=60)
        (OUT / f'{locale}-{automatic}.log').write_bytes(p.stdout + p.stderr)
        assert p.returncode == 0 and b'REGISTRY_OK' in p.stdout, (settings, p.returncode, p.stdout, p.stderr)
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        futures = [pool.submit(run, settings) for settings in [
            ('en-US', 'US', '244', 'Pacific Standard Time', '1'),
            ('en-US', 'US', '244', 'Pacific Standard Time', '0'),
            ('de-DE', 'DE', '94', 'W. Europe Standard Time', '1')]]
        while any(not future.done() for future in futures):
            assert snapshot() == before, 'Outside registry changed while environments were running'
            time.sleep(0.05)
        for future in futures: future.result()
    after = snapshot()
    (OUT / 'outside-after.json').write_text(json.dumps(after, ensure_ascii=False, indent=2), encoding='utf8')
    assert before == after, 'Outside registry values changed'
    print('PASS: concurrent US/Germany registry overlays, parent/child, ANSI/Unicode/native query and enumeration, buffer/type errors, fixed DST, unchanged outside registry')


if __name__ == '__main__':
    if '--child' in sys.argv: child(*sys.argv[2:7])
    else: main()
