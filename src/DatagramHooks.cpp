#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <mswsock.h>
#include <windows.h>
#include <unordered_map>
#include "detours.h"
#include "ConnectBroker.h"

static decltype(&sendto) originalSendTo = sendto;
static decltype(&WSASendTo) originalWSASendTo = WSASendTo;
static decltype(&recvfrom) originalRecvFrom = recvfrom;
static decltype(&WSARecvFrom) originalWSARecvFrom = WSARecvFrom;
static decltype(&WSAGetOverlappedResult) originalResult = WSAGetOverlappedResult;
static decltype(&GetOverlappedResult) originalFileResult = GetOverlappedResult;
static decltype(&GetQueuedCompletionStatus) originalCompletion = GetQueuedCompletionStatus;
static decltype(&GetQueuedCompletionStatusEx) originalCompletions = GetQueuedCompletionStatusEx;
static decltype(&WaitForSingleObject) originalWaitOne = WaitForSingleObject;
static decltype(&WaitForSingleObjectEx) originalWaitOneEx = WaitForSingleObjectEx;
static decltype(&WaitForMultipleObjects) originalWaitMany = WaitForMultipleObjects;
static decltype(&WaitForMultipleObjectsEx) originalWaitManyEx = WaitForMultipleObjectsEx;
static LPFN_WSARECVMSG originalRecvMsg;
static LPFN_WSASENDMSG originalSendMsg;
struct Pending { SOCKET socket; sockaddr* address; int* length; LPWSAOVERLAPPED_COMPLETION_ROUTINE callback; };
static SRWLOCK pendingLock = SRWLOCK_INIT;
static auto& pending = *new std::unordered_map<OVERLAPPED*, Pending>;
static thread_local bool polling;
void ForgetDatagramSocket(SOCKET socket) {
    AcquireSRWLockExclusive(&pendingLock);
    for (auto it = pending.begin(); it != pending.end();) {
        if (it->second.socket == socket && !it->second.callback) it = pending.erase(it); else ++it;
    }
    ReleaseSRWLockExclusive(&pendingLock);
}
static void remember(OVERLAPPED* ov, Pending value) {
    AcquireSRWLockExclusive(&pendingLock); pending[ov] = value; ReleaseSRWLockExclusive(&pendingLock);
}
static void erase(OVERLAPPED* ov) {
    AcquireSRWLockExclusive(&pendingLock); pending.erase(ov); ReleaseSRWLockExclusive(&pendingLock);
}
static LPWSAOVERLAPPED_COMPLETION_ROUTINE finish(OVERLAPPED* ov, bool ok, bool inCallback = false) {
    if (!ov || polling) return nullptr;
    LPWSAOVERLAPPED_COMPLETION_ROUTINE callback = nullptr;
    AcquireSRWLockExclusive(&pendingLock); auto it = pending.find(ov);
    if (it != pending.end()) {
        auto value = it->second; callback = value.callback;
        if (ok) RestoreDatagramPeer(value.socket, value.address, value.length);
        if (!callback || inCallback) pending.erase(it);
    }
    ReleaseSRWLockExclusive(&pendingLock); return callback;
}
static void CALLBACK completed(DWORD error, DWORD bytes, LPWSAOVERLAPPED ov, DWORD flags) {
    auto callback = finish(ov, error == 0, true);
    if (callback) callback(error, bytes, ov, flags);
}
// Event-based users may read the address immediately after a wait, without a
// separate GetOverlappedResult. Restore completed buffers before returning to them.
static void finishReady() {
    if (polling) return;
    polling = true;
    DWORD saved = GetLastError();
    AcquireSRWLockExclusive(&pendingLock);
    for (auto it = pending.begin(); it != pending.end();) {
        auto& value = it->second;
        if (value.callback || !it->first->hEvent) { ++it; continue; }
        DWORD bytes = 0, flags = 0;
        BOOL ok = originalResult(value.socket, it->first, &bytes, FALSE, &flags);
        if (!ok && WSAGetLastError() == WSA_IO_INCOMPLETE) { ++it; continue; }
        if (ok) RestoreDatagramPeer(value.socket, value.address, value.length);
        it = pending.erase(it);
    }
    ReleaseSRWLockExclusive(&pendingLock); SetLastError(saved); polling = false;
}
static int WSAAPI hookedSendTo(SOCKET s, const char* data, int length, int flags, const sockaddr* address, int size) {
    sockaddr_storage target; int route = PrepareDatagram(s, address, size, target);
    if (route < 0) return SOCKET_ERROR;
    if (route && length > 65497) { WSASetLastError(WSAEMSGSIZE); return SOCKET_ERROR; }
    return originalSendTo(s, data, length, flags, route ? (sockaddr*)&target : address, route ? route : size);
}
static int WSAAPI hookedWSASendTo(SOCKET s, LPWSABUF buffers, DWORD count, LPDWORD sent, DWORD flags, const sockaddr* address,
    int size, LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE callback) {
    sockaddr_storage target; int route = PrepareDatagram(s, address, size, target);
    if (route < 0) return SOCKET_ERROR;
    if (route && buffers) {
        uint64_t length = 0; for (DWORD i = 0; i < count; ++i) length += buffers[i].len;
        if (length > 65497) { WSASetLastError(WSAEMSGSIZE); return SOCKET_ERROR; }
    }
    return originalWSASendTo(s, buffers, count, sent, flags, route ? (sockaddr*)&target : address, route ? route : size, ov, callback);
}
static int WSAAPI hookedRecvFrom(SOCKET s, char* data, int length, int flags, sockaddr* address, int* size) {
    int result = originalRecvFrom(s, data, length, flags, address, size);
    if (result >= 0) RestoreDatagramPeer(s, address, size);
    return result;
}
static int WSAAPI hookedWSARecvFrom(SOCKET s, LPWSABUF buffers, DWORD count, LPDWORD received, LPDWORD flags,
    sockaddr* address, LPINT size, LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE callback) {
    if (ov) remember(ov, {s, address, size, callback});
    int result = originalWSARecvFrom(s, buffers, count, received, flags, address, size, ov, callback ? completed : nullptr);
    int error = WSAGetLastError();
    if (!result) { if (ov) finish(ov, true); else RestoreDatagramPeer(s, address, size); }
    else if (error != WSA_IO_PENDING && ov) erase(ov);
    WSASetLastError(error); return result;
}
static INT PASCAL hookedRecvMsg(SOCKET s, LPWSAMSG msg, LPDWORD received, LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE callback) {
    if (!msg) return originalRecvMsg(s, msg, received, ov, callback);
    if (ov) remember(ov, {s, msg->name, &msg->namelen, callback});
    int result = originalRecvMsg(s, msg, received, ov, callback ? completed : nullptr), error = WSAGetLastError();
    if (!result) { if (ov) finish(ov, true); else RestoreDatagramPeer(s, msg->name, &msg->namelen); }
    else if (error != WSA_IO_PENDING && ov) erase(ov);
    WSASetLastError(error); return result;
}
static INT PASCAL hookedSendMsg(SOCKET s, LPWSAMSG msg, DWORD flags, LPDWORD sent, LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE callback) {
    if (!msg) return originalSendMsg(s, msg, flags, sent, ov, callback);
    sockaddr_storage target; int route = PrepareDatagram(s, msg->name, msg->namelen, target);
    if (route < 0) return SOCKET_ERROR;
    WSAMSG copy = *msg;
    if (route) {
        uint64_t length = 0; if (msg->lpBuffers) for (DWORD i = 0; i < msg->dwBufferCount; ++i) length += msg->lpBuffers[i].len;
        if (length > 65497) { WSASetLastError(WSAEMSGSIZE); return SOCKET_ERROR; }
        copy.name = (sockaddr*)&target; copy.namelen = route;
    }
    return originalSendMsg(s, &copy, flags, sent, ov, callback);
}
bool ReplaceDatagramExtension(void* id, void* output) {
    static const GUID recvId = WSAID_WSARECVMSG, sendId = WSAID_WSASENDMSG;
    if (!memcmp(id, &recvId, sizeof(GUID))) {
        auto fn = *(LPFN_WSARECVMSG*)output;
        auto old = (LPFN_WSARECVMSG)InterlockedCompareExchangePointer((PVOID*)&originalRecvMsg, (PVOID)fn, nullptr);
        if (old && old != fn) return false;
        *(LPFN_WSARECVMSG*)output = hookedRecvMsg;
    } else if (!memcmp(id, &sendId, sizeof(GUID))) {
        auto fn = *(LPFN_WSASENDMSG*)output;
        auto old = (LPFN_WSASENDMSG)InterlockedCompareExchangePointer((PVOID*)&originalSendMsg, (PVOID)fn, nullptr);
        if (old && old != fn) return false;
        *(LPFN_WSASENDMSG*)output = hookedSendMsg;
    }
    return true;
}
static BOOL WSAAPI result(SOCKET s, LPWSAOVERLAPPED ov, LPDWORD bytes, BOOL wait, LPDWORD flags) {
    BOOL ok = originalResult(s, ov, bytes, wait, flags); int error = WSAGetLastError();
    if (ok || error != WSA_IO_INCOMPLETE) finish(ov, ok); WSASetLastError(error); return ok;
}
static BOOL WINAPI fileResult(HANDLE h, LPOVERLAPPED ov, LPDWORD bytes, BOOL wait) {
    BOOL ok = originalFileResult(h, ov, bytes, wait); DWORD error = GetLastError();
    if (ok || error != ERROR_IO_INCOMPLETE) finish(ov, ok); SetLastError(error); return ok;
}
static BOOL WINAPI completion(HANDLE port, LPDWORD bytes, PULONG_PTR key, LPOVERLAPPED* ov, DWORD timeout) {
    BOOL ok = originalCompletion(port, bytes, key, ov, timeout); DWORD error = GetLastError();
    if (ov && *ov) finish(*ov, ok); SetLastError(error); return ok;
}
static BOOL WINAPI completions(HANDLE port, LPOVERLAPPED_ENTRY entries, ULONG count, PULONG removed, DWORD timeout, BOOL alertable) {
    BOOL ok = originalCompletions(port, entries, count, removed, timeout, alertable); DWORD error = GetLastError();
    if (ok && entries && removed) for (ULONG i = 0; i < *removed; ++i) finish(entries[i].lpOverlapped, entries[i].Internal == 0);
    SetLastError(error); return ok;
}
static DWORD WINAPI waitOne(HANDLE handle, DWORD ms) { DWORD rc = originalWaitOne(handle, ms); finishReady(); return rc; }
static DWORD WINAPI waitOneEx(HANDLE handle, DWORD ms, BOOL alert) { DWORD rc = originalWaitOneEx(handle, ms, alert); finishReady(); return rc; }
static DWORD WINAPI waitMany(DWORD count, const HANDLE* handles, BOOL all, DWORD ms) { DWORD rc = originalWaitMany(count, handles, all, ms); finishReady(); return rc; }
static DWORD WINAPI waitManyEx(DWORD count, const HANDLE* handles, BOOL all, DWORD ms, BOOL alert) { DWORD rc = originalWaitManyEx(count, handles, all, ms, alert); finishReady(); return rc; }
bool AttachDatagramHooks() {
    return DetourAttach(&(PVOID&)originalSendTo, hookedSendTo) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalWSASendTo, hookedWSASendTo) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalRecvFrom, hookedRecvFrom) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalWSARecvFrom, hookedWSARecvFrom) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalResult, result) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalFileResult, fileResult) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalCompletion, completion) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalCompletions, completions) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalWaitOne, waitOne) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalWaitOneEx, waitOneEx) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalWaitMany, waitMany) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalWaitManyEx, waitManyEx) == NO_ERROR;
}
