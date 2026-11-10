#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <iphlpapi.h>
#include <mutex>
#include <condition_variable>
#include <thread>
#include <unordered_set>
#include <unordered_map>
#include <vector>
#include <stdio.h>
#include "ConnectBroker.h"
#include "NetworkProtocol.h"

static HANDLE brokerStop, brokerJob;
static USHORT socksPort;
static DWORD dnsAddress;
static std::thread pipeThread;
static std::mutex stateLock;
static std::condition_variable finished;
static std::unordered_set<SOCKET> sockets;
static unsigned workers;
static bool stopping() { return WaitForSingleObject(brokerStop, 0) == WAIT_OBJECT_0; }
struct Socket {
    SOCKET value = INVALID_SOCKET;
    explicit Socket(SOCKET s = INVALID_SOCKET) : value(s) {
        if (s != INVALID_SOCKET) { std::lock_guard<std::mutex> lock(stateLock); sockets.insert(s); }
    }
    ~Socket() {
        if (value != INVALID_SOCKET) {
            std::lock_guard<std::mutex> lock(stateLock); sockets.erase(value); closesocket(value);
        }
    }
    Socket(const Socket&) = delete;
    operator SOCKET() const { return value; }
};
static SOCKET tcp() { return WSASocketW(AF_INET, SOCK_STREAM, IPPROTO_TCP, nullptr, 0, WSA_FLAG_NO_HANDLE_INHERIT); }
static bool exact(SOCKET s, void* data, int length, bool write) {
    for (int offset = 0; offset < length;) {
        if (stopping()) return false;
        int count = write ? send(s, (char*)data + offset, length - offset, 0) : recv(s, (char*)data + offset, length - offset, 0);
        if (count <= 0) return false;
        offset += count;
    }
    return true;
}
static bool ready(SOCKET s, bool write, long ms) {
    fd_set set; FD_ZERO(&set); FD_SET(s, &set); timeval timeout = {ms / 1000, (ms % 1000) * 1000};
    return select(0, write ? nullptr : &set, write ? &set : nullptr, nullptr, &timeout) > 0;
}
static bool upstream(SOCKET s, const ConnectRequest& request, sockaddr_in* udpRelay = nullptr) {
    sockaddr_in proxy = {}; proxy.sin_family = AF_INET; proxy.sin_addr.s_addr = htonl(INADDR_LOOPBACK); proxy.sin_port = htons(socksPort);
    bool dns = dnsAddress && request.port == htons(53);
    if (dns) { proxy.sin_addr.s_addr = dnsAddress; proxy.sin_port = htons(53); }
    if (dns && udpRelay) { *udpRelay = proxy; return true; }
    DWORD timeout = 5000;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (char*)&timeout, sizeof(timeout));
    setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, (char*)&timeout, sizeof(timeout));
    u_long nonblock = 1; ioctlsocket(s, FIONBIO, &nonblock);
    int rc = connect(s, (sockaddr*)&proxy, sizeof(proxy));
    if (rc && (WSAGetLastError() != WSAEWOULDBLOCK || !ready(s, true, 5000))) return false;
    int error = 0, length = sizeof(error);
    if (getsockopt(s, SOL_SOCKET, SO_ERROR, (char*)&error, &length) || error) return false;
    nonblock = 0; ioctlsocket(s, FIONBIO, &nonblock);
    if (dns) return true;
    BYTE hello[] = {5, 1, 0}, answer[4] = {};
    if (!exact(s, hello, 3, true) || !exact(s, answer, 2, false) || answer[0] != 5 || answer[1] != 0) return false;
    BYTE command[10] = {5, 1, 0, 1}; memcpy(command + 4, &request.address, 4); memcpy(command + 8, &request.port, 2);
    if (udpRelay) { command[1] = 3; memset(command + 4, 0, 6); }
    if (!exact(s, command, 10, true) || !exact(s, answer, 4, false) || answer[0] != 5 || answer[1] || answer[2]) return false;
    BYTE discard[258]; int remaining = answer[3] == 1 ? 6 : answer[3] == 4 ? 18 : 0;
    if (answer[3] == 3) { if (!exact(s, discard, 1, false)) return false; remaining = discard[0] + 2; }
    if (!remaining || !exact(s, discard, remaining, false)) return false;
    if (udpRelay) {
        if (answer[3] != 1) return false;
        *udpRelay = {}; udpRelay->sin_family = AF_INET;
        memcpy(&udpRelay->sin_addr.s_addr, discard, 4); memcpy(&udpRelay->sin_port, discard + 4, 2);
        if (!udpRelay->sin_addr.s_addr) udpRelay->sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        if ((ntohl(udpRelay->sin_addr.s_addr) >> 24) != 127 || !udpRelay->sin_port) return false;
    }
    return true;
}
static bool peerOwnedBy(SOCKET s, ULONG pid) {
    sockaddr_in peer = {}; int len = sizeof(peer);
    if (getpeername(s, (sockaddr*)&peer, &len)) return false;
    DWORD size = 0;
    GetExtendedTcpTable(nullptr, &size, FALSE, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
    std::vector<BYTE> bytes(size);
    if (GetExtendedTcpTable(bytes.data(), &size, FALSE, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0)) return false;
    auto table = (MIB_TCPTABLE_OWNER_PID*)bytes.data();
    for (DWORD i = 0; i < table->dwNumEntries; ++i) {
        auto& row = table->table[i];
        if (row.dwLocalAddr == peer.sin_addr.s_addr && (USHORT)row.dwLocalPort == peer.sin_port && row.dwOwningPid == pid) return true;
    }
    return false;
}
struct Relay { SOCKET listener; ULONG pid; ConnectRequest request; };
struct UdpRelay { Relay base; USHORT localPort; HANDLE cancel; };
static std::unordered_map<USHORT, UdpRelay*> udpFlows;
static bool udpOwnedBy(USHORT port, ULONG pid) {
    for (ULONG family : {AF_INET, AF_INET6}) {
        DWORD size = 0; GetExtendedUdpTable(nullptr, &size, FALSE, family, UDP_TABLE_OWNER_PID, 0);
        std::vector<BYTE> data(size);
        if (GetExtendedUdpTable(data.data(), &size, FALSE, family, UDP_TABLE_OWNER_PID, 0)) continue;
        if (family == AF_INET) {
            auto table = (MIB_UDPTABLE_OWNER_PID*)data.data();
            for (DWORD i = 0; i < table->dwNumEntries; ++i) if ((USHORT)table->table[i].dwLocalPort == port && table->table[i].dwOwningPid == pid) return true;
        } else {
            auto table = (MIB_UDP6TABLE_OWNER_PID*)data.data();
            for (DWORD i = 0; i < table->dwNumEntries; ++i) if ((USHORT)table->table[i].dwLocalPort == port && table->table[i].dwOwningPid == pid) return true;
        }
    }
    return false;
}
static DWORD WINAPI udpRelay(void* opaque) {
    auto task = (UdpRelay*)opaque;
    HANDLE process = OpenProcess(SYNCHRONIZE, FALSE, task->base.pid);
    {
        Socket local(task->base.listener);
        sockaddr_in client = {};
        bool dns = dnsAddress && task->base.request.port == htons(53);
        while (process && !stopping() && WaitForSingleObject(task->cancel, 0) != WAIT_OBJECT_0 && WaitForSingleObject(process, 0) != WAIT_OBJECT_0) {
        Socket control(tcp());
        Socket remote(WSASocketW(AF_INET, SOCK_DGRAM, IPPROTO_UDP, nullptr, 0, WSA_FLAG_NO_HANDLE_INHERIT));
        sockaddr_in endpoint = {};
        bool ok = process && remote.value != INVALID_SOCKET && control.value != INVALID_SOCKET && upstream(control, task->base.request, &endpoint);
        if (ok) ok = !connect(remote, (sockaddr*)&endpoint, sizeof(endpoint));
        IN_ADDR ip; ip.s_addr = task->base.request.address; char text[INET_ADDRSTRLEN] = {};
        InetNtopA(AF_INET, &ip, text, sizeof(text));
        fprintf(stdout, "[ConnectBroker] PID=%lu UDP %s:%u %s SOCKS5 127.0.0.1:%u\n", task->base.pid, text, ntohs(task->base.request.port), ok ? "PROXY" : "FAILED", socksPort); fflush(stdout);
        std::vector<char> buffer(65536);
        while (ok && !stopping() && WaitForSingleObject(task->cancel, 0) != WAIT_OBJECT_0 && WaitForSingleObject(process, 0) != WAIT_OBJECT_0) {
            fd_set read; FD_ZERO(&read); FD_SET(local.value, &read); FD_SET(remote.value, &read); if (!dns) FD_SET(control.value, &read);
            timeval delay = {0, 200000}; int count = select(0, &read, nullptr, nullptr, &delay);
            if (count < 0 || (!dns && FD_ISSET(control.value, &read))) break;
            if (FD_ISSET(local.value, &read)) {
                sockaddr_in sender = {}; int length = sizeof(sender);
                int n = recvfrom(local, buffer.data() + 10, 65497, 0, (sockaddr*)&sender, &length);
                if (n < 0) continue;
                if (!client.sin_port) {
                    if (sender.sin_addr.s_addr != htonl(INADDR_LOOPBACK) || !udpOwnedBy(sender.sin_port, task->base.pid)) continue;
                    client = sender;
                }
                if (sender.sin_addr.s_addr != client.sin_addr.s_addr || sender.sin_port != client.sin_port) continue;
                memset(buffer.data(), 0, 3); buffer[3] = 1;
                memcpy(buffer.data() + 4, &task->base.request.address, 4); memcpy(buffer.data() + 8, &task->base.request.port, 2);
                int offset = dns ? 10 : 0, size = dns ? n : n + 10;
                if (send(remote, buffer.data() + offset, size, 0) != size) break;
            }
            if (FD_ISSET(remote.value, &read)) {
                int n = recv(remote, buffer.data(), (int)buffer.size(), 0);
                if (dns) {
                    if (n >= 0 && client.sin_port) sendto(local, buffer.data(), n, 0, (sockaddr*)&client, sizeof(client));
                    continue;
                }
                if (n < 10 || buffer[0] || buffer[1] || buffer[2] || buffer[3] != 1 || !client.sin_port) continue;
                if (memcmp(buffer.data() + 4, &task->base.request.address, 4) || memcmp(buffer.data() + 8, &task->base.request.port, 2)) continue;
                sendto(local, buffer.data() + 10, n - 10, 0, (sockaddr*)&client, sizeof(client));
            }
        }
        // Keep the local endpoint stable across an upstream restart. Lost packets
        // are left to the application's UDP retry; they never go directly outside.
        HANDLE waits[] = {brokerStop, task->cancel, process};
        if (WaitForMultipleObjects(3, waits, FALSE, 1000) != WAIT_TIMEOUT) break;
        }
    }
    if (process) CloseHandle(process);
    { std::lock_guard<std::mutex> lock(stateLock); udpFlows.erase(task->localPort); --workers; }
    CloseHandle(task->cancel); delete task; finished.notify_all(); return 0;
}
static DWORD WINAPI relay(void* opaque) {
    auto task = (Relay*)opaque;
    {
        Socket listener(task->listener);
        ULONGLONG deadline = GetTickCount64() + 10000;
        while (!stopping() && GetTickCount64() < deadline) {
            if (!ready(listener, false, 200)) continue;
            Socket client(accept(listener, nullptr, nullptr));
            if (client.value == INVALID_SOCKET) break;
            if (!peerOwnedBy(client, task->pid)) continue;
            Socket remote(tcp());
            bool ok = remote.value != INVALID_SOCKET && upstream(remote, task->request);
            IN_ADDR ip; ip.s_addr = task->request.address; char text[INET_ADDRSTRLEN] = {};
            InetNtopA(AF_INET, &ip, text, sizeof(text));
            fprintf(stdout, "[ConnectBroker] PID=%lu %s:%u %s SOCKS5 127.0.0.1:%u\n", task->pid, text, ntohs(task->request.port), ok ? "PROXY" : "FAILED", socksPort); fflush(stdout);
            if (!ok) { linger reset = {1, 0}; setsockopt(client, SOL_SOCKET, SO_LINGER, (char*)&reset, sizeof(reset)); break; }
            // Handshake has a deadline; a healthy stream may legitimately apply
            // backpressure for longer. StopConnectBroker interrupts blocked I/O.
            DWORD timeout = 0;
            setsockopt(remote, SOL_SOCKET, SO_SNDTIMEO, (char*)&timeout, sizeof(timeout));
            setsockopt(remote, SOL_SOCKET, SO_RCVTIMEO, (char*)&timeout, sizeof(timeout));
            BOOL noDelay = TRUE; setsockopt(remote, IPPROTO_TCP, TCP_NODELAY, (char*)&noDelay, sizeof(noDelay));
            bool clientRead = true, remoteRead = true;
            while (!stopping() && (clientRead || remoteRead)) {
                fd_set read; FD_ZERO(&read); if (clientRead) FD_SET(client.value, &read); if (remoteRead) FD_SET(remote.value, &read);
                timeval delay = {0, 200000}; int count = select(0, &read, nullptr, nullptr, &delay);
                if (count < 0) break;
                char buffer[32768]; bool failed = false;
                for (int i = 0; i < 2; ++i) {
                    SOCKET from = i ? remote.value : client.value, to = i ? client.value : remote.value;
                    if (!FD_ISSET(from, &read)) continue;
                    int n = recv(from, buffer, sizeof(buffer), 0);
                    if (n == 0) { (i ? remoteRead : clientRead) = false; shutdown(to, SD_SEND); }
                    else if (n < 0 || !exact(to, buffer, n, true)) { failed = true; break; }
                }
                if (failed) break;
            }
            break;
        }
    }
    delete task;
    { std::lock_guard<std::mutex> lock(stateLock); --workers; }
    finished.notify_all();
    return 0;
}
static void serve(HANDLE pipe) {
    ConnectRequest request; ConnectReply reply;
    ULONG pid = 0; BOOL inside = FALSE;
    HANDLE process = GetNamedPipeClientProcessId(pipe, &pid) ? OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid) : nullptr;
    bool allowed = process && IsProcessInJob(process, brokerJob, &inside) && inside;
    if (process) CloseHandle(process);
    if (!allowed || !PipeIO(pipe, &request, sizeof(request), false, brokerStop)) return;
    if (request.version != 2 || !request.port) return;
    if (request.protocol == 3) {
        { std::lock_guard<std::mutex> lock(stateLock); auto found = udpFlows.find(request.port);
          if (found != udpFlows.end() && found->second->base.pid == pid) SetEvent(found->second->cancel); }
        reply.error = 0; PipeIO(pipe, &reply, sizeof(reply), true, brokerStop); return;
    }
    if ((request.protocol != SOCK_STREAM && request.protocol != SOCK_DGRAM) || !request.address ||
        ((ntohl(request.address) >> 24) == 127 && !(dnsAddress && request.port == htons(53)))) return;
    bool udp = request.protocol == SOCK_DGRAM;
    SOCKET listener = udp ? WSASocketW(AF_INET, SOCK_DGRAM, IPPROTO_UDP, nullptr, 0, WSA_FLAG_NO_HANDLE_INHERIT) : tcp();
    if (listener == INVALID_SOCKET) return;
    sockaddr_in local = {}; local.sin_family = AF_INET; local.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    BOOL exclusive = TRUE; setsockopt(listener, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, (char*)&exclusive, sizeof(exclusive));
    int length = sizeof(local);
    bool ok = !bind(listener, (sockaddr*)&local, sizeof(local)) && (udp || !listen(listener, 1)) && !getsockname(listener, (sockaddr*)&local, &length);
    { std::lock_guard<std::mutex> lock(stateLock); if (workers >= 512) ok = false; if (ok) ++workers; }
    if (!ok) { closesocket(listener); return; }
    if (udp) {
        auto task = new UdpRelay{{listener, pid, request}, local.sin_port, CreateEventW(nullptr, TRUE, FALSE, nullptr)};
        { std::lock_guard<std::mutex> lock(stateLock); udpFlows[local.sin_port] = task; }
        if (!task->cancel || !QueueUserWorkItem(udpRelay, task, WT_EXECUTELONGFUNCTION)) {
            std::lock_guard<std::mutex> lock(stateLock); udpFlows.erase(local.sin_port); --workers;
            if (task->cancel) CloseHandle(task->cancel); closesocket(listener); delete task; return;
        }
    } else {
        auto task = new Relay{listener, pid, request};
        if (!QueueUserWorkItem(relay, task, WT_EXECUTELONGFUNCTION)) {
            closesocket(listener); delete task; std::lock_guard<std::mutex> lock(stateLock); --workers; return;
        }
    }
    reply.error = 0; reply.port = local.sin_port;
    PipeIO(pipe, &reply, sizeof(reply), true, brokerStop);
}
bool StartConnectBroker(const std::wstring& name, HANDLE job, USHORT port) {
    WSADATA data; if (WSAStartup(MAKEWORD(2, 2), &data)) return false;
    WCHAR dns[128] = {}; IN_ADDR parsed = {};
    GetEnvironmentVariableW(L"REGION_LAUNCHER_DNS_ADDRESS", dns, _countof(dns));
    dnsAddress = InetPtonW(AF_INET, dns, &parsed) == 1 && (ntohl(parsed.s_addr) >> 24) == 127 ? parsed.s_addr : 0;
    brokerStop = CreateEventW(nullptr, TRUE, FALSE, nullptr); brokerJob = job; socksPort = port;
    HANDLE pipe = CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, sizeof(ConnectReply), sizeof(ConnectRequest), 0, nullptr);
    if (!brokerStop || pipe == INVALID_HANDLE_VALUE) {
        if (pipe != INVALID_HANDLE_VALUE) CloseHandle(pipe);
        if (brokerStop) CloseHandle(brokerStop); brokerStop = nullptr; WSACleanup(); return false;
    }
    pipeThread = std::thread([pipe]() {
        while (!stopping()) {
            OVERLAPPED ov = {}; ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            BOOL ok = ConnectNamedPipe(pipe, &ov); DWORD error = ok ? 0 : GetLastError();
            if (!ok && error == ERROR_IO_PENDING) {
                HANDLE waits[] = {ov.hEvent, brokerStop}; DWORD bytes;
                if (WaitForMultipleObjects(2, waits, FALSE, INFINITE) == WAIT_OBJECT_0) ok = GetOverlappedResult(pipe, &ov, &bytes, FALSE);
                else { CancelIoEx(pipe, &ov); GetOverlappedResult(pipe, &ov, &bytes, TRUE); }
            } else if (error == ERROR_PIPE_CONNECTED) ok = TRUE;
            CloseHandle(ov.hEvent);
            if (ok && !stopping()) serve(pipe);
            DisconnectNamedPipe(pipe);
        }
        CloseHandle(pipe);
    });
    return true;
}
void StopConnectBroker() {
    if (!brokerStop) return;
    SetEvent(brokerStop);
    if (pipeThread.joinable()) pipeThread.join();
    std::unique_lock<std::mutex> lock(stateLock);
    for (auto s : sockets) shutdown(s, SD_BOTH);
    finished.wait(lock, [] { return workers == 0; });
    CloseHandle(brokerStop); brokerStop = nullptr; WSACleanup();
}
