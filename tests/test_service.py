"""Installed-service boundaries and ordinary-user lifecycle, without external traffic."""
import ctypes as c
from ctypes import wintypes as w
import json
import os
from pathlib import Path
import struct
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
BIN = Path(os.environ.get('REGION_TEST_BIN', ROOT/'build/service-bin'))
OUT = ROOT/'build/test-results/service'
OUT.mkdir(parents=True, exist_ok=True)
k = c.WinDLL('kernel32', use_last_error=True)
k.CreateFileW.argtypes = [w.LPCWSTR,w.DWORD,w.DWORD,c.c_void_p,w.DWORD,w.DWORD,w.HANDLE]
k.CreateFileW.restype = w.HANDLE
k.ReadFile.argtypes = [w.HANDLE,c.c_void_p,w.DWORD,c.POINTER(w.DWORD),c.c_void_p]
k.WriteFile.argtypes = k.ReadFile.argtypes
k.CloseHandle.argtypes = [w.HANDLE]
k.WaitNamedPipeW.argtypes = [w.LPCWSTR,w.DWORD]
k.OpenProcess.argtypes = [w.DWORD,w.BOOL,w.DWORD]
k.OpenProcess.restype = w.HANDLE
k.WaitForSingleObject.argtypes = [w.HANDLE,w.DWORD]
pipe = r'\\.\pipe\RegionLauncher.Network.v1'
assert not c.windll.shell32.IsUserAnAdmin(), 'Run this acceptance test at ordinary user privileges'

def request(version=1, port=17891, job=0, ready=0, stop=0, loopback=0, dns=0):
    assert k.WaitNamedPipeW(pipe, 5000), c.get_last_error()
    handle = k.CreateFileW(pipe, 0xc0000000, 0, None, 3, 0x100000, None)
    assert handle != c.c_void_p(-1).value, c.get_last_error()
    try:
        data = struct.pack('<IIQQQII',version,port,job,ready,stop,loopback,dns)
        count = w.DWORD()
        assert k.WriteFile(handle,data,len(data),c.byref(count),None)
        reply = c.create_string_buffer(16)
        assert k.ReadFile(handle,reply,16,c.byref(count),None) and count.value == 16, c.get_last_error()
        assert k.WriteFile(handle,b'\x01',1,c.byref(count),None)
        return struct.unpack('<IIQ',reply.raw)
    finally: k.CloseHandle(handle)

for change in ({'version':99},{'port':0},{'port':65536},{'loopback':2},{'dns':0x01010101}):
    assert request(**change)[0] == 87, change
assert request()[0] == 5, 'Invalid process-group handles must be rejected'
installed = Path(os.environ['ProgramFiles'])/'RegionLauncherNetwork'
for name in ('RegionNetworkService.exe','ProxyBridgeCore.dll','WinDivert.dll','WinDivert64.sys'):
    handle = k.CreateFileW(str(installed/name),0x40000000,7,None,3,0,None)
    if handle != c.c_void_p(-1).value:
        k.CloseHandle(handle)
        raise AssertionError(f'Ordinary user could open protected {name} for writing')
    assert c.get_last_error() == 5, (name,c.get_last_error())
print('PASS: ordinary user; invalid protocol/options/handles rejected; installed code not writable',flush=True)

def launch(label):
    cfg = OUT/(label+'.ini')
    cfg.write_text(f'''[session]
Job=Local\\RegionLauncher.{uuid.uuid4().hex}
Application={BIN/'RegionProbe.exe'}
Arguments=--hold
ProxyHost=127.0.0.1
ProxyPort=17891
DnsAddress=127.0.0.153
''',encoding='utf-16')
    return subprocess.Popen([str(BIN/'RegionSession.exe'),'--run',str(cfg)],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)

p = launch('owner-exit')
try:
    line = p.stdout.readline()
    assert line.startswith('STARTED PID='), (line,p.communicate(timeout=10))
    pid = int(line.split('=')[1])
    target = k.OpenProcess(0x100000,False,pid)
    assert target
    try:
        assert request()[0] == 170, 'Second network environment must be rejected while one is active'
        p.terminate(); p.wait(timeout=10)
        assert k.WaitForSingleObject(target,10000) == 0, 'Owner exit must terminate the selected application'
    finally: k.CloseHandle(target)
finally:
    if p.poll() is None: p.terminate(); p.wait(timeout=10)
for _ in range(50):
    response = request()[0]
    if response == 5: break
    assert response == 170, response
    time.sleep(.1)
else: raise AssertionError('Service did not release the previous environment')
(OUT/'results.json').write_text(json.dumps({'ordinaryUser':True,'invalidRequestsRejected':True,'installedCodeProtected':True,'concurrentSessionRejected':True,'ownerExitKillsTarget':True,'reusable':True},indent=2),encoding='utf8')
print('PASS: concurrent session rejected; owner exit closes target; service becomes reusable',flush=True)
