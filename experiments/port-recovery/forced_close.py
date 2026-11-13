"""Verify foreign-handle close hazards using only this fixture's child.

Never accepts a target PID, handle or port from the command line.
"""
import ctypes
import json
import socket
import subprocess
import sys
from pathlib import Path


def child():
    original = socket.socket()
    original.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
    original.bind(("127.0.0.1", 0))
    original.listen()
    old_handle = original.fileno()
    print(json.dumps({"handle": old_handle, "port": original.getsockname()[1]}), flush=True)
    if sys.stdin.readline().strip() != "closed":
        return
    retained = []
    try:
        # The application still has its socket object, unaware of the external close.
        reused = None
        for _ in range(256):
            candidate = socket.socket()
            retained.append(candidate)
            if candidate.fileno() == old_handle:
                reused = candidate
                break
        if reused is None:
            original.detach()
            print(json.dumps({"reused": False}), flush=True)
            return
        reused.bind(("127.0.0.1", 0))
        reused.listen()
        new_port = reused.getsockname()[1]
        # Normal cleanup of the old socket now addresses a different live socket.
        original.close()
        try:
            reused.getsockname()
            error = 0
        except OSError as ex:
            error = ex.winerror
        print(json.dumps({"reused": True, "new_port": new_port,
                          "unrelated_socket_error_after_old_cleanup": error}), flush=True)
        if error:
            reused.detach()
    finally:
        for candidate in retained:
            candidate.close()
        if original.fileno() != -1:
            original.detach()


def main():
    if len(sys.argv) == 2 and sys.argv[1] == "--child":
        child()
        return
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.DuplicateHandle.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                                      ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
    kernel.DuplicateHandle.restype = ctypes.c_int
    results = []
    for _ in range(3):
        proc = subprocess.Popen([sys.executable, str(Path(__file__).resolve()), "--child"],
                                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                text=True, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            endpoint = json.loads(proc.stdout.readline())
            # proc._handle is the retained handle of the child we just created.
            if not kernel.DuplicateHandle(int(proc._handle), endpoint["handle"], None, None, 0, False, 1):
                raise ctypes.WinError(ctypes.get_last_error())
            with socket.socket() as probe:
                probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
                probe.bind(("127.0.0.1", endpoint["port"]))
            proc.stdin.write("closed\n")
            proc.stdin.flush()
            observed = json.loads(proc.stdout.readline())
            proc.wait(timeout=5)
            assert proc.returncode == 0, proc.stderr.read()
            assert observed["reused"], "No handle reuse; test inconclusive"
            assert observed["unrelated_socket_error_after_old_cleanup"] == 10038, observed
            results.append({"old_port_released": True, **observed})
        finally:
            if proc.poll() is None:
                proc.kill()
            proc.communicate(timeout=5)
    print(json.dumps({"scenario": "uncooperative external close can break a later unrelated socket",
                      "runs": results}, indent=2))


if __name__ == "__main__":
    main()
