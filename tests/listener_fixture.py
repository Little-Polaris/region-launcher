"""Shared listener holders and ordinary clients for the native recovery test."""
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import time


def start(role):
    return subprocess.Popen([sys.executable, str(Path(__file__).resolve()), role],
                            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            text=True, creationflags=subprocess.CREATE_NO_WINDOW)


def reserve(kind, address, port=0):
    sock = socket.socket(socket.AF_INET, kind)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
    sock.bind((address, port))
    if kind == socket.SOCK_STREAM:
        sock.listen()
    return sock


role = sys.argv[1]
if role == "--holder":
    sockets = [socket.fromshare(bytes.fromhex(value)) for value in json.loads(sys.stdin.readline())]
    print("READY", flush=True)
    time.sleep(180)
elif role == "--client":
    request = json.loads(sys.stdin.readline())
    sockets = [socket.create_connection(("127.0.0.1", request["port"]))]
    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp.connect(("127.0.0.1", request["port"]))
    sockets.append(udp)
    print("READY", flush=True)
    time.sleep(180)
elif role == "--creator":
    sockets = [reserve(socket.SOCK_STREAM, "127.0.0.1")]
    port = sockets[0].getsockname()[1]
    sockets.append(reserve(socket.SOCK_DGRAM, "127.0.0.1", port))
    sockets.append(reserve(socket.SOCK_STREAM, "127.0.0.154"))
    dns_port = sockets[2].getsockname()[1]
    sockets.append(reserve(socket.SOCK_DGRAM, "127.0.0.154", dns_port))
    holders = [start("--holder"), start("--holder")]
    client = start("--client")
    for holder in holders:
        holder.stdin.write(json.dumps([s.share(holder.pid).hex() for s in sockets]) + "\n")
        holder.stdin.flush()
        assert holder.stdout.readline().strip() == "READY"
    client.stdin.write(json.dumps({"port": port}) + "\n")
    client.stdin.flush()
    assert client.stdout.readline().strip() == "READY"
    print(json.dumps({"pid": os.getpid(), "port": port, "dnsPort": dns_port,
                      "holders": [p.pid for p in holders], "client": client.pid}), flush=True)
    time.sleep(180)
else:
    raise ValueError(role)
