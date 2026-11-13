"""Windows-only fixture: release a dead creator's shared TCP/UDP endpoints.

Only uses ephemeral loopback ports and a child created by this script.
Does not enumerate or close handles in unrelated processes.
"""
import ctypes
import json
import os
from pathlib import Path
import socket
import struct
import subprocess
import sys
import time


def bound(kind, port=0):
    s = socket.socket(socket.AF_INET, kind)
    try:
        s.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        s.bind(("127.0.0.1", port))
        if kind == socket.SOCK_STREAM:
            s.listen(4)
        return s
    except BaseException:
        s.close()
        raise


def creator(target):
    with bound(socket.SOCK_STREAM) as tcp:
        port = tcp.getsockname()[1]
        with bound(socket.SOCK_DGRAM, port) as udp:
            print(json.dumps({"pid": os.getpid(), "port": port,
                              "tcp": tcp.share(target).hex(),
                              "udp": udp.share(target).hex()}), flush=True)
            sys.stdin.readline()


def rows(tcp, port):
    api = ctypes.WinDLL("iphlpapi")
    fn = api.GetExtendedTcpTable if tcp else api.GetExtendedUdpTable
    fn.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong),
                   ctypes.c_int, ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
    fn.restype = ctypes.c_ulong
    size = ctypes.c_ulong()
    rc = fn(None, ctypes.byref(size), False, socket.AF_INET, 5 if tcp else 1, 0)
    assert rc in (0, 122), rc
    for _ in range(5):
        buf = ctypes.create_string_buffer(max(size.value, 4))
        rc = fn(buf, ctypes.byref(size), False, socket.AF_INET, 5 if tcp else 1, 0)
        if rc != 122:
            break
    assert rc == 0, rc
    count = struct.unpack_from("<I", buf)[0]
    result = []
    for i in range(count):
        values = struct.unpack_from("<6I" if tcp else "<3I", buf, 4 + i * (24 if tcp else 12))
        local_port = socket.ntohs(values[2 if tcp else 1] & 0xffff)
        if local_port == port:
            result.append({"pid": values[-1], "state": values[0] if tcp else "Bound"})
    return result


def bind_error(kind, port):
    try:
        with bound(kind, port):
            return 0
    except OSError as e:
        return e.winerror


def control_alive(tcp, udp):
    with socket.create_connection(tcp.getsockname(), timeout=2) as c:
        tcp.settimeout(2)
        server, _ = tcp.accept()
        with server:
            c.sendall(b"t")
            assert server.recv(1) == b"t"
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as c:
        udp.settimeout(2)
        c.sendto(b"u", udp.getsockname())
        assert udp.recvfrom(16)[0] == b"u"


def run_once():
    child = subprocess.Popen([sys.executable, str(Path(__file__).resolve()), "--creator", str(os.getpid())],
                             stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                             text=True, creationflags=subprocess.CREATE_NO_WINDOW)
    sockets = []
    try:
        info = json.loads(child.stdout.readline())
        tcp = socket.fromshare(bytes.fromhex(info["tcp"]))
        sockets.append(tcp)
        udp = socket.fromshare(bytes.fromhex(info["udp"]))
        sockets.append(udp)
        port = info["port"]
        child.kill()
        child.wait(timeout=5)
        # The original process has exited; our deliberately shared references survive.
        before = {"tcp": rows(True, port), "udp": rows(False, port),
                  "tcp_bind_error": bind_error(socket.SOCK_STREAM, port),
                  "udp_bind_error": bind_error(socket.SOCK_DGRAM, port)}
        assert before["tcp_bind_error"] == before["udp_bind_error"] == 10048, before
        assert any(r["pid"] == child.pid and r["state"] == 2 for r in before["tcp"]), before
        assert any(r["pid"] == child.pid for r in before["udp"]), before
        started = time.monotonic()
        tcp.close()
        assert bind_error(socket.SOCK_STREAM, port) == 0
        assert bind_error(socket.SOCK_DGRAM, port) == 10048
        udp.close()
        assert bind_error(socket.SOCK_STREAM, port) == 0
        assert bind_error(socket.SOCK_DGRAM, port) == 0
        after = {"tcp": rows(True, port), "udp": rows(False, port)}
        assert not after["tcp"] and not after["udp"], after
        return {"creator_pid": child.pid, "creator_exit_code": child.returncode,
                "holder_pid": os.getpid(), "port": port, "before": before,
                "after": after, "release_ms": round((time.monotonic() - started) * 1000, 2)}
    finally:
        for s in sockets:
            s.close()
        if child.poll() is None:
            child.kill()
        child.communicate(timeout=5)


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--creator":
        creator(int(sys.argv[2]))
        return
    with bound(socket.SOCK_STREAM) as control_tcp, bound(socket.SOCK_DGRAM) as control_udp:
        results = []
        for _ in range(3):
            control_alive(control_tcp, control_udp)
            results.append(run_once())
            control_alive(control_tcp, control_udp)
        print(json.dumps({"scenario": "shared socket references survive creator exit",
                          "unrelated_control_tcp_udp": "passed before and after each run",
                          "runs": results}, indent=2))


if __name__ == "__main__":
    main()
