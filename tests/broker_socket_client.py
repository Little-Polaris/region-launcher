"""Socket semantics tested through the injected native runtime, no proxy API."""
from concurrent.futures import ThreadPoolExecutor
import socket
import sys


def exchange(mapped=False):
    family = socket.AF_INET6 if mapped else socket.AF_INET
    with socket.socket(family, socket.SOCK_STREAM) as stream:
        stream.settimeout(8)
        if mapped:
            stream.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 0)
        address = '::ffff:203.0.113.42' if mapped else '203.0.113.42'
        stream.connect((address, 443))
        assert stream.getpeername()[:2] == (address, 443), stream.getpeername()
        stream.sendall(b'probe')
        stream.shutdown(socket.SHUT_WR)
        response = b''
        while data := stream.recv(1024):
            response += data
        assert response == b'broker-ok', response


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] == '--failure':
        with socket.socket() as stream:
            stream.settimeout(8)
            try:
                stream.connect((sys.argv[2], int(sys.argv[3])))
                stream.sendall(b'probe')
                data = stream.recv(1024)
                assert not data, data
            except OSError:
                pass
        print('FAIL_CLOSED')
    else:
        with ThreadPoolExecutor(max_workers=16) as pool:
            list(pool.map(exchange, [False, True] * 24))
        with socket.socket(socket.AF_INET6, socket.SOCK_STREAM) as stream:
            stream.settimeout(1)
            try:
                stream.connect(('2001:db8::1', 443))
            except OSError as exc:
                assert exc.winerror == 10047, exc
            else:
                raise AssertionError('IPv6 was not blocked')
        print('SOCKET_SEMANTICS_OK')
