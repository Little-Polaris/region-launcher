import socket
import time
import sys

if len(sys.argv) == 3:
    with socket.socket(type=socket.SOCK_DGRAM) as s:
        s.settimeout(1)
        try:
            s.sendto(b'must-not-escape', (sys.argv[1], int(sys.argv[2])))
            s.recvfrom(256)
        except OSError: pass
        else: raise AssertionError('Unavailable proxy delivered a response')
    print('FAIL_CLOSED')
    sys.exit(0)

for family, bound in ((socket.AF_INET, False), (socket.AF_INET6, False), (socket.AF_INET, True), (socket.AF_INET6, True)):
    with socket.socket(family, socket.SOCK_DGRAM) as s:
        if family == socket.AF_INET6: s.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 0)
        if bound:
            local = ('::ffff:' if family == socket.AF_INET6 else '') + socket.gethostbyname(socket.gethostname())
            s.bind((local, 0))
            assert s.getsockname()[0] == local and s.getsockname()[1] != 0
        s.settimeout(2)
        for ip in ('203.0.113.42', '203.0.113.43'):
            address = (('::ffff:' if family == socket.AF_INET6 else '')+ip, 443)
            s.sendto(b'probe', address)
            data, peer = s.recvfrom(256)
            assert data == b'probe' and peer[:2] == address, (data, peer, address)
        s.sendto(b'restart', address); assert s.recvfrom(256)[0] == b'restart'
        time.sleep(1.5)
        s.sendto(b'recovered', address)
        data, peer = s.recvfrom(256)
        assert data == b'recovered' and peer[:2] == address
with socket.socket(socket.AF_INET6, socket.SOCK_DGRAM) as s:
    try: s.sendto(b'blocked', ('2001:db8::1', 443))
    except OSError: pass
    else: raise AssertionError('IPv6 was allowed')
print('DATAGRAM_SEMANTICS_OK')
