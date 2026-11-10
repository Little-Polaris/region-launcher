#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <mswsock.h>
#include <windows.h>
#include <stdio.h>
#include <string>
static volatile bool callbackDone;
static DWORD callbackError;
static void CALLBACK completed(DWORD error, DWORD, OVERLAPPED*, DWORD) { callbackError = error; callbackDone = true; }
int wmain(int argc, wchar_t** argv) {
    if (argc != 2) return 2;
    std::wstring mode = argv[1]; WSADATA data; WSAStartup(MAKEWORD(2,2), &data);
    SOCKET s = WSASocketW(AF_INET, SOCK_DGRAM, 0, nullptr, 0, WSA_FLAG_OVERLAPPED);
    DWORD timeout = 4000; setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (char*)&timeout, sizeof(timeout));
    sockaddr_in target = {}; target.sin_family = AF_INET; target.sin_port = htons(443); InetPtonW(AF_INET, L"203.0.113.42", &target.sin_addr);
    sockaddr_in local = {}; local.sin_family = AF_INET; bind(s, (sockaddr*)&local, sizeof(local));
    HANDLE port = nullptr; OVERLAPPED ov = {}; ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (mode == L"iocp" || mode == L"iocpex") { CloseHandle(ov.hEvent); ov.hEvent = nullptr; port = CreateIoCompletionPort((HANDLE)s, nullptr, 42, 0); }
    sockaddr_in from = {}; int fromSize = sizeof(from); char reply[64] = {}; WSABUF buffer = {sizeof(reply), reply}; DWORD bytes = 0, flags = 0;
    bool sync = mode == L"sync" || mode == L"connected" || mode == L"sendmsg";
    int rc = 0;
    if (!sync) {
        if (mode == L"recvmsg") {
            // The message must remain alive until completion; use a blocking call below.
        } else rc = WSARecvFrom(s, &buffer, 1, &bytes, &flags, (sockaddr*)&from, &fromSize, &ov, mode == L"callback" ? completed : nullptr);
        if (rc && WSAGetLastError() != WSA_IO_PENDING) return 3;
    }
    WSABUF sendBuffer = {5, (char*)"probe"}; DWORD sent = 0;
    if (mode == L"connected") { rc = connect(s, (sockaddr*)&target, sizeof(target)); if (!rc) rc = send(s, "probe", 5, 0) == 5 ? 0 : -1; }
    else if (mode == L"sendmsg") {
        GUID id = WSAID_WSASENDMSG; LPFN_WSASENDMSG fn = nullptr;
        rc = WSAIoctl(s, SIO_GET_EXTENSION_FUNCTION_POINTER, &id, sizeof(id), &fn, sizeof(fn), &bytes, nullptr, nullptr);
        WSAMSG msg = {}; msg.name = (sockaddr*)&target; msg.namelen = sizeof(target); msg.lpBuffers = &sendBuffer; msg.dwBufferCount = 1;
        if (!rc) rc = fn(s, &msg, 0, &sent, nullptr, nullptr);
    } else rc = WSASendTo(s, &sendBuffer, 1, &sent, 0, (sockaddr*)&target, sizeof(target), nullptr, nullptr);
    if (rc) return 4;
    bool ok = false;
    if (sync) ok = recvfrom(s, reply, sizeof(reply), 0, (sockaddr*)&from, &fromSize) == 5;
    else if (mode == L"recvmsg") {
        GUID id = WSAID_WSARECVMSG; LPFN_WSARECVMSG fn = nullptr;
        rc = WSAIoctl(s, SIO_GET_EXTENSION_FUNCTION_POINTER, &id, sizeof(id), &fn, sizeof(fn), &bytes, nullptr, nullptr);
        WSAMSG msg = {}; msg.name = (sockaddr*)&from; msg.namelen = sizeof(from); msg.lpBuffers = &buffer; msg.dwBufferCount = 1;
        if (!rc) rc = fn(s, &msg, &bytes, &ov, nullptr);
        ok = (!rc || WSAGetLastError() == WSA_IO_PENDING) && WaitForSingleObject(ov.hEvent, 5000) == WAIT_OBJECT_0 && WSAGetOverlappedResult(s, &ov, &bytes, FALSE, &flags);
        fromSize = msg.namelen;
    } else if (mode == L"iocp") {
        ULONG_PTR key = 0; OVERLAPPED* result = nullptr;
        ok = GetQueuedCompletionStatus(port, &bytes, &key, &result, 5000) && key == 42 && result == &ov;
    } else if (mode == L"iocpex") {
        OVERLAPPED_ENTRY entry = {}; ULONG count = 0;
        ok = GetQueuedCompletionStatusEx(port, &entry, 1, &count, 5000, FALSE) && count == 1 && entry.lpOverlapped == &ov && entry.Internal == 0;
    } else if (mode == L"callback") {
        ULONGLONG deadline = GetTickCount64() + 5000;
        while (!callbackDone && GetTickCount64() < deadline) SleepEx(100, TRUE);
        ok = callbackDone && callbackError == 0;
    } else {
        ok = WaitForSingleObject(ov.hEvent, 5000) == WAIT_OBJECT_0;
        if (mode == L"result") ok = ok && WSAGetOverlappedResult(s, &ov, &bytes, FALSE, &flags);
    }
    ok = ok && from.sin_addr.s_addr == target.sin_addr.s_addr && from.sin_port == target.sin_port && !memcmp(reply, "probe", 5);
    printf("UDP_%ls=%s error=%d\n", argv[1], ok ? "OK" : "FAIL", WSAGetLastError());
    closesocket(s); if (ov.hEvent) CloseHandle(ov.hEvent); if (port) CloseHandle(port); WSACleanup(); return ok ? 0 : 5;
}
