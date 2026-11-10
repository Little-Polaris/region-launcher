#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <mswsock.h>
#include <windows.h>
#include <unordered_map>
#include <vector>
#include "detours.h"
#include "ConnectBroker.h"
#include "NetworkProtocol.h"

static WCHAR brokerPipe[256];
static bool dedicatedDns;
static decltype(&connect) originalConnect = connect;
static decltype(&WSAConnect) originalWSAConnect = WSAConnect;
static decltype(&WSAIoctl) originalIoctl = WSAIoctl;
static decltype(&getpeername) originalPeer = getpeername;
static decltype(&closesocket) originalClose = closesocket;
static LPFN_CONNECTEX originalConnectEx;
static SRWLOCK peersLock = SRWLOCK_INIT;
struct Peer { sockaddr_storage address; int size; };
// Other DLLs may close sockets after this DLL's CRT teardown. The OS reclaims this
// process-lifetime table; individual socket entries are removed at closesocket.
static auto& peers = *new std::unordered_map<SOCKET, Peer>;
struct DatagramPeer { Peer original; USHORT localPort; DWORD address; USHORT port; };
static auto& datagrams = *new std::unordered_map<SOCKET, std::vector<DatagramPeer>>;
static bool exchange(ConnectRequest& request, ConnectReply& reply) {
    HANDLE pipe = INVALID_HANDLE_VALUE;
    ULONGLONG deadline = GetTickCount64() + 5000;
    do {
        pipe = CreateFileW(brokerPipe, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
        if (pipe != INVALID_HANDLE_VALUE || GetLastError() != ERROR_PIPE_BUSY) break;
        WaitNamedPipeW(brokerPipe, 100);
    } while (GetTickCount64() < deadline);
    if (pipe == INVALID_HANDLE_VALUE) return false;
    bool ok = PipeIO(pipe, &request, sizeof(request), true) && PipeIO(pipe, &reply, sizeof(reply), false);
    CloseHandle(pipe); return ok && !reply.error;
}

// Return the rewritten sockaddr size, 0 for unchanged local traffic, -1 for a closed path.
static int prepare(SOCKET socket, const sockaddr* address, int size, sockaddr_storage& target, bool connected = true) {
    if (!address || size < (int)sizeof(sockaddr_in)) return 0;
    ConnectRequest request;
    int addressSize;
    if (address->sa_family == AF_INET) {
        auto ip = (const sockaddr_in*)address; request.address = ip->sin_addr.s_addr; request.port = ip->sin_port;
        addressSize = sizeof(sockaddr_in);
    } else if (address->sa_family == AF_INET6 && size >= (int)sizeof(sockaddr_in6)) {
        auto ip = (const sockaddr_in6*)address;
        if (!IN6_IS_ADDR_V4MAPPED(&ip->sin6_addr)) { WSASetLastError(WSAEAFNOSUPPORT); return -1; }
        memcpy(&request.address, ip->sin6_addr.u.Byte + 12, 4); request.port = ip->sin6_port;
        addressSize = sizeof(sockaddr_in6);
    } else return 0;
    if ((ntohl(request.address) >> 24) == 127 && !(dedicatedDns && request.port == htons(53))) return 0;
    int type = 0, length = sizeof(type);
    if (getsockopt(socket, SOL_SOCKET, SO_TYPE, (char*)&type, &length)) return -1;
    if (type != SOCK_STREAM && type != SOCK_DGRAM) return 0;
    request.protocol = (USHORT)type;
    ConnectReply reply;
    if (type == SOCK_DGRAM) {
        AcquireSRWLockShared(&peersLock); auto found = datagrams.find(socket);
        if (found != datagrams.end()) for (auto& item : found->second)
            if (item.address == request.address && item.port == request.port) { reply.port = item.localPort; break; }
        ReleaseSRWLockShared(&peersLock);
    }
    bool cached = reply.port != 0;
    if (!cached && (!exchange(request, reply) || !reply.port)) { WSASetLastError(WSAECONNREFUSED); return -1; }
    target = {}; target.ss_family = address->sa_family;
    if (address->sa_family == AF_INET) {
        auto local = (sockaddr_in*)&target; local->sin_addr.s_addr = htonl(INADDR_LOOPBACK); local->sin_port = reply.port;
    } else {
        auto local = (sockaddr_in6*)&target; local->sin6_addr.u.Byte[10] = local->sin6_addr.u.Byte[11] = 255;
        local->sin6_addr.u.Byte[12] = 127; local->sin6_addr.u.Byte[15] = 1; local->sin6_port = reply.port;
    }
    Peer peer = {}; peer.size = addressSize; memcpy(&peer.address, address, addressSize);
    AcquireSRWLockExclusive(&peersLock);
    if (connected) peers[socket] = peer;
    if (type == SOCK_DGRAM && !cached) datagrams[socket].push_back({peer, reply.port, request.address, request.port});
    ReleaseSRWLockExclusive(&peersLock);
    return addressSize;
}
int PrepareDatagram(SOCKET s, const sockaddr* address, int size, sockaddr_storage& target) {
    int type = 0, length = sizeof(type);
    if (getsockopt(s, SOL_SOCKET, SO_TYPE, (char*)&type, &length) || type != SOCK_DGRAM) return 0;
    return prepare(s, address, size, target, false);
}
void RestoreDatagramPeer(SOCKET s, sockaddr* address, int* size) {
    if (!address || !size || *size < (int)sizeof(sockaddr_in)) return;
    USHORT port = 0;
    if (address->sa_family == AF_INET && ((sockaddr_in*)address)->sin_addr.s_addr == htonl(INADDR_LOOPBACK)) port = ((sockaddr_in*)address)->sin_port;
    if (address->sa_family == AF_INET6 && *size >= (int)sizeof(sockaddr_in6)) {
        auto ip = (sockaddr_in6*)address; DWORD mapped = 0; memcpy(&mapped, ip->sin6_addr.u.Byte + 12, 4);
        if (IN6_IS_ADDR_V4MAPPED(&ip->sin6_addr) && mapped == htonl(INADDR_LOOPBACK)) port = ip->sin6_port;
    }
    if (!port) return;
    AcquireSRWLockShared(&peersLock); auto found = datagrams.find(s);
    if (found != datagrams.end()) for (auto& item : found->second) if (item.localPort == port && *size >= item.original.size) {
        memcpy(address, &item.original.address, item.original.size); *size = item.original.size; break;
    }
    ReleaseSRWLockShared(&peersLock);
}
static int WSAAPI hookedConnect(SOCKET s, const sockaddr* address, int size) {
    sockaddr_storage target; int mode = prepare(s, address, size, target);
    if (mode < 0) return SOCKET_ERROR;
    return originalConnect(s, mode ? (sockaddr*)&target : address, mode ? mode : size);
}
static int WSAAPI hookedWSAConnect(SOCKET s, const sockaddr* address, int size, LPWSABUF caller, LPWSABUF callee, LPQOS sqos, LPQOS gqos) {
    sockaddr_storage target; int mode = prepare(s, address, size, target);
    if (mode < 0) return SOCKET_ERROR;
    return originalWSAConnect(s, mode ? (sockaddr*)&target : address, mode ? mode : size, caller, callee, sqos, gqos);
}
static BOOL PASCAL hookedConnectEx(SOCKET s, const sockaddr* address, int size, PVOID data, DWORD length, LPDWORD sent, LPOVERLAPPED ov) {
    sockaddr_storage target; int mode = prepare(s, address, size, target);
    if (mode < 0) return FALSE;
    return originalConnectEx(s, mode ? (sockaddr*)&target : address, mode ? mode : size, data, length, sent, ov);
}
static int WSAAPI hookedIoctl(SOCKET s, DWORD code, LPVOID in, DWORD inSize, LPVOID out, DWORD outSize, LPDWORD bytes,
    LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion) {
    int result = originalIoctl(s, code, in, inSize, out, outSize, bytes, ov, completion);
    static const GUID connectId = WSAID_CONNECTEX;
    if (!result && code == SIO_GET_EXTENSION_FUNCTION_POINTER && in && inSize == sizeof(GUID) &&
        !memcmp(in, &connectId, sizeof(GUID)) && out && outSize >= sizeof(LPFN_CONNECTEX)) {
        auto fn = *(LPFN_CONNECTEX*)out;
        // Windows' TCP provider shares one ConnectEx entry point. Do not guess for another provider.
        auto existing = (LPFN_CONNECTEX)InterlockedCompareExchangePointer((PVOID*)&originalConnectEx, (PVOID)fn, nullptr);
        if (existing && existing != fn) { WSASetLastError(WSAEOPNOTSUPP); return SOCKET_ERROR; }
        *(LPFN_CONNECTEX*)out = hookedConnectEx;
    }
    if (!result && code == SIO_GET_EXTENSION_FUNCTION_POINTER && in && inSize == sizeof(GUID) && out && outSize >= sizeof(void*)) {
        if (!ReplaceDatagramExtension(in, out)) { WSASetLastError(WSAEOPNOTSUPP); return SOCKET_ERROR; }
    }
    return result;
}
static int WSAAPI hookedPeer(SOCKET s, sockaddr* address, int* size) {
    int result = originalPeer(s, address, size);
    if (!result && address && size && *size >= (int)sizeof(sockaddr_in)) {
        AcquireSRWLockShared(&peersLock); auto it = peers.find(s);
        if (it != peers.end() && *size >= it->second.size) {
            memcpy(address, &it->second.address, it->second.size); *size = it->second.size;
        }
        ReleaseSRWLockShared(&peersLock);
    }
    return result;
}
static int WSAAPI hookedClose(SOCKET s) {
    ForgetDatagramSocket(s);
    std::vector<DatagramPeer> old;
    AcquireSRWLockExclusive(&peersLock); peers.erase(s);
    auto found = datagrams.find(s); if (found != datagrams.end()) { old = std::move(found->second); datagrams.erase(found); }
    ReleaseSRWLockExclusive(&peersLock);
    int result = originalClose(s), error = WSAGetLastError();
    for (auto& item : old) { ConnectRequest request; request.protocol = 3; request.port = item.localPort; ConnectReply reply; exchange(request, reply); }
    WSASetLastError(error); return result;
}
bool AttachConnectHooks() {
    if (!GetEnvironmentVariableW(L"REGION_LAUNCHER_CONNECT_PIPE", brokerPipe, _countof(brokerPipe))) return false;
    dedicatedDns = GetEnvironmentVariableW(L"REGION_LAUNCHER_DNS_ADDRESS", nullptr, 0) != 0;
    return DetourAttach(&(PVOID&)originalConnect, hookedConnect) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalWSAConnect, hookedWSAConnect) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalIoctl, hookedIoctl) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalPeer, hookedPeer) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalClose, hookedClose) == NO_ERROR && AttachDatagramHooks();
}
