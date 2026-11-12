"""NIC-bound and IPv4-mapped LAN sockets retain normal peer identity."""
import socket
import sys

host, tcp_port, udp_port = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
for mapped in (False, True):
    address = ('::ffff:' if mapped else '') + host
    for kind, port in [(socket.SOCK_STREAM, tcp_port), (socket.SOCK_DGRAM, udp_port)]:
        with socket.socket(socket.AF_INET6 if mapped else socket.AF_INET, kind) as s:
            if mapped:
                s.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 0)
            s.settimeout(5)
            s.bind((address, 0))
            assert s.getsockname()[0] == address
            s.connect((address, port))
            assert s.getpeername()[:2] == (address, port), s.getpeername()
            s.sendall(b'probe')
            if kind == socket.SOCK_STREAM:
                s.shutdown(socket.SHUT_WR)
            assert s.recv(64) == b'lan-ok'
print('LAN_SOCKET_OK')
